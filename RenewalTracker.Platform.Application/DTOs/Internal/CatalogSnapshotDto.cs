using RenewalTracker.Platform.Application.DTOs.Generic;

namespace RenewalTracker.Platform.Application.DTOs.Internal;

/// <summary>
/// The full current state of the platform-owned catalog tables
/// (RenewalTrackerPlatform database), returned by this API's GET
/// api/internal/catalog/sync-snapshot (RequireInternalServiceKey-protected,
/// not a user-facing endpoint) and consumed by RenewalTracker.Api's
/// CatalogSyncService to keep the Business database's (V1) local mirror
/// copies up to date. Shape-identical to
/// RenewalTracker.Application.DTOs.Internal.CatalogSnapshotDto (the Business
/// API's own copy, which deserializes this response) - kept as two separate
/// types, matched purely by JSON shape, so neither API project references
/// the other's code.
///
/// Menus dropped: dbo.Menus was merged into dbo.Modules (it held no real
/// rows) - ModuleDto now carries ParentModuleId/MenuUrl/Icon in its place
/// (TenantId was later removed again - dbo.Modules carries no per-tenant
/// relation). The Business side's own copy of this DTO still declares a
/// Menus property; that's fine - System.Text.Json defaults a missing JSON
/// property to an empty list rather than erroring, and Menus was already
/// always empty, so this changes no data. That other project isn't
/// reachable from this session, so it hasn't been updated to read the new
/// Module fields - see the note left for the user about this.
/// </summary>
public class CatalogSnapshotDto
{
    public List<TenantDto> Tenants { get; set; } = new();
    public List<ModuleDto> Modules { get; set; } = new();
    public List<PermissionDto> Permissions { get; set; } = new();

    /// <summary>One row per tenant that has an active License - see LicenseSummaryDto / InternalCatalogController.</summary>
    public List<LicenseSummaryDto> Licenses { get; set; } = new();
}
