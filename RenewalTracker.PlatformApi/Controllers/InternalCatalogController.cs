using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Application.DTOs.Internal;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Service-to-service only: RenewalTracker.Api's CatalogSyncService calls
/// this (on a timer, on startup, and right before finishing tenant
/// provisioning) to keep its local Tenants/Modules/Permissions mirror up
/// to date. Not reachable by any user token - see
/// RequireInternalServiceKeyAttribute.
///
/// NOTE: as of the RenewalTrackerPlatform1 schema, this side's Tenant now
/// carries IsActive/ContactName/ContactEmail/ContactMobile instead of
/// Status - the Business API's own CatalogSyncService/TenantDto/local
/// Tenant mirror were NOT updated to match (out of scope for this pass,
/// which is Admin/Platform-only). Business's mirror will keep whatever
/// Status value it already has and will not pick up IsActive from this
/// snapshot until that side is updated too.
/// </summary>
[ApiController]
[Route("api/internal/catalog")]
[AllowAnonymous]
[RequireInternalServiceKey]
public class InternalCatalogController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public InternalCatalogController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet("sync-snapshot")]
    public async Task<ActionResult<CatalogSnapshotDto>> GetSyncSnapshot()
    {
        var tenants = await _db.Tenants.AsNoTracking().ToListAsync();
        var modules = await _db.Modules.AsNoTracking().ToListAsync();
        var permissions = await _db.Permissions.AsNoTracking().ToListAsync();
        var licenses = await _db.Licenses.AsNoTracking().ToListAsync();

        return Ok(new CatalogSnapshotDto
        {
            Tenants = tenants.Select(t => new TenantDto
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
            }).ToList(),
            Modules = modules.Select(m => new ModuleDto
            {
                ModuleId = m.ModuleId,
                ModuleCode = m.ModuleCode,
                ModuleName = m.ModuleName,
                Description = m.Description,
                IsActive = m.IsActive,
                IsCore = m.IsCore,
                DisplayOrder = m.DisplayOrder,
                ParentModuleId = m.ParentModuleId,
                MenuUrl = m.MenuUrl,
                Icon = m.Icon,               
            }).ToList(),
            Permissions = permissions.Select(p => new PermissionDto
            {
                PermissionId = p.PermissionId,
                ModuleId = p.ModuleId,
                PermissionCode = p.PermissionCode,
                Description = p.Description,
            }).ToList(),
            // A tenant may have more than one License row (renewals leave the
            // old one in place) - contribute only the one that's IsActive,
            // and among those the one expiring latest, so the Business side
            // always enforces the current license, not a stale/expired one.
            Licenses = licenses
                .GroupBy(l => l.TenantId)
                .Select(g => g.OrderByDescending(l => l.IsActive).ThenByDescending(l => l.ExpiryDate).First())
                .Select(l => new LicenseSummaryDto
                {
                    TenantId = l.TenantId,
                    MaxDevices = l.MaxDevices,
                    IsActive = l.IsActive,
                    ExpiryDate = l.ExpiryDate,
                }).ToList(),
        });
    }
}