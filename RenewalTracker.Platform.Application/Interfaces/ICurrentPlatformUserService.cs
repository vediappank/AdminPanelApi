namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Reads the authenticated platform operator's identity out of the current
/// JWT - a completely separate identity system from RenewalTracker.Api's
/// ICurrentUserService (tenant users), which this project has no reference
/// to at all. IsAuthenticated requires the platform_user_id claim to be
/// present.
/// </summary>
public interface ICurrentPlatformUserService
{
    bool IsAuthenticated { get; }
    int PlatformUserId { get; }
    string Email { get; }

    /// <summary>
    /// Whether the current user's role currently grants the given top-level
    /// Module (by ModuleCode) - checked live against the database (never
    /// baked into the JWT), so a role/grant change applies on the user's very
    /// next request. A user with no role at all always gets false (fail
    /// closed) - see PlatformUser.PlatformRoleId. See
    /// RequirePlatformModuleAttribute.
    /// </summary>
    Task<bool> HasModuleAccessAsync(string moduleCode);
}
