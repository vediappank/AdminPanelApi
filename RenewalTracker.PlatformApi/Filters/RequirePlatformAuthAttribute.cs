using Microsoft.AspNetCore.Mvc.Filters;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Filters;

/// <summary>
/// Usage: [RequirePlatformAuth]. Gates an action to a genuine
/// platform-operator token (ICurrentPlatformUserService.IsAuthenticated).
/// Since the Platform/Business API split, this is the ONLY kind of user
/// token RenewalTracker.PlatformApi's JWT bearer scheme ever validates - a
/// tenant-user token is issued and validated exclusively by
/// RenewalTracker.Api now, signed with a different Jwt:Key, so it can't
/// even pass signature validation here. Unlike RequirePermissionAttribute
/// (RenewalTracker.Api) there is no permission code to check: every
/// platform user is currently equally privileged (see PlatformUser /
/// PlatformAuthService) - a granular platform role/permission system can
/// be layered on later the same way RequirePermission works for tenant
/// users, but isn't needed yet.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequirePlatformAuthAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var currentUser = context.HttpContext.RequestServices.GetRequiredService<ICurrentPlatformUserService>();
        if (!currentUser.IsAuthenticated)
        {
            context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
            return;
        }
        await next();
    }
}
