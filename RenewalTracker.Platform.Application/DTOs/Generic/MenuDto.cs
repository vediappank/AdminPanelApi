using System;

namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>
/// RETIRED - no longer used anywhere; Menus was merged into Module/ModuleDto.
/// Kept only because this session has no way to delete a file on your
/// machine. Safe to delete this file (and Menu.cs) once the merge is
/// verified working end to end.
/// </summary>
[Obsolete("Menus merged into Module/ModuleDto. Safe to delete this file.")]
public class MenuDto
{
    public int MenuId { get; set; }

    public int? TenantId { get; set; }
    public int? ParentMenuId { get; set; }
    public int? ModuleId { get; set; }
    public string MenuName { get; set; } = string.Empty;
    public string? MenuUrl { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}
