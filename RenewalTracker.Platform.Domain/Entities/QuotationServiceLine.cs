using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One manually-typed service line under a QuotationProduct. Deliberately
/// NOT linked to the Service Catalog (dbo.Services/ServiceItem) - that
/// table serves a different purpose (Enquiry Requirements - see
/// EnquiryRequirement.cs); a quotation's service lines are free text typed
/// against the picked Product, with no catalog backing at all.
///
/// New table (dbo.QuotationServiceLines) - see create-quotations.sql.
/// </summary>
public class QuotationServiceLine
{
    [Key]
    public int QuotationServiceLineId { get; set; }

    public int QuotationProductId { get; set; }

    public QuotationProduct QuotationProduct { get; set; } = null!;

    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    public int Qty { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VatPercent { get; set; }

    public DateTime CreatedDate { get; set; }
}
