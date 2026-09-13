namespace RenewalTracker.Platform.Application.DTOs.Platform;

public class PlatformRoleDto
{
    public int PlatformRoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>One grantable screen and whether a given Role grants it - same shape/pattern as TenantModuleStatusDto.
/// GroupName is set (e.g. "Settings", "Tenant Management") when this row is actually a child screen shown expanded
/// out of its parent group rather than a genuine top-level module - see PlatformRolesController.GetModules()/
/// ExpandedParentModuleCodes - and is null for an ordinary top-level row.</summary>
public class RoleModuleStatusDto
{
    public int ModuleId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string? GroupName { get; set; }
    public bool IsGranted { get; set; }
}
