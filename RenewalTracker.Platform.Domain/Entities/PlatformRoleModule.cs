using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One access grant: this PlatformRole can see/use this (top-level) Module.
/// A row's existence means "granted" - same insert/delete-based toggle
/// pattern as TenantModule used to use, chosen here deliberately since
/// there's no separate enable/disable date to track for a role grant (see
/// PlatformRolesController.ToggleModule).
/// </summary>
public class PlatformRoleModule
{
    [Key]
    public int PlatformRoleModuleId { get; set; }

    public int PlatformRoleId { get; set; }

    public PlatformRole PlatformRole { get; set; } = null!;

    public int ModuleId { get; set; }

    public Module Module { get; set; } = null!;
}
