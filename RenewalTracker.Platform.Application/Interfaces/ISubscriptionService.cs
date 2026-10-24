using RenewalTracker.Platform.Application.DTOs.Billing;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Billing ▸ Subscriptions - plans with monthly / yearly prices and each tenant's subscription terms.
/// Starting or renewing a term keeps the tenant's License in step (dates, seat / device limits, amount billed),
/// so payments (Billing ▸ Payments, against that License) and the Business Panel login check keep working.
/// See SubscriptionService.
/// </summary>
public interface ISubscriptionService
{
    Task<List<SubscriptionPlanDto>> GetPlansAsync(bool activeOnly, CancellationToken ct = default);
    Task<(SubscriptionPlanDto? Plan, string? Error)> SavePlanAsync(int? planId, SaveSubscriptionPlanRequest request, CancellationToken ct = default);
    /// <summary>Deletes an unused plan; a plan that was ever used is switched off instead.</summary>
    Task<(bool Found, string Message)> DeletePlanAsync(int planId, CancellationToken ct = default);

    Task<List<TenantSubscriptionDto>> ListAsync(string? status, int? tenantId, int? expiringWithinDays, CancellationToken ct = default);
    Task<TenantSubscriptionSummaryDto?> GetTenantSummaryAsync(int tenantId, CancellationToken ct = default);

    Task<SubscriptionResult> CreateAsync(int tenantId, CreateTenantSubscriptionRequest request, string? by, CancellationToken ct = default);
    Task<SubscriptionResult> RenewAsync(int subscriptionId, RenewTenantSubscriptionRequest request, string? by, CancellationToken ct = default);
    Task<SubscriptionResult> CancelAsync(int subscriptionId, CancelTenantSubscriptionRequest request, string? by, CancellationToken ct = default);
    Task<SubscriptionResult> SetAutoRenewAsync(int subscriptionId, bool autoRenew, CancellationToken ct = default);

    /// <summary>Daily job: Active terms past their end date are renewed (AutoRenew) or marked Expired. Returns how many were handled.</summary>
    Task<int> ProcessDueAsync(CancellationToken ct = default);

    /// <summary>
    /// Puts every active tenant that has never had a subscription on the default plan (Billing ▸ Subscriptions,
    /// plan marked Default). A tenant whose license is still valid keeps it: the first term runs until the license
    /// expiry at no charge and the plan is billed from the first renewal. Returns how many tenants were started.
    /// </summary>
    Task<int> EnsureDefaultSubscriptionsAsync(int? tenantId = null, CancellationToken ct = default);

    /// <summary>Active Business Panel menus, for choosing what a plan includes.</summary>
    Task<List<RenewalTracker.Platform.Domain.Entities.TenantMenuItem>> GetMenuCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Gives the tenant exactly the menus of its active subscription's plan (plus system menus and parent groups) and
    /// queues the update of its database. Returns false (nothing changed) when the plan includes no menus.
    /// </summary>
    Task<(bool Applied, string Message)> ApplyPlanMenusAsync(int tenantId, CancellationToken ct = default);
}
