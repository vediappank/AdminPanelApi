using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Sales ▸ Enquiry - a sales lead for onboarding a new tenant business.
/// Platform-owned (Bliss Point Group's own sales pipeline, not a tenant's
/// data) - see PlatformSalesController.
///
/// Column names/types match the real dbo.SalesEnquiries table exactly (see
/// the CREATE TABLE script the user supplied) - NOT the earlier draft, which
/// assumed a much smaller invented "Enquiries" table. CategoryId/
/// SubCategoryId/CustomerId/AssignedToEmployeeId are plain columns, not
/// enforced foreign keys - the table's own convention is a
/// "*Snapshot" name column recorded alongside each id, so history reads
/// correctly even if the category/customer/employee is renamed or deleted
/// later. No navigation properties for that reason.
/// </summary>
public class Enquiry
{
    [Key]
    public int SalesEnquiryId { get; set; }

    [MaxLength(30)]
    public string? EnquiryNo { get; set; }

    public DateTime EnquiryDate { get; set; }

    public int? CustomerId { get; set; }

    [MaxLength(150)]
    public string CustomerNameSnapshot { get; set; } = string.Empty;

    [MaxLength(150)]
    public string? ContactPerson { get; set; }

    [MaxLength(30)]
    public string Mobile { get; set; } = string.Empty;

    [MaxLength(30)]
    public string? WhatsApp { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(30)]
    public string CustomerType { get; set; } = "Individual";

    [MaxLength(50)]
    public string? Source { get; set; }

    public int? CategoryId { get; set; }

    [MaxLength(150)]
    public string? CategoryNameSnapshot { get; set; }

    public int? SubCategoryId { get; set; }

    [MaxLength(150)]
    public string? SubCategoryNameSnapshot { get; set; }

    [MaxLength(2000)]
    public string? ServiceIdsCsv { get; set; }

    public string? ServiceNamesSnapshot { get; set; }

    public string? ServiceDetailsJson { get; set; }

    public string? Requirement { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal EstimatedAmount { get; set; }

    public int? AssignedToEmployeeId { get; set; }

    [MaxLength(150)]
    public string? AssignedToEmployeeNameSnapshot { get; set; }

    [MaxLength(20)]
    public string Priority { get; set; } = "Normal";

    [MaxLength(30)]
    public string Status { get; set; } = "New";

    public DateTime? NextFollowUpDate { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    [MaxLength(100)]
    public string? ModifiedBy { get; set; }
}
