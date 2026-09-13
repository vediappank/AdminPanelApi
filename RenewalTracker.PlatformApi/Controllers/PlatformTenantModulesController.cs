using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Tenant ▸ Tenant Modules - which modules (from TenantModuleCatalog) are
/// enabled for a given Tenant. Platform-owned, hand-written against
/// PlatformDbContext. A TenantModule row carries a real IsEnabled flag
/// (matching the real dbo.TenantModules column) - toggling flips it in
/// place and stamps EnabledDate/DisabledDate, rather than inserting/deleting
/// the row. Points at dbo.TenantModuleCatalog (not dbo.Modules) - see
/// tenant-module-catalog-split.sql. Previously demo data
/// (MockDataService.tenantModuleState in the Angular app - see
/// TenantModulesComponent).
///
/// Gated by TENANTS_LIST or TENANT_MODULES (either one) - this used to be
/// called only from the Tenants screen's own Edit form (TenantListComponent),
/// hence TENANTS_LIST alone; it's now also the API behind the dedicated
/// Tenant Module screen's own checklist (TenantModulesComponent, rebuilt to
/// work like the Roles screen - a Tenants list, Edit opens this same
/// per-tenant checklist instead of a flat cross-tenant grid), which is
/// granted TENANT_MODULES instead. Widened rather than split, same reasoning
/// as PlatformTenantModuleCatalogController's own multi-code gate. See
/// RequirePlatformModuleAttribute's multi-code support,
/// PlatformRolesController.GetModules() and settings-tenant-submodule-split.sql.
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:int}/modules")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES")]
public class PlatformTenantModulesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformTenantModulesController(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>Every active module in the catalog, with whether it's enabled for this tenant.
    /// ParentTenantModuleCatalogId comes along so the Tenant Module screen's checklist can
    /// group entries into the same expand/collapse tree the Module Catalog screen edits.</summary>
    [HttpGet]
    public async Task<ActionResult<List<TenantModuleStatusDto>>> GetForTenant(int tenantId)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId))
        {
            return NotFound();
        }

        var statusByCatalogId = await _db.TenantModules.AsNoTracking()
            .Where(tm => tm.TenantId == tenantId && tm.TenantModuleCatalogId != null)
            .ToDictionaryAsync(tm => tm.TenantModuleCatalogId!.Value, tm => tm.IsEnabled);

        var modules = await _db.TenantModuleCatalogs.AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.DisplayOrder).ThenBy(m => m.ModuleName)
            .ToListAsync();

        return Ok(modules.Select(m => new TenantModuleStatusDto
        {
            TenantModuleCatalogId = m.TenantModuleCatalogId,
            ModuleCode = m.ModuleCode,
            ModuleName = m.ModuleName,
            IsEnabled = statusByCatalogId.TryGetValue(m.TenantModuleCatalogId, out var isEnabled) && isEnabled,
            ParentTenantModuleCatalogId = m.ParentTenantModuleCatalogId,
        }).ToList());
    }

    /// <summary>Flips one module on/off for this tenant; returns the new state.</summary>
    [HttpPost("{tenantModuleCatalogId:int}/toggle")]
    public async Task<ActionResult<TenantModuleStatusDto>> Toggle(int tenantId, int tenantModuleCatalogId)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (tenant == null) return NotFound();

        var module = await _db.TenantModuleCatalogs.FirstOrDefaultAsync(m => m.TenantModuleCatalogId == tenantModuleCatalogId);
        if (module == null) return NotFound();

        var existing = await _db.TenantModules.FirstOrDefaultAsync(tm => tm.TenantId == tenantId && tm.TenantModuleCatalogId == tenantModuleCatalogId);
        var now = DateTime.UtcNow;
        bool isEnabled;
        if (existing != null)
        {
            existing.IsEnabled = !existing.IsEnabled;
            existing.ModifiedDate = now;
            if (existing.IsEnabled)
            {
                existing.EnabledDate = now;
                existing.DisabledDate = null;
            }
            else
            {
                existing.DisabledDate = now;
            }
            isEnabled = existing.IsEnabled;
        }
        else
        {
            _db.TenantModules.Add(new TenantModule
            {
                TenantId = tenantId,
                TenantModuleCatalogId = tenantModuleCatalogId,
                IsEnabled = true,
                EnabledDate = now,
            });
            isEnabled = true;
        }
        await _db.SaveChangesAsync();

        return Ok(new TenantModuleStatusDto
        {
            TenantModuleCatalogId = module.TenantModuleCatalogId,
            ModuleCode = module.ModuleCode,
            ModuleName = module.ModuleName,
            IsEnabled = isEnabled,
            ParentTenantModuleCatalogId = module.ParentTenantModuleCatalogId,
        });
    }
}
