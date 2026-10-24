using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// GET api/platform/tenants/{id}/logo - a tenant's Business Panel logo as an image, for the Tenants list
/// (an &lt;img&gt; cannot send the login token, so this one is open; a logo is public by nature).
/// Served from the SuperAdminPanel copy (dbo.TenantBrandingCache) - 404 when the tenant has no logo.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/platform/tenants/{tenantId:int}/logo")]
public class PlatformTenantLogoController : ControllerBase
{
    private readonly ITenantDatabaseService _databases;

    public PlatformTenantLogoController(ITenantDatabaseService databases)
    {
        _databases = databases;
    }

    [HttpGet]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(int tenantId, CancellationToken ct)
    {
        try
        {
            var logo = await _databases.GetCachedLogoAsync(tenantId, ct);
            if (logo == null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            // SVG logos are shown as images only - never as a document that could run script
            if (logo.Value.ContentType.Contains("svg")) Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            return File(logo.Value.Bytes, logo.Value.ContentType);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return NotFound();
        }
    }
}
