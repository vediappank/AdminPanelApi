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
///   TenantDatabase:DefaultMode          "Automatic" (API creates the database) or "Manual" (you create it, e.g. in Plesk,
///                                       then provisioning fills it) - pre-selected on Provision tenant, chosen per tenant
///   TenantDatabase:ScriptsPath          folder holding Schema/ and MasterData/ (default: found automatically)
///   TenantDatabase:BackupFolder         folder on the SQL Server for backups (default: SQL Server's backup folder)
///   TenantDatabase:AutoMigrateMinutes   10 (0 = only at start-up)
///   TenantDatabase:TemplateDatabase     "BusinessPanel" (empty = no copy)
///   TenantDatabase:TemplateTables       list of tables to copy, parents first (default: DefaultTemplateTables)
///   TenantDatabase:BusinessApiLogin     SQL login of the Business Panel API (e.g. "businesspanel"): given read/write/execute
///                                       in every tenant database (empty = skip, e.g. Windows login on your PC)
///   TenantDatabase:CredentialKey        secret text used to encrypt the password of a tenant database's own SQL user
///                                       (Manual mode with a Plesk database user). Must equal MultiTenant:CredentialKey
///                                       of the Business Panel API. Changing it = every saved password must be entered again.
///
/// Manual mode with the database's own user (shared Plesk SQL servers): Plesk creates a new SQL user with every
/// database and our logins cannot be added to it. Enter that user and password on the Manual panel - every step
/// then opens the tenant database with that user (it needs read, write and create-table rights - Plesk's
/// "Read and Write"), and reads the template / menu data from the Admin Panel side over a second connection.
///
/// Business Panel login: the owner's e-mail is also written to SuperAdminPanel.dbo.TenantUsers (the login
/// directory - see DatabaseScripts/TenantLogin_SuperAdminPanel.sql), so the owner can sign in to the Business Panel.
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
    private string DefaultMode => TenantDatabaseModes.Normalize(_config["TenantDatabase:DefaultMode"]) ?? TenantDatabaseModes.Automatic;

    /// <summary>The SQL login the API connects with (null = Windows / integrated login).</summary>
    private string? SqlLogin
    {
        get
        {
            var b = new SqlConnectionStringBuilder(BaseConnectionString);
            return b.IntegratedSecurity || string.IsNullOrWhiteSpace(b.UserID) ? null : b.UserID;
        }
    }

    private string LoginText => SqlLogin ?? "the API's Windows account";

    private string CredentialKey => (_config["TenantDatabase:CredentialKey"] ?? string.Empty).Trim();

    private static readonly Regex ValidDatabaseName = new(@"^[A-Za-z0-9_\-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex ValidDbUser = new(@"^[A-Za-z0-9_\-\.@]{1,128}$", RegexOptions.Compiled);

    /// <summary>True when this tenant database is opened with its own SQL user (Manual mode + user entered).</summary>
    private static bool HasOwnLogin(TenantDatabase reg) =>
        reg.DatabaseMode == TenantDatabaseModes.Manual && !string.IsNullOrWhiteSpace(reg.DbUser);

    /// <summary>Who opens the tenant database - for messages.</summary>
    private string WhoText(TenantDatabase reg) => HasOwnLogin(reg) ? $"database user {reg.DbUser}" : LoginText;

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
        if (HasOwnLogin(reg))
            return new(false, $"{reg.DatabaseName} was created in Plesk with its own user - back it up in Plesk (Databases > {reg.DatabaseName} > Back Up).");
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

    public async Task QueueProvisioningAsync(int tenantId, TenantOwnerLogin? owner, TenantSettings? settings = null, TenantDatabaseOptions? database = null, CancellationToken ct = default)
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
                DatabaseMode = DefaultMode,
                ServerName = new SqlConnectionStringBuilder(BaseConnectionString).DataSource,
                IsEnabled = true,
                CreatedDate = DateTime.UtcNow,
                ProvisioningStep = 1,
            };
            _db.TenantDatabases.Add(reg);
        }
        // Mode / name chosen on Provision tenant - only while the database step is still to do.
        if (database != null && reg.ProvisioningStep <= StepDatabase)
        {
            var error = await ApplyDatabaseOptionsAsync(reg, database, ct);
            if (error != null) throw new InvalidOperationException(error);
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
        // Manual databases that are not there yet wait for the operator (create it, then Run again) -
        // retrying them every few minutes would only repeat the same "create it first" error.
        var waitingForManualDatabase = await _db.TenantDatabases.AsNoTracking()
            .Where(r => r.DatabaseMode == TenantDatabaseModes.Manual && r.Status == "Failed" && r.ProvisioningStep <= StepDatabase)
            .Select(r => r.TenantId).ToListAsync(ct);
        disabled.UnionWith(waitingForManualDatabase);
        foreach (var id in tenantIds.Where(id => !disabled.Contains(id)))
        {
            if (ct.IsCancellationRequested) break;
            await EnsureUpToDateAsync(id, ct);
        }
    }

    public async Task<TenantDatabaseRunResult> SetDatabaseOptionsAsync(int tenantId, TenantDatabaseOptions options, bool runNow, CancellationToken ct = default)
    {
        if (!FeatureEnabled) return new(false, "Tenant databases are switched off (TenantDatabase:Enabled = false).");
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
                DatabaseMode = DefaultMode,
                ServerName = new SqlConnectionStringBuilder(BaseConnectionString).DataSource,
                IsEnabled = true,
                CreatedDate = DateTime.UtcNow,
                ProvisioningStep = StepDatabase,
            };
            _db.TenantDatabases.Add(reg);
        }
        else if (reg.ProvisioningStep > StepDatabase)
        {
            return new(false, $"Database {reg.DatabaseName} is already created - its mode and name can no longer be changed.");
        }
        else if (reg.Status == "Running")
        {
            return new(false, "Provisioning is running right now - wait for it to stop, then change the database.");
        }

        var error = await ApplyDatabaseOptionsAsync(reg, options, ct);
        if (error != null) return new(false, error);

        if (runNow)
        {
            reg.Status = "Pending";
            reg.LastError = null;
        }
        else if (reg.Status == "Pending" && reg.TenantDatabaseId == 0)
        {
            reg.Status = "Ready";   // new row, nothing queued - must not look like it is running
        }
        reg.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var what = reg.DatabaseMode == TenantDatabaseModes.Manual
            ? $"Manual - provisioning will use the existing database {reg.DatabaseName}"
            : $"Automatic - provisioning will create {reg.DatabaseName}";
        if (!runNow) return new(true, what + ".");

        await RecordStepAsync(tenantId, StepRegistered, tenant.CreatedDate, tenant.CreatedDate, null, overwrite: false, ct);
        _queue.Enqueue(tenantId);
        return new(true, what + " - running now.");
    }

    public async Task<TenantDatabaseDefaults> GetDatabaseDefaultsAsync(string? tenantCode, CancellationToken ct = default)
    {
        string suggested;
        try
        {
            suggested = string.IsNullOrWhiteSpace(tenantCode) ? Prefix : await UniqueDatabaseNameAsync(tenantCode, ct);
        }
        catch (SqlException)
        {
            // registry table not created yet
            var clean = Regex.Replace(tenantCode ?? string.Empty, "[^A-Za-z0-9_]", string.Empty).ToUpperInvariant();
            suggested = Prefix + clean;
        }
        return new TenantDatabaseDefaults(DefaultMode, suggested, Prefix, SqlLogin, FeatureEnabled, CredentialKey.Length > 0);
    }

    public async Task<string?> ValidateDatabaseOptionsAsync(TenantDatabaseOptions? options, int? forTenantId, CancellationToken ct = default)
    {
        if (options == null || !FeatureEnabled) return null;
        if (!string.IsNullOrWhiteSpace(options.Mode) && TenantDatabaseModes.Normalize(options.Mode) == null)
            return "Database mode must be Automatic or Manual.";
        if (TenantDatabaseModes.Normalize(options.Mode) == TenantDatabaseModes.Manual && DbUserError(options.DbUser, options.DbPassword, savedUser: null, hasSavedPassword: false) is string userError)
            return userError;
        var name = options.DatabaseName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!ValidDatabaseName.IsMatch(name)) return "Database name: letters, digits, _ and - only, up to 100 characters.";
        var reserved = new[] { "master", "model", "msdb", "tempdb", AdminDatabase, TemplateDatabase };
        if (reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return $"{name} is a system / platform database and cannot be used for a tenant.";
        try
        {
            await EnsureRegistryTableAsync(ct);
            if (await _db.TenantDatabases.AnyAsync(r => r.DatabaseName == name && (forTenantId == null || r.TenantId != forTenantId), ct))
                return $"Database name {name} is already used by another tenant.";
        }
        catch (SqlException)
        {
            // registry not readable - QueueProvisioningAsync checks again
        }
        return null;
    }

    /// <summary>Applies the chosen mode / name to a registry row not past the database step. Returns an error text, or null.</summary>
    private async Task<string?> ApplyDatabaseOptionsAsync(TenantDatabase reg, TenantDatabaseOptions options, CancellationToken ct)
    {
        string mode;
        if (string.IsNullOrWhiteSpace(options.Mode)) mode = string.IsNullOrWhiteSpace(reg.DatabaseMode) ? DefaultMode : reg.DatabaseMode;
        else mode = TenantDatabaseModes.Normalize(options.Mode) ?? string.Empty;
        if (mode.Length == 0) return "Database mode must be Automatic or Manual.";

        var name = options.DatabaseName?.Trim();
        if (!string.IsNullOrEmpty(name) && !string.Equals(name, reg.DatabaseName, StringComparison.Ordinal))
        {
            if (!ValidDatabaseName.IsMatch(name))
                return "Database name: letters, digits, _ and - only, up to 100 characters.";
            var reserved = new[] { "master", "model", "msdb", "tempdb", AdminDatabase, TemplateDatabase };
            if (reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
                return $"{name} is a system / platform database and cannot be used for a tenant.";
            if (await _db.TenantDatabases.AnyAsync(r => r.DatabaseName == name && r.TenantId != reg.TenantId, ct))
                return $"Database name {name} is already used by another tenant.";
            reg.DatabaseName = name;
        }
        reg.DatabaseMode = mode;

        // The database's own SQL user - Manual only; Automatic always uses the API's login.
        if (mode == TenantDatabaseModes.Automatic)
        {
            reg.DbUser = null;
            reg.DbPassword = null;
            return null;
        }
        if (options.DbUser == null) return null;   // not sent = keep
        var user = options.DbUser.Trim();
        if (user.Length == 0)
        {
            reg.DbUser = null;
            reg.DbPassword = null;
            return null;
        }
        var sameUser = string.Equals(user, reg.DbUser, StringComparison.OrdinalIgnoreCase);
        if (DbUserError(user, options.DbPassword, reg.DbUser, sameUser && !string.IsNullOrEmpty(reg.DbPassword)) is string error)
            return error;
        if (!string.IsNullOrEmpty(options.DbPassword))
            reg.DbPassword = TenantDbSecret.Protect(options.DbPassword, CredentialKey);
        reg.DbUser = user;
        return null;
    }

    /// <summary>Checks a database user / password pair before it is saved. Null = fine.</summary>
    private string? DbUserError(string? user, string? password, string? savedUser, bool hasSavedPassword)
    {
        user = user?.Trim();
        if (string.IsNullOrEmpty(user)) return null;
        if (!ValidDbUser.IsMatch(user)) return "Database user: letters, digits and _ - . @ only, up to 128 characters.";
        var sameUser = string.Equals(user, savedUser, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(password))
            return sameUser && hasSavedPassword ? null : $"Enter the password of database user {user}.";
        if (password.Length > 128) return "Database password: up to 128 characters.";
        if (CredentialKey.Length == 0)
            return "TenantDatabase:CredentialKey is not set in the Admin Panel API's appsettings - it is needed to save the " +
                   "database password (encrypted). Add it (same value as MultiTenant:CredentialKey of the Business Panel API), restart the API and try again.";
        return null;
    }

    /// <summary>Connection string for the tenant database: its own user (Manual + user entered) or the API's login.</summary>
    private string TenantConnection(TenantDatabase reg)
    {
        var b = new SqlConnectionStringBuilder(BaseConnectionString) { InitialCatalog = reg.DatabaseName };
        if (!HasOwnLogin(reg)) return b.ConnectionString;

        if (string.IsNullOrEmpty(reg.DbPassword))
            throw new InvalidOperationException($"No password is saved for database user {reg.DbUser} - enter it on the provisioning screen and run again.");
        if (CredentialKey.Length == 0)
            throw new InvalidOperationException("TenantDatabase:CredentialKey is not set in the Admin Panel API's appsettings - the saved database password cannot be read.");
        string password;
        try
        {
            password = TenantDbSecret.Unprotect(reg.DbPassword, CredentialKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                $"The saved password of database user {reg.DbUser} cannot be read (TenantDatabase:CredentialKey was changed?) - enter the password again and run again.", ex);
        }
        b.IntegratedSecurity = false;
        b.UserID = reg.DbUser!.Trim();
        b.Password = password;
        return b.ConnectionString;
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

        string? openError = null;
        if (dbExists)
        try
        {
            await using var conn = new SqlConnection(TenantConnection(reg!));
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
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // e.g. the database user cannot sign in - show what is known instead of failing the screen
            openError = $"{dbName} cannot be opened with {WhoText(reg!)}: {ex.Message}";
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
            reg?.ProvisioningStep ?? 0, reg?.Status, openError ?? reg?.LastError, reg?.LastMigratedDate, DateTime.UtcNow,
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
                DatabaseMode = DefaultMode,
                ServerName = new SqlConnectionStringBuilder(BaseConnectionString).DataSource,
                Status = "Pending",
                IsEnabled = true,
                CreatedDate = DateTime.UtcNow,
            };
            _db.TenantDatabases.Add(reg);
            await _db.SaveChangesAsync(ct);
        }

        var applied = new List<string>();
        // "Tenant registered" is done once a registry row exists - a failure from here on shows on "Database created".
        if (reg.ProvisioningStep < StepDatabase) reg.ProvisioningStep = StepDatabase;
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
            bool created;
            if (reg.DatabaseMode == TenantDatabaseModes.Manual)
            {
                // Manual: created by the operator (e.g. Plesk) - only check it is there and usable.
                var firstTime = reg.ProvisioningStep <= StepDatabase;
                await EnsureManualDatabaseUsableAsync(reg, ct);
                created = firstTime;
            }
            else
            {
                created = await CreateDatabaseAutomaticAsync(reg.DatabaseName, ct);
            }
            await EnsureVersionsTableAsync(reg, ct);
            await StepDoneAsync(reg, StepDatabase, t0,
                created ? (reg.DatabaseMode != TenantDatabaseModes.Manual ? reg.DatabaseName
                    : HasOwnLogin(reg) ? $"{reg.DatabaseName} (created manually, user {reg.DbUser})" : $"{reg.DatabaseName} (created manually)") : null,
                created, ct);

            // 2. Schema - each new script once, in order; a changed one is reported, never re-run.
            t0 = DateTime.UtcNow;
            var schema = await ApplyScriptsAsync(reg, "Schema", rerunChanged: false, forceAll: false, ct);
            applied.AddRange(schema.Applied);
            if (schema.Last != null) reg.SchemaVersion = schema.Last;
            await StepDoneAsync(reg, StepSchema, t0, schema.Applied.Count > 0 ? string.Join(", ", schema.Applied) : null, schema.Applied.Count > 0, ct);

            // 3. Master data - copied from the template database, then MasterData scripts
            //    (new or changed ones; all of them on Re-seed).
            t0 = DateTime.UtcNow;
            var copy = await CopyTemplateDataAsync(reg, forceSeed, ct);
            applied.AddRange(copy.Applied);
            var seed = await ApplyScriptsAsync(reg, "MasterData", rerunChanged: true, forceAll: forceSeed, ct);
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

            // Business Panel access - every run, so tenants made before this catch up by themselves:
            // owner e-mail in the login directory, Business Panel API login allowed into the database.
            var access = await EnsureBusinessPanelAccessAsync(reg, ct);

            var warnings = schema.Warnings.Concat(copy.Warnings).Concat(menus.Warnings).Concat(access).ToList();
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

    private async Task<ScriptRun> ApplyScriptsAsync(TenantDatabase reg, string kind, bool rerunChanged, bool forceAll, CancellationToken ct)
    {
        var files = ScriptFiles(kind);
        var applied = new List<string>();
        var warnings = new List<string>();
        string? last = null;

        await using var conn = new SqlConnection(TenantConnection(reg));
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

        await using var conn = new SqlConnection(TenantConnection(reg));
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
    /// The Admin Panel side is read over its own connection and the tenant database is written over the
    /// tenant's - no cross-database queries, so this also works when the tenant database has its own user.
    /// </summary>
    private async Task<StepResult> ApplyMenusAsync(TenantDatabase reg, int tenantId, CancellationToken ct)
    {
        // 1. Admin Panel: the menu catalogue and this tenant's assigned menu keys.
        var catalogue = new Dictionary<string, AdminMenu>(StringComparer.OrdinalIgnoreCase);
        var keyById = new Dictionary<int, string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var adm = new SqlConnection(BaseConnectionString))
        {
            await adm.OpenAsync(ct);
            await using (var cmd = new SqlCommand(@"
SELECT MenuItemId, MenuKey, DisplayName, Icon, RoleRestriction, MenuGroup, Route, DisplayOrder,
       ApplicationCode, IsSystemMenu, IsActive, ParentMenuItemId, ModuleId
  FROM dbo.TenantMenuItems
 WHERE MenuKey IS NOT NULL
 ORDER BY MenuItemId;", adm))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var key = Convert.ToString(r["MenuKey"])!.Trim();
                    var id = Convert.ToInt32(r["MenuItemId"]);
                    keyById[id] = key;
                    catalogue.TryAdd(key, new AdminMenu(
                        key,
                        r["ParentMenuItemId"] is DBNull ? (int?)null : Convert.ToInt32(r["ParentMenuItemId"]),
                        r["IsSystemMenu"] is not DBNull && Convert.ToBoolean(r["IsSystemMenu"]),
                        r["IsActive"] is not DBNull && Convert.ToBoolean(r["IsActive"]),
                        new Dictionary<string, object>
                        {
                            ["DisplayName"] = r["DisplayName"], ["Icon"] = r["Icon"], ["RoleRestriction"] = r["RoleRestriction"],
                            ["MenuGroup"] = r["MenuGroup"], ["Route"] = r["Route"], ["DisplayOrder"] = r["DisplayOrder"],
                            ["ApplicationCode"] = r["ApplicationCode"], ["IsSystemMenu"] = r["IsSystemMenu"], ["ModuleId"] = r["ModuleId"],
                        }));
                }
            }
            await using (var cmd = new SqlCommand(@"
SELECT DISTINCT m.MenuKey
  FROM dbo.TenantMenuAssignments a
  JOIN dbo.TenantMenuItems m ON m.MenuItemId = a.MenuItemId
 WHERE a.TenantId = @tenantId AND m.MenuKey IS NOT NULL;", adm))
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) keys.Add(r.GetString(0).Trim());
            }
        }
        var assigned = keys.Count;

        // 2. Tenant database.
        await using var conn = new SqlConnection(TenantConnection(reg));
        await conn.OpenAsync(ct);

        async Task<(int On, int Total)> CountAsync(SqlTransaction? tx)
        {
            await using var cmd = new SqlCommand("SELECT (SELECT COUNT(*) FROM dbo.MenuItems WHERE IsActive = 1), (SELECT COUNT(*) FROM dbo.MenuItems);", conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetInt32(0), r.GetInt32(1));
        }

        var warnings = new List<string>();
        if (assigned == 0)
        {
            var (on0, total0) = await CountAsync(null);
            warnings.Add("No menus are assigned to this tenant yet - all copied menus are left on. Pick them under Modules and menus.");
            return new StepResult(false, $"{on0} of {total0} menus on (none assigned yet)", warnings);
        }

        // Every active system menu is always on.
        foreach (var m in catalogue.Values.Where(x => x.IsSystem && x.IsActive)) keys.Add(m.Key);

        // Tenant menus as they are now: key -> on/off (rows without a key count as off).
        var existing = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand("SELECT MenuKey, IsActive FROM dbo.MenuItems WHERE MenuKey IS NOT NULL;", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var key = r.GetString(0).Trim();
                var active = !r.IsDBNull(1) && Convert.ToBoolean(r.GetValue(1));
                // the same key twice with different states = null, so the update below sets every copy
                existing[key] = existing.TryGetValue(key, out var seen) && seen != active ? (bool?)null : active;
            }
        }

        int added = 0, changed = 0;
        await using var tran = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Assigned menus the database does not have yet - added from the catalogue (parent linked below).
            foreach (var key in keys.Where(k => !existing.ContainsKey(k)))
            {
                if (!catalogue.TryGetValue(key, out var m)) continue;
                await using var ins = new SqlCommand(@"
INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder,
                      ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate, ModuleId)
VALUES (@MenuKey, @DisplayName, @Icon, @RoleRestriction, 1, @MenuGroup, @Route, @DisplayOrder,
        @ApplicationCode, @IsSystemMenu, NULL, SYSUTCDATETIME(), @ModuleId);", conn, tran);
                ins.Parameters.AddWithValue("@MenuKey", m.Key);
                foreach (var (column, value) in m.Values) ins.Parameters.AddWithValue("@" + column, value);
                await ins.ExecuteNonQueryAsync(ct);
                added++;
            }

            if (added > 0)
            {
                // Same parent as in the catalogue, matched by MenuKey.
                foreach (var m in catalogue.Values)
                {
                    if (m.ParentId is not int parentId || !keyById.TryGetValue(parentId, out var parentKey)) continue;
                    await using var link = new SqlCommand(@"
UPDATE t SET t.ParentMenuItemId = (SELECT TOP (1) p.MenuItemId FROM dbo.MenuItems p WHERE p.MenuKey = @parent ORDER BY p.MenuItemId)
  FROM dbo.MenuItems t
 WHERE t.MenuKey = @key AND t.ParentMenuItemId IS NULL
   AND EXISTS (SELECT 1 FROM dbo.MenuItems p WHERE p.MenuKey = @parent);", conn, tran);
                    link.Parameters.AddWithValue("@key", m.Key);
                    link.Parameters.AddWithValue("@parent", parentKey);
                    await link.ExecuteNonQueryAsync(ct);
                }
            }

            // Assigned = on, the rest = off.
            foreach (var (key, isOn) in existing)
            {
                var wanted = keys.Contains(key);
                if (isOn == wanted) continue;
                await using var set = new SqlCommand(
                    "UPDATE dbo.MenuItems SET IsActive = @on, ModifiedDate = SYSUTCDATETIME() WHERE MenuKey = @key AND IsActive <> @on;", conn, tran);
                set.Parameters.AddWithValue("@on", wanted);
                set.Parameters.AddWithValue("@key", key);
                changed += await set.ExecuteNonQueryAsync(ct);
            }
            await using (var noKey = new SqlCommand(
                "UPDATE dbo.MenuItems SET IsActive = 0, ModifiedDate = SYSUTCDATETIME() WHERE MenuKey IS NULL AND IsActive = 1;", conn, tran))
                changed += await noKey.ExecuteNonQueryAsync(ct);

            await tran.CommitAsync(ct);
        }
        catch
        {
            await tran.RollbackAsync(ct);
            throw;
        }

        var (on, total) = await CountAsync(null);
        return new StepResult(added + changed > 0, $"{on} of {total} menus on" + (added > 0 ? $", {added} added" : ""), warnings);
    }

    /// <summary>One row of SuperAdminPanel.dbo.TenantMenuItems; Values = the columns copied into the tenant's dbo.MenuItems.</summary>
    private sealed record AdminMenu(string Key, int? ParentId, bool IsSystem, bool IsActive, Dictionary<string, object> Values);

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
        await using var conn = new SqlConnection(TenantConnection(reg));
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
        await using var conn = new SqlConnection(TenantConnection(reg));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@first", (object?)reg.OwnerFirstName ?? reg.OwnerEmail!);
        cmd.Parameters.AddWithValue("@last", (object?)reg.OwnerLastName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@email", reg.OwnerEmail!);
        cmd.Parameters.AddWithValue("@hash", reg.OwnerPasswordHash!);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ------------------------------------------------------------------ Business Panel access

    /// <summary>
    /// 1. Owner e-mail -> dbo.TenantUsers (when that table exists): the Business Panel finds the owner's company at login.
    ///    An e-mail already registered to another company is reported as a warning, never moved.
    /// 2. TenantDatabase:BusinessApiLogin -> user in the tenant database with db_datareader, db_datawriter and EXECUTE.
    /// Returns warnings for the Control Center; never throws.
    /// </summary>
    private async Task<List<string>> EnsureBusinessPanelAccessAsync(TenantDatabase reg, CancellationToken ct)
    {
        var warnings = new List<string>();

        if (reg.OwnerUserId != null && !string.IsNullOrWhiteSpace(reg.OwnerEmail))
        {
            try
            {
                await using var conn = new SqlConnection(BaseConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.TenantUsers', N'U') IS NULL BEGIN SELECT CAST(NULL AS INT); RETURN; END;
DECLARE @e NVARCHAR(200) = LOWER(LTRIM(RTRIM(@email)));
DECLARE @owner INT = (SELECT TenantId FROM dbo.TenantUsers WHERE Email = @e);
IF @owner IS NULL INSERT dbo.TenantUsers (Email, TenantId) VALUES (@e, @tenantId);
SELECT ISNULL(@owner, @tenantId);", conn);
                cmd.Parameters.AddWithValue("@email", reg.OwnerEmail);
                cmd.Parameters.AddWithValue("@tenantId", reg.TenantId);
                var owner = await cmd.ExecuteScalarAsync(ct);
                if (owner is int ownerTenant && ownerTenant != reg.TenantId)
                    warnings.Add($"Owner e-mail {reg.OwnerEmail} is already registered to tenant {ownerTenant} - the owner cannot sign in to the Business Panel with it.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant {TenantId}: owner e-mail not written to dbo.TenantUsers", reg.TenantId);
                warnings.Add($"Login directory not updated: {ex.Message}");
            }
        }

        // With its own database user the Business Panel opens the database with that user (it reads it from the
        // tenant directory), and that user cannot add other logins anyway - nothing to grant.
        var apiLogin = (_config["TenantDatabase:BusinessApiLogin"] ?? string.Empty).Trim();
        if (apiLogin.Length > 0 && !HasOwnLogin(reg))
        {
            try
            {
                await using var conn = new SqlConnection(TenantConnection(reg));
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
SET NOCOUNT ON;
IF SUSER_ID(@l) IS NULL BEGIN SELECT 0; RETURN; END;
DECLARE @q NVARCHAR(300) = QUOTENAME(@l), @s NVARCHAR(600);
IF DATABASE_PRINCIPAL_ID(@l) IS NULL BEGIN SET @s = N'CREATE USER ' + @q + N' FOR LOGIN ' + @q; EXEC (@s); END;
IF ISNULL(IS_ROLEMEMBER(N'db_datareader', @l), 0) = 0 BEGIN SET @s = N'ALTER ROLE db_datareader ADD MEMBER ' + @q; EXEC (@s); END;
IF ISNULL(IS_ROLEMEMBER(N'db_datawriter', @l), 0) = 0 BEGIN SET @s = N'ALTER ROLE db_datawriter ADD MEMBER ' + @q; EXEC (@s); END;
SET @s = N'GRANT EXECUTE TO ' + @q; EXEC (@s);
SELECT 1;", conn);
                cmd.Parameters.AddWithValue("@l", apiLogin);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 0)
                    warnings.Add($"SQL login {apiLogin} (TenantDatabase:BusinessApiLogin) does not exist on this server.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant {TenantId}: Business Panel API login {Login} not given access to {Db}", reg.TenantId, apiLogin, reg.DatabaseName);
                warnings.Add($"Business Panel API login {apiLogin} could not be given access: {ex.Message}");
            }
        }
        return warnings;
    }

    // ------------------------------------------------------------------ template copy

    /// <summary>
    /// Copies the master-data tables from the template database into the tenant database.
    /// Each table is copied once automatically (remembered in dbo.SchemaVersions as Kind 'Template');
    /// Re-seed copies again. Only rows whose primary key is not there yet are inserted.
    /// A table that fails is reported as a warning and the rest carry on.
    /// The template is read over the API's own connection and the rows are written over the tenant's
    /// connection - no cross-database query, so this also works when the tenant database has its own user.
    /// </summary>
    private async Task<ScriptRun> CopyTemplateDataAsync(TenantDatabase reg, bool forceAll, CancellationToken ct)
    {
        var applied = new List<string>();
        var warnings = new List<string>();
        var template = TemplateDatabase;
        var database = reg.DatabaseName;
        if (template.Length == 0 || string.Equals(template, database, StringComparison.OrdinalIgnoreCase))
            return new ScriptRun(applied, null, warnings);

        await using var src = new SqlConnection(ConnectionFor(template));
        try
        {
            await src.OpenAsync(ct);
        }
        catch (SqlException ex)
        {
            warnings.Add($"Template database {template} not found or {LoginText} cannot open it ({ex.Message}) - master data not copied.");
            return new ScriptRun(applied, null, warnings);
        }

        await using var conn = new SqlConnection(TenantConnection(reg));
        await conn.OpenAsync(ct);

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new SqlCommand("SELECT ScriptName FROM dbo.SchemaVersions WHERE Kind = N'Template'", conn))
        await using (var r = await read.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) done.Add(r.GetString(0));

        const string ColumnsSql = @"
SELECT c.name
  FROM sys.columns c
 WHERE c.object_id = OBJECT_ID(@t, N'U')
   AND c.is_computed = 0
   AND TYPE_NAME(c.user_type_id) NOT IN (N'timestamp', N'rowversion')
 ORDER BY c.column_id;";

        async Task<List<string>> ColumnsAsync(SqlConnection c, string table)
        {
            var list = new List<string>();
            await using var cmd = new SqlCommand(ColumnsSql, c);
            cmd.Parameters.AddWithValue("@t", "dbo." + Q(table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
            return list;
        }

        string? last = null;
        foreach (var table in TemplateTables)
        {
            var marker = "copy:" + table;
            if (!forceAll && done.Contains(marker)) { last = marker; continue; }

            // Columns present in both databases (computed / rowversion columns cannot be inserted).
            var templateColumns = new HashSet<string>(await ColumnsAsync(src, table), StringComparer.OrdinalIgnoreCase);
            var columns = (await ColumnsAsync(conn, table)).Where(templateColumns.Contains).ToList();
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
            // A key column the template does not have cannot be matched - treat the table as keyless.
            var keyIndexes = keys.Select(k => columns.FindIndex(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (keyIndexes.Any(i => i < 0)) keyIndexes.Clear();

            bool hasIdentity;
            await using (var id = new SqlCommand("SELECT CAST(OBJECTPROPERTY(OBJECT_ID(@t, N'U'), 'TableHasIdentity') AS INT)", conn))
            {
                id.Parameters.AddWithValue("@t", "dbo." + Q(table));
                hasIdentity = Convert.ToInt32(await id.ExecuteScalarAsync(ct) ?? 0) == 1;
            }

            var list = string.Join(", ", columns.Select(Q));
            var dst = "dbo." + Q(table);
            try
            {
                // Template rows.
                var rows = new List<object[]>();
                await using (var cmd = new SqlCommand($"SELECT {list} FROM {dst};", src) { CommandTimeout = 600 })
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        var values = new object[columns.Count];
                        r.GetValues(values);
                        rows.Add(values);
                    }
                }

                // Missing rows only (by primary key); a table without a key is copied only while empty.
                List<object[]> missing;
                if (keyIndexes.Count > 0)
                {
                    var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var cmd = new SqlCommand($"SELECT {string.Join(", ", keyIndexes.Select(i => Q(columns[i])))} FROM {dst};", conn) { CommandTimeout = 600 })
                    await using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        while (await r.ReadAsync(ct))
                        {
                            var values = new object[keyIndexes.Count];
                            r.GetValues(values);
                            have.Add(RowKey(values));
                        }
                    }
                    missing = rows.Where(row => !have.Contains(RowKey(keyIndexes.Select(i => row[i]).ToArray()))).ToList();
                }
                else
                {
                    await using var cmd = new SqlCommand($"SELECT CASE WHEN EXISTS (SELECT 1 FROM {dst}) THEN 1 ELSE 0 END;", conn);
                    missing = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1 ? new List<object[]>() : rows;
                }

                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                try
                {
                    var inserted = 0;
                    // Several rows per INSERT, under SQL Server's 2100-parameter / 1000-row limits.
                    var perBatch = Math.Max(1, Math.Min(1000, 2000 / columns.Count));
                    for (var start = 0; start < missing.Count; start += perBatch)
                    {
                        var batch = missing.Skip(start).Take(perBatch).ToList();
                        var sql = new StringBuilder();
                        if (hasIdentity) sql.Append($"SET IDENTITY_INSERT {dst} ON;\n");
                        sql.Append($"INSERT INTO {dst} ({list}) VALUES ");
                        await using var cmd = new SqlCommand { Connection = conn, Transaction = tx, CommandTimeout = 600 };
                        for (var row = 0; row < batch.Count; row++)
                        {
                            if (row > 0) sql.Append(", ");
                            sql.Append('(');
                            for (var col = 0; col < columns.Count; col++)
                            {
                                var name = $"@p{row}_{col}";
                                if (col > 0) sql.Append(", ");
                                sql.Append(name);
                                cmd.Parameters.Add(ParameterFor(name, batch[row][col]));
                            }
                            sql.Append(')');
                        }
                        sql.Append(";\n");
                        if (hasIdentity) sql.Append($"SET IDENTITY_INSERT {dst} OFF;");
                        cmd.CommandText = sql.ToString();
                        await cmd.ExecuteNonQueryAsync(ct);
                        inserted += batch.Count;
                    }

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
                    if (inserted > 0) applied.Add($"MasterData/{table} ({inserted} rows from {template})");
                    last = marker;
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Copying {Table} from {Template} into {Db} failed", table, template, database);
                warnings.Add($"Master data {table}: {ex.Message}");
            }
        }
        return new ScriptRun(applied, last, warnings);
    }

    /// <summary>Primary-key values as one comparable text (case-insensitive, like SQL Server's default collation).</summary>
    private static string RowKey(object[] values) =>
        string.Join("\u001F", values.Select(v => v is DBNull or null ? "\u0000" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A parameter for a copied value; dates as datetime2 so no value is rounded or out of range.</summary>
    private static SqlParameter ParameterFor(string name, object value) => value switch
    {
        DateTime d => new SqlParameter(name, System.Data.SqlDbType.DateTime2) { Value = d },
        byte[] b => new SqlParameter(name, System.Data.SqlDbType.VarBinary, -1) { Value = b },
        _ => new SqlParameter(name, value ?? DBNull.Value),
    };

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

    /// <summary>Automatic mode: CREATE DATABASE, with a clear message when the API's login is not allowed to.</summary>
    private async Task<bool> CreateDatabaseAutomaticAsync(string database, CancellationToken ct)
    {
        try
        {
            return await CreateDatabaseIfMissingAsync(database, ct);
        }
        catch (SqlException ex) when (ex.Number is 262 or 1802 or 5011)
        {
            throw new InvalidOperationException(
                $"SQL login {LoginText} is not allowed to create databases on this server ({ex.Message}) " +
                $"Either switch this tenant to Manual: create database {database} yourself (Plesk > Databases > Add Database, " +
                $"exact name {database}, {LoginText} as owner) and click Run again - or have the SQL Server administrator run " +
                $"USE [master]; CREATE USER [{SqlLogin ?? "login"}] FOR LOGIN [{SqlLogin ?? "login"}]; GRANT CREATE DATABASE TO [{SqlLogin ?? "login"}];", ex);
        }
    }

    /// <summary>
    /// Manual mode: the database must already exist and whoever opens it (its own user, or else the API's login)
    /// must be able to open it, read, write and create tables in it. Throws a message that tells the operator
    /// exactly what to do.
    /// </summary>
    private async Task EnsureManualDatabaseUsableAsync(TenantDatabase reg, CancellationToken ct)
    {
        var database = reg.DatabaseName;
        var own = HasOwnLogin(reg);
        var who = WhoText(reg);
        try
        {
            await using var conn = new SqlConnection(TenantConnection(reg));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0)
     & ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT'), 0)
     & ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'), 0)
     & ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE'), 0)", conn);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException(own
                    ? $"Database {database} opens with {who}, but that user cannot read, write and create tables in it. " +
                      $"In Plesk open Databases > {database} > User Management and give {reg.DbUser} \"Read and Write\" access, then run again."
                    : $"Database {database} exists, but {LoginText} cannot create tables in it. " +
                      $"In Plesk open the database > User Management and give {LoginText} full (db_owner) access - or enter the " +
                      $"database's own Plesk user and password on the Manual panel - then click Run again.");
        }
        catch (SqlException ex) when (ex.Number is 4060 or 916 or 18456)
        {
            if (own && ex.Number == 18456)
                throw new InvalidOperationException(
                    $"Database user {reg.DbUser} could not sign in to SQL Server - the user name or password is wrong, or the user " +
                    $"belongs to another server. Check it in Plesk (Databases > {database} > User Management), enter it again and run again.", ex);

            bool exists;
            try
            {
                await using var master = new SqlConnection(ConnectionFor("master"));
                await master.OpenAsync(ct);
                await using var check = new SqlCommand("SELECT CASE WHEN DB_ID(@n) IS NULL THEN 0 ELSE 1 END", master);
                check.Parameters.AddWithValue("@n", database);
                exists = Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 1;
            }
            catch (SqlException)
            {
                exists = false;
            }
            if (own)
                throw new InvalidOperationException(exists
                    ? $"Database {database} exists, but database user {reg.DbUser} cannot open it. In Plesk open Databases > {database} > " +
                      $"User Management and make sure {reg.DbUser} is a user of this database, then run again."
                    : $"Manual database: {database} has not been created yet. In Plesk go to Databases > Add Database, use the exact name " +
                      $"{database} on this SQL server with user {reg.DbUser}, then run again.", ex);
            throw new InvalidOperationException(exists
                ? $"Database {database} exists, but {LoginText} cannot open it. Enter the database's own Plesk user and password on " +
                  $"the Manual panel - or in Plesk add {LoginText} to it with full (db_owner) access - then click Run again."
                : $"Manual database: {database} has not been created yet. In Plesk go to Databases > Add Database, use the exact name " +
                  $"{database} on this SQL server, enter its user and password on the Manual panel, then click Run again.", ex);
        }
    }

    private async Task EnsureVersionsTableAsync(TenantDatabase reg, CancellationToken ct)
    {
        await using var conn = new SqlConnection(TenantConnection(reg));
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
IF COL_LENGTH(N'dbo.TenantDatabases', N'DatabaseMode') IS NULL ALTER TABLE dbo.TenantDatabases ADD DatabaseMode NVARCHAR(10) NOT NULL CONSTRAINT DF_TenantDatabases_DatabaseMode DEFAULT (N'Automatic');
IF COL_LENGTH(N'dbo.TenantDatabases', N'DbUser') IS NULL ALTER TABLE dbo.TenantDatabases ADD DbUser NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'DbPassword') IS NULL ALTER TABLE dbo.TenantDatabases ADD DbPassword NVARCHAR(500) NULL;
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
