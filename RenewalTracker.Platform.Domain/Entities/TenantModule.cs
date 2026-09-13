using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Tenant ▸ Tenant Modules - the mapping between a Tenant and a
/// TenantModuleCatalog entry (which modules are enabled for which tenant).
/// Matches the real dbo.TenantModules table's own IsEnabled flag (default
/// 1) plus nullable EnabledDate/DisabledDate/ModifiedDate - toggling flips
/// IsEnabled in place on the row and stamps the matching date, rather than
/// inserting/deleting the row.
///
/// Previously pointed at dbo.Modules (ModuleId/Module) - the same table
/// this Admin panel's own nav/permissions tree lives in. Split into its own
/// dedicated TenantModuleCatalog table (see that entity) so tenant
/// assignment no longer shares a table with the Admin nav; the historical
/// ModuleId column still exists on dbo.TenantModules for old rows (nullable
/// now) but the app no longer reads or writes it - see
/// tenant-module-catalog-split.sql.
/// </summary>
public class TenantModule
{
    [Key]
    public int TenantModuleId { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int? TenantModuleCatalogId { get; set; }

    public TenantModuleCatalog? TenantModuleCatalog { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? EnabledDate { get; set; }

    public DateTime? DisabledDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
