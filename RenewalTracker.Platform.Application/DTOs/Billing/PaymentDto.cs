using System;

namespace RenewalTracker.Platform.Application.DTOs.Billing;

/// <summary>
/// Mirrored by Angular's Payment model - see PlatformBillingController.
/// TenantName/QuoteNo/LicenseKey are resolved display snapshots (looked up
/// server-side, not stored columns) - only one of QuoteNo/LicenseKey is ever
/// non-null on a given row, matching QuotationId/LicenseId. Never posted
/// back on create - see RecordQuotationPaymentDto/RecordLicensePaymentDto.
/// </summary>
public class PaymentDto
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    public int? QuotationId { get; set; }
    public string? QuoteNo { get; set; }
    public int? LicenseId { get; set; }
    public string? LicenseKey { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Bank Transfer";
    public string Status { get; set; } = "Completed";
    public DateTime Date { get; set; }

    /// <summary>TenantCode + 4-digit sequence, shared by every payment for this tenant on the same day - see Payment.cs / PlatformBillingController.ResolveInvoiceNoAsync. Null for pre-invoice-numbering rows.</summary>
    public string? InvoiceNo { get; set; }
}

/// <summary>
/// Body for POST api/platform/billing/payments/quotation/{quotationId} - one
/// sale-side installment. TenantId/QuotationId are never accepted from the
/// client: TenantId is resolved server-side (see PlatformBillingController.
/// ResolveTenantIdForQuotationAsync), QuotationId comes from the route.
/// </summary>
public class RecordQuotationPaymentDto
{
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Bank Transfer";
}

/// <summary>
/// Body for POST api/platform/billing/payments/license/{licenseId} - one
/// renewal-side installment. TenantId/LicenseId are never accepted from the
/// client: TenantId comes straight from License.TenantId, LicenseId from
/// the route.
/// </summary>
public class RecordLicensePaymentDto
{
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Bank Transfer";
}

/// <summary>
/// Total/paid/balance for one Quotation or License - see
/// PlatformBillingController.GetQuotationBalance/GetLicenseBalance.
/// IsFullyPaid requires at least one payment - a License seeded with
/// RenewalAmount = 0 and no payments is not "fully paid", it's untracked.
/// </summary>
public class BalanceDto
{
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public decimal Balance { get; set; }
    public bool IsFullyPaid { get; set; }
}
