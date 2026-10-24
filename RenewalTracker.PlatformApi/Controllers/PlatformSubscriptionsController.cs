using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.Billing;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Billing ▸ Subscriptions - plans (monthly / yearly prices) and each tenant's subscription terms.
/// See SubscriptionService; tables and menu: DatabaseScripts/Subscriptions_SuperAdminPanel.sql.
///   GET    api/platform/subscriptions/plans?activeOnly=          plans
///   POST   api/platform/subscriptions/plans                      add plan
///   PUT    api/platform/subscriptions/plans/{id}                 edit plan
///   DELETE api/platform/subscriptions/plans/{id}                 delete / switch off
///   GET    api/platform/subscriptions?status=&amp;tenantId=&amp;expiringWithinDays=   all tenants' subscriptions
///   GET    api/platform/subscriptions/tenants/{tenantId}         one tenant: running term, history, balance
///   POST   api/platform/subscriptions/tenants/{tenantId}         start a subscription { planId, billingCycle, startDate, priceOverride, autoRenew }
///   GET    api/platform/subscriptions/menu-catalog               Business Panel menus a plan can include
///   POST   api/platform/subscriptions/tenants/{tenantId}/apply-plan-menus   give the tenant its plan's menus again
///   POST   api/platform/subscriptions/{id}/renew                 next term { billingCycle?, priceOverride? }
///   POST   api/platform/subscriptions/{id}/cancel                { immediately, reason }
///   PUT    api/platform/subscriptions/{id}/auto-renew            { autoRenew }
/// Every action is open to a role with the Subscriptions menu OR the Tenants screen (Control Center ▸ Subscription card).
/// </summary>
[ApiController]
[Route("api/platform/subscriptions")]
[RequirePlatformModule("SUBSCRIPTIONS", "TENANTS_LIST")]
public class PlatformSubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptions;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformSubscriptionsController(ISubscriptionService subscriptions, ICurrentPlatformUserService currentUser)
    {
        _subscriptions = subscriptions;
        _currentUser = currentUser;
    }

    private string By => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    // ----- plans

    [HttpGet("plans")]
    public async Task<ActionResult<List<SubscriptionPlanDto>>> GetPlans([FromQuery] bool activeOnly = false, CancellationToken ct = default)
        => Ok(await _subscriptions.GetPlansAsync(activeOnly, ct));

    [HttpPost("plans")]
    public async Task<ActionResult<SubscriptionPlanDto>> CreatePlan(SaveSubscriptionPlanRequest request, CancellationToken ct)
    {
        var (plan, error) = await _subscriptions.SavePlanAsync(null, request, ct);
        return plan == null ? BadRequest(new { message = error }) : Ok(plan);
    }

    [HttpPut("plans/{id:int}")]
    public async Task<ActionResult<SubscriptionPlanDto>> UpdatePlan(int id, SaveSubscriptionPlanRequest request, CancellationToken ct)
    {
        try
        {
            var (plan, error) = await _subscriptions.SavePlanAsync(id, request, ct);
            return plan == null ? BadRequest(new { message = error }) : Ok(plan);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("plans/{id:int}")]
    public async Task<IActionResult> DeletePlan(int id, CancellationToken ct)
    {
        var (found, message) = await _subscriptions.DeletePlanAsync(id, ct);
        return found ? Ok(new { message }) : NotFound(new { message });
    }

    [HttpGet("menu-catalog")]
    public async Task<ActionResult<List<RenewalTracker.Platform.Domain.Entities.TenantMenuItem>>> MenuCatalog(CancellationToken ct)
        => Ok(await _subscriptions.GetMenuCatalogAsync(ct));

    [HttpPost("tenants/{tenantId:int}/apply-plan-menus")]
    public async Task<IActionResult> ApplyPlanMenus(int tenantId, CancellationToken ct)
    {
        var (applied, message) = await _subscriptions.ApplyPlanMenusAsync(tenantId, ct);
        return applied ? Ok(new { message }) : BadRequest(new { message });
    }

    // ----- subscriptions

    [HttpGet]
    public async Task<ActionResult<List<TenantSubscriptionDto>>> List([FromQuery] string? status, [FromQuery] int? tenantId, [FromQuery] int? expiringWithinDays, CancellationToken ct)
        => Ok(await _subscriptions.ListAsync(status, tenantId, expiringWithinDays, ct));

    [HttpGet("tenants/{tenantId:int}")]
    public async Task<ActionResult<TenantSubscriptionSummaryDto>> GetTenant(int tenantId, CancellationToken ct)
    {
        var summary = await _subscriptions.GetTenantSummaryAsync(tenantId, ct);
        return summary == null ? NotFound(new { message = "Tenant not found." }) : Ok(summary);
    }

    [HttpPost("tenants/{tenantId:int}")]
    public async Task<ActionResult<SubscriptionResult>> Create(int tenantId, CreateTenantSubscriptionRequest request, CancellationToken ct)
        => ToResult(await _subscriptions.CreateAsync(tenantId, request, By, ct));

    [HttpPost("{id:int}/renew")]
    public async Task<ActionResult<SubscriptionResult>> Renew(int id, RenewTenantSubscriptionRequest? request, CancellationToken ct)
        => ToResult(await _subscriptions.RenewAsync(id, request ?? new RenewTenantSubscriptionRequest(), By, ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<SubscriptionResult>> Cancel(int id, CancelTenantSubscriptionRequest? request, CancellationToken ct)
        => ToResult(await _subscriptions.CancelAsync(id, request ?? new CancelTenantSubscriptionRequest(), By, ct));

    [HttpPut("{id:int}/auto-renew")]
    public async Task<ActionResult<SubscriptionResult>> SetAutoRenew(int id, SetAutoRenewRequest request, CancellationToken ct)
        => ToResult(await _subscriptions.SetAutoRenewAsync(id, request.AutoRenew, ct));

    private ActionResult<SubscriptionResult> ToResult(SubscriptionResult result) => result.Success ? Ok(result) : BadRequest(result);
}
