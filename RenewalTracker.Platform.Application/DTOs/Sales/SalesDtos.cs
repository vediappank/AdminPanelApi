using System;
using System.Collections.Generic;

namespace RenewalTracker.Platform.Application.DTOs.Sales;

/// <summary>
/// Mirrored by Angular's Enquiry model - see PlatformSalesController.
/// Field names deliberately keep the shorter names the Angular app already
/// uses (Customer/Contact/Amount/AssignedTo) even though the real
/// dbo.SalesEnquiries columns are named CustomerNameSnapshot/ContactPerson/
/// EstimatedAmount/AssignedToEmployeeNameSnapshot - the controller maps
/// between the two. Mobile is the one field the Angular form did not
/// previously collect but the real table requires (NOT NULL, no default).
/// Everything else on the real table (EnquiryNo, CustomerId, WhatsApp,
/// Email, CustomerType, Source, the Service*/Requirement columns) round-trips
/// through this DTO so GET never loses data, even though the current UI
/// doesn't yet edit those fields.
///
/// CategoryId/SubCategoryId/Amount are no longer set from the Enquiry form.
/// Requirements are now a plain multi-select of Products (see
/// EnquiryRequirementProductDto below) with no pricing at this stage, so
/// Amount is left untouched by CreateEnquiry/UpdateEnquiry/SaveRequirements
/// entirely - it only reads back whatever is already in the database
/// (legacy data, or eventually a Quotation's total once that's wired up).
/// CategoryId/SubCategoryId are likewise left alone (kept only for whatever
/// legacy data already has them set). RequirementCount/Products are
/// read-only, resolved server-side from EnquiryRequirementProducts purely
/// for the list grid (Products shows the picked Application names, or is
/// empty when none picked yet).
/// </summary>
public class EnquiryDto
{
    public int Id { get; set; }
    public string? EnquiryNo { get; set; }
    public DateTime EnquiryDate { get; set; }
    public int? CustomerId { get; set; }
    public string Customer { get; set; } = string.Empty;
    public string? Contact { get; set; }
    public string Mobile { get; set; } = string.Empty;
    public string? WhatsApp { get; set; }
    public string? Email { get; set; }
    public string CustomerType { get; set; } = "Individual";
    public string? Source { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? ServiceIdsCsv { get; set; }
    public string? ServiceNamesSnapshot { get; set; }
    public string? ServiceDetailsJson { get; set; }
    public string? Requirement { get; set; }
    public decimal Amount { get; set; }
    public int RequirementCount { get; set; }
    public List<string> Products { get; set; } = new();
    public int? AssignedToEmployeeId { get; set; }
    public string? AssignedTo { get; set; }
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "New";
    public DateTime? NextFollowUpDate { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// One Product (Application) an Enquiry is interested in - see
/// EnquiryRequirementProduct.cs. A plain multi-select: no per-product
/// pricing/service breakdown at this stage (that's a Quotation's job).
/// ApplicationName is read-only, resolved server-side from the picked
/// ApplicationId. Returned by GetRequirements() for display; SaveRequirements()
/// takes just the list of picked ApplicationIds - see its doc comment.
/// </summary>
public class EnquiryRequirementProductDto
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
}

/// <summary>Mirrored by Angular's FollowUp model - see PlatformSalesController.</summary>
public class FollowUpDto
{
    public int Id { get; set; }
    public int EnquiryId { get; set; }
    public string Type { get; set; } = "Phone";
    public int? EmployeeId { get; set; }
    public string? AssignedTo { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime NextDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// Mirrored by Angular's Lead model - see PlatformSalesController. A Lead
/// is created only by converting a Follow-up (ConvertFollowUpToLead()) -
/// there's no POST here to create one directly. PUT only ever touches
/// EmployeeId/AssignedTo/Status/Notes (Customer/Mobile/Email/LeadNo/
/// FollowUpId are a point-in-time snapshot taken at conversion, not
/// editable afterwards).
/// </summary>
public class LeadDto
{
    public int Id { get; set; }
    public int EnquiryId { get; set; }
    public int? FollowUpId { get; set; }
    public string? LeadNo { get; set; }
    public string Customer { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public int? EmployeeId { get; set; }
    public string? AssignedTo { get; set; }
    public string Status { get; set; } = "New";
    public string? Notes { get; set; }
    public DateTime ConvertedDate { get; set; }
    public DateTime CreatedDate { get; set; }
}
