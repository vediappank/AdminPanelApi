using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>Platform-owned menu entry matching SuperAdminPanel.dbo.TenantMenuItems.</summary>
public class TenantMenuItem
{
    [Key]
    public int MenuItemId { get; set; }
    [Required, MaxLength(100)]
    public string MenuKey { get; set; } = string.Empty;
    [Required, MaxLength(150)]
    public string DisplayName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? Icon { get; set; }
    [MaxLength(100)]
    public string? RoleRestriction { get; set; }
    public bool IsActive { get; set; }
    [MaxLength(100)]
    public string? MenuGroup { get; set; }
    [MaxLength(250)]
    public string? Route { get; set; }
    public int DisplayOrder { get; set; }
    [Required, MaxLength(30)]
    public string ApplicationCode { get; set; } = "TRACKER";
    public bool IsSystemMenu { get; set; }
    public int? ParentMenuItemId { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int? ModuleId { get; set; }
}
