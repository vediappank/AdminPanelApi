using RenewalTracker.Platform.Application.DTOs.Generic;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// One e-mail and one mobile per record in the Admin Panel:
///   tenants         contact e-mail and contact mobile unique across all tenants
///   platform users  e-mail unique, and not a Business Panel login of any tenant (dbo.TenantUsers);
///                   mobile (optional) unique across platform users
/// (Tenant owner logins: IPlatformTenantProvisioningService.CheckOwnerEmailAsync.)
/// field = "email" | "mobile". On edit, pass the record's id: its own unchanged value is always allowed.
/// </summary>
public interface IPlatformContactUniquenessService
{
    Task<UniqueCheckDto> CheckTenantContactAsync(string field, string? value, int? tenantId, CancellationToken ct = default);

    Task<UniqueCheckDto> CheckPlatformUserAsync(string field, string? value, int? platformUserId, CancellationToken ct = default);

    /// <summary>For save: the first problem with the e-mail or mobile, or null.</summary>
    Task<string?> TenantContactProblemAsync(string? email, string? mobile, int? tenantId, CancellationToken ct = default);

    Task<string?> PlatformUserProblemAsync(string? email, string? mobile, int? platformUserId, CancellationToken ct = default);
}
