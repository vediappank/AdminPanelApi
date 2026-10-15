namespace RenewalTracker.Platform.Application.DTOs.WebSettings;

/// <summary>One row of dbo.WebSetting - a value the public website reads at runtime (social links, ...).</summary>
public class WebSettingDto
{
    public string SettingKey { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
    public string ValueType { get; set; } = "url";
    public string? Placeholder { get; set; }
    public string? HelpText { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPublic { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public class WebSettingValueDto
{
    public string SettingKey { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
}

/// <summary>PUT api/platform/web-settings - only the changed keys need to be sent.</summary>
public class WebSettingsSaveRequest
{
    public List<WebSettingValueDto> Items { get; set; } = new();
}
