namespace RenewalTracker.Platform.Application.DTOs.Generic;

public class TenantMenuItemDto
{
    public int MenuItemId { get; set; }
    public string MenuKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? RoleRestriction { get; set; }
    public bool IsActive { get; set; }
    public string? MenuGroup { get; set; }
    public string? Route { get; set; }
    public int DisplayOrder { get; set; }
    public string ApplicationCode { get; set; } = "TRACKER";
    public bool IsSystemMenu { get; set; }
    public int? ParentMenuItemId { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int? ModuleId { get; set; }
}

public class SaveTenantMenuItemDto
{
    public string MenuKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? RoleRestriction { get; set; }
    public bool IsActive { get; set; } = true;
    public string? MenuGroup { get; set; }
    public string? Route { get; set; }
    public int DisplayOrder { get; set; }
    public string ApplicationCode { get; set; } = "TRACKER";
    public bool IsSystemMenu { get; set; }
    public int? ParentMenuItemId { get; set; }
    public int? ModuleId { get; set; }
}
