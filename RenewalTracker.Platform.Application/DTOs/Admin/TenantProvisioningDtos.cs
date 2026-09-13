namespace RenewalTracker.Platform.Application.DTOs.Admin;

/// <summary>
/// Input for standing up a brand-new tenant end-to-end: the tenant record
/// itself, its baseline configuration, every currently-active Module
/// enabled for it, the standard Role set, that tenant's SuperAdmin role
/// granted every Permission and every Menu, and one initial admin User tied
/// to that role. See IPlatformTenantProvisioningService for why this can't
/// be done through the generic CRUD screens.
/// </summary>
public class ProvisionTenantDto
{
    public string TenantCode { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    // Baseline TenantConfiguration - optional, sensible defaults applied server-side when omitted.
    public string? DateFormat { get; set; }
    public string? TimeZone { get; set; }
    public string? Currency { get; set; }

    // The tenant's first user - created with the new tenant's SuperAdmin role.
    public string AdminFirstName { get; set; } = string.Empty;
    public string? AdminLastName { get; set; }
    public string AdminEmail { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// Which TenantModuleCatalog entries (by TenantModuleCatalogId, from GET
    /// api/platform/tenant-module-catalog) to enable for this tenant.
    /// Null/omitted keeps the old default of every currently-active catalog
    /// module - this only takes effect once the Business API's own
    /// ProvisionTenantBusinessSideDto copy also reads this field instead of
    /// unconditionally enabling everything active; until that side is
    /// updated, whatever is sent here is accepted but ignored.
    /// </summary>
    public List<int>? ModuleIds { get; set; }

    /// <summary>
    /// Set only when this provisioning was launched from the Sales ▸ Leads
    /// screen's own "Convert to Tenant" action - the SalesLeads.LeadId being
    /// converted. Null for an ordinary Tenants ▸ + Provision tenant. Stamped
    /// onto the new Tenant row (Tenant.LeadId) so that Lead can be located
    /// and locked as converted - see PlatformTenantProvisioningService.
    /// </summary>
    public int? LeadId { get; set; }
}

public class ProvisionTenantResultDto
{
    public int TenantId { get; set; }
    public string TenantCode { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    public int AdminUserId { get; set; }
    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>Every role name seeded for this tenant (RoleName), for display/confirmation.</summary>
    public List<string> RolesCreated { get; set; } = new();

    /// <summary>How many Modules were enabled for this tenant.</summary>
    public int ModulesEnabled { get; set; }
}

/// <summary>
/// The wire contract for the Business-side half of provisioning, sent over
/// HTTP (POST api/internal/tenant-provisioning, see
/// RequireInternalServiceKeyAttribute) once this API has already created
/// the bare Tenant row on its own side. Shape-identical to
/// RenewalTracker.Application.DTOs.Admin.ProvisionTenantBusinessSideDto
/// (the Business API's own copy, which actually deserializes this request) -
/// kept as two separate types, matched purely by JSON shape, so neither API
/// project references the other's code.
/// </summary>
public class ProvisionTenantBusinessSideDto
{
    public int TenantId { get; set; }

    // Baseline TenantConfiguration - optional, sensible defaults applied server-side when omitted.
    public string? DateFormat { get; set; }
    public string? TimeZone { get; set; }
    public string? Currency { get; set; }

    // The tenant's first user - created with the new tenant's SuperAdmin role.
    public string AdminFirstName { get; set; } = string.Empty;
    public string? AdminLastName { get; set; }
    public string AdminEmail { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>Mirrors ProvisionTenantDto.ModuleIds above - add the same field
    /// to the Business API's own ProvisionTenantBusinessSideDto copy and use
    /// it there in place of "enable every active module" for this to take
    /// effect end-to-end.</summary>
    public List<int>? ModuleIds { get; set; }
}
