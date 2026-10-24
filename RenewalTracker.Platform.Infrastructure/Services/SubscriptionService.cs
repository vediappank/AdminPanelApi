using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RenewalTracker.Platform.Application.DTOs.Billing;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Billing ▸ Subscriptions - see ISubscriptionService.
///
/// A term always runs StartDate .. EndDate inclusive (Monthly: 1 month, Yearly: 1 year, minus one day).
/// Starting or renewing a term updates the tenant's License:
///   StartDate / ExpiryDate  = the term (ExpiryDate is what the Business Panel login checks)
///   UserLimit / MaxDevices  = the plan's limits (when the plan sets them, i.e. &gt; 0)
///   RenewalAmount          += the term's price   (total billed; Balance = RenewalAmount - payments)
///   IsActive                = true
/// Payments for subscriptions are recorded on Billing ▸ Payments against that License as before.
///
/// Default plan: the plan marked IsDefault is given automatically to every active tenant that has never had a
/// subscription (EnsureDefaultSubscriptionsAsync - background worker, and when the Control Center opens the tenant).
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly PlatformDbContext _db;
    private readonly ITenantDatabaseService _databases;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(PlatformDbContext db, ITenantDatabaseService databases, ILogger<SubscriptionService> logger)
    {
        _db = db;
        _databases = databases;
        _logger = logger;
    }

    private static DateTime Today => DateTime.Today;

    // ------------------------------------------------------------------ plans

    public async Task<List<SubscriptionPlanDto>> GetPlansAsync(bool activeOnly, CancellationToken ct = default)
    {
        var plans = await _db.SubscriptionPlans.AsNoTracking()
            .Where(p => !activeOnly || p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.MonthlyPrice).ThenBy(p => p.PlanName)
            .ToListAsync(ct);
        var counts = await _db.TenantSubscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatuses.Active)
            .GroupBy(s => s.SubscriptionPlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PlanId, x => x.Count, ct);
        var menus = await PlanMenusAsync(null, ct);
        return plans.Select(p =>
        {
            var dto = ToDto(p, counts.GetValueOrDefault(p.SubscriptionPlanId));
            dto.MenuItemIds = menus.GetValueOrDefault(p.SubscriptionPlanId) ?? new List<int>();
            return dto;
        }).ToList();
    }

    public async Task<(SubscriptionPlanDto? Plan, string? Error)> SavePlanAsync(int? planId, SaveSubscriptionPlanRequest request, CancellationToken ct = default)
    {
        var code = (request.PlanCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = (request.PlanName ?? string.Empty).Trim();
        if (code.Length == 0 || code.Length > 50) return (null, "Plan code is required (up to 50 characters).");
        if (name.Length == 0 || name.Length > 100) return (null, "Plan name is required (up to 100 characters).");
        if (request.MonthlyPrice < 0 || request.YearlyPrice < 0) return (null, "Prices cannot be negative.");
        if (request.UserLimit < 0 || request.MaxDevices < 0) return (null, "User and device limits cannot be negative.");
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "AED" : request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3) return (null, "Currency must be a 3-letter code, e.g. AED.");
        if (await _db.SubscriptionPlans.AnyAsync(p => p.PlanCode == code && p.SubscriptionPlanId != (planId ?? 0), ct))
            return (null, $"Plan code '{code}' is already used.");

        SubscriptionPlan plan;
        if (planId is int id)
        {
            plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == id, ct)
                   ?? throw new KeyNotFoundException("Plan not found.");
            plan.ModifiedDate = DateTime.UtcNow;
        }
        else
        {
            plan = new SubscriptionPlan { CreatedDate = DateTime.UtcNow };
            _db.SubscriptionPlans.Add(plan);
        }

        plan.PlanCode = code;
        plan.PlanName = name;
        plan.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        plan.MonthlyPrice = request.MonthlyPrice;
        plan.YearlyPrice = request.YearlyPrice;
        plan.Currency = currency;
        plan.UserLimit = request.UserLimit;
        plan.MaxDevices = request.MaxDevices;
        plan.IsActive = request.IsActive;
        plan.DisplayOrder = request.DisplayOrder;
        if (request.IsDefault && !request.IsActive) return (null, "The default plan must be active.");
        if (request.IsDefault && !plan.IsDefault)
        {
            // only one default plan
            var others = await _db.SubscriptionPlans.Where(p => p.IsDefault && p.SubscriptionPlanId != plan.SubscriptionPlanId).ToListAsync(ct);
            foreach (var o in others) { o.IsDefault = false; o.ModifiedDate = DateTime.UtcNow; }
            if (others.Count > 0) await _db.SaveChangesAsync(ct);   // frees the one-default index before this plan takes it
        }
        plan.IsDefault = request.IsDefault;
        await _db.SaveChangesAsync(ct);

        if (request.MenuItemIds != null)
        {
            var valid = await _db.TenantMenuItems.Where(m => m.IsActive && request.MenuItemIds.Contains(m.MenuItemId)).Select(m => m.MenuItemId).ToListAsync(ct);
            var old = await _db.SubscriptionPlanMenus.Where(x => x.SubscriptionPlanId == plan.SubscriptionPlanId).ToListAsync(ct);
            _db.SubscriptionPlanMenus.RemoveRange(old.Where(o => !valid.Contains(o.MenuItemId)));
            foreach (var menuId in valid.Where(v => old.All(o => o.MenuItemId != v)))
                _db.SubscriptionPlanMenus.Add(new SubscriptionPlanMenu { SubscriptionPlanId = plan.SubscriptionPlanId, MenuItemId = menuId });
            await _db.SaveChangesAsync(ct);
        }

        if (request.ApplyMenusToTenants)
        {
            var tenantIds = await _db.TenantSubscriptions.Where(s => s.SubscriptionPlanId == plan.SubscriptionPlanId && s.Status == SubscriptionStatuses.Active)
                .Select(s => s.TenantId).Distinct().ToListAsync(ct);
            foreach (var tid in tenantIds) await ApplyPlanMenusAsync(tid, ct);
        }

        var active = await _db.TenantSubscriptions.CountAsync(s => s.SubscriptionPlanId == plan.SubscriptionPlanId && s.Status == SubscriptionStatuses.Active, ct);
        var dto = ToDto(plan, active);
        dto.MenuItemIds = (await PlanMenusAsync(plan.SubscriptionPlanId, ct)).GetValueOrDefault(plan.SubscriptionPlanId) ?? new List<int>();
        return (dto, null);
    }

    public async Task<(bool Found, string Message)> DeletePlanAsync(int planId, CancellationToken ct = default)
    {
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == planId, ct);
        if (plan == null) return (false, "Plan not found.");
        if (plan.IsDefault) return (true, "This is the default plan - make another plan the default first.");
        if (await _db.TenantSubscriptions.AnyAsync(s => s.SubscriptionPlanId == planId, ct))
        {
            plan.IsActive = false;
            plan.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (true, "This plan has subscriptions, so it was switched off instead of deleted.");
        }
        _db.SubscriptionPlans.Remove(plan);
        await _db.SaveChangesAsync(ct);
        return (true, "Plan deleted.");
    }

    // ------------------------------------------------------------------ reading

    public async Task<List<TenantSubscriptionDto>> ListAsync(string? status, int? tenantId, int? expiringWithinDays, CancellationToken ct = default)
    {
        var query = Query();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(s => s.Status == status.Trim());
        if (tenantId.HasValue) query = query.Where(s => s.TenantId == tenantId);
        if (expiringWithinDays is int days)
        {
            var until = Today.AddDays(Math.Max(0, days));
            query = query.Where(s => s.Status == SubscriptionStatuses.Active && s.EndDate <= until);
        }
        var rows = await query.OrderBy(s => s.Status == SubscriptionStatuses.Active ? 0 : 1).ThenBy(s => s.EndDate).ThenByDescending(s => s.TenantSubscriptionId).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TenantSubscriptionSummaryDto?> GetTenantSummaryAsync(int tenantId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (tenant == null) return null;

        // A tenant that never had a subscription is put on the default plan as soon as it is looked at.
        try { await EnsureDefaultSubscriptionsAsync(tenantId, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _logger.LogWarning(ex, "Default plan for tenant {TenantId} could not be started.", tenantId); }
        _db.ChangeTracker.Clear();

        var rows = await Query().Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.StartDate).ThenByDescending(s => s.TenantSubscriptionId)
            .ToListAsync(ct);
        var history = rows.Select(ToDto).ToList();
        var active = history.FirstOrDefault(s => s.Status == SubscriptionStatuses.Active);

        var licenseId = active?.LicenseId ?? history.FirstOrDefault(h => h.LicenseId != null)?.LicenseId;
        License? license = licenseId is int lid
            ? await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.LicenseId == lid, ct)
            : await _db.Licenses.AsNoTracking().Where(l => l.TenantId == tenantId).OrderByDescending(l => l.ExpiryDate).FirstOrDefaultAsync(ct);
        var paid = license == null ? 0m
            : await _db.Payments.AsNoTracking().Where(p => p.LicenseId == license.LicenseId).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        return new TenantSubscriptionSummaryDto
        {
            TenantId = tenantId,
            TenantName = tenant.TenantName,
            Active = active,
            History = history,
            LicenseId = license?.LicenseId,
            LicenseKey = license?.LicenseKey,
            LicenseExpiryDate = license?.ExpiryDate,
            Billed = license?.RenewalAmount ?? 0m,
            Paid = paid,
            Balance = (license?.RenewalAmount ?? 0m) - paid,
        };
    }

    // ------------------------------------------------------------------ actions

    public async Task<SubscriptionResult> CreateAsync(int tenantId, CreateTenantSubscriptionRequest request, string? by, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (tenant == null) return SubscriptionResult.Fail("Tenant not found.");
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == request.PlanId, ct);
        if (plan == null) return SubscriptionResult.Fail("Choose a plan.");
        if (!plan.IsActive) return SubscriptionResult.Fail($"Plan {plan.PlanName} is switched off.");
        var cycle = SubscriptionCycles.Normalize(request.BillingCycle);
        if (cycle == null) return SubscriptionResult.Fail("Billing cycle must be Monthly or Yearly.");
        if (request.PriceOverride < 0) return SubscriptionResult.Fail("Price cannot be negative.");
        if (request.Notes?.Length > 500) return SubscriptionResult.Fail("Notes: up to 500 characters.");

        var start = (request.StartDate ?? Today).Date;
        var price = request.PriceOverride ?? PriceFor(plan, cycle);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var current = await _db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Status == SubscriptionStatuses.Active, ct);
        if (current != null)
        {
            current.Status = SubscriptionStatuses.Superseded;
            current.AutoRenew = false;
            current.ModifiedDate = DateTime.UtcNow;
            current.CancelledDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);   // frees the one-Active-per-tenant index before the insert
        }

        var licenseResult = await ResolveLicenseAsync(tenant, request.LicenseId ?? current?.LicenseId, ct);
        if (licenseResult.Error != null)
        {
            await tx.RollbackAsync(ct);
            return SubscriptionResult.Fail(licenseResult.Error);
        }
        var license = licenseResult.License!;

        var sub = new TenantSubscription
        {
            TenantId = tenantId,
            SubscriptionPlanId = plan.SubscriptionPlanId,
            BillingCycle = cycle,
            Price = price,
            Currency = plan.Currency,
            StartDate = start,
            EndDate = SubscriptionCycles.EndOf(start, cycle),
            Status = SubscriptionStatuses.Active,
            AutoRenew = request.AutoRenew,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Clip(by, 200),
        };
        ApplyToLicense(license, plan, sub);
        sub.License = license;
        _db.TenantSubscriptions.Add(sub);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Tenant {TenantId} subscribed to {Plan} ({Cycle}) {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} for {Price} by {By}",
            tenantId, plan.PlanCode, cycle, sub.StartDate, sub.EndDate, price, by);
        var message = current == null ? "Subscription started." : "Subscription started - the previous one was replaced.";
        // a new plan (or a changed one) decides the tenant's menus when it includes any
        if (current == null || current.SubscriptionPlanId != plan.SubscriptionPlanId)
        {
            var (applied, _) = await ApplyPlanMenusAsync(tenantId, ct);
            if (applied) message += $" Business Panel menus set to the {plan.PlanName} plan.";
        }
        return new SubscriptionResult(true, message, await GetDtoAsync(sub.TenantSubscriptionId, ct));
    }

    public async Task<SubscriptionResult> RenewAsync(int subscriptionId, RenewTenantSubscriptionRequest request, string? by, CancellationToken ct = default)
    {
        var sub = await _db.TenantSubscriptions.Include(s => s.Plan).Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.TenantSubscriptionId == subscriptionId, ct);
        if (sub == null) return SubscriptionResult.Fail("Subscription not found.");
        var result = await RenewCoreAsync(sub, request.BillingCycle, request.PriceOverride, request.Notes, by, ct);
        return result;
    }

    private async Task<SubscriptionResult> RenewCoreAsync(TenantSubscription sub, string? billingCycle, decimal? priceOverride, string? notes, string? by, CancellationToken ct)
    {
        if (sub.Status is not (SubscriptionStatuses.Active or SubscriptionStatuses.Expired))
            return SubscriptionResult.Fail($"A {sub.Status.ToLowerInvariant()} subscription cannot be renewed - start a new one instead.");
        if (await _db.TenantSubscriptions.AnyAsync(s => s.TenantId == sub.TenantId && s.TenantSubscriptionId != sub.TenantSubscriptionId
                && (s.RenewedFromId == sub.TenantSubscriptionId || (s.Status == SubscriptionStatuses.Active && sub.Status == SubscriptionStatuses.Expired)), ct))
            return SubscriptionResult.Fail("This subscription was already continued - renew the latest one.");
        if (!sub.Plan.IsActive) return SubscriptionResult.Fail($"Plan {sub.Plan.PlanName} is switched off - start a new subscription on another plan.");
        var cycle = string.IsNullOrWhiteSpace(billingCycle) ? sub.BillingCycle : SubscriptionCycles.Normalize(billingCycle);
        if (cycle == null) return SubscriptionResult.Fail("Billing cycle must be Monthly or Yearly.");
        if (priceOverride < 0) return SubscriptionResult.Fail("Price cannot be negative.");

        // Continue the day after the current term; a term that already ran out restarts today.
        var start = sub.EndDate.Date.AddDays(1);
        if (sub.Status == SubscriptionStatuses.Expired && start < Today) start = Today;
        var price = priceOverride ?? PriceFor(sub.Plan, cycle);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        sub.Status = SubscriptionStatuses.Renewed;
        sub.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var licenseResult = await ResolveLicenseAsync(sub.Tenant, sub.LicenseId, ct);
        if (licenseResult.Error != null)
        {
            await tx.RollbackAsync(ct);
            return SubscriptionResult.Fail(licenseResult.Error);
        }
        var license = licenseResult.License!;

        var next = new TenantSubscription
        {
            TenantId = sub.TenantId,
            SubscriptionPlanId = sub.SubscriptionPlanId,
            BillingCycle = cycle,
            Price = price,
            Currency = sub.Plan.Currency,
            StartDate = start,
            EndDate = SubscriptionCycles.EndOf(start, cycle),
            Status = SubscriptionStatuses.Active,
            AutoRenew = sub.AutoRenew,
            RenewedFromId = sub.TenantSubscriptionId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : Clip(notes.Trim(), 500),
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Clip(by, 200),
        };
        ApplyToLicense(license, sub.Plan, next);
        next.License = license;
        _db.TenantSubscriptions.Add(next);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Tenant {TenantId} subscription {Id} renewed as {NextId} ({Cycle}) until {End:yyyy-MM-dd} for {Price} by {By}",
            sub.TenantId, sub.TenantSubscriptionId, next.TenantSubscriptionId, cycle, next.EndDate, price, by);
        return new SubscriptionResult(true, $"Renewed until {next.EndDate:dd MMM yyyy}.", await GetDtoAsync(next.TenantSubscriptionId, ct));
    }

    public async Task<SubscriptionResult> CancelAsync(int subscriptionId, CancelTenantSubscriptionRequest request, string? by, CancellationToken ct = default)
    {
        var sub = await _db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantSubscriptionId == subscriptionId, ct);
        if (sub == null) return SubscriptionResult.Fail("Subscription not found.");
        if (sub.Status != SubscriptionStatuses.Active) return SubscriptionResult.Fail("Only an active subscription can be cancelled.");

        sub.Status = SubscriptionStatuses.Cancelled;
        sub.AutoRenew = false;
        sub.CancelledDate = DateTime.UtcNow;
        sub.ModifiedDate = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Reason))
            sub.Notes = Clip(string.IsNullOrWhiteSpace(sub.Notes) ? $"Cancelled: {request.Reason.Trim()}" : $"{sub.Notes} | Cancelled: {request.Reason.Trim()}", 500);

        if (request.Immediately && sub.LicenseId is int lid)
        {
            var license = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseId == lid, ct);
            if (license != null && license.ExpiryDate >= Today)
                license.ExpiryDate = Today.AddDays(-1);   // the Business Panel login refuses from today
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Tenant {TenantId} subscription {Id} cancelled (immediately: {Now}) by {By}", sub.TenantId, sub.TenantSubscriptionId, request.Immediately, by);
        return new SubscriptionResult(true,
            request.Immediately ? "Subscription cancelled - the company can no longer sign in." : $"Subscription cancelled - access continues until {sub.EndDate:dd MMM yyyy}.",
            await GetDtoAsync(sub.TenantSubscriptionId, ct));
    }

    public async Task<SubscriptionResult> SetAutoRenewAsync(int subscriptionId, bool autoRenew, CancellationToken ct = default)
    {
        var sub = await _db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantSubscriptionId == subscriptionId, ct);
        if (sub == null) return SubscriptionResult.Fail("Subscription not found.");
        if (sub.Status != SubscriptionStatuses.Active) return SubscriptionResult.Fail("Auto-renew can only be changed on the active subscription.");
        sub.AutoRenew = autoRenew;
        sub.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new SubscriptionResult(true, autoRenew ? "Auto-renew switched on." : "Auto-renew switched off.", await GetDtoAsync(sub.TenantSubscriptionId, ct));
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var today = Today;
        var dueIds = await _db.TenantSubscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatuses.Active && s.EndDate < today)
            .Select(s => s.TenantSubscriptionId)
            .ToListAsync(ct);

        var handled = 0;
        foreach (var id in dueIds)
        {
            try
            {
                _db.ChangeTracker.Clear();
                var sub = await _db.TenantSubscriptions.Include(s => s.Plan).Include(s => s.Tenant)
                    .FirstOrDefaultAsync(s => s.TenantSubscriptionId == id, ct);
                if (sub == null || sub.Status != SubscriptionStatuses.Active) continue;

                if (sub.AutoRenew && sub.Plan.IsActive && sub.Tenant.IsActive)
                {
                    var result = await RenewCoreAsync(sub, null, null, "Renewed automatically", "auto-renew", ct);
                    if (!result.Success) _logger.LogWarning("Auto-renew of subscription {Id} failed: {Message}", id, result.Message);
                }
                else
                {
                    sub.Status = SubscriptionStatuses.Expired;
                    sub.ModifiedDate = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation("Tenant {TenantId} subscription {Id} expired on {End:yyyy-MM-dd}", sub.TenantId, id, sub.EndDate);
                }
                handled++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Subscription {Id} could not be processed - retried on the next run.", id);
            }
        }
        return handled;
    }

    public async Task<int> EnsureDefaultSubscriptionsAsync(int? tenantId = null, CancellationToken ct = default)
    {
        var plan = await _db.SubscriptionPlans.AsNoTracking()
            .Where(p => p.IsDefault && p.IsActive)
            .OrderBy(p => p.DisplayOrder).FirstOrDefaultAsync(ct);
        if (plan == null) return 0;

        var tenantIds = await _db.Tenants.AsNoTracking()
            .Where(t => t.IsActive && (tenantId == null || t.TenantId == tenantId))
            .Where(t => !_db.TenantSubscriptions.Any(s => s.TenantId == t.TenantId))
            .Select(t => t.TenantId)
            .ToListAsync(ct);

        var started = 0;
        foreach (var id in tenantIds)
        {
            try
            {
                _db.ChangeTracker.Clear();
                if (await StartDefaultAsync(id, plan.SubscriptionPlanId, ct)) started++;
            }
            catch (DbUpdateException ex)
            {
                // e.g. started at the same moment by the worker and the Control Center - the other one won
                _logger.LogInformation(ex, "Default plan for tenant {TenantId} was not started (already has one).", id);
            }
        }
        return started;
    }

    private async Task<bool> StartDefaultAsync(int tenantId, int planId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == planId, ct);
        if (tenant == null || plan == null) return false;
        if (await _db.TenantSubscriptions.AnyAsync(s => s.TenantId == tenantId, ct)) return false;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var licenseResult = await ResolveLicenseAsync(tenant, null, ct);
        var license = licenseResult.License!;
        var today = Today;

        TenantSubscription sub;
        if (license.LicenseId != 0 && license.ExpiryDate.Date >= today)
        {
            // Keep the license the tenant already has: no charge and no date change for this first term -
            // the default plan is billed from its first renewal, the day after the license expiry.
            var end = license.ExpiryDate.Date;
            sub = new TenantSubscription
            {
                TenantId = tenantId,
                SubscriptionPlanId = plan.SubscriptionPlanId,
                BillingCycle = (end - today).TotalDays > 62 ? SubscriptionCycles.Yearly : SubscriptionCycles.Monthly,
                Price = 0m,
                Currency = plan.Currency,
                StartDate = today,
                EndDate = end,
                Status = SubscriptionStatuses.Active,
                AutoRenew = true,
                Notes = $"Default plan - existing license kept until {end:dd MMM yyyy}, billed from the first renewal.",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "default plan",
                License = license,
            };
        }
        else
        {
            sub = new TenantSubscription
            {
                TenantId = tenantId,
                SubscriptionPlanId = plan.SubscriptionPlanId,
                BillingCycle = SubscriptionCycles.Monthly,
                Price = plan.MonthlyPrice,
                Currency = plan.Currency,
                StartDate = today,
                EndDate = SubscriptionCycles.EndOf(today, SubscriptionCycles.Monthly),
                Status = SubscriptionStatuses.Active,
                AutoRenew = true,
                Notes = "Default plan",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "default plan",
            };
            ApplyToLicense(license, plan, sub);
            sub.License = license;
        }
        _db.TenantSubscriptions.Add(sub);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("Tenant {TenantId} put on the default plan {Plan} ({Cycle}) {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} for {Price}",
            tenantId, plan.PlanCode, sub.BillingCycle, sub.StartDate, sub.EndDate, sub.Price);
        await ApplyPlanMenusAsync(tenantId, ct);
        return true;
    }

    // ------------------------------------------------------------------ plan menus

    public Task<List<TenantMenuItem>> GetMenuCatalogAsync(CancellationToken ct = default) =>
        _db.TenantMenuItems.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.DisplayOrder).ThenBy(m => m.DisplayName).ToListAsync(ct);

    public async Task<(bool Applied, string Message)> ApplyPlanMenusAsync(int tenantId, CancellationToken ct = default)
    {
        var planId = await _db.TenantSubscriptions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == SubscriptionStatuses.Active)
            .Select(s => (int?)s.SubscriptionPlanId).FirstOrDefaultAsync(ct);
        if (planId == null) return (false, "The tenant has no active subscription.");
        var planMenus = await _db.SubscriptionPlanMenus.AsNoTracking().Where(x => x.SubscriptionPlanId == planId).Select(x => x.MenuItemId).ToListAsync(ct);
        if (planMenus.Count == 0) return (false, "This plan does not include menus - the tenant's menus are chosen by hand.");

        // same rules as Control Center ▸ Modules and menus: the plan's menus, every system menu and each menu's parent group
        var menus = await _db.TenantMenuItems.AsNoTracking().Where(m => m.IsActive).ToListAsync(ct);
        var byId = menus.ToDictionary(m => m.MenuItemId);
        var chosen = new HashSet<int>(planMenus.Where(byId.ContainsKey));
        foreach (var m in menus.Where(m => m.IsSystemMenu)) chosen.Add(m.MenuItemId);
        foreach (var id in chosen.ToList())
        {
            var parentId = byId[id].ParentMenuItemId;
            while (parentId is int pid && byId.TryGetValue(pid, out var parent) && chosen.Add(pid))
                parentId = parent.ParentMenuItemId;
        }

        var existing = await _db.TenantMenuAssignments.Where(a => a.TenantId == tenantId).ToListAsync(ct);
        var keep = existing.Where(a => chosen.Contains(a.MenuItemId)).Select(a => a.MenuItemId).ToHashSet();
        var removed = existing.Count(a => !chosen.Contains(a.MenuItemId));
        _db.TenantMenuAssignments.RemoveRange(existing.Where(a => !chosen.Contains(a.MenuItemId)));
        var added = 0;
        foreach (var id in chosen.Where(id => !keep.Contains(id)))
        {
            _db.TenantMenuAssignments.Add(new TenantMenuAssignment { TenantId = tenantId, MenuItemId = id, CreatedDate = DateTime.UtcNow });
            added++;
        }
        await _db.SaveChangesAsync(ct);

        // the tenant's own database follows (menus + Administrator role); the data of hidden modules is kept
        if ((added > 0 || removed > 0) && await _db.TenantDatabases.AnyAsync(r => r.TenantId == tenantId, ct))
            await _databases.QueueProvisioningAsync(tenantId, null, ct: ct);
        _logger.LogInformation("Tenant {TenantId}: menus set from plan {PlanId} ({Added} added, {Removed} removed)", tenantId, planId, added, removed);
        return (true, $"Menus set from the plan: {added} added, {removed} removed.");
    }

    /// <summary>Plan id -> its menu ids (all plans, or one).</summary>
    private async Task<Dictionary<int, List<int>>> PlanMenusAsync(int? planId, CancellationToken ct)
    {
        var rows = await _db.SubscriptionPlanMenus.AsNoTracking()
            .Where(x => planId == null || x.SubscriptionPlanId == planId)
            .ToListAsync(ct);
        return rows.GroupBy(x => x.SubscriptionPlanId).ToDictionary(g => g.Key, g => g.Select(x => x.MenuItemId).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private static decimal PriceFor(SubscriptionPlan plan, string cycle) => cycle == SubscriptionCycles.Yearly ? plan.YearlyPrice : plan.MonthlyPrice;

    private static void ApplyToLicense(License license, SubscriptionPlan plan, TenantSubscription term)
    {
        // A fresh license (or one whose dates are behind the term) starts with the term; renewals keep the original start.
        if (license.LicenseId == 0 || license.StartDate == default || license.StartDate > term.StartDate || license.ExpiryDate < term.StartDate.AddDays(-1))
            license.StartDate = term.StartDate;
        license.ExpiryDate = term.EndDate;
        if (plan.UserLimit > 0) license.UserLimit = plan.UserLimit;
        if (plan.MaxDevices > 0) license.MaxDevices = plan.MaxDevices;
        license.RenewalAmount += term.Price;
        license.IsActive = true;
    }

    /// <summary>The license to keep up to date: the given one (must belong to the tenant), else the tenant's latest, else a new one.</summary>
    private async Task<(License? License, string? Error)> ResolveLicenseAsync(Tenant tenant, int? licenseId, CancellationToken ct)
    {
        if (licenseId is int id)
        {
            var given = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseId == id, ct);
            if (given != null)
                return given.TenantId == tenant.TenantId ? (given, null) : (null, "That license belongs to another tenant.");
        }

        var latest = await _db.Licenses.Where(l => l.TenantId == tenant.TenantId)
            .OrderByDescending(l => l.IsActive).ThenByDescending(l => l.ExpiryDate).FirstOrDefaultAsync(ct);
        if (latest != null) return (latest, null);

        var license = new License
        {
            TenantId = tenant.TenantId,
            LicenseKey = await NewLicenseKeyAsync(tenant.TenantCode, ct),
            IsActive = true,
        };
        _db.Licenses.Add(license);
        return (license, null);
    }

    private async Task<string> NewLicenseKeyAsync(string tenantCode, CancellationToken ct)
    {
        var prefix = new string((tenantCode ?? "TEN").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (prefix.Length == 0) prefix = "TEN";
        if (prefix.Length > 40) prefix = prefix[..40];
        for (var i = 0; i < 10; i++)
        {
            var key = $"{prefix}-SUB-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            if (!await _db.Licenses.AnyAsync(l => l.LicenseKey == key, ct)) return key;
        }
        return $"{prefix}-SUB-{Guid.NewGuid():N}"[..Math.Min(100, prefix.Length + 37)];
    }

    private IQueryable<TenantSubscription> Query() =>
        _db.TenantSubscriptions.AsNoTracking().Include(s => s.Plan).Include(s => s.Tenant).Include(s => s.License);

    private async Task<TenantSubscriptionDto?> GetDtoAsync(int id, CancellationToken ct)
    {
        var row = await Query().FirstOrDefaultAsync(s => s.TenantSubscriptionId == id, ct);
        return row == null ? null : ToDto(row);
    }

    private static TenantSubscriptionDto ToDto(TenantSubscription s) => new()
    {
        Id = s.TenantSubscriptionId,
        TenantId = s.TenantId,
        TenantCode = s.Tenant?.TenantCode,
        TenantName = s.Tenant?.TenantName,
        PlanId = s.SubscriptionPlanId,
        PlanCode = s.Plan?.PlanCode ?? string.Empty,
        PlanName = s.Plan?.PlanName ?? string.Empty,
        LicenseId = s.LicenseId,
        LicenseKey = s.License?.LicenseKey,
        BillingCycle = s.BillingCycle,
        Price = s.Price,
        Currency = s.Currency,
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        Status = s.Status,
        AutoRenew = s.AutoRenew,
        RenewedFromId = s.RenewedFromId,
        Notes = s.Notes,
        DaysRemaining = s.Status == SubscriptionStatuses.Active ? Math.Max(0, (int)(s.EndDate.Date - Today).TotalDays + 1) : 0,
        CreatedDate = s.CreatedDate,
        CreatedBy = s.CreatedBy,
        CancelledDate = s.CancelledDate,
    };

    private static SubscriptionPlanDto ToDto(SubscriptionPlan p, int activeCount) => new()
    {
        Id = p.SubscriptionPlanId,
        PlanCode = p.PlanCode,
        PlanName = p.PlanName,
        Description = p.Description,
        MonthlyPrice = p.MonthlyPrice,
        YearlyPrice = p.YearlyPrice,
        Currency = p.Currency,
        UserLimit = p.UserLimit,
        MaxDevices = p.MaxDevices,
        IsActive = p.IsActive,
        IsDefault = p.IsDefault,
        DisplayOrder = p.DisplayOrder,
        ActiveSubscriptions = activeCount,
    };

    private static string? Clip(string? value, int max) => value == null ? null : value.Length > max ? value[..max] : value;
}
