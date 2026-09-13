using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One Product (Application - dbo.Applications) picked onto a Quotation.
/// A Combined quotation has several of these; an Independent quotation has
/// exactly one - see Quotation.cs. ApplicationNameSnapshot follows this
/// schema's usual "id + snapshot name" convention (Enquiry.
/// CategoryNameSnapshot etc.) so a quotation still reads correctly even if
/// the Application is later renamed.
///
/// New table (dbo.QuotationProducts) - see create-quotations.sql.
/// </summary>
public class QuotationProduct
{
    [Key]
    public int QuotationProductId { get; set; }

    public int QuotationId { get; set; }

    public Quotation Quotation { get; set; } = null!;

    public int ApplicationId { get; set; }

    public AppDefinition? Application { get; set; }

    [MaxLength(200)]
    public string ApplicationNameSnapshot { get; set; } = string.Empty;
}
