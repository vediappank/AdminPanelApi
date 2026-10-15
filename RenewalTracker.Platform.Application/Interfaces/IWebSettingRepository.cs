using RenewalTracker.Platform.Application.DTOs.WebSettings;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Website Settings (dbo.WebSetting) - values blisspointgroup.com reads at runtime.
/// Dapper over dbo.usp_WebSetting_List / dbo.usp_WebSetting_Save
/// (DatabaseScripts/WebsiteSettings_SuperAdminPanel.sql).
/// </summary>
public interface IWebSettingRepository
{
    Task<List<WebSettingDto>> ListAsync(bool publicOnly, CancellationToken ct = default);

    /// <summary>Saves every item in one transaction; an unknown key throws WebLeadException(404).</summary>
    Task SaveAsync(IEnumerable<WebSettingValueDto> items, string changedBy, CancellationToken ct = default);
}
