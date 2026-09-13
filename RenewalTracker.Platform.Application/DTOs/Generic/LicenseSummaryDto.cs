using System;

namespace RenewalTracker.Platform.Application.DTOs.Generic;

/// <summary>
/// The slice of a tenant's License that the Business Application needs
/// mirrored locally to enforce its device-activation limit - see
/// License.cs (MaxDevices), CatalogSnapshotDto and
/// InternalCatalogController.GetSyncSnapshot. Deliberately not the full
/// LicenseDto (Applications/ApplicationsDtos.cs) - billing fields like
/// LicenseKey/RenewalAmount have no business on the tenant side.
///
/// One row per Tenant: a tenant with more than one License row (e.g. an
/// old expired one plus a current one) contributes only its most recent
/// active license by ExpiryDate - see InternalCatalogController.
/// </summary>
public class LicenseSummaryDto
{
    public int TenantId { get; set; }
    public int MaxDevices { get; set; }
    public bool IsActive { get; set; }
    public DateTime ExpiryDate { get; set; }
}
