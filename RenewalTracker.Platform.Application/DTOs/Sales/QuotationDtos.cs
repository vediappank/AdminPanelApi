using System;
using System.Collections.Generic;

namespace RenewalTracker.Platform.Application.DTOs.Sales;

/// <summary>
/// List/detail shape for a Quotation - see PlatformQuotationsController.
/// Mirrored by Angular's Quotation model. Subtotal/VatAmount/GrandTotal are
/// always server-computed from Products/Services, never accepted from the
/// client directly.
/// </summary>
public class QuotationDto
{
    public int Id { get; set; }
    public int EnquiryId { get; set; }
    public string? Customer { get; set; }
    public string? QuoteNo { get; set; }
    public DateTime QuoteDate { get; set; }
    public DateTime? ValidTill { get; set; }
    public string Status { get; set; } = "Draft";
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public string? Notes { get; set; }
    public List<QuotationProductDto> Products { get; set; } = new();
    public DateTime CreatedDate { get; set; }
}

/// <summary>One Product (Application) on a Quotation - see QuotationProduct.cs.</summary>
public class QuotationProductDto
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public List<QuotationServiceLineDto> Services { get; set; } = new();
}

/// <summary>One manually-typed service line under a Product - see QuotationServiceLine.cs. Never sourced from the Service Catalog.</summary>
public class QuotationServiceLineDto
{
    public int Id { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public int Qty { get; set; } = 1;
    public decimal Amount { get; set; }
    public decimal VatPercent { get; set; }
    public decimal LineTotal { get; set; }
}

/// <summary>
/// Body for POST api/platform/quotations/generate - see
/// PlatformQuotationsController.GenerateQuotations(). Mode is "Combined"
/// (one Quotation covering every listed Product) or "Independent" (one
/// separate Quotation per Product).
/// </summary>
public class GenerateQuotationDto
{
    public int EnquiryId { get; set; }
    public string Mode { get; set; } = "Combined";
    public List<GenerateQuotationProductDto> Products { get; set; } = new();
}

public class GenerateQuotationProductDto
{
    public int ApplicationId { get; set; }
    public List<GenerateQuotationServiceDto> Services { get; set; } = new();
}

public class GenerateQuotationServiceDto
{
    public string ServiceName { get; set; } = string.Empty;
    public int Qty { get; set; } = 1;
    public decimal Amount { get; set; }
    public decimal VatPercent { get; set; }
}

/// <summary>Body for PUT api/platform/quotations/{id}/status.</summary>
public class UpdateQuotationStatusDto
{
    public string Status { get; set; } = "Draft";
}

/// <summary>
/// Body for PUT api/platform/quotations/{id} - see
/// PlatformQuotationsController.UpdateQuotation(). Edits an already-
/// generated quotation's pricing: which Products are on it never changes
/// here (that's fixed at Generate time, from the Enquiry's own
/// Requirements) - only each Product's own service lines (name/qty/amount/
/// VAT), plus Notes/ValidTill. Subtotal/VatAmount/GrandTotal are always
/// recomputed server-side from the submitted lines, same as Generate.
/// </summary>
public class UpdateQuotationDto
{
    public DateTime? ValidTill { get; set; }
    public string? Notes { get; set; }
    public List<UpdateQuotationProductDto> Products { get; set; } = new();
}

/// <summary>One Product's replacement service lines - Id is the existing QuotationProductId, identifying which product on the quotation these lines belong to (see QuotationProduct.cs).</summary>
public class UpdateQuotationProductDto
{
    public int Id { get; set; }
    public List<GenerateQuotationServiceDto> Services { get; set; } = new();
}
