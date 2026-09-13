using RenewalTracker.Platform.Application.DTOs.Platform;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Issues JWTs for platform operators, signed with this project's own
/// Jwt:Key/Issuer/Audience - a separate signing key from RenewalTracker.Api's
/// tenant-user tokens, so a leaked key from one process can't forge tokens
/// for the other. Carries a platform_user_id claim and nothing else (no
/// tenant_id/roles/permissions). See AppClaimTypes.PlatformUserId and
/// PlatformCurrentUserService.IsAuthenticated.
/// </summary>
public interface IPlatformJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateToken(CurrentPlatformUserDto user);
}
