using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.WebSettings;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel ▸ Website Leads ▸ Website Settings (dbo.WebSetting): social media links
/// and other values the website reads at runtime (see PublicWebSettingsController).
/// Access: WEBSITE_SETTINGS module (granted to every role with WEBSITE by
/// DatabaseScripts/WebsiteSettings_SuperAdminPanel.sql).
/// </summary>
[ApiController]
[Route("api/platform/web-settings")]
[RequirePlatformModule("WEBSITE_SETTINGS")]
[WebLeadExceptionFilter]
public class PlatformWebSettingsController : ControllerBase
{
    private readonly IWebSettingRepository _repo;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformWebSettingsController(IWebSettingRepository repo, ICurrentPlatformUserService currentUser)
    {
        _repo = repo;
        _currentUser = currentUser;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    [HttpGet]
    public async Task<ActionResult<List<WebSettingDto>>> List(CancellationToken ct) =>
        Ok(await _repo.ListAsync(publicOnly: false, ct));

    /// <summary>Saves the changed values (empty = not set). URL settings must be a full http(s) address.</summary>
    [HttpPut]
    public async Task<ActionResult<List<WebSettingDto>>> Save([FromBody] WebSettingsSaveRequest request, CancellationToken ct)
    {
        if (request?.Items == null || request.Items.Count == 0)
            return BadRequest(new { message = "Nothing to save." });

        var known = (await _repo.ListAsync(publicOnly: false, ct)).ToDictionary(s => s.SettingKey, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var item in request.Items)
        {
            item.SettingKey = (item.SettingKey ?? string.Empty).Trim();
            item.SettingValue = string.IsNullOrWhiteSpace(item.SettingValue) ? null : item.SettingValue.Trim();

            if (!known.TryGetValue(item.SettingKey, out var setting))
            {
                errors.Add($"Unknown setting '{item.SettingKey}'.");
                continue;
            }
            item.SettingKey = setting.SettingKey;
            if (item.SettingValue == null) continue;
            if (item.SettingValue.Length > 500)
                errors.Add($"{setting.Label}: too long (500 characters max).");
            else if (setting.ValueType == "url" && !IsWebUrl(item.SettingValue))
                errors.Add($"{setting.Label}: enter the full address, starting with https://");
        }
        if (errors.Count > 0)
            return BadRequest(new { message = string.Join(" ", errors) });

        await _repo.SaveAsync(request.Items, Me, ct);
        PublicWebSettingsController.ClearCache();
        return Ok(await _repo.ListAsync(publicOnly: false, ct));
    }

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Contains('.');
}
