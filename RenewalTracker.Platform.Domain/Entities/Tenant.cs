using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Authoritative Tenant row (RenewalTrackerPlatform database) - owned by
/// RenewalTracker.PlatformApi. RenewalTracker.Api (Business) keeps its own,
/// separate copy of this class (RenewalTracker.Domain.Entities.Tenant) for
/// its local read-only mirror, kept in sync by CatalogSyncService. The two
/// are intentionally two different types now, not one shared class, so
/// neither API project references the other's code - see PlatformDbContext.
///
/// Shape matches the production RenewalTrackerPlatform1 schema: a plain
/// IsActive enable/disable flag plus contact details, not a provisioning
/// state machine. See PlatformTenantProvisioningService for how "still
/// mid-provisioning" is now handled without a persisted status column.
/// </summary>
public class Tenant
{
    [Key]
    public int TenantId { get; set; }

    [MaxLength(50)]
    public string TenantCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string TenantName { get; set; } = string.Empty;

    [MaxLength(150)]
    public string? ContactName { get; set; }

    [MaxLength(150)]
    public string? ContactEmail { get; set; }

    [MaxLength(30)]
    public string? ContactMobile { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }

    /// <summary>
    /// dbo.SalesLeads.LeadId this tenant was converted from, when it was
    /// provisioned via the Sales ▸ Leads screen's own "Convert to Tenant"
    /// action (see PlatformTenantProvisioningService.ProvisionAsync and
    /// alter-tenants-add-leadid.sql) - null for every tenant provisioned the
    /// ordinary way, from Tenants ▸ + Provision tenant directly. Purely
    /// informational/for locking that Lead row once converted; nothing else
    /// in provisioning depends on it.
    /// </summary>
    public int? LeadId { get; set; }
}
