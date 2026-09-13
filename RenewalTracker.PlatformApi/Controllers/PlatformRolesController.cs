using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Platform Roles - coarse, screen-level access control for Platform users
/// (see PlatformRole/PlatformRoleModule). A role grants a set of Modules;
/// a Platform user has exactly one role (PlatformUser.PlatformRoleId).
/// Gated by its own ROLES code again (back from the brief SETTINGS-combined
/// gate) - "Role" still lives nested under the Settings nav group alongside
/// "User" (PlatformUsersController) and "Module" (PlatformCatalogController),
/// but each is once again individually grantable - see GetModules below,
/// which now expands a handful of top-level groups (Settings, Tenant
/// Management) into their own children instead of listing the parent as
/// one combined checkbox, and settings-tenant-submodule-split.sql for the
/// grant migration that preserves access for any role that was only
/// granted the combined parent.
/// </summary>
[ApiController]
[Route("api/platform/roles")]
[RequirePlatformModule("ROLES")]
public class PlatformRolesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformRolesController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<PlatformRoleDto>>> GetRoles()
    {
        var roles = await _db.PlatformRoles.AsNoTracking().OrderBy(r => r.RoleName).ToListAsync();
        return Ok(roles.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<PlatformRoleDto>> CreateRole(PlatformRoleDto dto)
    {
        if (await _db.PlatformRoles.AnyAsync(r => r.RoleName == dto.RoleName))
        {
            return BadRequest(new { message = $"A role named '{dto.RoleName}' already exists." });
        }

        var entity = new PlatformRole
        {
            RoleName = dto.RoleName,
            Description = dto.Description,
            IsActive = dto.IsActive,
            CreatedDate = DateTime.UtcNow,
        };
        _db.PlatformRoles.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PlatformRoleDto>> UpdateRole(int id, PlatformRoleDto dto)
    {
        var entity = await _db.PlatformRoles.FirstOrDefaultAsync(r => r.PlatformRoleId == id);
        if (entity == null) return NotFound();

        if (await _db.PlatformRoles.AnyAsync(r => r.RoleName == dto.RoleName && r.PlatformRoleId != id))
        {
            return BadRequest(new { message = $"A role named '{dto.RoleName}' already exists." });
        }

        entity.RoleName = dto.RoleName;
        entity.Description = dto.Description;
        entity.IsActive = dto.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteRole(int id)
    {
        var entity = await _db.PlatformRoles.FirstOrDefaultAsync(r => r.PlatformRoleId == id);
        if (entity == null) return NotFound();

        if (await _db.PlatformUsers.AnyAsync(u => u.PlatformRoleId == id))
        {
            return BadRequest(new { message = "Reassign the platform users on this role first." });
        }

        _db.PlatformRoles.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Explicit, hand-maintained list of top-level ModuleCodes that get
    /// expanded into their own children below instead of listed as one
    /// combined checkbox - the groups where each child screen has gone back
    /// to its own real [RequirePlatformModule] code on its backend
    /// controller (Settings: PLATFORM_USERS/ROLES/CATALOG - see
    /// PlatformUsersController/PlatformRolesController/PlatformCatalogController.
    /// Tenant Management: TENANTS_LIST/TENANT_MODULES - see
    /// PlatformTenantsController/PlatformTenantModulesController/
    /// PlatformTenantModuleAssignmentsController). Every other top-level
    /// group (Sales Pipeline, Reports, Service Catalog, Billing,
    /// Application, Dashboard) still grants/checks as one unit - those
    /// don't have per-child backend enforcement yet, so expanding them here
    /// would just be a cosmetic checklist with nothing behind it.
    /// </summary>
    private static readonly HashSet<string> ExpandedParentModuleCodes = new(StringComparer.OrdinalIgnoreCase) { "SETTINGS", "TENANTS" };

    /// <summary>
    /// Every screen this role can be granted, with whether it grants it -
    /// NOT every top-level Modules row. dbo.Modules also holds the separate
    /// business catalog of modules assignable to tenants (Core Platform,
    /// Renewals, Licensing, User Management, etc. - see TenantModule) which
    /// happen to be top-level rows too but aren't Admin screens at all. The
    /// rule for "is this a real screen" has to match PlatformShellComponent.
    /// buildMenu() exactly, or Roles would let you grant/see screens that
    /// don't exist in the nav and vice versa: a top-level module counts
    /// only if it has at least one active child with a MenuUrl, or (having
    /// no such children) has a MenuUrl of its own.
    ///
    /// A top-level group listed in ExpandedParentModuleCodes is shown as
    /// its own children instead of itself - each returned row's GroupName
    /// is set to the parent's name (e.g. "Settings") so the UI can label it
    /// "Settings ▸ Role" rather than a bare "Role" that reads oddly on its
    /// own; GroupName is null for every ordinary top-level row.
    /// </summary>
    [HttpGet("{id:int}/modules")]
    public async Task<ActionResult<List<RoleModuleStatusDto>>> GetModules(int id)
    {
        if (!await _db.PlatformRoles.AnyAsync(r => r.PlatformRoleId == id))
        {
            return NotFound();
        }

        var grantedModuleIds = await _db.PlatformRoleModules.AsNoTracking()
            .Where(rm => rm.PlatformRoleId == id)
            .Select(rm => rm.ModuleId)
            .ToListAsync();
        var grantedSet = grantedModuleIds.ToHashSet();

        var allModules = await _db.Modules.AsNoTracking()
            .Where(m => m.IsActive)
            .ToListAsync();

        var childrenByParent = allModules
            .Where(m => m.ParentModuleId != null)
            .GroupBy(m => m.ParentModuleId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var navModules = allModules
            .Where(m => m.ParentModuleId == null)
            .Where(top =>
            {
                var hasNavChildren = childrenByParent.TryGetValue(top.ModuleId, out var kids) && kids.Any(c => !string.IsNullOrEmpty(c.MenuUrl));
                return hasNavChildren || !string.IsNullOrEmpty(top.MenuUrl);
            })
            .OrderBy(m => m.DisplayOrder).ThenBy(m => m.ModuleName)
            .ToList();

        var result = new List<RoleModuleStatusDto>();
        foreach (var top in navModules)
        {
            var navChildren = childrenByParent.TryGetValue(top.ModuleId, out var kids)
                ? kids.Where(c => !string.IsNullOrEmpty(c.MenuUrl)).OrderBy(c => c.DisplayOrder).ThenBy(c => c.ModuleName).ToList()
                : new List<Module>();

            if (ExpandedParentModuleCodes.Contains(top.ModuleCode) && navChildren.Count > 0)
            {
                foreach (var child in navChildren)
                {
                    result.Add(new RoleModuleStatusDto
                    {
                        ModuleId = child.ModuleId,
                        ModuleCode = child.ModuleCode,
                        ModuleName = child.ModuleName,
                        GroupName = top.ModuleName,
                        IsGranted = grantedSet.Contains(child.ModuleId),
                    });
                }
            }
            else
            {
                result.Add(new RoleModuleStatusDto
                {
                    ModuleId = top.ModuleId,
                    ModuleCode = top.ModuleCode,
                    ModuleName = top.ModuleName,
                    GroupName = null,
                    IsGranted = grantedSet.Contains(top.ModuleId),
                });
            }
        }

        return Ok(result);
    }

    /// <summary>Flips one module's grant for this role; returns the new state.</summary>
    [HttpPost("{id:int}/modules/{moduleId:int}/toggle")]
    public async Task<ActionResult<RoleModuleStatusDto>> ToggleModule(int id, int moduleId)
    {
        var role = await _db.PlatformRoles.FirstOrDefaultAsync(r => r.PlatformRoleId == id);
        if (role == null) return NotFound();

        var module = await _db.Modules.FirstOrDefaultAsync(m => m.ModuleId == moduleId);
        if (module == null) return NotFound();

        var existing = await _db.PlatformRoleModules.FirstOrDefaultAsync(rm => rm.PlatformRoleId == id && rm.ModuleId == moduleId);
        bool isGranted;
        if (existing != null)
        {
            _db.PlatformRoleModules.Remove(existing);
            isGranted = false;
        }
        else
        {
            _db.PlatformRoleModules.Add(new PlatformRoleModule { PlatformRoleId = id, ModuleId = moduleId });
            isGranted = true;
        }
        await _db.SaveChangesAsync();

        return Ok(new RoleModuleStatusDto { ModuleId = module.ModuleId, ModuleCode = module.ModuleCode, ModuleName = module.ModuleName, IsGranted = isGranted });
    }

    private static PlatformRoleDto ToDto(PlatformRole r) => new()
    {
        PlatformRoleId = r.PlatformRoleId,
        RoleName = r.RoleName,
        Description = r.Description,
        IsActive = r.IsActive,
    };
}
