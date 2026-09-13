using System;

namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>Own copy for RenewalTracker.PlatformApi - see TenantDto.cs for why.</summary>
public class ModuleDto
{
    public int ModuleId { get; set; }

    public string ModuleCode { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsCore { get; set; }
    public int DisplayOrder { get; set; }

    // Merged in from the old Menu table - left-nav placement for this module.
    public int? ParentModuleId { get; set; }
    public string? MenuUrl { get; set; }
    public string? Icon { get; set; }
}
