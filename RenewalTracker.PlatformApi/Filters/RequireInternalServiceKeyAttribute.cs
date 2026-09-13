using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace RenewalTracker.PlatformApi.Filters;

/// <summary>
/// Usage: [AllowAnonymous][RequireInternalServiceKey]. Gates a
/// service-to-service endpoint behind a shared secret header instead of any
/// user token - see InternalCatalogController's sync-snapshot endpoint,
/// which RenewalTracker.Api's CatalogSyncService/CatalogSyncBackgroundService
/// call. Same header name and config key
/// (RenewalTracker.Api.Filters.RequireInternalServiceKeyAttribute in the
/// other project checks the identical "Internal:ServiceKey" value) -
/// duplicated rather than shared via RenewalTracker.Infrastructure because
/// ASP.NET Core action filters belong to a Web SDK project and each API
/// project already has its own Filters folder.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequireInternalServiceKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Internal-Service-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = configuration["Internal:ServiceKey"];
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(expected, provided))
        {
            context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
            return;
        }

        await next();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
