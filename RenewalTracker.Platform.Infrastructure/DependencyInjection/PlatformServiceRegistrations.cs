using Microsoft.Extensions.DependencyInjection;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Auth;
using RenewalTracker.Platform.Infrastructure.Services;

namespace RenewalTracker.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the platform-operator identity system used by the Platform
/// Panel (platform-admin-ui) - entirely separate from RenewalTracker.Api's
/// tenant-user services (ICurrentUserService/IJwtTokenService/IAuthService),
/// which this project has no reference to at all. See PlatformUser,
/// AppClaimTypes.PlatformUserId, and RequirePlatformAuthAttribute.
/// </summary>
public static class PlatformServiceRegistrations
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<ICurrentPlatformUserService, PlatformCurrentUserService>();
        services.AddScoped<IPlatformJwtTokenService, PlatformJwtTokenService>();
        services.AddScoped<IPlatformAuthService, PlatformAuthService>();
        services.AddScoped<IPlatformUserAdminService, PlatformUserAdminService>();
    }
}
