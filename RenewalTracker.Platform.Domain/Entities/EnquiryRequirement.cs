using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales ▸ Enquiry Requirement - one product/service line item a customer
/// asked about on an Enquiry. An enquiry can carry any number of these -
/// this table is what replaces the old assumption that an enquiry has a
/// single Category/Sub-category/EstimatedAmount (those three fields still
/// exist on Enquiry for backward compatibility, but are no longer the
/// source of truth once an enquiry has requirement rows - Enquiry.
/// EstimatedAmount is instead recomputed as the sum of these rows'
/// Qty*Amount every time they're saved - see PlatformSalesController.
/// SaveRequirements()).
///
/// Product-based: every row picks a real ServiceItem (dbo.Services) rather
/// than a free Category/Sub-category pair typed by hand. CategoryId/
/// SubCategoryId/*NameSnapshot are copied from that service at save time
/// (same "id + snapshot name" convention Enquiry itself already uses) -
/// purely for reporting/history; the product is what's actually picked and
/// what a Quotation line will eventually be generated from.
///
/// New table (dbo.SalesEnquiryRequirements) - not part of the original
/// schema dump the user supplied - see create-sales-enquiry-requirements.sql.
/// </summary>
public class EnquiryRequirement
{
    [Key]
    public int SalesEnquiryRequirementId { get; set; }

    public int SalesEnquiryId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;

    public int ServiceId { get; set; }

    public ServiceItem? Service { get; set; }

    [MaxLength(150)]
    public string ServiceNameSnapshot { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    [MaxLength(150)]
    public string? CategoryNameSnapshot { get; set; }

    public int SubCategoryId { get; set; }

    [MaxLength(150)]
    public string? SubCategoryNameSnapshot { get; set; }

    public int Qty { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VatPercent { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
