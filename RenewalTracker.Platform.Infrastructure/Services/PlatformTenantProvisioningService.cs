using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RenewalTracker.Platform.Application.DTOs.Admin;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>See IPlatformTenantProvisioningService for the overall sequence this implements.</summary>
public class PlatformTenantProvisioningService : IPlatformTenantProvisioningService
{
    private readonly PlatformDbContext _db;
    private readonly HttpClient _businessApiClient;
    private readonly ILogger<PlatformTenantProvisioningService> _logger;
    private readonly ITenantDatabaseService _tenantDatabases;

    // Temporary decoupling switch: "Provisioning:SkipBusinessApiCall" (see
    // appsettings.Development.json). While true, ProvisionAsync and
    // CompleteProvisioningAsync stop at the Platform-side Tenant row and
    // never call RenewalTracker.Api at all - by request, so Admin panel
    // work isn't blocked on that integration. Flip it back to false (or
    // remove the key - it defaults to false) once the two APIs should be
    // wired together again; nothing else in this file needs to change to
    // re-enable it, CallBusinessApiAsync is untouched and ready to go.
    private readonly bool _skipBusinessApiCall;

    public PlatformTenantProvisioningService(
        PlatformDbContext db,
        HttpClient businessApiClient,
        ILogger<PlatformTenantProvisioningService> logger,
        IConfiguration configuration,
        ITenantDatabaseService tenantDatabases)
    {
        _tenantDatabases = tenantDatabases;
        _db = db;
        _businessApiClient = businessApiClient;
        _logger = logger;
        _skipBusinessApiCall = configuration.GetValue<bool>("Provisioning:SkipBusinessApiCall");
    }

    public async Task<ProvisionTenantResultDto> ProvisionAsync(ProvisionTenantDto dto)
    {
        var tenantCode = dto.TenantCode.Trim();
        if (string.IsNullOrWhiteSpace(tenantCode)) throw new InvalidOperationException("Tenant code is required.");
        if (string.IsNullOrWhiteSpace(dto.TenantName)) throw new InvalidOperationException("Tenant name is required.");
        if (string.IsNullOrWhiteSpace(dto.AdminEmail)) throw new InvalidOperationException("Admin email is required.");
        if (string.IsNullOrWhiteSpace(dto.AdminPassword)) throw new InvalidOperationException("Admin password is required.");

        // Before anything is created: the owner e-mail must not be another tenant's login.
        await EnsureOwnerEmailAvailableAsync(dto.AdminEmail, forTenantId: null);

        var codeTaken = await _db.Tenants.AnyAsync(t => t.TenantCode == tenantCode);
        if (codeTaken) throw new InvalidOperationException($"Tenant code '{tenantCode}' is already in use.");

        // Database mode / name (Automatic or Manual) - checked before the tenant is created.
        var databaseError = await _tenantDatabases.ValidateDatabaseOptionsAsync(DatabaseOf(dto), forTenantId: null);
        if (databaseError != null) throw new InvalidOperationException(databaseError);

        await EnsureLeadNotAlreadyConvertedAsync(dto.LeadId);

        var tenant = new Tenant
        {
            TenantCode = tenantCode,
            TenantName = dto.TenantName.Trim(),
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
            LeadId = dto.LeadId,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Tenant {TenantId} ({TenantCode}) created on the Platform side.",
            tenant.TenantId, tenant.TenantCode);

        // This is the actual "Convert to Tenant" moment (Tenant row now
        // carries this Lead's id) - log it as a real Follow-up entry against
        // the Lead's own Enquiry, same as ConvertFollowUpToLead() does for
        // its own conversion (see alter-enquirystatus-add-conversion-codes.sql
        // for the new "Lead to Tenant" EnquiryStatus code this writes).
        // Deliberately NOT repeated in CompleteProvisioningAsync - that only
        // retries the Business API call for a Tenant row this already ran
        // for once.
        if (dto.LeadId is int leadId)
        {
            await LogConvertedToTenantFollowUpAsync(leadId);
        }

        await SaveMenuAssignmentsAsync(tenant.TenantId, dto);

        // The tenant's own database - created, schema migrated, master data seeded and the owner
        // login added - by the background worker, starting now. This call returns at once; the
        // Control Center shows the progress live. A failure is stored on dbo.TenantDatabases and
        // retried automatically - it does not undo the tenant.
        await _tenantDatabases.QueueProvisioningAsync(tenant.TenantId, OwnerOf(dto), SettingsOf(dto), DatabaseOf(dto));

        if (_skipBusinessApiCall)
        {
            return SkippedResult(tenant, dto);
        }

        var result = await CallBusinessApiAsync(tenant, dto);
        await _tenantDatabases.MarkActiveAsync(tenant.TenantId);
        return result;
    }

    public async Task<ProvisionTenantResultDto> CompleteProvisioningAsync(int tenantId, ProvisionTenantDto dto)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (tenant == null) throw new InvalidOperationException($"Tenant {tenantId} was not found.");
        if (string.IsNullOrWhiteSpace(dto.AdminEmail)) throw new InvalidOperationException("Admin email is required.");

        // Before anything is changed: the owner e-mail must not be another tenant's login.
        await EnsureOwnerEmailAvailableAsync(dto.AdminEmail, forTenantId: tenant.TenantId);
        var databaseError = await _tenantDatabases.ValidateDatabaseOptionsAsync(DatabaseOf(dto), forTenantId: tenant.TenantId);
        if (databaseError != null) throw new InvalidOperationException(databaseError);

        await SaveMenuAssignmentsAsync(tenant.TenantId, dto);
        await _tenantDatabases.QueueProvisioningAsync(tenant.TenantId, OwnerOf(dto), SettingsOf(dto), DatabaseOf(dto));

        if (_skipBusinessApiCall)
        {
            return SkippedResult(tenant, dto);
        }

        // No persisted "still mid-provisioning" flag to check here (see
        // IPlatformTenantProvisioningService) - always safe to re-attempt
        // because the Business side no-ops if this tenant's Roles already
        // exist (see ProvisionBusinessSideAsync's idempotency check).
        var completed = await CallBusinessApiAsync(tenant, dto);
        await _tenantDatabases.MarkActiveAsync(tenant.TenantId);
        return completed;
    }

    /// <summary>
    /// Writes dbo.TenantMenuAssignments from dto.MenuItemIds (Provision tenant ▸
    /// Modules and menus): the ticked menus, every active system menu, and the
    /// parent of each ticked menu, replacing whatever the tenant had. Unknown or
    /// inactive ids are ignored. When dto.ModuleIds is not sent, it is filled from
    /// the chosen menus' ModuleId so the module-based Business-side setup gets the
    /// same selection. No-op when MenuItemIds is null.
    /// </summary>
    private async Task SaveMenuAssignmentsAsync(int tenantId, ProvisionTenantDto dto)
    {
        if (dto.MenuItemIds == null) return;

        var menus = await _db.TenantMenuItems.AsNoTracking().Where(m => m.IsActive).ToListAsync();
        var byId = menus.ToDictionary(m => m.MenuItemId);
        var chosen = new HashSet<int>(dto.MenuItemIds.Where(byId.ContainsKey));
        foreach (var m in menus.Where(m => m.IsSystemMenu)) chosen.Add(m.MenuItemId);
        foreach (var id in chosen.ToList())
        {
            var parentId = byId[id].ParentMenuItemId;
            while (parentId is int pid && byId.TryGetValue(pid, out var parent) && chosen.Add(pid))
                parentId = parent.ParentMenuItemId;
        }

        var existing = await _db.TenantMenuAssignments.Where(a => a.TenantId == tenantId).ToListAsync();
        _db.TenantMenuAssignments.RemoveRange(existing);
        var now = DateTime.UtcNow;
        foreach (var id in chosen)
            _db.TenantMenuAssignments.Add(new TenantMenuAssignment { TenantId = tenantId, MenuItemId = id, CreatedDate = now });
        await _db.SaveChangesAsync();

        if (dto.ModuleIds == null)
        {
            dto.ModuleIds = chosen.Select(id => byId[id].ModuleId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        }

        _logger.LogInformation("Tenant {TenantId}: {Count} Business Panel menus assigned.", tenantId, chosen.Count);
    }

    /// <summary>
    /// Stores the Lead - Tenant relationship's one rule: a Lead can only be
    /// converted once. Pulled out of ProvisionAsync as its own step so the
    /// "convert from Lead" concern is isolated from ordinary tenant
    /// creation - no-ops when leadId is null (an ordinary Tenants ▸ +
    /// Provision tenant, not a Convert to Tenant from the Leads screen).
    /// </summary>
    // ------------------------------------------------------------------ owner e-mail: one tenant per e-mail

    private async Task EnsureOwnerEmailAvailableAsync(string email, int? forTenantId)
    {
        var check = await CheckOwnerEmailAsync(email, forTenantId);
        if (!check.Available) throw new InvalidOperationException(check.Message ?? "This e-mail cannot be used.");
    }

    public async Task<OwnerEmailCheckDto> CheckOwnerEmailAsync(string? email, int? forTenantId, CancellationToken ct = default)
    {
        var e = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (e.Length == 0) return new OwnerEmailCheckDto { Available = false, Message = "Admin email is required." };

        // dbo.TenantUsers = the Business Panel login directory (one row per e-mail);
        // dbo.TenantDatabases.OwnerEmail = owners still being provisioned (not in the directory yet).
        // Both may be missing on an older database - checked before use.
        const string sql = @"
SET NOCOUNT ON;
DECLARE @hits TABLE (TenantId INT NOT NULL);
IF OBJECT_ID(N'dbo.TenantUsers', N'U') IS NOT NULL
    INSERT @hits (TenantId) SELECT TenantId FROM dbo.TenantUsers WHERE Email = @e;
IF OBJECT_ID(N'dbo.TenantDatabases', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.TenantDatabases', N'OwnerEmail') IS NOT NULL
    INSERT @hits (TenantId)
    EXEC sys.sp_executesql N'SELECT TenantId FROM dbo.TenantDatabases WHERE LOWER(LTRIM(RTRIM(OwnerEmail))) = @e', N'@e NVARCHAR(200)', @e = @e;
SELECT TOP (1) t.TenantId, t.TenantCode, t.TenantName
FROM @hits h
JOIN dbo.Tenants t ON t.TenantId = h.TenantId
WHERE @forTenantId IS NULL OR h.TenantId <> @forTenantId
ORDER BY t.TenantId;";

        var conn = _db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var tx = _db.Database.CurrentTransaction;
            if (tx != null) cmd.Transaction = Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(tx);

            var pEmail = cmd.CreateParameter();
            pEmail.ParameterName = "@e"; pEmail.DbType = System.Data.DbType.String; pEmail.Size = 200; pEmail.Value = e;
            cmd.Parameters.Add(pEmail);
            var pTenant = cmd.CreateParameter();
            pTenant.ParameterName = "@forTenantId"; pTenant.DbType = System.Data.DbType.Int32; pTenant.Value = (object?)forTenantId ?? DBNull.Value;
            cmd.Parameters.Add(pTenant);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                await r.CloseAsync();
                // one e-mail = one login: an Admin Panel (platform operator) login cannot also be a tenant owner
                var isOperator = await _db.PlatformUsers.AsNoTracking().AnyAsync(u => u.Email.Trim().ToLower() == e, ct);
                return isOperator
                    ? new OwnerEmailCheckDto { Available = false, Message = "Already an Admin Panel login. Use a different e-mail." }
                    : new OwnerEmailCheckDto { Available = true };
            }

            var tenantId = r.GetInt32(0);
            var code = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            var name = r.IsDBNull(2) ? string.Empty : r.GetString(2);
            return new OwnerEmailCheckDto
            {
                Available = false,
                TenantId = tenantId,
                TenantCode = code,
                TenantName = name,
                Message = $"Already the owner login of {name} ({code}). Use a different e-mail.",
            };
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private async Task EnsureLeadNotAlreadyConvertedAsync(int? leadId)
    {
        if (leadId is not int id) return;

        var alreadyConverted = await _db.Tenants.AnyAsync(t => t.LeadId == id);
        if (alreadyConverted) throw new InvalidOperationException("This lead has already been converted to a tenant.");
    }

    /// <summary>
    /// Writes a "Lead to Tenant" Follow-up entry against this Lead's own
    /// Enquiry and re-syncs that Enquiry's Status from it - same pattern as
    /// PlatformSalesController.ConvertFollowUpToLead()'s own "Converted to
    /// Lead" entry. No-ops quietly if the Lead can't be found (shouldn't
    /// happen - EnsureLeadNotAlreadyConvertedAsync already ran against this
    /// same id) rather than failing the whole provisioning call over a
    /// history-log detail.
    /// </summary>
    private async Task LogConvertedToTenantFollowUpAsync(int leadId)
    {
        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.LeadId == leadId);
        if (lead == null) return;

        _db.FollowUps.Add(new FollowUp
        {
            SalesEnquiryId = lead.SalesEnquiryId,
            FollowUpDate = DateTime.UtcNow.Date,
            FollowUpType = "Other",
            EmployeeId = lead.EmployeeId,
            EmployeeNameSnapshot = lead.EmployeeNameSnapshot,
            Status = "Lead to Tenant",
            Notes = $"Converted to Tenant from Lead {lead.LeadNo}.",
            NextFollowUpDate = DateTime.UtcNow.Date,
            CreatedDate = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await SyncEnquiryStatusFromLatestFollowUpAsync(lead.SalesEnquiryId);
    }

    /// <summary>
    /// Keeps Enquiry.Status mirroring whichever Follow-up logged against it
    /// is latest - same rule/query as
    /// PlatformSalesController.SyncEnquiryStatusFromLatestFollowUpAsync
    /// (duplicated here rather than shared, since that one is private to a
    /// different project/layer - this Infrastructure service has no
    /// reference back to the Api project's controllers).
    /// </summary>
    private async Task SyncEnquiryStatusFromLatestFollowUpAsync(int enquiryId)
    {
        var latest = await _db.FollowUps
            .Where(f => f.SalesEnquiryId == enquiryId)
            .OrderByDescending(f => f.NextFollowUpDate ?? f.FollowUpDate)
            .ThenByDescending(f => f.SalesFollowUpId)
            .FirstOrDefaultAsync();
        if (latest == null) return;

        var enquiry = await _db.Enquiries.FirstOrDefaultAsync(e => e.SalesEnquiryId == enquiryId);
        if (enquiry == null || enquiry.Status == latest.Status) return;

        enquiry.Status = latest.Status;
        enquiry.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Builds a result without ever calling RenewalTracker.Api - used while
    /// _skipBusinessApiCall is on. No TenantConfiguration, roles, modules
    /// or admin user are created anywhere - this tenant exists ONLY as a
    /// Platform-side row, with no working login, until the Business API is
    /// wired back in and Complete provisioning is run for real. Logged as a
    /// warning (not info) every time, specifically so this is never
    /// mistaken for a fully working tenant later.
    /// </summary>
    private static TenantSettings SettingsOf(ProvisionTenantDto dto) => new(dto.DateFormat, dto.TimeZone, dto.Currency);

    private static TenantDatabaseOptions? DatabaseOf(ProvisionTenantDto dto) =>
        string.IsNullOrWhiteSpace(dto.DatabaseMode) && string.IsNullOrWhiteSpace(dto.DatabaseName) && string.IsNullOrWhiteSpace(dto.DbUser)
            ? null
            : new TenantDatabaseOptions(dto.DatabaseMode, dto.DatabaseName,
                string.IsNullOrWhiteSpace(dto.DbUser) ? null : dto.DbUser, string.IsNullOrEmpty(dto.DbPassword) ? null : dto.DbPassword);

    private static TenantOwnerLogin? OwnerOf(ProvisionTenantDto dto) =>
        string.IsNullOrWhiteSpace(dto.AdminEmail) || string.IsNullOrEmpty(dto.AdminPassword)
            ? null
            : new TenantOwnerLogin(dto.AdminFirstName, dto.AdminLastName, dto.AdminEmail, dto.AdminPassword);

    private ProvisionTenantResultDto SkippedResult(Tenant tenant, ProvisionTenantDto dto)
    {
        _logger.LogWarning(
            "Provisioning:SkipBusinessApiCall is on - tenant {TenantId} ({TenantCode}) was NOT sent to the Business API. " +
            "No TenantConfiguration, roles, modules or admin login exist for it yet - it is Platform-side only.",
            tenant.TenantId, tenant.TenantCode);

        return new ProvisionTenantResultDto
        {
            TenantId = tenant.TenantId,
            TenantCode = tenant.TenantCode,
            TenantName = tenant.TenantName,
            AdminUserId = 0,
            AdminEmail = dto.AdminEmail,
            RolesCreated = new List<string>(),
            ModulesEnabled = 0,
        };
    }

    private async Task<ProvisionTenantResultDto> CallBusinessApiAsync(Tenant tenant, ProvisionTenantDto dto)
    {
        var businessDto = new ProvisionTenantBusinessSideDto
        {
            TenantId = tenant.TenantId,
            DateFormat = dto.DateFormat,
            TimeZone = dto.TimeZone,
            Currency = dto.Currency,
            AdminFirstName = dto.AdminFirstName,
            AdminLastName = dto.AdminLastName,
            AdminEmail = dto.AdminEmail,
            AdminPassword = dto.AdminPassword,
            ModuleIds = dto.ModuleIds,
            MenuItemIds = dto.MenuItemIds,
        };

        try
        {
            var response = await _businessApiClient.PostAsJsonAsync("api/internal/tenant-provisioning", businessDto);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Business API rejected provisioning for tenant {TenantId}: {Status} {Body}",
                    tenant.TenantId, response.StatusCode, body);
                throw new InvalidOperationException(
                    $"The Business API could not finish provisioning this tenant ({response.StatusCode}). " +
                    "Use Complete provisioning to retry once the issue is fixed.");
            }

            var result = await response.Content.ReadFromJsonAsync<ProvisionTenantResultDto>()
                ?? throw new InvalidOperationException("The Business API returned an empty provisioning result.");

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the Business API to provision tenant {TenantId}.", tenant.TenantId);
            throw new InvalidOperationException(
                "Could not reach the Business API to finish provisioning this tenant. " +
                "Use Complete provisioning to retry once the Business API is reachable.");
        }
    }
}
