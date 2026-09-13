using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Sales;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Sales ▸ Picklists - the selectable values behind Enquiry Status/Priority
/// (and, going forward, any other Sales picklist - Follow-up Type/Status,
/// Quotation Status - just by seeding more rows under a new ListName, no
/// schema change). Replaces what used to be hardcoded TypeScript arrays -
/// see SalesEnquiriesComponent. Platform-owned, hand-written against
/// PlatformDbContext. Talks to the new dbo.SalesPicklists table - see
/// SalesPicklistValue.cs.
///
/// Not gated by module code (same as PlatformSalesController) - anyone with
/// platform access can manage these; there's no meaningful "view only" here
/// since it's pure picklist configuration, not tenant or financial data.
/// </summary>
[ApiController]
[Route("api/platform/sales/picklists")]
[RequirePlatformAuth]
public class PlatformSalesPicklistsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformSalesPicklistsController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<SalesPicklistValueDto>>> GetPicklists([FromQuery] string? listName)
    {
        var query = _db.SalesPicklists.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(listName)) query = query.Where(p => p.ListName == listName);

        var rows = await query.OrderBy(p => p.ListName).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Code).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<SalesPicklistValueDto>> CreatePicklistValue(SalesPicklistValueDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ListName) || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest(new { message = "List name and code are both required." });
        }
        if (await _db.SalesPicklists.AnyAsync(p => p.ListName == dto.ListName && p.Code == dto.Code))
        {
            return BadRequest(new { message = $"'{dto.Code}' already exists in this list." });
        }

        var entity = new SalesPicklistValue
        {
            ListName = dto.ListName,
            Code = dto.Code,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive,
            CreatedDate = DateTime.UtcNow,
        };
        _db.SalesPicklists.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SalesPicklistValueDto>> UpdatePicklistValue(int id, SalesPicklistValueDto dto)
    {
        var entity = await _db.SalesPicklists.FirstOrDefaultAsync(p => p.SalesPicklistId == id);
        if (entity == null) return NotFound();

        if (await _db.SalesPicklists.AnyAsync(p => p.SalesPicklistId != id && p.ListName == dto.ListName && p.Code == dto.Code))
        {
            return BadRequest(new { message = $"'{dto.Code}' already exists in this list." });
        }

        entity.ListName = dto.ListName;
        entity.Code = dto.Code;
        entity.DisplayOrder = dto.DisplayOrder;
        entity.IsActive = dto.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    /// <summary>
    /// No usage check before deleting - Code is never an enforced FK (see
    /// SalesPicklistValue.cs), so removing a value only stops it being
    /// offered going forward; any Enquiry already carrying that Status/
    /// Priority text keeps it untouched.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeletePicklistValue(int id)
    {
        var entity = await _db.SalesPicklists.FirstOrDefaultAsync(p => p.SalesPicklistId == id);
        if (entity == null) return NotFound();

        _db.SalesPicklists.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static SalesPicklistValueDto ToDto(SalesPicklistValue p) => new()
    {
        Id = p.SalesPicklistId,
        ListName = p.ListName,
        Code = p.Code,
        DisplayOrder = p.DisplayOrder,
        IsActive = p.IsActive,
    };
}
