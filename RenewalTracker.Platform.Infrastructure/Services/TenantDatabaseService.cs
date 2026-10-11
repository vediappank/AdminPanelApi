using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Database per tenant, fully automatic - no manual steps.
///
/// Scripts live in the TenantDatabase folder (see TenantDatabase/README.md):
///   TenantDatabase/Schema/*.sql      tables, keys, indexes - run once each, in file-name order
///   TenantDatabase/MasterData/*.sql  extra default records - run when new or changed (must be re-runnable)
///
/// Master data is also copied by itself from the template database (TenantDatabase:TemplateDatabase,
/// default "BusinessPanel" on the same server): every table in TenantDatabase:TemplateTables gets the
/// template's rows the first time the tenant database is built. Only rows whose primary key is missing
/// are inserted, so a tenant's own edits are never overwritten. Re-seed adds missing rows again.
///
/// For each tenant this service creates [Prefix + TenantCode] on the same SQL Server as
/// SuperAdminPanel, keeps a dbo.SchemaVersions table inside it (which scripts ran, with a
/// checksum), and records the database, versions, step and last error in
/// SuperAdminPanel.dbo.TenantDatabases. It runs on Provision tenant, on Complete
/// provisioning, from the Control Center buttons, and from TenantDatabaseMigrationWorker
/// at API start-up and every few minutes - so a newly added script reaches every tenant
/// database by itself.
///
/// Config (appsettings, all optional):
///   TenantDatabase:Enabled              true
///   TenantDatabase:DatabasePrefix       "BP_"
///   TenantDatabase:ScriptsPath          folder holding Schema/ and MasterData/ (default: found automatically)
///   TenantDatabase:BackupFolder         folder on the SQL Server for backups (default: SQL Server's backup folder)
///   TenantDatabase:AutoMigrateMinutes   10 (0 = only at start-up)
///   TenantDatabase:TemplateDatabase     "BusinessPanel" (empty = no copy)
///   TenantDatabase:TemplateTables       list of tables to copy, parents first (default: DefaultTemplateTables)
/// </summary>
public class TenantDatabaseService : ITenantDatabaseService
{
    // One run at a time per API process - provisioning, buttons and the worker can overlap.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Regex GoSplitter = new(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PlatformDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<TenantDatabaseService> _logger;
    private readonly TenantDatabaseQueue _queue;

    public TenantDatabaseService(PlatformDbContext db, IConfiguration config, IHostEnvironment env, ILogger<TenantDatabaseService> logger, TenantDatabaseQueue queue)
    {
        _queue = queue;
        _db = db;
        _config = config;
        _env = env;
        _logger = logger;
    }

    // Provisioning steps (TenantDatabase.ProvisioningStep = the step in progress; every lower one is done).
    public const int StepRegistered = 0, StepDatabase = 1, StepSchema = 2, StepMasterData = 3, StepSettings = 4,
        StepMenus = 5, StepRole = 6, StepOwner = 7, StepVerify = 8, StepComplete = 9;

    public static readonly string[] StepNames =
    {
        "Tenant registered", "Database created", "Schema migrated", "Master data seeded", "Tenant settings applied",
        "Menus & modules applied", "Administrator role & permissions", "Owner user created", "Verified - provisioning complete",
    };

    private bool FeatureEnabled => _config.GetValue("TenantDatabase:Enabled", true);
    private string Prefix => _config["TenantDatabase:DatabasePrefix"] ?? "BP_";
    private string TemplateDatabase => (_config["TenantDatabase:TemplateDatabase"] ?? "BusinessPanel").Trim();

    /// <summary>Lookup / setup tables copied from the template, parents before children (foreign keys).
    /// Not copied: tenant data (customers, trackers, quotations, invoices, payments, follow-ups, documents),
    /// users and employees (the tenant admin is created separately), application settings and
    /// notification rules / recipients (they hold your own company's details).</summary>
    private static readonly string[] DefaultTemplateTables =
    {
        "Roles", "RolePermissions", "MenuItems",
        "EmailTemplates", "FollowUpFrequencies",
        "CustomerTypes", "PaymentMethods", "Relationships",
        "TrackerStatuses", "QuotationStatuses",
        "ServiceCategories", "ServiceSubCategories", "Services",
    };

    private string[] TemplateTables
    {
        get
        {
            var list = _config.GetSection("TenantDatabase:TemplateTables").GetChildren()
                .Select(c => (c.Value ?? string.Empty).Trim()).Where(v => v.Length > 0).ToArray();
            return list.Length > 0 ? list : DefaultTemplateTables;
        }
    }

    // ------------------------------------------------------------------ public

    public async Task<TenantDatabaseRunResult> EnsureUpToDateAsync(int tenantId, CancellationToken ct = default)
    {
        if (!FeatureEnabled) return new(false, "Tenant databases are switched off (TenantDatabase:Enabled = false).");
        await Gate.WaitAsync(ct);
        try
        {
            return await RunAsync(tenantId, forceSeed: false, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<TenantDatabaseRunResult> ReseedAsync(int tenantId, CancellationToken ct = default)
    {
        if (!FeatureEnabled) return new(false, "Tenant databases are switched off (TenantDatabase:Enabled = false).");
        await Gate.WaitAsync(ct);
        try
        {
            return await RunAsync(tenantId, forceSeed: true, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<TenantDatabaseRunResult> BackupAsync(int tenantId, CancellationToken ct = default)
    {
        var reg = await _db.TenantDatabases.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        if (reg == null || reg.ProvisioningStep <= StepDatabase) return new(false, "This tenant has no database yet.");
        try
        {
            var folder = (_config["TenantDatabase:BackupFolder"] ?? string.Empty).Trim();
            var file = $"{reg.DatabaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            var path = folder.Length > 0 ? Path.Combine(folder, file) : file;
            await using var conn = new SqlConnection(ConnectionFor("master"));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand($"BACKUP DATABASE {Q(reg.DatabaseName)} TO DISK = @path WITH COPY_ONLY, INIT", conn) { CommandTimeout = 0 };
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync(ct);
            reg.LastBackupDate = DateTime.UtcNow;
            reg.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new(true, $"Backup saved as {path}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed for tenant {TenantId}", tenantId);
            return new(false, $"Backup failed: {ex.Message}");
        }
    }

    public async Task QueueProvisioningAsync(int tenantId, TenantOwnerLogin? owner, TenantSettings? settings = null, CancellationToken ct = default)
    {
        if (!FeatureEnabled) return;
        await EnsureRegistryTableAsync(ct);
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} was not found.");

        var reg = await _db.TenantDatabases.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        if (reg == null)
        {
            reg = new TenantDatabase
            {
                TenantId = tenantId,
                DatabaseName = await UniqueDatabaseNameAsync(tenant.TenantCode, ct),
                ServerName = new SqlConnectionStringBuilder(BaseConnectionString).DataSource,
                IsEnabled = true,
                CreatedDate = DateTime.UtcNow,
                ProvisioningStep = 1,
            };
            _db.TenantDatabases.Add(reg);
        }
        if (owner != null && !string.IsNullOrWhiteSpace(owner.Email) && !string.IsNullOrEmpty(owner.Password))
        {
            reg.OwnerFirstName = owner.FirstName?.Trim();
            reg.OwnerLastName = string.IsNullOrWhiteSpace(owner.LastName) ? null : owner.LastName.Trim();
            reg.OwnerEmail = owner.Email.Trim();
            reg.OwnerPasswordHash = BCrypt.Net.BCrypt.HashPassword(owner.Password);   // same hashing as the Business Panel login
            reg.OwnerUserId = null;
            if (reg.ProvisioningStep > StepOwner) reg.ProvisioningStep = StepOwner;
        }
        if (settings != null)
        {
            // Written into the tenant's dbo.ApplicationSettings by the "Tenant settings" step.
            var values = new Dictionary<string, string>
            {
                ["Tenant.Code"] = tenant.TenantCode,
                ["Tenant.Name"] = tenant.TenantName,
                ["Tenant.DateFormat"] = Clean(settings.DateFormat) ?? "dd-MM-yyyy",
                ["Tenant.TimeZone"] = Clean(settings.TimeZone) ?? "UTC",
                ["Tenant.Currency"] = (Clean(settings.Currency) ?? "USD").ToUpperInvariant(),
            };
            var contactName = Clean(tenant.ContactName) ?? Clean($"{owner?.FirstName} {owner?.LastName}");
            var contactEmail = Clean(tenant.ContactEmail) ?? Clean(owner?.Email);
            if (contactName != null) values["Tenant.ContactName"] = contactName;
            if (contactEmail != null) values["Tenant.ContactEmail"] = contactEmail;
            if (Clean(tenant.ContactMobile) is string mobile) values["Tenant.ContactMobile"] = mobile;
            reg.SettingsJson = System.Text.Json.JsonSerializer.Serialize(values);
        }
        reg.Status = "Pending";
        reg.LastError = null;
        reg.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await RecordStepAsync(tenantId, StepRegistered, tenant.CreatedDate, tenant.CreatedDate, null, overwrite: false, ct);
        _queue.Enqueue(tenantId);
    }

    public async Task MigrateAllAsync(CancellationToken ct = default, bool registryOnly = false)
    {
        if (!FeatureEnabled) return;
        await EnsureRegistryTableAsync(ct);
        if (registryOnly) return;
        var tenantIds = await _db.Tenants.AsNoTracking().Select(t => t.TenantId).ToListAsync(ct);
        var disabled = (await _db.TenantDatabases.AsNoTracking().Where(r => !r.IsEnabled).Select(r => r.TenantId).ToListAsync(ct)).ToHashSet();
        foreach (var id in tenantIds.Where(id => !disabled.Contains(id)))
        {
            if (ct.IsCancellationRequested) break;
            await EnsureUpToDateAsync(id, ct);
        }
    }

    /// <summary>Business API side finished (only when Provisioning:SkipBusinessApiCall is off) - logged only;
    /// completion is the operator's Verify on the Control Center.</summary>
    public Task MarkActiveAsync(int tenantId, CancellationToken ct = default)
    {
        _logger.LogInformation("Business-side provisioning finished for tenant {TenantId}.", tenantId);
        return Task.CompletedTask;
    }

    public async Task<TenantDatabaseRunResult> VerifyAsync(int tenantId, string? verifiedBy, CancellationToken ct = default)
    {
        var reg = await _db.TenantDatabases.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        if (reg == null) return new(false, "This tenant has no database yet.");
        if (reg.Status is "Pending" or "Running") return new(false, "Provisioning is still running - wait until every step is done.");
        if (reg.ProvisioningStep >= StepComplete) return new(true, "Provisioning was already verified.");
        if (reg.ProvisioningStep < StepVerify) return new(false, $"Not every step is done yet - next: {StepNames[reg.ProvisioningStep]}.");

        var now = DateTime.UtcNow;
        reg.ProvisioningStep = StepComplete;
        reg.VerifiedDate = now;
        reg.VerifiedBy = string.IsNullOrWhiteSpace(verifiedBy) ? null : verifiedBy.Trim();
        reg.ModifiedDate = now;
        await _db.SaveChangesAsync(ct);
        await RecordStepAsync(tenantId, StepVerify, now, now, reg.VerifiedBy == null ? null : "by " + reg.VerifiedBy, overwrite: true, ct);
        return new(true, "Provisioning verified and complete.");
    }

    public async Task<List<TenantProvisioningStepInfo>> GetStepsAsync(int tenantId, CancellationToken ct = default)
    {
        var list = new List<TenantProvisioningStepInfo>();
        try
        {
            await using var conn = new SqlConnection(BaseConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT StepNo, StepName, StartedDate, CompletedDate, Detail FROM dbo.TenantProvisioningSteps WHERE TenantId = @t ORDER BY StepNo", conn);
            cmd.Parameters.AddWithValue("@t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new TenantProvisioningStepInfo(r.GetInt32(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }
        catch (SqlException) { /* table not created yet */ }
        return list;
    }

    /// <summary>Upserts one row of dbo.TenantProvisioningSteps. overwrite = false keeps an existing row as it is
    /// (so the first completion time stays); true replaces it (the step really ran again and changed something).</summary>
    private async Task RecordStepAsync(int tenantId, int stepNo, DateTime started, DateTime completed, string? detail, bool overwrite, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(BaseConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
MERGE dbo.TenantProvisioningSteps AS t
USING (SELECT @t AS TenantId, @n AS StepNo) AS s ON t.TenantId = s.TenantId AND t.StepNo = s.StepNo
WHEN MATCHED AND @o = 1 THEN UPDATE SET StepName = @name, StartedDate = @s, CompletedDate = @c, Detail = @d
WHEN NOT MATCHED THEN INSERT (TenantId, StepNo, StepName, StartedDate, CompletedDate, Detail) VALUES (@t, @n, @name, @s, @c, @d);", conn);
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@n", stepNo);
            cmd.Parameters.AddWithValue("@name", StepNames[stepNo]);
            cmd.Parameters.AddWithValue("@s", started);
            cmd.Parameters.AddWithValue("@c", completed);
            cmd.Parameters.AddWithValue("@d", (object?)(detail is { Length: > 500 } ? detail[..500] : detail) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@o", overwrite);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record provisioning step {Step} for tenant {TenantId}", stepNo, tenantId);
        }
    }

    // ------------------------------------------------------------------ progress (read-only)

    private static readonly Regex CreateObject = new(
        @"\bCREATE\s+(?:OR\s+ALTER\s+)?(TABLE|VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER)\s+(?:\[?dbo\]?\s*\.\s*)?\[?([A-Za-z0-9_]+)\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<TenantDatabaseProgress?> GetProgressAsync(int tenantId, CancellationToken ct = default)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return null;
        TenantDatabase? reg = null;
        try { reg = await _db.TenantDatabases.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId, ct); }
        catch (SqlException) { /* registry table not created yet */ }

        // 1. What the scripts say the database should have.
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ScriptFiles("Schema"))
        {
            var text = StripComments(await File.ReadAllTextAsync(file, ct));
            foreach (Match m in CreateObject.Matches(text))
            {
                var type = m.Groups[1].Value.ToUpperInvariant() switch
                {
                    "TABLE" => "Table", "VIEW" => "View", "FUNCTION" => "Function", "TRIGGER" => "Trigger", _ => "Procedure",
                };
                expected.TryAdd(m.Groups[2].Value, type);
            }
        }

        var dbName = reg?.DatabaseName;
        var template = TemplateDatabase;
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var templateRows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, (string Kind, string Checksum, DateTime Applied)>(StringComparer.OrdinalIgnoreCase);
        bool dbExists = false, templateExists = false;

        await using (var master = new SqlConnection(ConnectionFor("master")))
        {
            await master.OpenAsync(ct);
            async Task<bool> Exists(string? n)
            {
                if (string.IsNullOrEmpty(n)) return false;
                await using var c = new SqlCommand("SELECT DB_ID(@n)", master);
                c.Parameters.AddWithValue("@n", n);
                return await c.ExecuteScalarAsync(ct) is not (null or DBNull);
            }
            dbExists = await Exists(dbName);
            templateExists = template.Length > 0 && await Exists(template);
        }

        const string RowCounts = @"SELECT t.name, SUM(p.rows) FROM {0}sys.tables t
JOIN {0}sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.schema_id = 1 GROUP BY t.name;";

        if (dbExists)
        {
            await using var conn = new SqlConnection(ConnectionFor(dbName!));
            await conn.OpenAsync(ct);
            await using (var cmd = new SqlCommand(
                "SELECT name, type FROM sys.objects WHERE schema_id = SCHEMA_ID(N'dbo') AND type IN ('U','V','P','FN','IF','TF','TR')", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) existing[r.GetString(0)] = r.GetString(1).Trim();

            await using (var cmd = new SqlCommand(string.Format(RowCounts, ""), conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) rows[r.GetString(0)] = Convert.ToInt64(r.GetValue(1));

            if (existing.ContainsKey("SchemaVersions"))
            {
                await using var cmd = new SqlCommand("SELECT ScriptName, Kind, Checksum, AppliedDate FROM dbo.SchemaVersions", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    versions[r.GetString(1) + "|" + r.GetString(0)] = (r.GetString(1), r.GetString(2), r.GetDateTime(3));
            }
        }

        if (templateExists)
        {
            await using var conn = new SqlConnection(ConnectionFor("master"));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(string.Format(RowCounts, Q(template) + "."), conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) templateRows[r.GetString(0)] = Convert.ToInt64(r.GetValue(1));
        }

        var objects = expected
            .Select(e => new TenantDatabaseObjectProgress(e.Key, e.Value, existing.ContainsKey(e.Key),
                e.Value == "Table" && rows.TryGetValue(e.Key, out var n) ? n : null))
            .OrderBy(o => TypeOrder(o.Type)).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var masterData = TemplateTables
            .Select(t =>
            {
                var done = versions.TryGetValue("Template|copy:" + t, out var v);
                return new TenantMasterDataProgress(t, done, done ? v.Applied : null,
                    rows.TryGetValue(t, out var tr) ? tr : null,
                    templateRows.TryGetValue(t, out var mr) ? mr : null);
            })
            .ToList();

        var scripts = new List<TenantScriptProgress>();
        foreach (var kind in new[] { "Schema", "MasterData" })
        {
            foreach (var file in ScriptFiles(kind))
            {
                var name = Path.GetFileName(file);
                var sum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(file, ct))));
                if (!versions.TryGetValue(kind + "|" + name, out var v)) scripts.Add(new(kind, name, "Pending", null));
                else if (!string.Equals(v.Checksum, sum, StringComparison.OrdinalIgnoreCase)) scripts.Add(new(kind, name, "Changed", v.Applied));
                else scripts.Add(new(kind, name, "Applied", v.Applied));
            }
        }

        return new TenantDatabaseProgress(tenantId, dbName, dbExists, template.Length > 0 ? template : null, templateExists,
            reg?.ProvisioningStep ?? 0, reg?.Status, reg?.LastError, reg?.LastMigratedDate, DateTime.UtcNow,
            objects, masterData, scripts);
    }

    private static int TypeOrder(string type) => type switch { "Table" => 0, "View" => 1, "Function" => 2, "Procedure" => 3, _ => 4 };

    /// <summary>Removes -- and /* */ comments so commented-out CREATE statements are not counted.</summary>
    private static string StripComments(string sql)
    {
        var noBlock = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"--[^\r\n]*", " ");
    }

    // ------------------------------------------------------------------ the run

    private async Task<TenantDatabaseRunResult> RunAsync(int tenantId, bool forceSeed, CancellationToken ct)
    {
        await EnsureRegistryTableAsync(ct);
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (tenant == null) return new(false, "Tenant not found.");

        var reg = await _db.TenantDatabases.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        if (reg == null)
        {
            reg = new TenantDatabase
            {
                TenantId = tenantId,
                DatabaseName = await UniqueDatabaseNameAsync(tenant.TenantCode, ct),
                ServerName = new SqlConnectionStringBuilder(BaseConnectionString).DataSource,
                Status = "Pending",
                IsEnabled = true,
                CreatedDate = DateTime.UtcNow,
            };
            _db.TenantDatabases.Add(reg);
            await _db.SaveChangesAsync(ct);
        }

        var applied = new List<string>();
        reg.Status = "Running";
        reg.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await RecordStepAsync(tenantId, StepRegistered, tenant.CreatedDate, tenant.CreatedDate, null, overwrite: false, ct);
        try
        {
            // Each step is saved as soon as it is done, with its start / finish time
            // (dbo.TenantProvisioningSteps), so the Control Center shows live progress.

            // 1. Database
            var t0 = DateTime.UtcNow;
            var created = await CreateDatabaseIfMissingAsync(reg.DatabaseName, ct);
            await EnsureVersionsTableAsync(reg.DatabaseName, ct);
            await StepDoneAsync(reg, StepDatabase, t0, created ? reg.DatabaseName : null, created, ct);

            // 2. Schema - each new script once, in order; a changed one is reported, never re-run.
            t0 = DateTime.UtcNow;
            var schema = await ApplyScriptsAsync(reg.DatabaseName, "Schema", rerunChanged: false, forceAll: false, ct);
            applied.AddRange(schema.Applied);
            if (schema.Last != null) reg.SchemaVersion = schema.Last;
            await StepDoneAsync(reg, StepSchema, t0, schema.Applied.Count > 0 ? string.Join(", ", schema.Applied) : null, schema.Applied.Count > 0, ct);

            // 3. Master data - copied from the template database, then MasterData scripts
            //    (new or changed ones; all of them on Re-seed).
            t0 = DateTime.UtcNow;
            var copy = await CopyTemplateDataAsync(reg.DatabaseName, forceSeed, ct);
            applied.AddRange(copy.Applied);
            var seed = await ApplyScriptsAsync(reg.DatabaseName, "MasterData", rerunChanged: true, forceAll: forceSeed, ct);
            applied.AddRange(seed.Applied);
            if (seed.Last != null) reg.SeedVersion = seed.Last;
            else if (copy.Last != null && reg.SeedVersion == null) reg.SeedVersion = copy.Last;
            var seeded = copy.Applied.Count + seed.Applied.Count;
            await StepDoneAsync(reg, StepMasterData, t0, seeded > 0 ? $"{copy.Applied.Count} table(s) copied, {seed.Applied.Count} script(s)" : null, seeded > 0, ct);

            // 4. Tenant settings - company name / code, date format, time zone, currency, contact
            //    into the tenant's dbo.ApplicationSettings (keys "Tenant.*").
            t0 = DateTime.UtcNow;
            var settings = await ApplyTenantSettingsAsync(reg, tenant, ct);
            if (settings.Changed) applied.Add("Settings: " + settings.Detail);
            await StepDoneAsync(reg, StepSettings, t0, settings.Detail, settings.Changed, ct);

            // 5. Menus & modules - the menus assigned on Provision tenant / Control Center (every run,
            //    so a change made there reaches the tenant database by itself).
            t0 = DateTime.UtcNow;
            var menus = await ApplyMenusAsync(reg, tenantId, ct);
            if (menus.Changed) applied.Add("Menus: " + menus.Detail);
            await StepDoneAsync(reg, StepMenus, t0, menus.Detail, menus.Changed, ct);

            // 6. Administrator role - full rights on exactly those menus; other roles lose switched-off menus.
            t0 = DateTime.UtcNow;
            var role = await ApplyRolePermissionsAsync(reg, ct);
            if (role.Changed) applied.Add("Role: " + role.Detail);
            await StepDoneAsync(reg, StepRole, t0, role.Detail, role.Changed, ct);

            // 7. Owner user - the login entered on Provision tenant, with the Administrator role.
            if (reg.OwnerUserId == null && !string.IsNullOrEmpty(reg.OwnerEmail) && !string.IsNullOrEmpty(reg.OwnerPasswordHash))
            {
                t0 = DateTime.UtcNow;
                reg.OwnerUserId = await EnsureOwnerAsync(reg, ct);
                reg.OwnerPasswordHash = null;   // the hash now lives only in the tenant's dbo.Users
                applied.Add($"Owner {reg.OwnerEmail}");
                await StepDoneAsync(reg, StepOwner, t0, reg.OwnerEmail, true, ct);
            }
            // 8. Verify - done by the operator on the Control Center (VerifyAsync).

            var warnings = schema.Warnings.Concat(copy.Warnings).Concat(menus.Warnings).ToList();
            reg.Status = "Ready";
            reg.LastError = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            reg.LastMigratedDate = DateTime.UtcNow;
            reg.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (applied.Count > 0)
                _logger.LogInformation("Tenant {TenantId} database {Db}: applied {Scripts}", tenantId, reg.DatabaseName, string.Join(", ", applied));
            return new(true, applied.Count > 0 ? $"{reg.DatabaseName}: {applied.Count} change(s) applied." : $"{reg.DatabaseName} is up to date.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant {TenantId} database {Db} failed", tenantId, reg.DatabaseName);
            reg.Status = "Failed";
            reg.LastError = ex.Message;
            reg.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new(false, $"{reg.DatabaseName}: {ex.Message}");
        }
    }

    /// <summary>Marks a step done: moves ProvisioningStep past it, saves, and records its times
    /// (replacing earlier times only when the step really changed something this run).</summary>
    private async Task StepDoneAsync(TenantDatabase reg, int step, DateTime started, string? detail, bool changed, CancellationToken ct)
    {
        if (reg.ProvisioningStep < step + 1) reg.ProvisioningStep = step + 1;
        reg.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await RecordStepAsync(reg.TenantId, step, started, DateTime.UtcNow, detail, overwrite: changed, ct);
    }

    private sealed record ScriptRun(List<string> Applied, string? Last, List<string> Warnings);

    private async Task<ScriptRun> ApplyScriptsAsync(string database, string kind, bool rerunChanged, bool forceAll, CancellationToken ct)
    {
        var files = ScriptFiles(kind);
        var applied = new List<string>();
        var warnings = new List<string>();
        string? last = null;

        await using var conn = new SqlConnection(ConnectionFor(database));
        await conn.OpenAsync(ct);
        var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new SqlCommand("SELECT ScriptName, Checksum FROM dbo.SchemaVersions WHERE Kind = @kind", conn))
        {
            read.Parameters.AddWithValue("@kind", kind);
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) done[r.GetString(0)] = r.GetString(1);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var sql = await File.ReadAllTextAsync(file, ct);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            var known = done.TryGetValue(name, out var oldSum);
            var changed = known && !string.Equals(oldSum, checksum, StringComparison.OrdinalIgnoreCase);

            if (known && !changed && !forceAll) { last = name; continue; }
            if (known && changed && !rerunChanged && !forceAll)
            {
                warnings.Add($"{kind}/{name} was changed after it ran - add a new numbered script instead.");
                last = name;
                continue;
            }

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var batch in GoSplitter.Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0))
                {
                    await using var cmd = new SqlCommand(batch, conn, tx) { CommandTimeout = 600 };
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using var mark = new SqlCommand(
                    "MERGE dbo.SchemaVersions AS t USING (SELECT @n AS ScriptName) AS s ON t.ScriptName = s.ScriptName " +
                    "WHEN MATCHED THEN UPDATE SET Checksum = @c, Kind = @k, AppliedDate = SYSUTCDATETIME() " +
                    "WHEN NOT MATCHED THEN INSERT (ScriptName, Kind, Checksum, AppliedDate) VALUES (@n, @k, @c, SYSUTCDATETIME());", conn, tx);
                mark.Parameters.AddWithValue("@n", name);
                mark.Parameters.AddWithValue("@k", kind);
                mark.Parameters.AddWithValue("@c", checksum);
                await mark.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                throw new InvalidOperationException($"{kind}/{name} failed: {ex.Message}", ex);
            }
            applied.Add($"{kind}/{name}");
            last = name;
        }
        return new ScriptRun(applied, last, warnings);
    }

    // ------------------------------------------------------------------ menus & role

    private sealed record StepResult(bool Changed, string Detail, List<string> Warnings);

    /// <summary>Picks the tenant's Administrator role into @roleId, creating it when the database has none.</summary>
    private const string PickAdminRoleSql = @"
DECLARE @roleId INT = (
    SELECT TOP (1) RoleId FROM dbo.Roles
     WHERE IsActive = 1
     ORDER BY CASE WHEN RoleName IN (N'Administrator', N'Admin', N'Super Admin', N'SuperAdmin') THEN 0
                   WHEN RoleName LIKE N'%admin%' THEN 1 ELSE 2 END, RoleId);
IF @roleId IS NULL
BEGIN
    INSERT dbo.Roles (RoleName, Description, IsActive, CreatedDate)
    VALUES (N'Administrator', N'System administrator - full access', 1, SYSUTCDATETIME());
    SET @roleId = SCOPE_IDENTITY();
END;";

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// The tenant's own settings in dbo.ApplicationSettings (keys "Tenant.*"). Values entered on
    /// Provision tenant / Complete provisioning (TenantDatabase.SettingsJson) are written over what is
    /// there, then cleared; otherwise only missing keys are added (code, name and defaults), so a
    /// tenant's own later changes are kept.
    /// </summary>
    private async Task<StepResult> ApplyTenantSettingsAsync(TenantDatabase reg, Tenant tenant, CancellationToken ct)
    {
        var pending = string.IsNullOrEmpty(reg.SettingsJson)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reg.SettingsJson);
        var values = pending ?? new Dictionary<string, string>
        {
            ["Tenant.Code"] = tenant.TenantCode,
            ["Tenant.Name"] = tenant.TenantName,
            ["Tenant.DateFormat"] = "dd-MM-yyyy",
            ["Tenant.TimeZone"] = "UTC",
            ["Tenant.Currency"] = "USD",
        };
        var overwrite = pending != null;

        await using var conn = new SqlConnection(ConnectionFor(reg.DatabaseName));
        await conn.OpenAsync(ct);
        var changed = 0;
        foreach (var (key, value) in values)
        {
            await using var cmd = new SqlCommand(@"
MERGE dbo.ApplicationSettings AS t
USING (SELECT @k AS SettingKey, @v AS SettingValue) AS s ON t.SettingKey = s.SettingKey
WHEN MATCHED AND @o = 1 AND (ISNULL(t.SettingValue, N'') <> ISNULL(s.SettingValue, N'') OR t.IsActive = 0)
    THEN UPDATE SET SettingValue = s.SettingValue, IsActive = 1, ModifiedDate = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, IsActive, ModifiedDate) VALUES (s.SettingKey, s.SettingValue, 1, SYSUTCDATETIME());
SELECT @@ROWCOUNT;", conn);
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@o", overwrite);
            changed += Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        if (pending != null) reg.SettingsJson = null;

        string V(string k) => values.TryGetValue(k, out var v) ? v : "-";
        var detail = $"{V("Tenant.Currency")} · {V("Tenant.DateFormat")} · {V("Tenant.TimeZone")}";
        return new StepResult(changed > 0, detail, new List<string>());
    }

    private string AdminDatabase => new SqlConnectionStringBuilder(BaseConnectionString).InitialCatalog;

    /// <summary>
    /// The tenant database's dbo.MenuItems follow the menus assigned to the tenant
    /// (SuperAdminPanel.dbo.TenantMenuAssignments + every system menu), matched by MenuKey:
    /// assigned = on, the rest = off, and an assigned menu the database doesn't have yet is added
    /// from dbo.TenantMenuItems. With nothing assigned the menus are left as copied.
    /// </summary>
    private async Task<StepResult> ApplyMenusAsync(TenantDatabase reg, int tenantId, CancellationToken ct)
    {
        var adm = Q(AdminDatabase);
        var sql = $@"
SET NOCOUNT ON;
DECLARE @keys TABLE (MenuKey NVARCHAR(100) COLLATE DATABASE_DEFAULT PRIMARY KEY);
INSERT @keys (MenuKey)
SELECT DISTINCT m.MenuKey
  FROM {adm}.dbo.TenantMenuAssignments a
  JOIN {adm}.dbo.TenantMenuItems m ON m.MenuItemId = a.MenuItemId
 WHERE a.TenantId = @tenantId;
DECLARE @assigned INT = (SELECT COUNT(*) FROM @keys), @added INT = 0, @changed INT = 0;
IF @assigned > 0
BEGIN
    INSERT @keys (MenuKey)
    SELECT m.MenuKey FROM {adm}.dbo.TenantMenuItems m
     WHERE m.IsSystemMenu = 1 AND m.IsActive = 1
       AND NOT EXISTS (SELECT 1 FROM @keys k WHERE k.MenuKey = m.MenuKey COLLATE DATABASE_DEFAULT);

    INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder,
                          ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate, ModuleId)
    SELECT m.MenuKey, m.DisplayName, m.Icon, m.RoleRestriction, 1, m.MenuGroup, m.Route, m.DisplayOrder,
           m.ApplicationCode, m.IsSystemMenu, NULL, SYSUTCDATETIME(), m.ModuleId
      FROM {adm}.dbo.TenantMenuItems m
      JOIN @keys k ON k.MenuKey = m.MenuKey COLLATE DATABASE_DEFAULT
     WHERE NOT EXISTS (SELECT 1 FROM dbo.MenuItems t WHERE t.MenuKey = k.MenuKey);
    SET @added = @@ROWCOUNT;

    IF @added > 0
        UPDATE t SET t.ParentMenuItemId = tp.MenuItemId
          FROM dbo.MenuItems t
          JOIN {adm}.dbo.TenantMenuItems m  ON m.MenuKey COLLATE DATABASE_DEFAULT = t.MenuKey
          JOIN {adm}.dbo.TenantMenuItems mp ON mp.MenuItemId = m.ParentMenuItemId
          JOIN dbo.MenuItems tp ON tp.MenuKey = mp.MenuKey COLLATE DATABASE_DEFAULT
         WHERE t.ParentMenuItemId IS NULL;

    UPDATE t
       SET t.IsActive = CASE WHEN k.MenuKey IS NULL THEN 0 ELSE 1 END, t.ModifiedDate = SYSUTCDATETIME()
      FROM dbo.MenuItems t
      LEFT JOIN @keys k ON k.MenuKey = t.MenuKey
     WHERE t.IsActive <> CASE WHEN k.MenuKey IS NULL THEN 0 ELSE 1 END;
    SET @changed = @@ROWCOUNT;
END;
SELECT @assigned, @added, @changed,
       (SELECT COUNT(*) FROM dbo.MenuItems WHERE IsActive = 1), (SELECT COUNT(*) FROM dbo.MenuItems);";
        await using var conn = new SqlConnection(ConnectionFor(reg.DatabaseName));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        int assigned = r.GetInt32(0), added = r.GetInt32(1), changed = r.GetInt32(2), on = r.GetInt32(3), total = r.GetInt32(4);
        var warnings = new List<string>();
        if (assigned == 0)
        {
            warnings.Add("No menus are assigned to this tenant yet - all copied menus are left on. Pick them under Modules and menus.");
            return new StepResult(false, $"{on} of {total} menus on (none assigned yet)", warnings);
        }
        return new StepResult(added + changed > 0, $"{on} of {total} menus on" + (added > 0 ? $", {added} added" : ""), warnings);
    }

    /// <summary>
    /// The Administrator role gets every right (view, add, edit, delete, import, export) on every
    /// switched-on menu; permissions on switched-off menus are removed from every role.
    /// </summary>
    private async Task<StepResult> ApplyRolePermissionsAsync(TenantDatabase reg, CancellationToken ct)
    {
        var sql = "SET NOCOUNT ON;" + PickAdminRoleSql + @"
DELETE p FROM dbo.RolePermissions p
 WHERE NOT EXISTS (SELECT 1 FROM dbo.MenuItems m WHERE m.MenuKey = p.MenuKey AND m.IsActive = 1);
DECLARE @removed INT = @@ROWCOUNT;

UPDATE p SET CanView = 1, CanAdd = 1, CanEdit = 1, CanDelete = 1, CanImport = 1, CanExport = 1
  FROM dbo.RolePermissions p
 WHERE p.RoleId = @roleId
   AND (p.CanView = 0 OR p.CanAdd = 0 OR p.CanEdit = 0 OR p.CanDelete = 0 OR p.CanImport = 0 OR p.CanExport = 0);
DECLARE @updated INT = @@ROWCOUNT;

INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
SELECT @roleId, m.MenuKey, 1, 1, 1, 1, 1, 1
  FROM dbo.MenuItems m
 WHERE m.IsActive = 1 AND LEN(m.MenuKey) <= 50
   AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions p WHERE p.RoleId = @roleId AND p.MenuKey = m.MenuKey);
DECLARE @added INT = @@ROWCOUNT;

SELECT (SELECT RoleName FROM dbo.Roles WHERE RoleId = @roleId), @removed + @updated + @added,
       (SELECT COUNT(*) FROM dbo.RolePermissions WHERE RoleId = @roleId);";
        await using var conn = new SqlConnection(ConnectionFor(reg.DatabaseName));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var name = r.IsDBNull(0) ? "Administrator" : r.GetString(0);
        int changes = r.GetInt32(1), count = r.GetInt32(2);
        return new StepResult(changes > 0, $"{name}: full rights on {count} menus", new List<string>());
    }

    // ------------------------------------------------------------------ owner

    /// <summary>
    /// The tenant's Administrator role (from the copied Roles; created if there is none) and the owner
    /// user in dbo.Users with the BCrypt hash - the same columns and hashing the Business Panel login
    /// (usp_Users_Login + BCrypt.Verify) reads. An existing user with that email keeps its row and gets
    /// the Administrator role. Returns the UserId.
    /// </summary>
    private async Task<int> EnsureOwnerAsync(TenantDatabase reg, CancellationToken ct)
    {
        const string sql = "SET NOCOUNT ON;" + PickAdminRoleSql + @"

DECLARE @userId INT = (SELECT TOP (1) UserId FROM dbo.Users WHERE Email = @email);
IF @userId IS NULL
BEGIN
    INSERT dbo.Users (FirstName, LastName, Email, PasswordHash, Status, RoleId, CreatedDate)
    VALUES (@first, @last, @email, @hash, N'Active', @roleId, SYSUTCDATETIME());
    SET @userId = SCOPE_IDENTITY();
END
ELSE
    UPDATE dbo.Users SET RoleId = @roleId, Status = N'Active', ModifiedDate = SYSUTCDATETIME() WHERE UserId = @userId;

SELECT @userId;";
        await using var conn = new SqlConnection(ConnectionFor(reg.DatabaseName));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@first", (object?)reg.OwnerFirstName ?? reg.OwnerEmail!);
        cmd.Parameters.AddWithValue("@last", (object?)reg.OwnerLastName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@email", reg.OwnerEmail!);
        cmd.Parameters.AddWithValue("@hash", reg.OwnerPasswordHash!);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ------------------------------------------------------------------ template copy

    /// <summary>
    /// Copies the master-data tables from the template database into the tenant database.
    /// Each table is copied once automatically (remembered in dbo.SchemaVersions as Kind 'Template');
    /// Re-seed copies again. Only rows whose primary key is not there yet are inserted.
    /// A table that fails is reported as a warning and the rest carry on.
    /// </summary>
    private async Task<ScriptRun> CopyTemplateDataAsync(string database, bool forceAll, CancellationToken ct)
    {
        var applied = new List<string>();
        var warnings = new List<string>();
        var template = TemplateDatabase;
        if (template.Length == 0 || string.Equals(template, database, StringComparison.OrdinalIgnoreCase))
            return new ScriptRun(applied, null, warnings);

        await using var conn = new SqlConnection(ConnectionFor(database));
        await conn.OpenAsync(ct);

        await using (var exists = new SqlCommand("SELECT DB_ID(@n)", conn))
        {
            exists.Parameters.AddWithValue("@n", template);
            if (await exists.ExecuteScalarAsync(ct) is null or DBNull)
            {
                warnings.Add($"Template database {template} not found - master data not copied.");
                return new ScriptRun(applied, null, warnings);
            }
        }

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new SqlCommand("SELECT ScriptName FROM dbo.SchemaVersions WHERE Kind = N'Template'", conn))
        await using (var r = await read.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) done.Add(r.GetString(0));

        var tq = Q(template);
        string? last = null;
        foreach (var table in TemplateTables)
        {
            var marker = "copy:" + table;
            if (!forceAll && done.Contains(marker)) { last = marker; continue; }

            // Columns present in both databases (computed / rowversion columns cannot be inserted).
            var columns = new List<string>();
            await using (var cols = new SqlCommand($@"
SELECT c.name
  FROM sys.columns c
 WHERE c.object_id = OBJECT_ID(@t, N'U')
   AND c.is_computed = 0
   AND TYPE_NAME(c.user_type_id) NOT IN (N'timestamp', N'rowversion')
   AND EXISTS (SELECT 1 FROM {tq}.sys.columns s WHERE s.object_id = OBJECT_ID(@tt, N'U') AND s.name = c.name)
 ORDER BY c.column_id;", conn))
            {
                cols.Parameters.AddWithValue("@t", "dbo." + Q(table));
                cols.Parameters.AddWithValue("@tt", $"{tq}.dbo.{Q(table)}");
                await using var r = await cols.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) columns.Add(r.GetString(0));
            }
            if (columns.Count == 0)
            {
                warnings.Add($"Master data {table}: table missing in {database} or {template} - skipped.");
                continue;
            }

            var keys = new List<string>();
            await using (var pk = new SqlCommand(@"
SELECT c.name
  FROM sys.indexes i
  JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
 WHERE i.object_id = OBJECT_ID(@t, N'U') AND i.is_primary_key = 1
 ORDER BY ic.key_ordinal;", conn))
            {
                pk.Parameters.AddWithValue("@t", "dbo." + Q(table));
                await using var r = await pk.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
            }

            bool hasIdentity;
            await using (var id = new SqlCommand("SELECT CAST(OBJECTPROPERTY(OBJECT_ID(@t, N'U'), 'TableHasIdentity') AS INT)", conn))
            {
                id.Parameters.AddWithValue("@t", "dbo." + Q(table));
                hasIdentity = Convert.ToInt32(await id.ExecuteScalarAsync(ct) ?? 0) == 1;
            }

            var list = string.Join(", ", columns.Select(Q));
            var src = $"{tq}.dbo.{Q(table)}";
            var dst = "dbo." + Q(table);
            // Missing rows only (by primary key); a table without a key is copied only while empty.
            var filter = keys.Count > 0
                ? $"WHERE NOT EXISTS (SELECT 1 FROM {dst} d WHERE {string.Join(" AND ", keys.Select(k => $"d.{Q(k)} = s.{Q(k)}"))})"
                : $"WHERE NOT EXISTS (SELECT 1 FROM {dst})";
            var sql = new StringBuilder();
            if (hasIdentity) sql.Append($"SET IDENTITY_INSERT {dst} ON;\n");
            sql.Append($"INSERT INTO {dst} ({list}) SELECT {string.Join(", ", columns.Select(c => "s." + Q(c)))} FROM {src} s {filter};\n");
            sql.Append("DECLARE @rows INT = @@ROWCOUNT;\n");
            if (hasIdentity) sql.Append($"SET IDENTITY_INSERT {dst} OFF;\n");
            sql.Append("SELECT @rows;");

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                int rows;
                await using (var cmd = new SqlCommand(sql.ToString(), conn, tx) { CommandTimeout = 600 })
                    rows = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);

                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template + "|" + list)));
                await using (var mark = new SqlCommand(
                    "MERGE dbo.SchemaVersions AS t USING (SELECT @n AS ScriptName) AS s ON t.ScriptName = s.ScriptName " +
                    "WHEN MATCHED THEN UPDATE SET Checksum = @c, Kind = N'Template', AppliedDate = SYSUTCDATETIME() " +
                    "WHEN NOT MATCHED THEN INSERT (ScriptName, Kind, Checksum, AppliedDate) VALUES (@n, N'Template', @c, SYSUTCDATETIME());", conn, tx))
                {
                    mark.Parameters.AddWithValue("@n", marker);
                    mark.Parameters.AddWithValue("@c", checksum);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                if (rows > 0) applied.Add($"MasterData/{table} ({rows} rows from {template})");
                last = marker;
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex, "Copying {Table} from {Template} into {Db} failed", table, template, database);
                warnings.Add($"Master data {table}: {ex.Message}");
            }
        }
        return new ScriptRun(applied, last, warnings);
    }

    // ------------------------------------------------------------------ helpers

    private string BaseConnectionString =>
        _config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");

    private string ConnectionFor(string database)
    {
        var b = new SqlConnectionStringBuilder(BaseConnectionString) { InitialCatalog = database };
        return b.ConnectionString;
    }

    private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";

    private async Task<string> UniqueDatabaseNameAsync(string tenantCode, CancellationToken ct)
    {
        var clean = Regex.Replace(tenantCode ?? string.Empty, "[^A-Za-z0-9_]", string.Empty);
        if (clean.Length == 0) clean = "TENANT";
        var baseName = (Prefix + clean.ToUpperInvariant());
        if (baseName.Length > 100) baseName = baseName[..100];
        var name = baseName;
        for (var i = 2; await _db.TenantDatabases.AnyAsync(r => r.DatabaseName == name, ct); i++) name = $"{baseName}_{i}";
        return name;
    }

    /// <summary>True when the database was created now, false when it was already there.</summary>
    private async Task<bool> CreateDatabaseIfMissingAsync(string database, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionFor("master"));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"IF DB_ID(@n) IS NULL BEGIN CREATE DATABASE {Q(database)}; SELECT 1; END ELSE SELECT 0;", conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@n", database);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private async Task EnsureVersionsTableAsync(string database, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionFor(database));
        await conn.OpenAsync(ct);
        const string sql = @"IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
CREATE TABLE dbo.SchemaVersions
(
    ScriptName  NVARCHAR(260) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
    Kind        NVARCHAR(20)  NOT NULL,
    Checksum    CHAR(64)      NOT NULL,
    AppliedDate DATETIME2(7)  NOT NULL
);";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Creates SuperAdminPanel.dbo.TenantDatabases on first use, so no manual script is needed.</summary>
    private async Task EnsureRegistryTableAsync(CancellationToken ct)
    {
        const string sql = @"IF OBJECT_ID(N'dbo.TenantDatabases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantDatabases
    (
        TenantDatabaseId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantDatabases PRIMARY KEY,
        TenantId         INT            NOT NULL,
        DatabaseName     NVARCHAR(128)  NOT NULL,
        ServerName       NVARCHAR(128)  NULL,
        SchemaVersion    NVARCHAR(260)  NULL,
        SeedVersion      NVARCHAR(260)  NULL,
        ProvisioningStep INT            NOT NULL CONSTRAINT DF_TenantDatabases_Step DEFAULT (0),
        Status           NVARCHAR(20)   NOT NULL CONSTRAINT DF_TenantDatabases_Status DEFAULT (N'Pending'),
        LastError        NVARCHAR(MAX)  NULL,
        IsEnabled        BIT            NOT NULL CONSTRAINT DF_TenantDatabases_IsEnabled DEFAULT (1),
        CreatedDate      DATETIME2(7)   NOT NULL CONSTRAINT DF_TenantDatabases_CreatedDate DEFAULT (SYSUTCDATETIME()),
        LastMigratedDate DATETIME2(7)   NULL,
        LastBackupDate   DATETIME2(7)   NULL,
        ModifiedDate     DATETIME2(7)   NULL,
        CONSTRAINT FK_TenantDatabases_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_TenantDatabases_TenantId ON dbo.TenantDatabases (TenantId);
    CREATE UNIQUE INDEX IX_TenantDatabases_DatabaseName ON dbo.TenantDatabases (DatabaseName);
END;
-- Columns added later (owner login) - added to an existing table automatically.
IF COL_LENGTH(N'dbo.TenantDatabases', N'OwnerFirstName') IS NULL ALTER TABLE dbo.TenantDatabases ADD OwnerFirstName NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'OwnerLastName') IS NULL ALTER TABLE dbo.TenantDatabases ADD OwnerLastName NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'OwnerEmail') IS NULL ALTER TABLE dbo.TenantDatabases ADD OwnerEmail NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'OwnerPasswordHash') IS NULL ALTER TABLE dbo.TenantDatabases ADD OwnerPasswordHash NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'OwnerUserId') IS NULL ALTER TABLE dbo.TenantDatabases ADD OwnerUserId INT NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'VerifiedDate') IS NULL ALTER TABLE dbo.TenantDatabases ADD VerifiedDate DATETIME2(7) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'VerifiedBy') IS NULL ALTER TABLE dbo.TenantDatabases ADD VerifiedBy NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'SettingsJson') IS NULL ALTER TABLE dbo.TenantDatabases ADD SettingsJson NVARCHAR(MAX) NULL;
-- Start / finish time of every provisioning step, per tenant.
IF OBJECT_ID(N'dbo.TenantProvisioningSteps', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantProvisioningSteps
    (
        TenantProvisioningStepId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantProvisioningSteps PRIMARY KEY,
        TenantId      INT            NOT NULL,
        StepNo        INT            NOT NULL,
        StepName      NVARCHAR(100)  NOT NULL,
        StartedDate   DATETIME2(7)   NULL,
        CompletedDate DATETIME2(7)   NULL,
        Detail        NVARCHAR(500)  NULL,
        CONSTRAINT FK_TenantProvisioningSteps_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_TenantProvisioningSteps_Tenant_Step ON dbo.TenantProvisioningSteps (TenantId, StepNo);
END;";
        await _db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>The *.sql files of one kind, in file-name order. A missing folder simply means no scripts yet.</summary>
    private List<string> ScriptFiles(string kind)
    {
        var root = ScriptsRoot();
        if (root == null) return new List<string>();
        var dir = Path.Combine(root, kind);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>TenantDatabase:ScriptsPath, else the first TenantDatabase folder next to / above the API (source tree first, then the build output).</summary>
    private string? ScriptsRoot()
    {
        var configured = (_config["TenantDatabase:ScriptsPath"] ?? string.Empty).Trim();
        if (configured.Length > 0) return Directory.Exists(configured) ? configured : null;
        foreach (var start in new[] { _env.ContentRootPath, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "TenantDatabase");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
