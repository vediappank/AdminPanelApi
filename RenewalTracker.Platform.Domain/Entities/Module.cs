using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Authoritative Module row (RenewalTrackerPlatform database) - owned by
/// RenewalTracker.PlatformApi. See Tenant.cs for why this is a separate
/// type from RenewalTracker.Domain.Entities.Module (Business's local
/// read-only mirror copy).
///
/// Merged with the old Menu table (dbo.Menus - dropped; it held no real
/// rows at merge time): a Module row is now both "a licensable feature"
/// and "a left-nav entry" for it. ParentModuleId self-references for a
/// sub-menu item (same role ParentMenuId played on Menu). This table has
/// no per-tenant relation - every row is a standard/global entry shared
/// by every tenant (TenantId, formerly carried over from Menus.TenantId,
/// was removed - see drop-modules-tenantid.sql - there is no such thing
/// as a tenant-specific custom Module row).
///
/// IsCore/DisplayOrder/CreatedDate/ModifiedDate map the real dbo.Modules
/// columns that pre-date this merge (unmapped by the old, smaller version
/// of this class). ModuleName/Description lengths (100/255) match the
/// real column sizes.
/// </summary>
public class Module
{
    [Key]
    public int ModuleId { get; set; }

    [MaxLength(50)]
    public string ModuleCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ModuleName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public bool IsCore { get; set; }

    public int DisplayOrder { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }

    // ----- Merged in from Menu (dbo.Menus, dropped) -----

    public int? ParentModuleId { get; set; }

    public Module? ParentModule { get; set; }

    [MaxLength(300)]
    public string? MenuUrl { get; set; }

    [MaxLength(100)]
    public string? Icon { get; set; }
}
