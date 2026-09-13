using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// The catalog of modules a Tenant can be assigned (via TenantModule) -
/// deliberately separate from dbo.Modules, which remains this Admin
/// panel's own nav/permissions tree only - see tenant-module-catalog-split.sql
/// for the migration that split these two concerns apart (they used to
/// share dbo.Modules) and PlatformTenantModuleCatalogController for CRUD.
///
/// Originally details-only (name/description/active), it now also carries
/// its own nav shape - ParentTenantModuleCatalogId/MenuUrl/DisplayOrder -
/// mirroring the ParentModuleId/MenuUrl/DisplayOrder pattern Module.cs uses
/// for THIS Admin panel's sidebar (see tenant-module-catalog-nav-fields.sql).
/// The two hierarchies stay independent: this one describes the Tenant-side
/// (Business/Tracker) application's own menu - a catalog entry with
/// children is a submenu whose items each carry their own Menu Name
/// (ModuleName)/Path (MenuUrl)/OrderBy (DisplayOrder); a childless entry
/// with its own MenuUrl redirects directly. A tenant is still assigned
/// individual catalog rows via TenantModule regardless of depth - nothing
/// about assignment itself changed.
/// </summary>
public class TenantModuleCatalog
{
    [Key]
    public int TenantModuleCatalogId { get; set; }

    [MaxLength(50)]
    public string ModuleCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ModuleName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }

    // ----- Nav shape (added by tenant-module-catalog-nav-fields.sql) -----

    public int? ParentTenantModuleCatalogId { get; set; }

    public TenantModuleCatalog? ParentTenantModuleCatalog { get; set; }

    [MaxLength(300)]
    public string? MenuUrl { get; set; }

    public int DisplayOrder { get; set; }

    // ----- Field parity with Module.cs (added by tenant-module-catalog-icon-core.sql) -----

    [MaxLength(100)]
    public string? Icon { get; set; }

    /// <summary>Mirrors Module.IsCore's name/shape for consistency with the Module screen.
    /// Not yet read anywhere - tenant provisioning does not auto-assign any catalog module
    /// today (a deliberate choice, not a gap - see the provisioning discussion), so this is
    /// just a flag on the catalog entry for now, free for future use.</summary>
    public bool IsCore { get; set; }
}
