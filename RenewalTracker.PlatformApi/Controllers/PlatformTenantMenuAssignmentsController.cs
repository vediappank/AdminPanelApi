using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Which Business Panel menus (dbo.TenantMenuItems) each tenant has - dbo.TenantMenuAssignments.
/// Written by Provision tenant and edited from the Tenant Control Center and Tenants ▸ Edit.
///   GET api/platform/tenant-menu-assignments          every tenant's MenuItemIds (Tenants list column)
///   GET api/platform/tenants/{id}/menu-items          one tenant's MenuItemIds
///   PUT api/platform/tenants/{id}/menu-items          replace one tenant's menus (body: MenuItemIds)
/// Saving always keeps every active system menu and the parent group of each chosen menu.
/// </summary>
[ApiController]
[Route("api/platform")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES", "TENANT_MODULE_CATALOG")]
public class PlatformTenantMenuAssignmentsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly ITenantDatabaseService _databases;

    public PlatformTenantMenuAssignmentsController(PlatformDbContext db, ITenantDatabaseService databases)
    {
        _db = db;
        _databases = databases;
    }

    public class TenantMenuAssignmentRow
    {
        public int TenantId { get; set; }
        public int MenuItemId { get; set; }
    }

    [HttpGet("tenant-menu-assignments")]
    public async Task<ActionResult<List<TenantMenuAssignmentRow>>> GetAll()
    {
        var rows = await _db.TenantMenuAssignments.AsNoTracking()
            .Select(a => new TenantMenuAssignmentRow { TenantId = a.TenantId, MenuItemId = a.MenuItemId })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpGet("tenants/{tenantId:int}/menu-items")]
    public async Task<ActionResult<List<int>>> Get(int tenantId)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId)) return NotFound(new { message = "Tenant not found." });
        var ids = await _db.TenantMenuAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.MenuItemId)
            .ToListAsync();
        return Ok(ids);
    }

    [HttpPut("tenants/{tenantId:int}/menu-items")]
    public async Task<ActionResult<List<int>>> Save(int tenantId, [FromBody] List<int> menuItemIds)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId)) return NotFound(new { message = "Tenant not found." });

        var menus = await _db.TenantMenuItems.AsNoTracking().Where(m => m.IsActive).ToListAsync();
        var byId = menus.ToDictionary(m => m.MenuItemId);
        var chosen = new HashSet<int>((menuItemIds ?? new List<int>()).Where(byId.ContainsKey));
        foreach (var m in menus.Where(m => m.IsSystemMenu)) chosen.Add(m.MenuItemId);
        foreach (var id in chosen.ToList())
        {
            var parentId = byId[id].ParentMenuItemId;
            while (parentId is int pid && byId.TryGetValue(pid, out var parent) && chosen.Add(pid))
                parentId = parent.ParentMenuItemId;
        }

        var existing = await _db.TenantMenuAssignments.Where(a => a.TenantId == tenantId).ToListAsync();
        var keep = existing.Where(a => chosen.Contains(a.MenuItemId)).Select(a => a.MenuItemId).ToHashSet();
        _db.TenantMenuAssignments.RemoveRange(existing.Where(a => !chosen.Contains(a.MenuItemId)));
        var now = DateTime.UtcNow;
        foreach (var id in chosen.Where(id => !keep.Contains(id)))
            _db.TenantMenuAssignments.Add(new TenantMenuAssignment { TenantId = tenantId, MenuItemId = id, CreatedDate = now });
        await _db.SaveChangesAsync();

        // Apply the new menus to the tenant's own database right away (menus + Administrator role).
        if (await _db.TenantDatabases.AnyAsync(r => r.TenantId == tenantId))
            await _databases.QueueProvisioningAsync(tenantId, null);

        return Ok(chosen.ToList());
    }
}
