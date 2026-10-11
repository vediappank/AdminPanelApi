namespace RenewalTracker.Platform.Application.DTOs.ProductServices;

/// <summary>
/// One master service under a Product - see PlatformProductServicesController.
/// Mirrored by Angular's ProductService model. Total is server-computed:
/// DefaultQty × Amount × (1 + VatPercent ÷ 100).
/// </summary>
public class ProductServiceDto
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int DefaultQty { get; set; } = 1;
    public decimal Amount { get; set; }
    public decimal VatPercent { get; set; }
    public decimal Total { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}

/// <summary>Body for POST/PUT api/platform/product-services.</summary>
public class SaveProductServiceDto
{
    public int ApplicationId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public int DefaultQty { get; set; } = 1;
    public decimal Amount { get; set; }
    public decimal VatPercent { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}
