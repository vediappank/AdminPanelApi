using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Admin;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Tenant management for the Platform Panel: list/view every tenant across
/// the whole system, edit a tenant's name/contact details/active flag, and
/// provision brand-new ones. Hand-written against PlatformDbContext
/// (RenewalTrackerPlatform - Tenants is authored here now, not in the
/// Business database) rather than any generic-CRUD controller.
///
/// Provisioning a tenant now spans two databases with no shared
/// transaction - see IPlatformTenantProvisioningService for the full
/// "create Tenant row here, then call the Business API" sequence and its
/// retry story.
///
/// Gated by TENANTS_LIST (the "Tenants" screen's own code, ModuleCode
/// TENANTS_LIST under the Tenant Management nav group) rather than the
/// generic [RequirePlatformAuth] it used before - Tenant Management is now
/// individually grantable per screen (Tenants vs. Tenant Module) instead
/// of one combined checkbox, so the base tenant list itself needs a real
/// gate of its own - see PlatformRolesController.GetModules() and
/// settings-tenant-submodule-split.sql for the migration that preserves
/// access for any role that only had the old combined "Tenant Management"
/// grant.
/// </summary>
[ApiController]
[Route("api/platform/tenants")]
[RequirePlatformModule("TENANTS_LIST")]
public class PlatformTenantsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IPlatformTenantProvisioningService _provisioning;
    private readonly IPlatformContactUniquenessService _unique;

    public PlatformTenantsController(PlatformDbContext db, IPlatformTenantProvisioningService provisioning, IPlatformContactUniquenessService unique)
    {
        _db = db;
        _provisioning = provisioning;
        _unique = unique;
    }

    /// <summary>Live check for the Edit tenant form: contact e-mail / mobile already used by another tenant?
    /// GET api/platform/tenants/contact-check?field=email|mobile&amp;value=...&amp;tenantId=2</summary>
    [HttpGet("contact-check")]
    public async Task<ActionResult<UniqueCheckDto>> CheckContact([FromQuery] string field, [FromQuery] string? value, [FromQuery] int? tenantId, CancellationToken ct)
        => Ok(await _unique.CheckTenantContactAsync(field, value, tenantId, ct));

    [HttpGet]
    public async Task<ActionResult<List<TenantDto>>> GetAll()
    {
        var tenants = await _db.Tenants.AsNoTracking().OrderBy(t => t.TenantName).ToListAsync();
        return Ok(tenants.Select(ToDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TenantDto>> GetById(int id)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == id);
        return tenant == null ? NotFound() : Ok(ToDto(tenant));
    }

    /// <summary>
    /// Name/contact details/active flag only - see Provision below for
    /// actually standing up a new, usable tenant (config + modules + roles
    /// + an admin login).
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<TenantDto>> Update(int id, TenantDto dto)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == id);
        if (tenant == null) return NotFound();

        // contact e-mail / mobile must not belong to another tenant (unchanged values are always allowed)
        if (await _unique.TenantContactProblemAsync(dto.ContactEmail, dto.ContactMobile, id) is string problem)
            return BadRequest(new { message = problem });

        tenant.TenantName = dto.TenantName;
        tenant.ContactName = dto.ContactName;
        tenant.ContactEmail = dto.ContactEmail;
        tenant.ContactMobile = dto.ContactMobile;
        tenant.IsActive = dto.IsActive;
        tenant.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(tenant));
    }

    /// <summary>
    /// Live check for the Provision / Complete provisioning form: can this e-mail be the owner login?
    /// tenantId = the tenant being completed (its own e-mail is fine); omit for a new tenant.
    /// Provision and CompleteProvisioning run the same check before creating anything.
    /// </summary>
    [HttpGet("owner-email-check")]
    public async Task<ActionResult<OwnerEmailCheckDto>> CheckOwnerEmail([FromQuery] string? email, [FromQuery] int? tenantId, CancellationToken ct)
        => Ok(await _provisioning.CheckOwnerEmailAsync(email, tenantId, ct));

    [HttpPost("provision")]
    public async Task<ActionResult<ProvisionTenantResultDto>> Provision(ProvisionTenantDto dto)
    {
        try
        {
            return Ok(await _provisioning.ProvisionAsync(dto));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Retries the Business-side half of provisioning for a tenant whose
    /// first attempt failed (Business API was unreachable, or rejected the
    /// request) - re-submit the same admin/config details. Safe to call
    /// even if the Business side actually did complete despite the error
    /// reaching Platform, and safe to call again even if it already
    /// succeeded (see ITenantProvisioningService.ProvisionBusinessSideAsync's
    /// idempotency check) - there is no persisted "still pending" flag on
    /// Tenant to gate this on, by design (see IPlatformTenantProvisioningService).
    /// </summary>
    [HttpPut("{id:int}/complete-provisioning")]
    public async Task<ActionResult<ProvisionTenantResultDto>> CompleteProvisioning(int id, ProvisionTenantDto dto)
    {
        try
        {
            return Ok(await _provisioning.CompleteProvisioningAsync(id, dto));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private static TenantDto ToDto(Tenant t) => new()
    {
        TenantId = t.TenantId,
        TenantCode = t.TenantCode,
        TenantName = t.TenantName,
        ContactName = t.ContactName,
        ContactEmail = t.ContactEmail,
        ContactMobile = t.ContactMobile,
        IsActive = t.IsActive,
        CreatedDate = t.CreatedDate,
        ModifiedDate = t.ModifiedDate,
        LeadId = t.LeadId,
    };
}
