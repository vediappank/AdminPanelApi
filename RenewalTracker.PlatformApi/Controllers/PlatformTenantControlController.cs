using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.Platform.Infrastructure.Services;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Tenant Control Center - the tenant's own database (dbo.TenantDatabases, kept up to date
/// automatically by TenantDatabaseService) plus the database on/off switch and the
/// maintenance buttons. Admin role / owner actions live on the Business side and are not
/// served here yet.
///   GET  api/platform/tenants/{id}/control
///   PUT  api/platform/tenants/{id}/database/enabled   { enabled }
///   POST api/platform/tenants/{id}/database/migrate | seed | backup
///   GET  api/platform/tenants/{id}/database/progress   (tables, master data and scripts: done / not done)
///   POST api/platform/tenants/{id}/database/verify     (operator confirms - provisioning complete)
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:int}")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES", "TENANT_MODULE_CATALOG")]
public class PlatformTenantControlController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly ITenantDatabaseService _databases;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformTenantControlController(PlatformDbContext db, ITenantDatabaseService databases, ICurrentPlatformUserService currentUser)
    {
        _db = db;
        _databases = databases;
        _currentUser = currentUser;
    }

    public class TenantControlDto
    {
        public int TenantId { get; set; }
        public string? DatabaseName { get; set; }
        public string? ServerName { get; set; }
        public string? SchemaVersion { get; set; }
        public string? SeedVersion { get; set; }
        public DateTime? LastBackupDate { get; set; }
        public DateTime? DatabaseCreatedDate { get; set; }
        public bool IsDatabaseEnabled { get; set; }
        public int? ProvisioningStep { get; set; }
        public string? ProvisioningError { get; set; }
        /// <summary>True while the background worker is building / updating the database (Pending or Running).</summary>
        public bool IsProvisioning { get; set; }
        /// <summary>True when the database is ready but no owner login was entered (tenants created before this) -
        /// use Complete provisioning to enter one.</summary>
        public bool OwnerMissing { get; set; }
        public string? OwnerFullName { get; set; }
        public string? OwnerEmail { get; set; }
        /// <summary>Every step done - waiting for the operator's Verify.</summary>
        public bool AwaitingVerification { get; set; }
        public DateTime? VerifiedDate { get; set; }
        public string? VerifiedBy { get; set; }
        /// <summary>Tenant row created (date and time).</summary>
        public DateTime? TenantCreatedDate { get; set; }
        /// <summary>Start / finish time of each step, 0..7.</summary>
        public List<TenantProvisioningStepInfo> Steps { get; set; } = new();
    }

    public class SetEnabledRequest
    {
        public bool Enabled { get; set; }
    }

    public class DatabaseActionResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public TenantControlDto? Control { get; set; }
    }

    [HttpGet("control")]
    public async Task<ActionResult<TenantControlDto>> Get(int tenantId)
    {
        var control = await LoadAsync(tenantId);
        return control == null ? NotFound(new { message = "Tenant not found." }) : Ok(control);
    }

    [HttpPut("database/enabled")]
    public async Task<ActionResult<TenantControlDto>> SetEnabled(int tenantId, SetEnabledRequest request)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (tenant == null) return NotFound(new { message = "Tenant not found." });

        tenant.IsActive = request.Enabled;
        tenant.ModifiedDate = DateTime.UtcNow;
        var reg = await _db.TenantDatabases.FirstOrDefaultAsync(r => r.TenantId == tenantId);
        if (reg != null)
        {
            reg.IsEnabled = request.Enabled;
            reg.ModifiedDate = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(await LoadAsync(tenantId));
    }

    [HttpGet("database/progress")]
    public async Task<ActionResult<TenantDatabaseProgress>> Progress(int tenantId, CancellationToken ct)
    {
        var progress = await _databases.GetProgressAsync(tenantId, ct);
        return progress == null ? NotFound(new { message = "Tenant not found." }) : Ok(progress);
    }

    [HttpPost("database/verify")]
    public async Task<ActionResult<DatabaseActionResult>> Verify(int tenantId, CancellationToken ct)
    {
        var by = _currentUser.IsAuthenticated ? _currentUser.Email : null;
        return await RunAsync(tenantId, () => _databases.VerifyAsync(tenantId, by, ct));
    }

    [HttpPost("database/migrate")]
    public async Task<ActionResult<DatabaseActionResult>> Migrate(int tenantId, CancellationToken ct)
        => await RunAsync(tenantId, () => _databases.EnsureUpToDateAsync(tenantId, ct));

    [HttpPost("database/seed")]
    public async Task<ActionResult<DatabaseActionResult>> Seed(int tenantId, CancellationToken ct)
        => await RunAsync(tenantId, () => _databases.ReseedAsync(tenantId, ct));

    [HttpPost("database/backup")]
    public async Task<ActionResult<DatabaseActionResult>> Backup(int tenantId, CancellationToken ct)
        => await RunAsync(tenantId, () => _databases.BackupAsync(tenantId, ct));

    private async Task<ActionResult<DatabaseActionResult>> RunAsync(int tenantId, Func<Task<TenantDatabaseRunResult>> action)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId)) return NotFound(new { message = "Tenant not found." });
        var result = await action();
        _db.ChangeTracker.Clear();
        var body = new DatabaseActionResult { Success = result.Success, Message = result.Message, Control = await LoadAsync(tenantId) };
        return result.Success ? Ok(body) : BadRequest(body);
    }

    private async Task<TenantControlDto?> LoadAsync(int tenantId)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (tenant == null) return null;
        TenantDatabase? reg = null;
        try
        {
            reg = await _db.TenantDatabases.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // dbo.TenantDatabases not created yet - the background worker creates it on its first run.
        }
        return new TenantControlDto
        {
            TenantId = tenantId,
            DatabaseName = reg?.DatabaseName,
            ServerName = reg?.ServerName,
            SchemaVersion = reg?.SchemaVersion,
            SeedVersion = reg?.SeedVersion,
            LastBackupDate = reg?.LastBackupDate,
            DatabaseCreatedDate = reg != null && reg.ProvisioningStep > TenantDatabaseService.StepDatabase ? reg.CreatedDate : null,
            IsDatabaseEnabled = reg?.IsEnabled ?? tenant.IsActive,
            ProvisioningStep = reg?.ProvisioningStep ?? 0,
            ProvisioningError = reg?.Status == "Failed" || !string.IsNullOrEmpty(reg?.LastError) ? reg?.LastError : null,
            IsProvisioning = reg?.Status is "Pending" or "Running",
            OwnerMissing = reg != null && reg.ProvisioningStep == TenantDatabaseService.StepOwner && reg.Status == "Ready" && reg.OwnerUserId == null && string.IsNullOrEmpty(reg.OwnerPasswordHash),
            AwaitingVerification = reg != null && reg.ProvisioningStep == TenantDatabaseService.StepVerify && reg.Status == "Ready",
            VerifiedDate = reg?.VerifiedDate,
            VerifiedBy = reg?.VerifiedBy,
            TenantCreatedDate = tenant.CreatedDate,
            Steps = await _databases.GetStepsAsync(tenantId),
            OwnerFullName = reg?.OwnerEmail == null ? null : $"{reg.OwnerFirstName} {reg.OwnerLastName}".Trim(),
            OwnerEmail = reg?.OwnerEmail,
        };
    }
}
