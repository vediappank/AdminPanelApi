using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>Admin Panel > Website Leads > Visitors: GET api/platform/website-visits?days=30</summary>
[ApiController]
[Route("api/platform/website-visits")]
[RequirePlatformModule("WEBSITE_VISITS", "WEBSITE")]
public class PlatformWebsiteVisitsController : ControllerBase
{
    private readonly WebsiteVisitStore _store;
    public PlatformWebsiteVisitsController(WebsiteVisitStore store) { _store = store; }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 30, CancellationToken ct = default) => Ok(await _store.ReportAsync(days, ct));
}
