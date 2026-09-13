using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One manually-typed service line under an EnquiryRequirementProduct.
/// Deliberately NOT linked to the Service Catalog (dbo.Services/
/// ServiceItem) - mirrors QuotationServiceLine.cs exactly, one level up.
///
/// New table (dbo.EnquiryRequirementServices) - see
/// create-sales-enquiry-requirement-products.sql.
/// </summary>
public class EnquiryRequirementService
{
    [Key]
    public int EnquiryRequirementServiceId { get; set; }

    public int EnquiryRequirementProductId { get; set; }

    public EnquiryRequirementProduct EnquiryRequirementProduct { get; set; } = null!;

    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    public int Qty { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal VatPercent { get; set; }

    public DateTime CreatedDate { get; set; }
}
