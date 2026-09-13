using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.Platform.Infrastructure.Services;

namespace RenewalTracker.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Everything RenewalTracker.PlatformApi's Program.cs needs from this
/// project: PlatformDbContext (the RenewalTrackerPlatform database), the
/// platform-operator identity system (PlatformServiceRegistrations), and
/// the tenant-provisioning client that calls across to the Business API
/// over plain HTTP (the only connection between the two APIs - no shared
/// code, no shared database).
/// </summary>
public static class PlatformInfrastructureRegistration
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlatformDbContext>(options =>
        {
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection"));
        });

        services.AddHttpContextAccessor();

        PlatformServiceRegistrations.Register(services);

        // See IPlatformTenantProvisioningService - calls across to
        // RenewalTracker.Api's RequireInternalServiceKey-gated
        // api/internal/tenant-provisioning endpoint to finish standing up
        // a tenant once the Tenant row itself exists here.
        //
        // Config key is "BusinessApiInternal:BaseUrl", not "BusinessApi:BaseUrl" -
        // deliberately renamed. On this deployment, something outside this
        // codebase (an OS-level environment variable, never located) was
        // overriding "BusinessApi:BaseUrl" to this API's OWN address
        // (causing it to call itself and get a 401 from its own auth
        // pipeline instead of reaching RenewalTracker.Api). Renaming the
        // key sidesteps that collision entirely - if this ever needs
        // renaming again, grep for the old key name first.
        services.AddHttpClient<IPlatformTenantProvisioningService, PlatformTenantProvisioningService>((sp, client) =>
        {
            var baseUrl = configuration["BusinessApiInternal:BaseUrl"] ?? "https://localhost:5081/";
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("X-Internal-Service-Key", configuration["Internal:ServiceKey"] ?? string.Empty);
        });

        return services;
    }
}
