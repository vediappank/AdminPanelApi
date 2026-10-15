using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// GET api/public/web-settings - the public Website Settings (social links, ...) as
/// { "social.facebook": "https://...", "social.instagram": null, ... }.
/// Read by js/bp-social.js on every page of blisspointgroup.com; an empty value hides
/// that icon. Anonymous, "Website" CORS policy, cached in memory for 60 seconds
/// (cleared as soon as someone saves in the Admin Panel) so page views don't hit the DB.
/// </summary>
[ApiController]
[Route("api/public/web-settings")]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicWebSettingsController : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, string?>? _cached;
    private static DateTime _cachedAt;

    private readonly IWebSettingRepository _repo;
    private readonly ILogger<PublicWebSettingsController> _logger;

    public PublicWebSettingsController(IWebSettingRepository repo, ILogger<PublicWebSettingsController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Called after an Admin Panel save so the website sees the change on its next page view.</summary>
    public static void ClearCache() => _cached = null;

    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<Dictionary<string, string?>>> Get(CancellationToken ct)
    {
        var cached = _cached;
        if (cached != null && DateTime.UtcNow - _cachedAt < CacheFor) return Ok(cached);

        await Gate.WaitAsync(ct);
        try
        {
            if (_cached != null && DateTime.UtcNow - _cachedAt < CacheFor) return Ok(_cached);
            var rows = await _repo.ListAsync(publicOnly: true, ct);
            _cached = rows.ToDictionary(r => r.SettingKey, r => string.IsNullOrWhiteSpace(r.SettingValue) ? null : r.SettingValue);
            _cachedAt = DateTime.UtcNow;
            return Ok(_cached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the website keeps the links baked into its HTML when this fails
            _logger.LogError(ex, "Website settings could not be read.");
            Response.OnStarting(() => { Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Settings unavailable." });
        }
        finally
        {
            Gate.Release();
        }
    }
}
