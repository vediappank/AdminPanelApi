using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Billing ▸ Payment - one installment against EITHER a Quotation (sale-side,
/// paying down Quotation.GrandTotal on the way to Convert to Tenant) OR a
/// License (renewal-side, paying down License.RenewalAmount to extend
/// ExpiryDate) - never both, never neither. Enforced at the app layer
/// (PlatformBillingController.RecordQuotationPayment/RecordLicensePayment -
/// each only ever sets one of the two) and at the database layer
/// (CK_Payments_ExactlyOneTarget - see PlatformDbContext.Platform.cs /
/// alter-payments-billing-model.sql). Platform-owned (Bliss Point Group's
/// own billing, not a tenant's data).
///
/// TenantId is a denormalized convenience FK, nullable, filled in as soon as
/// it's known:
///   - License payments: always known (License.TenantId), set immediately.
///   - Quotation payments: only known once the originating Lead has been
///     converted to a Tenant - null until then. Resolving it doesn't rely on
///     Tenant.LeadId being set (that column is null for tenants provisioned
///     without a Lead), so this is the only reliable way to query "every
///     payment for tenant X" in one shot instead of joining Quotation ->
///     Enquiry -> SalesLeads -> Tenants, which simply has no row to find for
///     those tenants.
///
/// Outstanding balance is always COMPUTED, never stored on this row:
///   Quotation.GrandTotal  - SUM(Payment.Amount WHERE QuotationId = X)
///   License.RenewalAmount - SUM(Payment.Amount WHERE LicenseId = X)
/// See PlatformBillingController.GetQuotationBalance/GetLicenseBalance.
///
/// Replaces the old free-text Customer column (no FK, previously demo data
/// from MockDataService.payments) - see alter-payments-billing-model.sql.
/// </summary>
public class Payment
{
    [Key]
    public int PaymentId { get; set; }

    public int? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public int? QuotationId { get; set; }

    public Quotation? Quotation { get; set; }

    public int? LicenseId { get; set; }

    public License? License { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>
    /// The real dbo.Payments column is PaymentMethodId (a plain app-owned
    /// int, no FK to any lookup table - confirmed against the real CREATE
    /// TABLE script), NOT a free-text Method column - there is no such
    /// column on the table at all. Mapped by convention (property name
    /// matches the column exactly - see PlatformDbContext.Platform.cs).
    /// </summary>
    public int PaymentMethodId { get; set; } = PaymentMethodCodes.ToId(null);

    /// <summary>
    /// Thin pass-through over PaymentMethodId via PaymentMethodCodes, so the
    /// rest of the app (PlatformBillingController, PaymentDto, the Angular
    /// UI) keeps working with the human-readable name ("Bank Transfer") and
    /// never has to know the numeric code exists. [NotMapped] - EF must
    /// never try to write/read a literal "Method" column, since the real
    /// table has none (this is what regressed and broke GET
    /// /api/platform/billing/payments with "Invalid column name 'Method'" -
    /// restored here, kept in sync with PlatformDbContext.Platform.cs's own
    /// comment on this same property).
    /// </summary>
    [NotMapped]
    public string Method
    {
        get => PaymentMethodCodes.ToName(PaymentMethodId);
        set => PaymentMethodId = PaymentMethodCodes.ToId(value);
    }

    /// <summary>Completed / Failed / Refunded - a recorded row defaults to Completed; it represents money already received, not a pending promise.</summary>
    [MaxLength(30)]
    public string Status { get; set; } = "Completed";

    /// <summary>Mapped to the real table's PaymentDate column, not a literal "Date" column - see PlatformDbContext.Platform.cs.</summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// The billed-to name at the time this payment was recorded - real NOT
    /// NULL column on the legacy dbo.Payments table (it pre-dates this
    /// project's Tenant/Quotation/License model), so PlatformBillingController
    /// always sets it: the resolved Tenant's name once known, falling back to
    /// the originating Enquiry's CustomerNameSnapshot for a sale-side payment
    /// collected before Lead-to-Tenant conversion (License payments always
    /// have a Tenant already, so they never need the fallback). Snapshot by
    /// design, same convention as Enquiry.CustomerNameSnapshot - it records
    /// who was billed at the time, not a live join, so it stays correct even
    /// if the Tenant/Enquiry name changes later.
    /// </summary>
    [MaxLength(150)]
    public string CustomerNameSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// TenantCode + the Quotation/License's own numeric id (4-digit) + a
    /// 4-digit sequence, e.g. "KSVTECH00010001" - that SPECIFIC quotation/
    /// license's own running invoice count (1st, 2nd, ...), never the
    /// tenant's overall count and never a platform-wide counter - a tenant
    /// with several quotations gets a fresh 0001 on each one rather than
    /// sharing one number space. Every payment gets its OWN unique number -
    /// two payments against the same quotation still each mint a fresh one,
    /// never reused - see PlatformBillingController.ResolveInvoiceNoAsync/
    /// SaveWithUniqueInvoiceNoAsync (the latter is what actually guarantees
    /// no two payments ever collide, backed by a unique index - see
    /// alter-payments-invoiceno-unique-index.sql). Null for payments
    /// recorded before this column existed, and for the rare pre-conversion
    /// sale-side deposit where TenantId isn't resolved yet (nothing to
    /// number against) - see alter-payments-invoice-no.sql.
    /// </summary>
    [MaxLength(60)]
    public string? InvoiceNo { get; set; }
}

/// <summary>
/// dbo.Payments.PaymentMethodId has no FK to a lookup table, so this is the
/// one and only place the 4 known methods and their codes live - both
/// directions (name -> id when saving, id -> name when reading back) go
/// through here, never a duplicated switch elsewhere. An unrecognized name
/// or id falls back to Bank Transfer (id 1) rather than throwing, since a
/// payment method is never worth failing the whole request over.
/// </summary>
public static class PaymentMethodCodes
{
    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bank Transfer"] = 1,
        ["Cash"] = 2,
        ["Cheque"] = 3,
        ["Credit Card"] = 4,
    };

    private static readonly Dictionary<int, string> ByCode = new()
    {
        [1] = "Bank Transfer",
        [2] = "Cash",
        [3] = "Cheque",
        [4] = "Credit Card",
    };

    public static int ToId(string? method) =>
        method != null && ByName.TryGetValue(method, out var id) ? id : 1;

    public static string ToName(int id) =>
        ByCode.TryGetValue(id, out var name) ? name : "Bank Transfer";
}
