namespace RenewalTracker.Platform.Application.DTOs.Sales;

/// <summary>Mirrored by Angular's SalesPicklistValue model - see PlatformSalesPicklistsController.</summary>
public class SalesPicklistValueDto
{
    public int Id { get; set; }
    public string ListName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
