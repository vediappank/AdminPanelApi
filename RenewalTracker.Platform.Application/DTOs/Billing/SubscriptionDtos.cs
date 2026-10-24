using System;
using System.Collections.Generic;

namespace RenewalTracker.Platform.Application.DTOs.Billing;

/// <summary>Billing ▸ Subscription Plans row. Mirrored by Angular's SubscriptionPlan model.</summary>
public class SubscriptionPlanDto
{
    public int Id { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal YearlyPrice { get; set; }
    public string Currency { get; set; } = "AED";
    public int UserLimit { get; set; }
    public int MaxDevices { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Given automatically to every tenant without a subscription.</summary>
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>Business Panel menus (TenantMenuItems ids) the plan includes; empty = menus are chosen by hand per tenant.</summary>
    public List<int> MenuItemIds { get; set; } = new();
    /// <summary>Tenants currently on this plan (Active subscriptions).</summary>
    public int ActiveSubscriptions { get; set; }
}

/// <summary>Body for POST/PUT api/platform/subscriptions/plans.</summary>
public class SaveSubscriptionPlanRequest
{
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal YearlyPrice { get; set; }
    public string? Currency { get; set; }
    public int UserLimit { get; set; }
    public int MaxDevices { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Make this the default plan (the previous default stops being one).</summary>
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>Menus the plan includes; null = leave unchanged, empty = the plan does not decide menus.</summary>
    public List<int>? MenuItemIds { get; set; }
    /// <summary>Also give the plan's menus now to every tenant currently on it.</summary>
    public bool ApplyMenusToTenants { get; set; }
}

/// <summary>One subscription term. Mirrored by Angular's TenantSubscription model.</summary>
public class TenantSubscriptionDto
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string? TenantCode { get; set; }
    public string? TenantName { get; set; }
    public int PlanId { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public int? LicenseId { get; set; }
    public string? LicenseKey { get; set; }
    /// <summary>"Monthly" / "Yearly".</summary>
    public string BillingCycle { get; set; } = "Monthly";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "AED";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    /// <summary>Active / Renewed / Superseded / Cancelled / Expired.</summary>
    public string Status { get; set; } = "Active";
    public bool AutoRenew { get; set; }
    public int? RenewedFromId { get; set; }
    public string? Notes { get; set; }
    /// <summary>Days left in the term (Active only; 0 once the last day has passed).</summary>
    public int DaysRemaining { get; set; }
    public DateTime CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CancelledDate { get; set; }
}

/// <summary>Everything the tenant's Subscription card shows: running term, history and what is owed.</summary>
public class TenantSubscriptionSummaryDto
{
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public TenantSubscriptionDto? Active { get; set; }
    public List<TenantSubscriptionDto> History { get; set; } = new();
    /// <summary>License the subscription keeps up to date (payments are recorded against it).</summary>
    public int? LicenseId { get; set; }
    public string? LicenseKey { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    /// <summary>License.RenewalAmount - total billed on that license.</summary>
    public decimal Billed { get; set; }
    public decimal Paid { get; set; }
    public decimal Balance { get; set; }
}

/// <summary>Body for POST api/platform/subscriptions/tenants/{tenantId} - put the tenant on a plan.</summary>
public class CreateTenantSubscriptionRequest
{
    public int PlanId { get; set; }
    /// <summary>"Monthly" or "Yearly".</summary>
    public string BillingCycle { get; set; } = "Monthly";
    /// <summary>First day of the term - default today.</summary>
    public DateTime? StartDate { get; set; }
    /// <summary>Discount / custom deal - default the plan's price for the cycle.</summary>
    public decimal? PriceOverride { get; set; }
    public bool AutoRenew { get; set; }
    /// <summary>Keep this License up to date - default the tenant's current one (created when it has none).</summary>
    public int? LicenseId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Body for POST api/platform/subscriptions/{id}/renew - start the next term.</summary>
public class RenewTenantSubscriptionRequest
{
    /// <summary>Change the cycle for the next term - default the same cycle.</summary>
    public string? BillingCycle { get; set; }
    /// <summary>Default the plan's current price for the cycle.</summary>
    public decimal? PriceOverride { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Body for POST api/platform/subscriptions/{id}/cancel.</summary>
public class CancelTenantSubscriptionRequest
{
    /// <summary>True = the company is locked out from today (License ends yesterday); false = it keeps access until the term's end date.</summary>
    public bool Immediately { get; set; }
    public string? Reason { get; set; }
}

public class SetAutoRenewRequest
{
    public bool AutoRenew { get; set; }
}

/// <summary>Result of a subscription action - Success false carries the message for the user.</summary>
public record SubscriptionResult(bool Success, string? Message, TenantSubscriptionDto? Subscription = null)
{
    public static SubscriptionResult Fail(string message) => new(false, message);
}
