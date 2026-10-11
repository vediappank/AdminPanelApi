using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Service Catalog ▸ Product Services - the master list of services offered
/// under each Product (dbo.Applications, e.g. "Renewal &amp; Reminder Tracker").
/// Product → Service only, no category/sub-category level. Replaces the old
/// Category / Sub-Category / Service screens in the Platform Panel.
///
/// When a Product is ticked on a new quotation, its active services are
/// copied in as the starting service lines (ServiceName, DefaultQty, Amount,
/// VatPercent). Each QuotationServiceLine keeps its own copy of those values
/// (and an optional ProductServiceId back-reference), so changing a price
/// here never alters an existing quotation.
///
/// New table (dbo.ProductServices) - see DatabaseScripts/ProductServices_Schema.sql.
/// </summary>
public class ProductService
{
    [Key]
    public int ProductServiceId { get; set; }

    /// <summary>The Product (dbo.Applications.ApplicationId) this service belongs to.</summary>
    public int ApplicationId { get; set; }

    public AppDefinition? Application { get; set; }

    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Default quantity ("Count") used when the service is added to a quotation.</summary>
    public int DefaultQty { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VatPercent { get; set; }

    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
