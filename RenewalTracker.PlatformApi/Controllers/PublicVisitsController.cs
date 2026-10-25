using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Website visitor counter (js/bp-visits.js on blisspointworld.com):
///   POST api/public/visits           { visitorId, path, source, device }  - one page view
///   GET  api/public/visits/summary   visitors this year / month / today, and whether the public counter may show
/// Anonymous, "Website" CORS policy. See WebsiteVisitStore for what is stored (no IP, no personal data).
/// </summary>
[ApiController]
[Route("api/public/visits")]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicVisitsController : ControllerBase
{
    public sealed record VisitRequest(string? VisitorId, string? Path, string? Source, string? Device);

    private readonly WebsiteVisitStore _store;
    private readonly ILogger<PublicVisitsController> _logger;

    public PublicVisitsController(WebsiteVisitStore store, ILogger<PublicVisitsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Record([FromBody] VisitRequest req, CancellationToken ct)
    {
        // never an error for the visitor - a counter must not break the page
        try
        {
            if (WebsiteVisitStore.IsBot(Request.Headers.UserAgent.ToString())) return NoContent();
            var hit = WebsiteVisitStore.Clean(req.VisitorId, req.Path, req.Source, req.Device);
            if (hit == null) return NoContent();
            var forwarded = Request.Headers["X-Forwarded-For"].ToString();
            var ip = !string.IsNullOrWhiteSpace(forwarded) ? forwarded.Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await _store.RecordAsync(hit, ip, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Website visit not recorded");
        }
        return NoContent();
    }

    [HttpGet("summary")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var s = await _store.SummaryAsync(ct);
        // the public page only needs the numbers it may show
        return Ok(new { s.Show, s.Year, visitorsThisYear = s.Show ? s.VisitorsThisYear : (int?)null, visitorsThisMonth = s.Show ? s.VisitorsThisMonth : (int?)null });
    }
}
