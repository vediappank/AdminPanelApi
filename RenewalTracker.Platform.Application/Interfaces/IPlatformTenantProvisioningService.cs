using RenewalTracker.Platform.Application.DTOs.Admin;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Platform-side half of standing up a brand-new tenant. Since the Tenant
/// row (this API's own RenewalTrackerPlatform database) and everything else
/// a usable tenant needs (config/modules/roles/admin user - the Business
/// API's own database) now live behind two fully independent APIs with no
/// shared code and no shared transaction, this is sequenced as:
///   1. ProvisionAsync creates only the Tenant row here (IsActive = true),
///      then calls the Business API's internal endpoint
///      (POST api/internal/tenant-provisioning) to do the rest.
///   2. On success, the full result (roles created, modules enabled, admin
///      email) is returned to the caller.
///   3. On failure (Business API unreachable, or it errors), the Tenant row
///      is left as-is - there is no separate "provisioning failed" status
///      column on Tenant (it's a plain IsActive flag, matching the
///      production schema) - and CompleteProvisioningAsync can be called
///      again later to retry, safe to re-run because the Business side
///      checks whether that TenantId already has Roles before writing
///      anything. The Tenant row itself carries no admin-user/config
///      fields, so a retry takes the same admin+config input again (the
///      Platform Panel's "complete provisioning" form re-submits it) rather
///      than this service persisting pending credentials anywhere.
/// This is a deliberately simple "idempotent retry" design, not a full
/// distributed transaction/saga - acceptable because the retry path is
/// safe and always available, not because failures can't happen. Since
/// there's no persisted pending/failed flag, the Platform Panel currently
/// relies on the immediate error from Provision to prompt a retry rather
/// than being able to list "tenants still needing completion" later.
/// </summary>
public interface IPlatformTenantProvisioningService
{
    Task<ProvisionTenantResultDto> ProvisionAsync(ProvisionTenantDto dto);

    Task<ProvisionTenantResultDto> CompleteProvisioningAsync(int tenantId, ProvisionTenantDto dto);

    /// <summary>
    /// Is <paramref name="email"/> free to be the owner login of a tenant? Not free when another tenant
    /// already has it - in the Business Panel login directory (dbo.TenantUsers) or as the owner of its
    /// database (dbo.TenantDatabases.OwnerEmail). <paramref name="forTenantId"/> = the tenant being
    /// completed (its own e-mail is fine); null for a new tenant.
    /// </summary>
    Task<OwnerEmailCheckDto> CheckOwnerEmailAsync(string? email, int? forTenantId, CancellationToken ct = default);
}
