using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Service Catalog ▸ Service - a sellable service/product, priced with VAT,
/// tied to a required Category/Sub-Category. See ServiceCategory.cs for
/// context.
///
/// Maps to the real dbo.Services table - confirmed from the actual column
/// list this time (ServiceId/CategoryId/SubCategoryId/ServiceName/IsActive/
/// CreatedDate/ModifiedDate/Amount/VAT), not inferred like the "ServiceItems"
/// mapping this replaced. CategoryId/SubCategoryId are NOT NULL on the real
/// table, so unlike before, a service always needs both - see
/// PlatformServiceCatalogController and ServiceItemsComponent's form
/// (Category/Sub-category are now required, not optional).
/// </summary>
public class ServiceItem
{
    [Key]
    public int ServiceId { get; set; }

    [MaxLength(150)]
    public string ServiceName { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    public ServiceCategory? ServiceCategory { get; set; }

    public int SubCategoryId { get; set; }

    public ServiceSubCategory? ServiceSubCategory { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VAT { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
