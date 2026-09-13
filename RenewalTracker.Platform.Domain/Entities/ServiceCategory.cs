using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Service Catalog ▸ Category (RenewalTrackerPlatform database) - the top
/// level of the Sales/Service Catalog hierarchy used by Enquiries and
/// Services. Platform-owned, not tenant-scoped, the same way
/// Modules/Permissions are.
///
/// Column names/types match the real dbo.ServiceCategories table exactly
/// (CategoryId/CategoryName/IsActive/CreatedDate/ModifiedDate) rather than
/// this project's usual &lt;TableName&gt;Id/Name convention, since that table
/// already existed - see PlatformServiceCatalogController.
/// </summary>
public class ServiceCategory
{
    [Key]
    public int CategoryId { get; set; }

    [MaxLength(100)]
    public string CategoryName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
