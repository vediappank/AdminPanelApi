using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Applications ▸ License - a license grant for a tenant (key, validity
/// window, seat limit). See PlatformApplicationsController. Previously demo
/// data (MockDataService.licenses).
///
/// Deliberately has no Type column - the real dbo.Licenses table has no
/// such column (confirmed: querying it threw "Invalid column name 'Type'"
/// the moment the old Type property, mapped by convention, was touched by
/// any query - including the full-entity materialization ToDictionaryAsync
/// does in PlatformBillingController.ToDtosAsync). Removed outright rather
/// than mapped to a differently-named real column, per direct instruction.
/// </summary>
public class License
{
    [Key]
    public int LicenseId { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    [MaxLength(100)]
    public string LicenseKey { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime ExpiryDate { get; set; }

    public int UserLimit { get; set; }

    /// <summary>
    /// Device-activation cap for the Business Application (renewal-tracker-ui/
    /// RenewalTrackerApi) - how many distinct browser/device tokens may be
    /// logged in for this tenant at once. Separate from UserLimit, which is
    /// an unrelated named-user-seat count and keeps its existing meaning.
    /// Synced to the Business database via the CatalogSync pipeline (see
    /// InternalCatalogController/CatalogSnapshotDto) and enforced there by
    /// AuthService.LoginAsync against the local DeviceActivations table.
    /// </summary>
    public int MaxDevices { get; set; }

    public bool IsActive { get; set; }

    /// <summary>
    /// The amount due to renew this license for its current/next term - the
    /// total that Payment rows with LicenseId = this License pay down (see
    /// Payment.cs / PlatformBillingController.GetLicenseBalance). Zero for
    /// licenses that don't track billing (e.g. older/seeded rows) - a zero
    /// RenewalAmount reads as "nothing outstanding", never as "overpaid".
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal RenewalAmount { get; set; }
}
