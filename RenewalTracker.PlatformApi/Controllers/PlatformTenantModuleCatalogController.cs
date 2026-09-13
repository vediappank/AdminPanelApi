using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// CRUD over the tenant-assignable module catalog (dbo.TenantModuleCatalog) -
/// details only (name/description/active), split out of dbo.Modules (which
/// remains this Admin panel's own nav/permissions tree) so a "module" a
/// tenant can be given no longer shares a table with this app's own sidebar
/// entries. See tenant-module-catalog-split.sql for the migration and
/// TenantModuleCatalog.cs for why. ModuleCode is never typed by hand - it's
/// derived from ModuleName and kept unique here.
///
/// Gated by ANY of TENANTS_LIST, TENANT_MODULES or TENANT_MODULE_CATALOG,
/// not a single code - GetAll() is shared read/reference data all three
/// Tenant Management screens call just to look up catalog names (the
/// Tenants screen's per-tenant checklist, the Tenant Module grid, and this
/// controller's own Module Catalog screen - TenantModuleCatalogComponent,
/// /tenants/catalog), so it can't be pinned to just one of them. The
/// Create/Update/Delete actions stay behind this same whole-controller gate
/// rather than being split out to TENANT_MODULE_CATALOG alone - simpler,
/// and consistent with every other controller in this API gating at the
/// controller level - so in practice a role with any one of the three
/// Tenant Management screens granted can also manage the catalog, not just
/// the one with the dedicated Module Catalog screen. See
/// RequirePlatformModuleAttribute's multi-code support and
/// settings-tenant-submodule-split.sql.
///
/// Create/Update also accept ParentTenantModuleCatalogId/MenuUrl/DisplayOrder -
/// this catalog's own nav shape for the Tenant-side (Business/Tracker) app,
/// mirroring how PlatformCatalogController's Modules already work for this
/// Admin panel's own sidebar (parent = submenu, childless + MenuUrl = direct
/// redirect). See TenantModuleCatalog.cs and tenant-module-catalog-nav-fields.sql.
/// </summary>
[ApiController]
[Route("api/platform/tenant-module-catalog")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES", "TENANT_MODULE_CATALOG")]
public class PlatformTenantModuleCatalogController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformTenantModuleCatalogController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<TenantModuleCatalogDto>>> GetAll()
    {
        var rows = await _db.TenantModuleCatalogs.AsNoTracking().OrderBy(m => m.DisplayOrder).ThenBy(m => m.ModuleName).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TenantModuleCatalogDto>> Create(CreateTenantModuleCatalogDto dto)
    {
        var name = dto.ModuleName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Module name is required." });
        }
        if (dto.ParentTenantModuleCatalogId.HasValue && !await _db.TenantModuleCatalogs.AnyAsync(m => m.TenantModuleCatalogId == dto.ParentTenantModuleCatalogId.Value))
        {
            return BadRequest(new { message = "The selected parent module does not exist." });
        }

        var entity = new TenantModuleCatalog
        {
            ModuleCode = await GenerateUniqueCodeAsync(name),
            ModuleName = name,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description!.Trim(),
            IsActive = true,
            ParentTenantModuleCatalogId = dto.ParentTenantModuleCatalogId,
            MenuUrl = string.IsNullOrWhiteSpace(dto.MenuUrl) ? null : dto.MenuUrl!.Trim(),
            DisplayOrder = dto.DisplayOrder,
            Icon = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon!.Trim(),
            IsCore = dto.IsCore,
            CreatedDate = DateTime.UtcNow,
        };
        _db.TenantModuleCatalogs.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TenantModuleCatalogDto>> Update(int id, UpdateTenantModuleCatalogDto dto)
    {
        var entity = await _db.TenantModuleCatalogs.FirstOrDefaultAsync(m => m.TenantModuleCatalogId == id);
        if (entity == null) return NotFound();

        var name = dto.ModuleName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Module name is required." });
        }
        if (dto.ParentTenantModuleCatalogId == id)
        {
            return BadRequest(new { message = "A module cannot be its own parent." });
        }
        if (dto.ParentTenantModuleCatalogId.HasValue && !await _db.TenantModuleCatalogs.AnyAsync(m => m.TenantModuleCatalogId == dto.ParentTenantModuleCatalogId.Value))
        {
            return BadRequest(new { message = "The selected parent module does not exist." });
        }

        entity.ModuleName = name;
        entity.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description!.Trim();
        entity.IsActive = dto.IsActive;
        entity.ParentTenantModuleCatalogId = dto.ParentTenantModuleCatalogId;
        entity.MenuUrl = string.IsNullOrWhiteSpace(dto.MenuUrl) ? null : dto.MenuUrl!.Trim();
        entity.DisplayOrder = dto.DisplayOrder;
        entity.Icon = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon!.Trim();
        entity.IsCore = dto.IsCore;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.TenantModuleCatalogs.FirstOrDefaultAsync(m => m.TenantModuleCatalogId == id);
        if (entity == null) return NotFound();

        if (await _db.TenantModules.AnyAsync(tm => tm.TenantModuleCatalogId == id))
        {
            return BadRequest(new { message = "This module is still assigned to at least one tenant - remove those assignments first." });
        }
        if (await _db.TenantModuleCatalogs.AnyAsync(m => m.ParentTenantModuleCatalogId == id))
        {
            return BadRequest(new { message = "Remove or reassign this module's sub-menu items first." });
        }

        _db.TenantModuleCatalogs.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>e.g. "Renewals" -> "RENEWALS"; a collision gets "_2", "_3", ... appended.</summary>
    private async Task<string> GenerateUniqueCodeAsync(string name)
    {
        var baseCode = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        if (string.IsNullOrEmpty(baseCode)) baseCode = "MODULE";
        if (baseCode.Length > 50) baseCode = baseCode[..50];

        var code = baseCode;
        var suffix = 2;
        while (await _db.TenantModuleCatalogs.AnyAsync(m => m.ModuleCode == code))
        {
            var suffixText = "_" + suffix;
            code = (baseCode.Length + suffixText.Length > 50 ? baseCode[..(50 - suffixText.Length)] : baseCode) + suffixText;
            suffix++;
        }
        return code;
    }

    private static TenantModuleCatalogDto ToDto(TenantModuleCatalog m) => new()
    {
        TenantModuleCatalogId = m.TenantModuleCatalogId,
        ModuleCode = m.ModuleCode,
        ModuleName = m.ModuleName,
        Description = m.Description,
        IsActive = m.IsActive,
        ParentTenantModuleCatalogId = m.ParentTenantModuleCatalogId,
        MenuUrl = m.MenuUrl,
        DisplayOrder = m.DisplayOrder,
        Icon = m.Icon,
        IsCore = m.IsCore,
    };
}
