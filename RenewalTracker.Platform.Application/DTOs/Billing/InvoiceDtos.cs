using System;
using System.Collections.Generic;

namespace RenewalTracker.Platform.Application.DTOs.Billing;

/// <summary>One invoice per Accepted quotation - see Invoice.cs / PlatformInvoicesController.</summary>
public class InvoiceDto
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public int QuotationId { get; set; }
    public string? QuoteNo { get; set; }
    public int EnquiryId { get; set; }
    public string? Customer { get; set; }
    public DateTime InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrandTotal { get; set; }
    /// <summary>Sum of Payments against the invoice's quotation (computed).</summary>
    public decimal Paid { get; set; }
    public decimal Balance { get; set; }
    /// <summary>Unpaid / Partially paid / Paid (computed from Payments).</summary>
    public string PaymentStatus { get; set; } = "Unpaid";
    /// <summary>Created or Sent.</summary>
    public string Status { get; set; } = "Created";
    public DateTime? SentDate { get; set; }
    public string? SentVia { get; set; }
    public int SentCount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class CreateInvoiceDto
{
    /// <summary>Defaults to today.</summary>
    public DateTime? InvoiceDate { get; set; }
    /// <summary>Defaults to invoice date + 7 days.</summary>
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Everything the "Send invoice" dialog needs: contact details and ready-to-edit texts.</summary>
public class InvoiceSendDetailsDto
{
    public InvoiceDto Invoice { get; set; } = new();
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerMobile { get; set; } = string.Empty;
    public string DefaultSubject { get; set; } = string.Empty;
    public string DefaultBody { get; set; } = string.Empty;
    public string DefaultWhatsAppMessage { get; set; } = string.Empty;
    /// <summary>False when the Smtp section in appsettings.json is not filled in - the dialog then explains why Email is unavailable.</summary>
    public bool EmailConfigured { get; set; }
}

public class InvoiceEmailRequest
{
    public string To { get; set; } = string.Empty;
    public List<string>? Cc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

public class InvoiceWhatsAppRequest
{
    public string Mobile { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class InvoiceSendResultDto
{
    public string Message { get; set; } = string.Empty;
    /// <summary>WhatsApp only: the wa.me link the browser opens.</summary>
    public string? WhatsAppUrl { get; set; }
    public InvoiceDto Invoice { get; set; } = new();
}
