using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales ▸ Follow-up - a follow-up activity against an Enquiry. See
/// Enquiry.cs for context. Deleting the parent enquiry removes its
/// follow-ups too (real FK CASCADE - see PlatformDbContext.Platform.cs).
///
/// Column names/types match the real dbo.SalesFollowUps table exactly (see
/// the CREATE TABLE script the user supplied). EmployeeId/
/// EmployeeNameSnapshot follows the same "id + snapshot name" convention as
/// Enquiry - no enforced foreign key into PlatformUsers.
/// </summary>
public class FollowUp
{
    [Key]
    public int SalesFollowUpId { get; set; }

    public int SalesEnquiryId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;

    public DateTime FollowUpDate { get; set; }

    [MaxLength(30)]
    public string FollowUpType { get; set; } = "Phone";

    public int? EmployeeId { get; set; }

    [MaxLength(150)]
    public string? EmployeeNameSnapshot { get; set; }

    // 30, not 20 - Status now also carries the EnquiryStatus picklist's own
    // codes (Contacted/Requirement Identified/Follow-up/Quotation Sent/
    // Negotiation/Won/Lost, not just the original Pending/Completed/
    // Cancelled), and "Requirement Identified" alone is 22 characters - see
    // alter-salesfollowups-status-length.sql. Must match dbo.SalesFollowUps.
    // Status's real column width or EF throws a length-mismatch warning at
    // startup and SQL Server truncates/rejects the INSERT/UPDATE at runtime.
    [MaxLength(30)]
    public string Status { get; set; } = "Pending";

    public string? Notes { get; set; }

    public DateTime? NextFollowUpDate { get; set; }

    public DateTime CreatedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    [MaxLength(100)]
    public string? ModifiedBy { get; set; }
}
