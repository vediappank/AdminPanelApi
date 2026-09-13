using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Applications ▸ Application - one of the apps Bliss Point Group ships
/// (e.g. "Platform Admin Panel", "Renewal &amp; Reminder Tracker"). Named
/// AppDefinition, not Application, only to avoid the generic-sounding CLR
/// name; the Angular app's demo model was called AppDefinition too. See
/// PlatformApplicationsController. Previously demo data
/// (MockDataService.applications).
///
/// Maps to the real dbo.Applications table (columns: ApplicationId,
/// ApplicationCode, ApplicationName, IsActive, CreatedDate - see
/// PlatformDbContext.Platform.cs for the Code/Name column-name mapping).
/// There is no Description column on the real table at all - an earlier
/// version of this entity assumed plain "Name"/"Description" columns that
/// never existed on the live database, which is why GetApplications() (and
/// every screen built on top of it) failed with "Invalid column name
/// 'Name'"/"'Description'" until this was fixed.
/// </summary>
public class AppDefinition
{
    [Key]
    public int ApplicationId { get; set; }

    /// <summary>Short code, e.g. "ADMIN"/"TRACKER" - maps to ApplicationCode.</summary>
    [MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name, e.g. "Platform Admin Panel" - maps to ApplicationName.</summary>
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }
}
