using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One Product (Application - dbo.Applications) a customer's Enquiry is
/// interested in. Requirements are a plain multi-select now: pick one or
/// more Products from the Applications catalog on the Enquiry form -
/// there's no per-product pricing/service breakdown at this stage anymore
/// (that used to live on a nested EnquiryRequirementService child row, now
/// removed - detailed, priced service lines are a Quotation's job, see
/// QuotationServiceLine.cs). The Service Catalog (dbo.Services/ServiceItem)
/// is not referenced here at all.
///
/// ApplicationNameSnapshot follows this schema's usual "id + snapshot name"
/// convention so a saved requirement still reads correctly even if the
/// Application is later renamed.
///
/// New table (dbo.EnquiryRequirementProducts) - see
/// create-sales-enquiry-requirement-products.sql, which also drops the
/// now-unused dbo.SalesEnquiryRequirements and dbo.EnquiryRequirementServices
/// tables. The stale EnquiryRequirement.cs / EnquiryRequirementService.cs
/// entity files can be deleted once this is deployed - nothing references
/// either of them anymore.
/// </summary>
public class EnquiryRequirementProduct
{
    [Key]
    public int EnquiryRequirementProductId { get; set; }

    public int SalesEnquiryId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;

    public int ApplicationId { get; set; }

    public AppDefinition? Application { get; set; }

    [MaxLength(200)]
    public string ApplicationNameSnapshot { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
