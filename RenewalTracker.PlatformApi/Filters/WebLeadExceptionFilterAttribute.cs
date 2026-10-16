using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Filters;

/// <summary>
/// Turns a WebLeadException (a business rule refused by a dbo.usp_Web* stored
/// procedure, e.g. "Enquiry not found." / "Invalid status." / "This form is
/// currently switched off.") into the same { message } JSON shape
/// ExceptionHandlingMiddleware uses, with the right status code - instead of a
/// generic 500.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class WebLeadExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not WebLeadException ex) return;

        // A business rule refused by a stored procedure (form switched off, not found, invalid status...).
        // Logged as a warning (Logs/errors.log) with the request id the caller also receives.
        var id = RenewalTracker.PlatformApi.Middleware.RequestId.Get(context.HttpContext);
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebLeads");
        logger.LogWarning(ex.InnerException, "[{RequestId}] {Method} {Path} refused ({Status}): {Message}",
            id, context.HttpContext.Request.Method, context.HttpContext.Request.Path, ex.StatusCode, ex.Message);

        context.Result = new ObjectResult(new { message = ex.Message, requestId = id }) { StatusCode = ex.StatusCode };
        context.ExceptionHandled = true;
    }
}
