using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Manages the global Modules/Permissions catalog (RenewalTrackerPlatform
/// database) shared by every tenant - what a tenant's Tenant Modules
/// checklist can enable, and what a tenant's Role<->Permission mapping
/// screen can grant. Deliberately hand-written against PlatformDbContext
/// rather than reusing RenewalTracker.Api's generic-CRUD
/// ModuleController/PermissionController (api/modules, api/permissions):
/// those stay reachable tenant-side for View only (ADMIN.VIEW, reading the
/// local synced mirror - see CatalogSyncService), but their Create/Edit/
/// Delete actions are now locked to PLATFORM.* permission codes that no
/// tenant role is ever granted (see schema/gen_generic_crud.py's
/// PERMISSION_ACTION_OVERRIDE) - this controller, gated by a genuine
/// platform-operator token instead of a tenant permission code, is the
/// only way to actually maintain the catalog. Edits here reach the
/// Business database's mirror the next time CatalogSyncService runs (on
/// its timer, on Business API startup, or via the admin on-demand trigger)
/// - not instantly.
///
/// A Module row now doubles as a left-nav entry: the old dbo.Menus table
/// was merged into dbo.Modules (it held no real data at merge time).
/// ParentModuleId ties a sub-menu item back to its top-level Module
/// (ParentModuleId IS NULL = a top-level entry). This table carries no
/// per-tenant relation - every row is a standard entry shared by every
/// tenant (the old TenantId column/FK was dropped - see
/// drop-modules-tenantid.sql).
///
/// Gated by its own CATALOG code again (back from the brief SETTINGS-combined
/// gate) - the CATALOG module (labeled "Module" in the nav) still lives
/// nested under the Settings nav group alongside "Role"
/// (PlatformRolesController) and "User" (PlatformUsersController), but each
/// is once again individually grantable per role - see
/// PlatformRolesController.GetModules() and settings-tenant-submodule-split.sql.
/// NOTE: GetModules() below is also how every signed-in user's own
/// sidebar/route-guard gets built (PlatformCatalogService.getModules(),
/// called from PlatformShellComponent and platform-module.guard.ts) - a
/// role without CATALOG granted won't be able to load its nav at all, not
/// just be unable to manage the catalog. That's a pre-existing quirk (this
/// endpoint has always doubled as both), not something this change
/// introduces - every role that needs a working sidebar needs CATALOG
/// granted specifically, same as before Settings was ever merged.
/// </summary>
[ApiController]
[Route("api/platform/catalog")]
[RequirePlatformModule("CATALOG")]
public class PlatformCatalogController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformCatalogController(PlatformDbContext db)
    {
        _db = db;
    }

    // ----- Modules -----

    [HttpGet("modules")]
    public async Task<ActionResult<List<ModuleDto>>> GetModules()
    {
        var modules = await _db.Modules.AsNoTracking().OrderBy(m => m.DisplayOrder).ThenBy(m => m.ModuleName).ToListAsync();
        return Ok(modules.Select(ToDto).ToList());
    }

    [HttpGet("modules/{id:int}")]
    public async Task<ActionResult<ModuleDto>> GetModule(int id)
    {
        var module = await _db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.ModuleId == id);
        return module == null ? NotFound() : Ok(ToDto(module));
    }

    [HttpPost("modules")]
    public async Task<ActionResult<ModuleDto>> CreateModule(ModuleDto dto)
    {
        if (await _db.Modules.AnyAsync(m => m.ModuleCode == dto.ModuleCode))
        {
            return BadRequest(new { message = $"A module with code '{dto.ModuleCode}' already exists." });
        }
        if (dto.ParentModuleId.HasValue && !await _db.Modules.AnyAsync(m => m.ModuleId == dto.ParentModuleId.Value))
        {
            return BadRequest(new { message = "The selected parent module does not exist." });
        }

        var entity = new Module
        {
            ModuleCode = dto.ModuleCode,
            ModuleName = dto.ModuleName,
            Description = dto.Description,
            IsActive = dto.IsActive,
            IsCore = dto.IsCore,
            DisplayOrder = dto.DisplayOrder,
            ParentModuleId = dto.ParentModuleId,
            MenuUrl = dto.MenuUrl,
            Icon = dto.Icon,
            CreatedDate = DateTime.UtcNow,
        };
        _db.Modules.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("modules/{id:int}")]
    public async Task<ActionResult<ModuleDto>> UpdateModule(int id, ModuleDto dto)
    {
        var entity = await _db.Modules.FirstOrDefaultAsync(m => m.ModuleId == id);
        if (entity == null) return NotFound();

        if (dto.ParentModuleId == id)
        {
            return BadRequest(new { message = "A module cannot be its own parent." });
        }
        if (dto.ParentModuleId.HasValue && !await _db.Modules.AnyAsync(m => m.ModuleId == dto.ParentModuleId.Value))
        {
            return BadRequest(new { message = "The selected parent module does not exist." });
        }

        entity.ModuleCode = dto.ModuleCode;
        entity.ModuleName = dto.ModuleName;
        entity.Description = dto.Description;
        entity.IsActive = dto.IsActive;
        entity.IsCore = dto.IsCore;
        entity.DisplayOrder = dto.DisplayOrder;
        entity.ParentModuleId = dto.ParentModuleId;
        entity.MenuUrl = dto.MenuUrl;
        entity.Icon = dto.Icon;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("modules/{id:int}")]
    public async Task<IActionResult> DeleteModule(int id)
    {
        var entity = await _db.Modules.FirstOrDefaultAsync(m => m.ModuleId == id);
        if (entity == null) return NotFound();

        if (await _db.Modules.AnyAsync(m => m.ParentModuleId == id))
        {
            return BadRequest(new { message = "Remove or reassign this module's sub-menu items first." });
        }

        _db.Modules.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ----- Permissions -----

    [HttpGet("permissions")]
    public async Task<ActionResult<List<PermissionDto>>> GetPermissions([FromQuery] int? moduleId)
    {
        var query = _db.Permissions.AsNoTracking().AsQueryable();
        if (moduleId.HasValue) query = query.Where(p => p.ModuleId == moduleId.Value);

        var permissions = await query.OrderBy(p => p.PermissionCode).ToListAsync();
        return Ok(permissions.Select(ToDto).ToList());
    }

    [HttpGet("permissions/{id:int}")]
    public async Task<ActionResult<PermissionDto>> GetPermission(int id)
    {
        var permission = await _db.Permissions.AsNoTracking().FirstOrDefaultAsync(p => p.PermissionId == id);
        return permission == null ? NotFound() : Ok(ToDto(permission));
    }

    [HttpPost("permissions")]
    public async Task<ActionResult<PermissionDto>> CreatePermission(PermissionDto dto)
    {
        if (!await _db.Modules.AnyAsync(m => m.ModuleId == dto.ModuleId))
        {
            return BadRequest(new { message = "The selected module does not exist." });
        }
        if (await _db.Permissions.AnyAsync(p => p.PermissionCode == dto.PermissionCode))
        {
            return BadRequest(new { message = $"A permission with code '{dto.PermissionCode}' already exists." });
        }

        var entity = new Permission
        {
            ModuleId = dto.ModuleId,
            PermissionCode = dto.PermissionCode,
            Description = dto.Description,
        };
        _db.Permissions.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("permissions/{id:int}")]
    public async Task<ActionResult<PermissionDto>> UpdatePermission(int id, PermissionDto dto)
    {
        var entity = await _db.Permissions.FirstOrDefaultAsync(p => p.PermissionId == id);
        if (entity == null) return NotFound();

        entity.ModuleId = dto.ModuleId;
        entity.PermissionCode = dto.PermissionCode;
        entity.Description = dto.Description;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("permissions/{id:int}")]
    public async Task<IActionResult> DeletePermission(int id)
    {
        var entity = await _db.Permissions.FirstOrDefaultAsync(p => p.PermissionId == id);
        if (entity == null) return NotFound();

        _db.Permissions.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static ModuleDto ToDto(Module m) => new()
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
    };

    private static PermissionDto ToDto(Permission p) => new()
    {
        PermissionId = p.PermissionId,
        ModuleId = p.ModuleId,
        PermissionCode = p.PermissionCode,
        Description = p.Description,
    };
}
