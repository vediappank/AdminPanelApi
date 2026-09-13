using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales ▸ Quotation - a priced proposal generated from a Sales Enquiry.
/// Deliberately separate from Enquiry/EnquiryRequirement: an enquiry's
/// requirements record what the customer initially asked about (picked from
/// the Service Catalog, dbo.Services); a Quotation is the actual document
/// sent back, built from one or more Products (Applications - dbo.
/// Applications, NOT the Service Catalog, which serves a different purpose
/// entirely here) plus manually-typed service lines under each Product -
/// see QuotationProduct.cs / QuotationServiceLine.cs.
///
/// One Enquiry can have several Quotations over time - see
/// PlatformQuotationsController.GenerateQuotations(): picking more than one
/// Product at generation time offers a choice between one Combined
/// Quotation (a single QuoteNo covering every picked Product) or several
/// Independent Quotations (one per Product, each with its own QuoteNo,
/// totals and Status, trackable/acceptable separately).
///
/// Subtotal/VatAmount/GrandTotal are stored, recomputed server-side every
/// time the quotation's lines are generated or changed - never typed by
/// hand.
///
/// New table (dbo.Quotations) - not part of the original schema dump - see
/// create-quotations.sql.
/// </summary>
public class Quotation
{
    [Key]
    public int QuotationId { get; set; }

    public int SalesEnquiryId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;

    [MaxLength(30)]
    public string? QuoteNo { get; set; }

    public DateTime QuoteDate { get; set; }

    public DateTime? ValidTill { get; set; }

    /// <summary>Draft / Sent / Accepted / Rejected - see PlatformQuotationsController.ValidStatuses.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "Draft";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrandTotal { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    [MaxLength(100)]
    public string? ModifiedBy { get; set; }
}
