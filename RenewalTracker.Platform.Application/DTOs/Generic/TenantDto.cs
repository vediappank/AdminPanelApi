using System;

namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>
/// Own copy for RenewalTracker.PlatformApi - shape-identical to
/// RenewalTracker.Application.DTOs.Generic.TenantDto (Business's copy, used
/// there for its read-only local mirror), kept as two separate types so
/// neither API project references the other's code. See
/// RenewalTracker.Platform.Domain.Entities.Tenant.
/// </summary>
public class TenantDto
{
    public int TenantId { get; set; }

    public string TenantCode { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactMobile { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }

    /// <summary>The Lead this tenant was converted from, if any - see Tenant.LeadId.</summary>
    public int? LeadId { get; set; }
}
