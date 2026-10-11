using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales Pipeline ▸ Invoice - one per Accepted Quotation (unique QuotationId).
/// Created from the pipeline's "Create invoice" button once the customer has
/// accepted the quotation; amounts are copied from the quotation at that
/// moment (Net / VAT / Grand total), so later quotation edits can't change an
/// issued invoice - the quotation is locked once invoiced (see
/// PlatformQuotationsController). Sale-side payments (dbo.Payments with
/// QuotationId) are only accepted once the invoice exists, and they pay down
/// this invoice's GrandTotal - see PlatformBillingController.
/// Status: Created -> Sent (email or WhatsApp). Paid / balance are computed
/// from Payments, never stored. Table dbo.SalesInvoices - see DatabaseScripts/SalesInvoices_Schema.sql.
/// </summary>
public class Invoice
{
    [Key]
    public int InvoiceId { get; set; }

    /// <summary>INV-000001 style, unique.</summary>
    [MaxLength(30)]
    public string InvoiceNumber { get; set; } = string.Empty;

    public int QuotationId { get; set; }

    public Quotation? Quotation { get; set; }

    public DateTime InvoiceDate { get; set; }

    public DateTime? DueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrandTotal { get; set; }

    /// <summary>Created or Sent.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "Created";

    public DateTime? SentDate { get; set; }

    /// <summary>Email or WhatsApp - the last channel it was sent through.</summary>
    [MaxLength(20)]
    public string? SentVia { get; set; }

    public int SentCount { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
