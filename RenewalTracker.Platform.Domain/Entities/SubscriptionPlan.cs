using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Billing ▸ Subscription Plans (dbo.SubscriptionPlans) - the plans a tenant can be put on, each with a
/// monthly and a yearly price. A tenant's subscription (TenantSubscription) copies the price of the cycle
/// chosen, so changing a plan's price later never changes what an existing subscription was billed.
/// UserLimit / MaxDevices are written onto the tenant's License when the subscription starts.
/// See DatabaseScripts/Subscriptions_SuperAdminPanel.sql and PlatformSubscriptionsController.
/// </summary>
public class SubscriptionPlan
{
    [Key]
    public int SubscriptionPlanId { get; set; }

    [MaxLength(50)]
    public string PlanCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string PlanName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal YearlyPrice { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "AED";

    /// <summary>Named-user seats - copied to License.UserLimit.</summary>
    public int UserLimit { get; set; }

    /// <summary>Device activations - copied to License.MaxDevices.</summary>
    public int MaxDevices { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The plan every tenant gets automatically when it has no subscription yet (at most one plan).
    /// See SubscriptionService.EnsureDefaultSubscriptionsAsync.
    /// </summary>
    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
