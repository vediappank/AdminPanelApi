using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.ProductServices;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Service Catalog ▸ Product Services - master services per Product
/// (dbo.Applications). Product → Service only. Replaces the Category /
/// Sub-Category / Service screens in the Platform Panel (the old
/// PlatformServiceCatalogController and its tables are left in place for
/// Enquiry Requirements, just no longer on the menu).
///
/// The quotation screen reads GET ?activeOnly=true to pre-fill each ticked
/// Product's service lines - see ProductService.cs.
/// </summary>
[ApiController]
[Route("api/platform/product-services")]
[RequirePlatformAuth]
public class PlatformProductServicesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformProductServicesController(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>All product services, optionally for one Product and/or active ones only, ordered by product then DisplayOrder/name.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ProductServiceDto>>> GetAll([FromQuery] int? applicationId, [FromQuery] bool activeOnly = false)
    {
        var query = _db.ProductServices.AsNoTracking().Include(s => s.Application).AsQueryable();
        if (applicationId.HasValue) query = query.Where(s => s.ApplicationId == applicationId.Value);
        if (activeOnly) query = query.Where(s => s.IsActive);

        var rows = await query
            .OrderBy(s => s.Application!.Name)
            .ThenBy(s => s.DisplayOrder)
            .ThenBy(s => s.ServiceName)
            .ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductServiceDto>> GetById(int id)
    {
        var entity = await _db.ProductServices.AsNoTracking().Include(s => s.Application).FirstOrDefaultAsync(s => s.ProductServiceId == id);
        if (entity == null) return NotFound();
        return Ok(ToDto(entity));
    }

    [HttpPost]
    public async Task<ActionResult<ProductServiceDto>> Create(SaveProductServiceDto dto)
    {
        var error = await ValidateAsync(dto, null);
        if (error != null) return BadRequest(new { message = error });

        var entity = new ProductService
        {
            ApplicationId = dto.ApplicationId,
            ServiceName = dto.ServiceName.Trim(),
            DefaultQty = dto.DefaultQty,
            Amount = dto.Amount,
            VatPercent = dto.VatPercent,
            IsActive = dto.IsActive,
            DisplayOrder = dto.DisplayOrder,
            CreatedDate = DateTime.UtcNow,
        };
        _db.ProductServices.Add(entity);
        await _db.SaveChangesAsync();

        await _db.Entry(entity).Reference(e => e.Application).LoadAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProductServiceDto>> Update(int id, SaveProductServiceDto dto)
    {
        var entity = await _db.ProductServices.FirstOrDefaultAsync(s => s.ProductServiceId == id);
        if (entity == null) return NotFound();

        var error = await ValidateAsync(dto, id);
        if (error != null) return BadRequest(new { message = error });

        entity.ApplicationId = dto.ApplicationId;
        entity.ServiceName = dto.ServiceName.Trim();
        entity.DefaultQty = dto.DefaultQty;
        entity.Amount = dto.Amount;
        entity.VatPercent = dto.VatPercent;
        entity.IsActive = dto.IsActive;
        entity.DisplayOrder = dto.DisplayOrder;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _db.Entry(entity).Reference(e => e.Application).LoadAsync();
        return Ok(ToDto(entity));
    }

    /// <summary>
    /// Deletes the master row. Quotation lines already taken from it keep
    /// their own copied name/price - the FK is ON DELETE SET NULL, so they
    /// simply become free-text lines.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.ProductServices.FirstOrDefaultAsync(s => s.ProductServiceId == id);
        if (entity == null) return NotFound();

        _db.ProductServices.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ValidateAsync(SaveProductServiceDto dto, int? id)
    {
        if (string.IsNullOrWhiteSpace(dto.ServiceName)) return "Service name is required.";
        if (dto.ServiceName.Trim().Length > 200) return "Service name can be at most 200 characters.";
        if (dto.DefaultQty < 1) return "Count must be at least 1.";
        if (dto.Amount < 0) return "Amount can't be negative.";
        if (dto.VatPercent < 0 || dto.VatPercent > 100) return "VAT % must be between 0 and 100.";
        if (!await _db.Applications.AnyAsync(a => a.ApplicationId == dto.ApplicationId)) return "Select a product.";

        var name = dto.ServiceName.Trim();
        var duplicate = await _db.ProductServices.AnyAsync(s =>
            s.ApplicationId == dto.ApplicationId && s.ServiceName == name && (id == null || s.ProductServiceId != id.Value));
        if (duplicate) return $"'{name}' already exists under this product.";

        return null;
    }

    private static ProductServiceDto ToDto(ProductService s) => new()
    {
        Id = s.ProductServiceId,
        ApplicationId = s.ApplicationId,
        ApplicationName = s.Application?.Name ?? string.Empty,
        ServiceName = s.ServiceName,
        DefaultQty = s.DefaultQty,
        Amount = s.Amount,
        VatPercent = s.VatPercent,
        Total = Math.Round(s.DefaultQty * s.Amount * (1 + s.VatPercent / 100m), 2),
        IsActive = s.IsActive,
        DisplayOrder = s.DisplayOrder,
    };
}
