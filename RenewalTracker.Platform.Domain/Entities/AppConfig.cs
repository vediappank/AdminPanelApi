using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Applications ▸ App Config - per-tenant display settings (date format,
/// time zone, currency). One row per Tenant. See
/// PlatformApplicationsController. Previously demo data
/// (MockDataService.appConfigs).
/// </summary>
public class AppConfig
{
    [Key]
    public int AppConfigId { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    [MaxLength(30)]
    public string DateFormat { get; set; } = "dd-MM-yyyy";

    [MaxLength(100)]
    public string TimeZone { get; set; } = "Asia/Dubai";

    [MaxLength(10)]
    public string Currency { get; set; } = "AED";
}
