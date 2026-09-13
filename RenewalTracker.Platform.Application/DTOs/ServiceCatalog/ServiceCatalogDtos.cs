namespace RenewalTracker.Platform.Application.DTOs.ServiceCatalog;

/// <summary>Mirrored by Angular's ServiceCategory model - see PlatformServiceCatalogController.</summary>
public class ServiceCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

/// <summary>Mirrored by Angular's ServiceSubCategory model - see PlatformServiceCatalogController.</summary>
public class ServiceSubCategoryDto
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

/// <summary>
/// Mirrored by Angular's ServiceItem model - see PlatformServiceCatalogController.
/// CategoryId/SubCategoryId are required (not nullable) - the real
/// dbo.Services table has them as NOT NULL FK columns.
/// </summary>
public class ServiceItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public int SubCategoryId { get; set; }
    public decimal Amount { get; set; }
    public decimal VatPercent { get; set; }
    public bool IsActive { get; set; }
}
