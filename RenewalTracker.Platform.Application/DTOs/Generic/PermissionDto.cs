using System;

namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>Own copy for RenewalTracker.PlatformApi - see TenantDto.cs for why.</summary>
public class PermissionDto
{
    public int PermissionId { get; set; }

    public int ModuleId { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}
