using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Service Catalog ▸ Sub-Category - a child of ServiceCategory. Column
/// names match the real dbo.ServiceSubCategories table exactly
/// (SubCategoryId/CategoryId/SubCategoryName/IsActive/CreatedDate/
/// ModifiedDate) - see ServiceCategory.cs for context.
/// </summary>
public class ServiceSubCategory
{
    [Key]
    public int SubCategoryId { get; set; }

    public int CategoryId { get; set; }

    public ServiceCategory ServiceCategory { get; set; } = null!;

    [MaxLength(100)]
    public string SubCategoryName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
