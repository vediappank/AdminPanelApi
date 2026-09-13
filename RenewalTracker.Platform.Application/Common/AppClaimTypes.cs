namespace RenewalTracker.Platform.Application.Common;

/// <summary>
/// Custom JWT claim type names used by RenewalTracker.PlatformApi's own
/// token issuing/reading code. A separate copy from
/// RenewalTracker.Application.Common.AppClaimTypes (Business) - the two
/// processes sign and validate their tokens with different keys and never
/// need to share this constant, but keeping the name identical avoids
/// surprises if anyone ever compares the two token shapes.
/// </summary>
public static class AppClaimTypes
{
    /// <summary>
    /// Present only on a platform-operator token (see PlatformJwtTokenService).
    /// A tenant-user token, issued by RenewalTracker.Api with a different
    /// signing key, can't reach this API's bearer scheme at all - this
    /// claim-presence check (see PlatformCurrentUserService.IsAuthenticated)
    /// is kept as defense in depth rather than the sole guard it used to be.
    /// </summary>
    public const string PlatformUserId = "platform_user_id";
}
