using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Authoritative Permission row (RenewalTrackerPlatform database) - owned by
/// RenewalTracker.PlatformApi. See Tenant.cs for why this is a separate
/// type from RenewalTracker.Domain.Entities.Permission (Business's local
/// read-only mirror copy).
/// </summary>
public class Permission
{
    [Key]
    public int PermissionId { get; set; }

    public int ModuleId { get; set; }

    public Module Module { get; set; } = null!;

    [MaxLength(100)]
    public string PermissionCode { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

}
