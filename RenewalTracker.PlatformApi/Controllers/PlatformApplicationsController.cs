using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Applications;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Applications ▸ Application / App Config / License - the apps Bliss
/// Point Group ships, each tenant's per-app display settings, and each
/// tenant's license grants. Platform-owned, hand-written against
/// PlatformDbContext. Previously demo data
/// (MockDataService.applications/appConfigs/licenses in the Angular app -
/// see ApplicationsComponent/AppConfigComponent/LicensesComponent).
/// </summary>
[ApiController]
[Route("api/platform/applications")]
[RequirePlatformAuth]
public class PlatformApplicationsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformApplicationsController(PlatformDbContext db)
    {
        _db = db;
    }

    // ----- Applications -----

    [HttpGet]
    public async Task<ActionResult<List<ApplicationDto>>> GetApplications()
    {
        var rows = await _db.Applications.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationDto>> CreateApplication(ApplicationDto dto)
    {
        var entity = new AppDefinition
        {
            Code = dto.Code,
            Name = dto.Name,
            IsActive = dto.IsActive,
            CreatedDate = DateTime.UtcNow,
        };
        _db.Applications.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApplicationDto>> UpdateApplication(int id, ApplicationDto dto)
    {
        var entity = await _db.Applications.FirstOrDefaultAsync(a => a.ApplicationId == id);
        if (entity == null) return NotFound();

        entity.Code = dto.Code;
        entity.Name = dto.Name;
        entity.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteApplication(int id)
    {
        var entity = await _db.Applications.FirstOrDefaultAsync(a => a.ApplicationId == id);
        if (entity == null) return NotFound();

        _db.Applications.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ----- App Configs -----

    [HttpGet("app-configs")]
    public async Task<ActionResult<List<AppConfigDto>>> GetAppConfigs()
    {
        var rows = await _db.AppConfigs.AsNoTracking().OrderBy(c => c.TenantId).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>Upserts by TenantId - one config row per tenant, matching the Angular screen (one form per tenant, no separate "create").</summary>
    [HttpPut("app-configs/{tenantId:int}")]
    public async Task<ActionResult<AppConfigDto>> UpsertAppConfig(int tenantId, AppConfigDto dto)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId))
        {
            return BadRequest(new { message = "The selected tenant does not exist." });
        }

        var entity = await _db.AppConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (entity == null)
        {
            entity = new AppConfig { TenantId = tenantId };
            _db.AppConfigs.Add(entity);
        }

        entity.DateFormat = dto.DateFormat;
        entity.TimeZone = dto.TimeZone;
        entity.Currency = dto.Currency;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    // ----- Licenses -----

    [HttpGet("licenses")]
    public async Task<ActionResult<List<LicenseDto>>> GetLicenses()
    {
        var rows = await _db.Licenses.AsNoTracking().OrderBy(l => l.ExpiryDate).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("licenses")]
    public async Task<ActionResult<LicenseDto>> CreateLicense(LicenseDto dto)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == dto.TenantId))
        {
            return BadRequest(new { message = "The selected tenant does not exist." });
        }
        if (await _db.Licenses.AnyAsync(l => l.LicenseKey == dto.LicenseKey))
        {
            return BadRequest(new { message = $"A license with key '{dto.LicenseKey}' already exists." });
        }

        var entity = new License
        {
            TenantId = dto.TenantId,
            LicenseKey = dto.LicenseKey,
            StartDate = dto.StartDate,
            ExpiryDate = dto.ExpiryDate,
            UserLimit = dto.UserLimit,
            MaxDevices = dto.MaxDevices,
            IsActive = dto.IsActive,
            RenewalAmount = dto.RenewalAmount,
        };
        _db.Licenses.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpPut("licenses/{id:int}")]
    public async Task<ActionResult<LicenseDto>> UpdateLicense(int id, LicenseDto dto)
    {
        var entity = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseId == id);
        if (entity == null) return NotFound();

        entity.TenantId = dto.TenantId;
        entity.LicenseKey = dto.LicenseKey;
        entity.StartDate = dto.StartDate;
        entity.ExpiryDate = dto.ExpiryDate;
        entity.UserLimit = dto.UserLimit;
        entity.MaxDevices = dto.MaxDevices;
        entity.IsActive = dto.IsActive;
        entity.RenewalAmount = dto.RenewalAmount;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("licenses/{id:int}")]
    public async Task<IActionResult> DeleteLicense(int id)
    {
        var entity = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseId == id);
        if (entity == null) return NotFound();

        _db.Licenses.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static ApplicationDto ToDto(AppDefinition a) => new()
    {
        Id = a.ApplicationId,
        Code = a.Code,
        Name = a.Name,
        IsActive = a.IsActive,
        CreatedDate = a.CreatedDate,
    };

    private static AppConfigDto ToDto(AppConfig c) => new()
    {
        Id = c.AppConfigId,
        TenantId = c.TenantId,
        DateFormat = c.DateFormat,
        TimeZone = c.TimeZone,
        Currency = c.Currency,
    };

    private static LicenseDto ToDto(License l) => new()
    {
        Id = l.LicenseId,
        TenantId = l.TenantId,
        LicenseKey = l.LicenseKey,
        StartDate = l.StartDate,
        ExpiryDate = l.ExpiryDate,
        UserLimit = l.UserLimit,
        MaxDevices = l.MaxDevices,
        IsActive = l.IsActive,
        RenewalAmount = l.RenewalAmount,
    };
}
