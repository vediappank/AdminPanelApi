using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One subscription term of a tenant (dbo.TenantSubscriptions) - plan, Monthly / Yearly, price and the
/// period it covers. Every term is its own row, so the list is the tenant's subscription history:
///   Active      the running term (at most one per tenant - filtered unique index)
///   Renewed     finished and continued by the next term (renewal row points back via RenewedFromId)
///   Superseded  replaced early by a new subscription (plan / cycle change)
///   Cancelled   stopped by an operator
///   Expired     ended without renewal
///
/// The subscription drives the tenant's License (LicenseId): starting or renewing a term sets
/// License.StartDate / ExpiryDate to the term, copies the plan's UserLimit / MaxDevices and adds the
/// term's price to License.RenewalAmount (the amount billed). Payments are still recorded against that
/// License (Billing ▸ Payments), so Balance = RenewalAmount - payments, and the Business Panel login keeps
/// using License.ExpiryDate to let the company in.
/// </summary>
public class TenantSubscription
{
    [Key]
    public int TenantSubscriptionId { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int SubscriptionPlanId { get; set; }

    public SubscriptionPlan Plan { get; set; } = null!;

    /// <summary>The License this subscription keeps up to date.</summary>
    public int? LicenseId { get; set; }

    public License? License { get; set; }

    /// <summary>"Monthly" or "Yearly" (see SubscriptionCycles).</summary>
    [MaxLength(10)]
    public string BillingCycle { get; set; } = SubscriptionCycles.Monthly;

    /// <summary>Price of this term (the plan's price for the cycle, or the operator's override).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "AED";

    /// <summary>First day of the term.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Last day of the term (inclusive) - License.ExpiryDate is set to this.</summary>
    public DateTime EndDate { get; set; }

    /// <summary>Active / Renewed / Superseded / Cancelled / Expired (see SubscriptionStatuses).</summary>
    [MaxLength(20)]
    public string Status { get; set; } = SubscriptionStatuses.Active;

    /// <summary>True = the daily job starts the next term automatically when this one ends.</summary>
    public bool AutoRenew { get; set; }

    /// <summary>The term this one continues (renewals only).</summary>
    public int? RenewedFromId { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; }

    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public DateTime? CancelledDate { get; set; }
}

public static class SubscriptionCycles
{
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    /// <summary>"Monthly" / "Yearly" (any case) - null when it is neither.</summary>
    public static string? Normalize(string? value) =>
        string.Equals(value?.Trim(), Monthly, StringComparison.OrdinalIgnoreCase) ? Monthly
        : string.Equals(value?.Trim(), Yearly, StringComparison.OrdinalIgnoreCase) ? Yearly
        : null;

    /// <summary>Last day of a term that starts on <paramref name="start"/>: 1 month / 1 year later, minus one day.</summary>
    public static DateTime EndOf(DateTime start, string cycle) =>
        (cycle == Yearly ? start.Date.AddYears(1) : start.Date.AddMonths(1)).AddDays(-1);
}

public static class SubscriptionStatuses
{
    public const string Active = "Active";
    public const string Renewed = "Renewed";
    public const string Superseded = "Superseded";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}
