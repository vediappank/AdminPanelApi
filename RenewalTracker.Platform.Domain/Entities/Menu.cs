using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// RETIRED - no longer used anywhere. Menus has been merged into Module
/// (see Module.cs: ParentModuleId/MenuUrl/Icon/TenantId) and the dbo.Menus
/// table has been dropped from RenewalTrackerPlatform1. This class is no
/// longer referenced by PlatformDbContext or any controller - it is dead
/// code, kept only because this session has no way to delete a file on
/// your machine. Safe to delete this file (and MenuDto.cs) once the merge
/// is verified working end to end.
/// </summary>
[Obsolete("Menus merged into Module - see Module.cs. Safe to delete this file.")]
public class Menu
{
    [Key]
    public int MenuId { get; set; }

    public int? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public int? ParentMenuId { get; set; }

    public Menu? ParentMenu { get; set; }

    public int? ModuleId { get; set; }

    public Module? Module { get; set; }

    [MaxLength(150)]
    public string MenuName { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? MenuUrl { get; set; }

    [MaxLength(100)]
    public string? Icon { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

}
