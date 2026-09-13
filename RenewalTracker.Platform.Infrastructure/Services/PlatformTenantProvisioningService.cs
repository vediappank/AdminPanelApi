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
        IConfiguration configuration)
    {
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

        var codeTaken = await _db.Tenants.AnyAsync(t => t.TenantCode == tenantCode);
        if (codeTaken) throw new InvalidOperationException($"Tenant code '{tenantCode}' is already in use.");

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

        if (_skipBusinessApiCall)
        {
            return SkippedResult(tenant, dto);
        }

        return await CallBusinessApiAsync(tenant, dto);
    }

    public async Task<ProvisionTenantResultDto> CompleteProvisioningAsync(int tenantId, ProvisionTenantDto dto)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (tenant == null) throw new InvalidOperationException($"Tenant {tenantId} was not found.");

        if (_skipBusinessApiCall)
        {
            return SkippedResult(tenant, dto);
        }

        // No persisted "still mid-provisioning" flag to check here (see
        // IPlatformTenantProvisioningService) - always safe to re-attempt
        // because the Business side no-ops if this tenant's Roles already
        // exist (see ProvisionBusinessSideAsync's idempotency check).
        return await CallBusinessApiAsync(tenant, dto);
    }

    /// <summary>
    /// Stores the Lead - Tenant relationship's one rule: a Lead can only be
    /// converted once. Pulled out of ProvisionAsync as its own step so the
    /// "convert from Lead" concern is isolated from ordinary tenant
    /// creation - no-ops when leadId is null (an ordinary Tenants ▸ +
    /// Provision tenant, not a Convert to Tenant from the Leads screen).
    /// </summary>
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
