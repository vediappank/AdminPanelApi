using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.ServiceCatalog;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Service Catalog ▸ Category / Sub-Category / Service - the hierarchy
/// Sales Enquiries and future billing are priced against. Platform-owned,
/// hand-written against PlatformDbContext the same way
/// PlatformCatalogController is. Previously demo data
/// (MockDataService.categories/subCategories/services in the Angular app -
/// see ServiceCategoriesComponent/ServiceSubCategoriesComponent/ServiceItemsComponent).
/// </summary>
[ApiController]
[Route("api/platform/service-catalog")]
[RequirePlatformAuth]
public class PlatformServiceCatalogController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformServiceCatalogController(PlatformDbContext db)
    {
        _db = db;
    }

    // ----- Categories -----

    [HttpGet("categories")]
    public async Task<ActionResult<List<ServiceCategoryDto>>> GetCategories()
    {
        var rows = await _db.ServiceCategories.AsNoTracking().OrderBy(c => c.CategoryName).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("categories")]
    public async Task<ActionResult<ServiceCategoryDto>> CreateCategory(ServiceCategoryDto dto)
    {
        if (await _db.ServiceCategories.AnyAsync(c => c.CategoryName == dto.Name))
        {
            return BadRequest(new { message = $"A category named '{dto.Name}' already exists." });
        }

        var entity = new ServiceCategory { CategoryName = dto.Name, IsActive = dto.IsActive, CreatedDate = DateTime.UtcNow };
        _db.ServiceCategories.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("categories/{id:int}")]
    public async Task<ActionResult<ServiceCategoryDto>> UpdateCategory(int id, ServiceCategoryDto dto)
    {
        var entity = await _db.ServiceCategories.FirstOrDefaultAsync(c => c.CategoryId == id);
        if (entity == null) return NotFound();

        entity.CategoryName = dto.Name;
        entity.IsActive = dto.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var entity = await _db.ServiceCategories.FirstOrDefaultAsync(c => c.CategoryId == id);
        if (entity == null) return NotFound();

        _db.ServiceCategories.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ----- Sub-Categories -----

    [HttpGet("sub-categories")]
    public async Task<ActionResult<List<ServiceSubCategoryDto>>> GetSubCategories()
    {
        var rows = await _db.ServiceSubCategories.AsNoTracking().OrderBy(s => s.SubCategoryName).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("sub-categories")]
    public async Task<ActionResult<ServiceSubCategoryDto>> CreateSubCategory(ServiceSubCategoryDto dto)
    {
        if (!await _db.ServiceCategories.AnyAsync(c => c.CategoryId == dto.CategoryId))
        {
            return BadRequest(new { message = "The selected category does not exist." });
        }
        if (await _db.ServiceSubCategories.AnyAsync(s => s.CategoryId == dto.CategoryId && s.SubCategoryName == dto.Name))
        {
            return BadRequest(new { message = $"A sub-category named '{dto.Name}' already exists under this category." });
        }

        var entity = new ServiceSubCategory { CategoryId = dto.CategoryId, SubCategoryName = dto.Name, IsActive = dto.IsActive, CreatedDate = DateTime.UtcNow };
        _db.ServiceSubCategories.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("sub-categories/{id:int}")]
    public async Task<ActionResult<ServiceSubCategoryDto>> UpdateSubCategory(int id, ServiceSubCategoryDto dto)
    {
        var entity = await _db.ServiceSubCategories.FirstOrDefaultAsync(s => s.SubCategoryId == id);
        if (entity == null) return NotFound();

        if (!await _db.ServiceCategories.AnyAsync(c => c.CategoryId == dto.CategoryId))
        {
            return BadRequest(new { message = "The selected category does not exist." });
        }

        entity.CategoryId = dto.CategoryId;
        entity.SubCategoryName = dto.Name;
        entity.IsActive = dto.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("sub-categories/{id:int}")]
    public async Task<IActionResult> DeleteSubCategory(int id)
    {
        var entity = await _db.ServiceSubCategories.FirstOrDefaultAsync(s => s.SubCategoryId == id);
        if (entity == null) return NotFound();

        _db.ServiceSubCategories.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ----- Services -----

    [HttpGet("services")]
    public async Task<ActionResult<List<ServiceItemDto>>> GetServices()
    {
        var rows = await _db.ServiceItems.AsNoTracking().OrderBy(s => s.ServiceName).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("services")]
    public async Task<ActionResult<ServiceItemDto>> CreateService(ServiceItemDto dto)
    {
        if (!await _db.ServiceCategories.AnyAsync(c => c.CategoryId == dto.CategoryId))
        {
            return BadRequest(new { message = "Select a category." });
        }
        if (!await _db.ServiceSubCategories.AnyAsync(s => s.SubCategoryId == dto.SubCategoryId))
        {
            return BadRequest(new { message = "Select a sub-category." });
        }

        var entity = new ServiceItem
        {
            ServiceName = dto.Name,
            CategoryId = dto.CategoryId,
            SubCategoryId = dto.SubCategoryId,
            Amount = dto.Amount,
            VAT = dto.VatPercent,
            IsActive = dto.IsActive,
            CreatedDate = DateTime.UtcNow,
        };
        _db.ServiceItems.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("services/{id:int}")]
    public async Task<ActionResult<ServiceItemDto>> UpdateService(int id, ServiceItemDto dto)
    {
        var entity = await _db.ServiceItems.FirstOrDefaultAsync(s => s.ServiceId == id);
        if (entity == null) return NotFound();

        if (!await _db.ServiceCategories.AnyAsync(c => c.CategoryId == dto.CategoryId))
        {
            return BadRequest(new { message = "Select a category." });
        }
        if (!await _db.ServiceSubCategories.AnyAsync(s => s.SubCategoryId == dto.SubCategoryId))
        {
            return BadRequest(new { message = "Select a sub-category." });
        }

        entity.ServiceName = dto.Name;
        entity.CategoryId = dto.CategoryId;
        entity.SubCategoryId = dto.SubCategoryId;
        entity.Amount = dto.Amount;
        entity.VAT = dto.VatPercent;
        entity.IsActive = dto.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("services/{id:int}")]
    public async Task<IActionResult> DeleteService(int id)
    {
        var entity = await _db.ServiceItems.FirstOrDefaultAsync(s => s.ServiceId == id);
        if (entity == null) return NotFound();

        _db.ServiceItems.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static ServiceCategoryDto ToDto(ServiceCategory c) => new() { Id = c.CategoryId, Name = c.CategoryName, IsActive = c.IsActive };

    private static ServiceSubCategoryDto ToDto(ServiceSubCategory s) => new()
    {
        Id = s.SubCategoryId,
        CategoryId = s.CategoryId,
        Name = s.SubCategoryName,
        IsActive = s.IsActive,
    };

    private static ServiceItemDto ToDto(ServiceItem s) => new()
    {
        Id = s.ServiceId,
        Name = s.ServiceName,
        CategoryId = s.CategoryId,
        SubCategoryId = s.SubCategoryId,
        Amount = s.Amount,
        VatPercent = s.VAT,
        IsActive = s.IsActive,
    };
}
