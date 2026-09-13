namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>
/// One row of the Tenant ▸ Tenant Modules grid: a module from the
/// TenantModuleCatalog plus whether it is currently enabled for the tenant
/// named in the request URL. See PlatformTenantModulesController.
/// </summary>
public class TenantModuleStatusDto
{
    public int TenantModuleCatalogId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    /// <summary>Set when this catalog entry is a child of another (a submenu item) - lets
    /// the Tenant Module screen's checklist group entries into the same expand/collapse
    /// tree the Module Catalog screen itself edits. Null for a top-level entry.</summary>
    public int? ParentTenantModuleCatalogId { get; set; }
}

/// <summary>
/// One row of the flat Tenant Modules CRUD grid (every Tenant<->TenantModuleCatalog
/// assignment across every tenant, not just one). See
/// PlatformTenantModuleAssignmentsController.
/// </summary>
public class TenantModuleRowDto
{
    public int TenantModuleId { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int TenantModuleCatalogId { get; set; }
    public string ModuleName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime? EnabledDate { get; set; }
    public DateTime? DisabledDate { get; set; }
}

public class CreateTenantModuleDto
{
    public int TenantId { get; set; }
    public int TenantModuleCatalogId { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>TenantId/TenantModuleCatalogId are included (not just IsEnabled) so an
/// existing assignment row can be reassigned to a different tenant/module,
/// not only toggled on or off - see PlatformTenantModuleAssignmentsController.Update().</summary>
public class UpdateTenantModuleDto
{
    public int TenantId { get; set; }
    public int TenantModuleCatalogId { get; set; }
    public bool IsEnabled { get; set; }
}

/// <summary>One row of the module catalog itself. See PlatformTenantModuleCatalogController.
/// ParentTenantModuleCatalogId/MenuUrl/DisplayOrder describe the Tenant-side
/// (Business/Tracker) app's own menu shape for this entry - a childless row
/// with its own MenuUrl redirects directly; a row with children is a
/// submenu, each child carrying its own Menu Name (ModuleName)/Path
/// (MenuUrl)/OrderBy (DisplayOrder). See TenantModuleCatalog.cs.</summary>
public class TenantModuleCatalogDto
{
    public int TenantModuleCatalogId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int? ParentTenantModuleCatalogId { get; set; }
    public string? MenuUrl { get; set; }
    public int DisplayOrder { get; set; }
    public string? Icon { get; set; }
    public bool IsCore { get; set; }
}

/// <summary>Name/description plus the nav shape - ModuleCode is derived and kept unique server-side, never typed by hand.</summary>
public class CreateTenantModuleCatalogDto
{
    public string ModuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ParentTenantModuleCatalogId { get; set; }
    public string? MenuUrl { get; set; }
    public int DisplayOrder { get; set; }
    public string? Icon { get; set; }
    public bool IsCore { get; set; }
}

public class UpdateTenantModuleCatalogDto
{
    public string ModuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int? ParentTenantModuleCatalogId { get; set; }
    public string? MenuUrl { get; set; }
    public int DisplayOrder { get; set; }
    public string? Icon { get; set; }
    public bool IsCore { get; set; }
}
