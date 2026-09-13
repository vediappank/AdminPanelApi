using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Filters;

/// <summary>
/// Usage: [RequirePlatformModule("SALES")]. Gates an action/controller to a
/// genuine platform-operator token (same check as [RequirePlatformAuth])
/// AND requires the current user's role to grant the named Module - see
/// ICurrentPlatformUserService.HasModuleAccessAsync (a live DB check, not a
/// JWT claim, so a role/grant change applies immediately). Use this instead
/// of (not alongside) [RequirePlatformAuth] on a module-owned
/// controller/action.
///
/// Accepts more than one code - [RequirePlatformModule("TENANTS_LIST",
/// "TENANT_MODULES")] passes if the role has ANY of them - for a
/// controller that legitimately serves more than one screen (read-only
/// reference data both screens need, e.g. PlatformTenantModuleCatalogController,
/// which the Tenants screen's edit form AND the separate Tenant Module
/// screen both call just to look up catalog names). Most controllers still
/// pass exactly one code.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequirePlatformModuleAttribute : Attribute, IAsyncActionFilter
{
    private readonly string[] _moduleCodes;

    public RequirePlatformModuleAttribute(params string[] moduleCodes)
    {
        _moduleCodes = moduleCodes;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var currentUser = context.HttpContext.RequestServices.GetRequiredService<ICurrentPlatformUserService>();
        if (!currentUser.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        foreach (var moduleCode in _moduleCodes)
        {
            if (await currentUser.HasModuleAccessAsync(moduleCode))
            {
                await next();
                return;
            }
        }

        context.Result = new ObjectResult(new { message = "You don't have access to this section." }) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
