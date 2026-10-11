using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>CRUD for SuperAdminPanel.dbo.TenantMenuItems.</summary>
[ApiController]
[Route("api/platform/tenant-menu-items")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES", "TENANT_MODULE_CATALOG")]
public class PlatformTenantMenuItemsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public PlatformTenantMenuItemsController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<TenantMenuItemDto>>> GetAll()
    {
        var rows = await _db.TenantMenuItems.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.DisplayName).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TenantMenuItemDto>> Create(SaveTenantMenuItemDto dto)
    {
        var error = await ValidateAsync(dto, null);
        if (error != null) return BadRequest(new { message = error });

        var entity = new TenantMenuItem();
        Apply(entity, dto);
        entity.CreatedDate = DateTime.UtcNow;
        _db.TenantMenuItems.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TenantMenuItemDto>> Update(int id, SaveTenantMenuItemDto dto)
    {
        var entity = await _db.TenantMenuItems.FirstOrDefaultAsync(x => x.MenuItemId == id);
        if (entity == null) return NotFound(new { message = "Menu item not found." });
        var error = await ValidateAsync(dto, id);
        if (error != null) return BadRequest(new { message = error });

        Apply(entity, dto);
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.TenantMenuItems.FirstOrDefaultAsync(x => x.MenuItemId == id);
        if (entity == null) return NotFound(new { message = "Menu item not found." });
        if (await _db.TenantMenuItems.AnyAsync(x => x.ParentMenuItemId == id))
            return BadRequest(new { message = "This menu item has child items. Remove or reassign them first." });

        _db.TenantMenuItems.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ValidateAsync(SaveTenantMenuItemDto dto, int? editingId)
    {
        var key = dto.MenuKey?.Trim();
        var name = dto.DisplayName?.Trim();
        var appCode = dto.ApplicationCode?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return "Menu Key is required.";
        if (key.Length > 100) return "Menu Key cannot exceed 100 characters.";
        if (string.IsNullOrWhiteSpace(name)) return "Display Name is required.";
        if (name.Length > 150) return "Display Name cannot exceed 150 characters.";
        if (string.IsNullOrWhiteSpace(appCode)) return "Application Code is required.";
        if (appCode.Length > 30) return "Application Code cannot exceed 30 characters.";
        if (await _db.TenantMenuItems.AnyAsync(x => x.MenuKey == key && (!editingId.HasValue || x.MenuItemId != editingId.Value)))
            return $"Menu Key '{key}' already exists.";
        if (dto.ParentMenuItemId.HasValue)
        {
            if (editingId == dto.ParentMenuItemId) return "A menu item cannot be its own parent.";
            if (!await _db.TenantMenuItems.AnyAsync(x => x.MenuItemId == dto.ParentMenuItemId.Value))
                return "The selected parent menu item does not exist.";
            // Prevent introducing a cycle by choosing one of this item's descendants as its parent.
            if (editingId.HasValue)
            {
                var parentId = dto.ParentMenuItemId;
                var visited = new HashSet<int>();
                while (parentId.HasValue && visited.Add(parentId.Value))
                {
                    if (parentId.Value == editingId.Value) return "A menu item cannot be moved beneath one of its own children.";
                    parentId = await _db.TenantMenuItems.Where(x => x.MenuItemId == parentId.Value)
                        .Select(x => x.ParentMenuItemId).FirstOrDefaultAsync();
                }
            }
        }
        return null;
    }

    private static void Apply(TenantMenuItem entity, SaveTenantMenuItemDto dto)
    {
        entity.MenuKey = dto.MenuKey.Trim();
        entity.DisplayName = dto.DisplayName.Trim();
        entity.Icon = Clean(dto.Icon);
        entity.RoleRestriction = Clean(dto.RoleRestriction);
        entity.IsActive = dto.IsActive;
        entity.MenuGroup = Clean(dto.MenuGroup);
        entity.Route = Clean(dto.Route);
        entity.DisplayOrder = dto.DisplayOrder;
        entity.ApplicationCode = dto.ApplicationCode.Trim().ToUpperInvariant();
        entity.IsSystemMenu = dto.IsSystemMenu;
        entity.ParentMenuItemId = dto.ParentMenuItemId;
        entity.ModuleId = dto.ModuleId;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TenantMenuItemDto ToDto(TenantMenuItem x) => new()
    {
        MenuItemId = x.MenuItemId, MenuKey = x.MenuKey, DisplayName = x.DisplayName,
        Icon = x.Icon, RoleRestriction = x.RoleRestriction, IsActive = x.IsActive,
        MenuGroup = x.MenuGroup, Route = x.Route, DisplayOrder = x.DisplayOrder,
        ApplicationCode = x.ApplicationCode, IsSystemMenu = x.IsSystemMenu,
        ParentMenuItemId = x.ParentMenuItemId, CreatedDate = x.CreatedDate,
        ModifiedDate = x.ModifiedDate, ModuleId = x.ModuleId
    };
}
