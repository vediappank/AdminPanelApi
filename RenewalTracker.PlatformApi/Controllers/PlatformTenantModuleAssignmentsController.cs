using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Tenant Modules ▸ full CRUD grid across every tenant at once (as opposed
/// to PlatformTenantModulesController's per-tenant toggle-chip view). Lets a
/// platform operator assign a catalog module to a tenant, edit which
/// tenant/module a row points at and whether it's enabled, or remove the
/// assignment entirely (reverting that tenant back to the catalog default
/// of "not enabled" for that module) - all against the same dbo.TenantModules
/// row PlatformTenantModulesController's toggle endpoint creates. Points at
/// dbo.TenantModuleCatalog (not dbo.Modules) - see
/// tenant-module-catalog-split.sql. Platform-owned, hand-written against
/// PlatformDbContext.
///
/// Gated by TENANT_MODULES, not the parent TENANTS code - this used to back
/// the "Tenant Module" nav screen (TenantModulesComponent, /tenants/modules)
/// as a flat cross-tenant grid. That screen has since been rebuilt to work
/// like the Roles screen instead - a Tenants list, Edit opens the same
/// per-tenant checklist PlatformTenantModulesController already served the
/// Tenants screen's own Edit form with (now gated by either TENANTS_LIST or
/// TENANT_MODULES - see that controller) - so nothing in the frontend calls
/// this controller's endpoints any more. Left in place (still fully
/// functional, still gated the same way) rather than removed, in case
/// something still needs direct row-level CRUD across every tenant at once;
/// not itself wired up to any current screen. See
/// PlatformRolesController.GetModules() and settings-tenant-submodule-split.sql.
/// </summary>
[ApiController]
[Route("api/platform/tenant-modules")]
[RequirePlatformModule("TENANT_MODULES")]
public class PlatformTenantModuleAssignmentsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformTenantModuleAssignmentsController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<TenantModuleRowDto>>> GetAll()
    {
        var rows = await _db.TenantModules.AsNoTracking()
            .Include(tm => tm.Tenant)
            .Include(tm => tm.TenantModuleCatalog)
            .Where(tm => tm.TenantModuleCatalogId != null)
            .OrderBy(tm => tm.Tenant.TenantName).ThenBy(tm => tm.TenantModuleCatalog!.ModuleName)
            .ToListAsync();

        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TenantModuleRowDto>> Create(CreateTenantModuleDto dto)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == dto.TenantId))
        {
            return BadRequest(new { message = "Select a valid tenant." });
        }
        if (!await _db.TenantModuleCatalogs.AnyAsync(m => m.TenantModuleCatalogId == dto.TenantModuleCatalogId))
        {
            return BadRequest(new { message = "Select a valid module." });
        }
        if (await _db.TenantModules.AnyAsync(tm => tm.TenantId == dto.TenantId && tm.TenantModuleCatalogId == dto.TenantModuleCatalogId))
        {
            return BadRequest(new { message = "This tenant already has that module assigned." });
        }

        var now = DateTime.UtcNow;
        var entity = new TenantModule
        {
            TenantId = dto.TenantId,
            TenantModuleCatalogId = dto.TenantModuleCatalogId,
            IsEnabled = dto.IsEnabled,
            EnabledDate = dto.IsEnabled ? now : null,
        };
        _db.TenantModules.Add(entity);
        await _db.SaveChangesAsync();

        await _db.Entry(entity).Reference(e => e.Tenant).LoadAsync();
        await _db.Entry(entity).Reference(e => e.TenantModuleCatalog).LoadAsync();
        return Ok(ToDto(entity));
    }

    /// <summary>
    /// Updates every editable column on an assignment - Tenant, Module, and
    /// Enabled - not just the Enabled flag. Reassigning Tenant/Module goes
    /// through the same validation Create() does (real tenant, real
    /// catalog module, no duplicate for that tenant+module pair elsewhere).
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<TenantModuleRowDto>> Update(int id, UpdateTenantModuleDto dto)
    {
        var entity = await _db.TenantModules
            .Include(tm => tm.Tenant)
            .Include(tm => tm.TenantModuleCatalog)
            .FirstOrDefaultAsync(tm => tm.TenantModuleId == id);
        if (entity == null) return NotFound();

        if (!await _db.Tenants.AnyAsync(t => t.TenantId == dto.TenantId))
        {
            return BadRequest(new { message = "Select a valid tenant." });
        }
        if (!await _db.TenantModuleCatalogs.AnyAsync(m => m.TenantModuleCatalogId == dto.TenantModuleCatalogId))
        {
            return BadRequest(new { message = "Select a valid module." });
        }
        if (await _db.TenantModules.AnyAsync(tm =>
                tm.TenantModuleId != id && tm.TenantId == dto.TenantId && tm.TenantModuleCatalogId == dto.TenantModuleCatalogId))
        {
            return BadRequest(new { message = "This tenant already has that module assigned." });
        }

        var now = DateTime.UtcNow;
        var reassigned = entity.TenantId != dto.TenantId || entity.TenantModuleCatalogId != dto.TenantModuleCatalogId;
        entity.TenantId = dto.TenantId;
        entity.TenantModuleCatalogId = dto.TenantModuleCatalogId;

        if (entity.IsEnabled != dto.IsEnabled)
        {
            entity.IsEnabled = dto.IsEnabled;
            if (dto.IsEnabled)
            {
                entity.EnabledDate = now;
                entity.DisabledDate = null;
            }
            else
            {
                entity.DisabledDate = now;
            }
        }
        entity.ModifiedDate = now;
        await _db.SaveChangesAsync();

        // The Tenant/TenantModuleCatalog navigation properties above were
        // loaded against the OLD ids - reload them so ToDto() below reports
        // the new tenant/module names, not the ones this row used to point at.
        if (reassigned)
        {
            await _db.Entry(entity).Reference(e => e.Tenant).LoadAsync();
            await _db.Entry(entity).Reference(e => e.TenantModuleCatalog).LoadAsync();
        }
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.TenantModules.FirstOrDefaultAsync(tm => tm.TenantModuleId == id);
        if (entity == null) return NotFound();

        _db.TenantModules.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static TenantModuleRowDto ToDto(TenantModule tm) => new()
    {
        TenantModuleId = tm.TenantModuleId,
        TenantId = tm.TenantId,
        TenantName = tm.Tenant?.TenantName ?? $"Tenant #{tm.TenantId}",
        TenantModuleCatalogId = tm.TenantModuleCatalogId ?? 0,
        ModuleName = tm.TenantModuleCatalog?.ModuleName ?? $"Module #{tm.TenantModuleCatalogId}",
        IsEnabled = tm.IsEnabled,
        EnabledDate = tm.EnabledDate,
        DisabledDate = tm.DisabledDate,
    };
}
