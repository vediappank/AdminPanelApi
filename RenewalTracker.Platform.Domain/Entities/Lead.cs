using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales ▸ Lead - a further-qualified prospect, created by converting one
/// Follow-up (see PlatformSalesController.ConvertFollowUpToLead()). There is
/// no manual "+ New lead" - SalesFollowUpId records which Follow-up it came
/// from (nullable - kept even if that Follow-up is later deleted, see
/// create-sales-leads.sql for the ON DELETE SET NULL reasoning).
///
/// CustomerNameSnapshot/Mobile/Email are copied from the parent Enquiry at
/// conversion time (same "id + snapshot" convention as Enquiry/FollowUp) -
/// a Lead is a point-in-time record of a qualified prospect, not a live
/// pointer back to the Enquiry, so it keeps its own copy.
///
/// Status is a plain string validated against dbo.SalesPicklists
/// ("LeadStatus" list) at the application layer - deliberately NO CHECK
/// constraint here, see create-sales-leads.sql's doc comment for why.
///
/// New table (dbo.SalesLeads) - not part of the original schema dump - see
/// create-sales-leads.sql.
/// </summary>
public class Lead
{
    [Key]
    public int LeadId { get; set; }

    public int SalesEnquiryId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;

    public int? SalesFollowUpId { get; set; }

    public FollowUp? FollowUp { get; set; }

    [MaxLength(30)]
    public string? LeadNo { get; set; }

    [MaxLength(150)]
    public string CustomerNameSnapshot { get; set; } = string.Empty;

    [MaxLength(30)]
    public string? Mobile { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    public int? EmployeeId { get; set; }

    [MaxLength(150)]
    public string? EmployeeNameSnapshot { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = "New";

    public string? Notes { get; set; }

    public DateTime ConvertedDate { get; set; }

    public DateTime CreatedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    [MaxLength(100)]
    public string? ModifiedBy { get; set; }
}
