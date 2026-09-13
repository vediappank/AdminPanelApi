using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One selectable value in a Sales picklist (e.g. an Enquiry Status or an
/// Enquiry Priority) - replaces what used to be a hardcoded TypeScript
/// array (STATUSES/PRIORITIES in sales-enquiries.component.ts). ListName
/// groups values into a named list ("EnquiryStatus", "EnquiryPriority" so
/// far); adding a brand-new picklist elsewhere in Sales (Follow-up Type/
/// Status, Quotation Status, ...) means seeding more rows under a new
/// ListName, not a schema change or a new table.
///
/// Code is NOT a validated foreign key anywhere - Enquiry.Status/Priority
/// are plain nvarchar columns (same "no enforced FK" convention as
/// CategoryId/AssignedToEmployeeId on that table), so this list only
/// controls what the UI offers going forward; it never touches existing
/// data. IMPORTANT: dbo.SalesEnquiries.Status also has a real CHECK
/// constraint (CK_SalesEnquiries_Status) restricting it to a fixed set of
/// values - the seeded EnquiryStatus rows match that constraint exactly.
/// Adding a new status Code here without also updating (or dropping) that
/// CHECK constraint means the value will show in the dropdown but the
/// server will reject saving it - see create-sales-picklists.sql.
///
/// New table (dbo.SalesPicklists) - see create-sales-picklists.sql.
/// </summary>
public class SalesPicklistValue
{
    [Key]
    public int SalesPicklistId { get; set; }

    [MaxLength(50)]
    public string ListName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    public int DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
