using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One service line under a QuotationProduct. Usually pre-filled from the
/// Product's master services (dbo.ProductServices - see ProductService.cs)
/// when the Product is ticked on a new quotation, but always stored as its
/// own copy (ServiceName/Qty/Amount/VatPercent) so later master price
/// changes never touch an existing quotation. Free-text lines are allowed
/// too - ProductServiceId is null for those.
///
/// Still NOT linked to the old Service Catalog (dbo.Services/ServiceItem) -
/// that table serves Enquiry Requirements only (see EnquiryRequirement.cs).
///
/// Table dbo.QuotationServiceLines - see create-quotations.sql;
/// ProductServiceId added by DatabaseScripts/ProductServices_Schema.sql.
/// </summary>
public class QuotationServiceLine
{
    [Key]
    public int QuotationServiceLineId { get; set; }

    public int QuotationProductId { get; set; }

    public QuotationProduct QuotationProduct { get; set; } = null!;

    /// <summary>The master Product Service this line was taken from; null for a free-text line (or when that master row was deleted).</summary>
    public int? ProductServiceId { get; set; }

    public ProductService? ProductService { get; set; }

    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    public int Qty { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VatPercent { get; set; }

    public DateTime CreatedDate { get; set; }
}
