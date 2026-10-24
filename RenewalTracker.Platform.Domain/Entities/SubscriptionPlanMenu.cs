namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One Business Panel menu (dbo.TenantMenuItems) included in a subscription plan - dbo.SubscriptionPlanMenus.
/// A plan with menus decides a tenant's menus (dbo.TenantMenuAssignments) when the tenant starts or changes to it;
/// a plan without any keeps the menus chosen by hand. See SubscriptionService.ApplyPlanMenusAsync.
/// </summary>
public class SubscriptionPlanMenu
{
    public int SubscriptionPlanId { get; set; }

    public int MenuItemId { get; set; }
}
