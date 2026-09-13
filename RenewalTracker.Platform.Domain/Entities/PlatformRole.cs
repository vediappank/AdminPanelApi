using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// A role a Platform operator (PlatformUser) can be assigned - coarse,
/// module-level access control: a role grants a set of top-level Modules
/// (see PlatformRoleModule), and a user can see/use a screen only if their
/// role grants that screen's Module. One role per user (PlatformUser.
/// PlatformRoleId), enforced live against the database on every request
/// (see RequirePlatformModuleAttribute / ICurrentPlatformUserService) - not
/// baked into the JWT, so a role or its grants can be changed and it takes
/// effect on the user's very next request, no re-login required.
/// </summary>
public class PlatformRole
{
    [Key]
    public int PlatformRoleId { get; set; }

    [MaxLength(100)]
    public string RoleName { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
