using System;

namespace RenewalTracker.Platform.Application.DTOs.Applications;

/// <summary>
/// Mirrored by Angular's AppDefinition model - see PlatformApplicationsController.
/// Code/Name map to the real dbo.Applications columns ApplicationCode/
/// ApplicationName - there is no Description column on that table.
/// </summary>
public class ApplicationDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>Mirrored by Angular's AppConfig model - see PlatformApplicationsController.</summary>
public class AppConfigDto
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DateFormat { get; set; } = "dd-MM-yyyy";
    public string TimeZone { get; set; } = "Asia/Dubai";
    public string Currency { get; set; } = "AED";
}

/// <summary>Mirrored by Angular's License model - see PlatformApplicationsController.</summary>
public class LicenseDto
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime ExpiryDate { get; set; }
    public int UserLimit { get; set; }

    /// <summary>Device-activation cap for the Business Application - see License.cs.</summary>
    public int MaxDevices { get; set; }
    public bool IsActive { get; set; }

    /// <summary>The amount due to renew this license - see License.cs and PlatformBillingController's Quotation/License balance endpoints.</summary>
    public decimal RenewalAmount { get; set; }
}
