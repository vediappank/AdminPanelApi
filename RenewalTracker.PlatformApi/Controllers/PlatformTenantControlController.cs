using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.Platform.Infrastructure.Services;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

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
///   PUT  api/platform/tenants/{id}/database/options    { databaseMode, databaseName, dbUser, dbPassword, runNow }  (Automatic / Manual)
///   GET  api/platform/tenants/database-defaults?tenantCode=   (Provision tenant form: default mode, suggested name, SQL login)
///   GET  api/platform/tenants/{id}/branding           company name + logo the Business Panel shows after login
///   PUT  api/platform/tenants/{id}/branding           { companyName, logoDataUrl }
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
        /// <summary>"Automatic" (the API creates the database) or "Manual" (created by you, e.g. in Plesk).</summary>
        public string? DatabaseMode { get; set; }
        /// <summary>True while the database step is not done yet - mode and name can still be changed.</summary>
        public bool CanChangeDatabase { get; set; }
        /// <summary>The SQL login the API uses - in Manual mode it must own the database you create, unless the
        /// database's own user is entered (DbUser).</summary>
        public string? SqlLogin { get; set; }
        /// <summary>Manual mode: the database's own SQL user (e.g. created by Plesk) the apps open it with. Null = SqlLogin.</summary>
        public string? DbUser { get; set; }
        /// <summary>A password is saved for DbUser (the password itself is never sent back).</summary>
        public bool HasDbPassword { get; set; }
        /// <summary>TenantDatabase:CredentialKey is set - a database user's password can be saved.</summary>
        public bool CanStoreDbPassword { get; set; }
        public string? ServerName { get; set; }
        public string? SchemaVersion { get; set; }
        public string? SeedVersion { get; set; }
        public DateTime? LastBackupDate { get; set; }
        public DateTime? DatabaseCreatedDate { get; set; }
        public bool IsDatabaseEnabled { get; set; }
        public int? ProvisioningStep { get; set; }
        /// <summary>Set only when the last run stopped with an error (Status = Failed).</summary>
        public string? ProvisioningError { get; set; }
        /// <summary>The last run finished, but reported something to check (e.g. a changed script, a missing master-data table).</summary>
        public string? ProvisioningWarning { get; set; }
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

    public class SetDatabaseOptionsRequest
    {
        public string? DatabaseMode { get; set; }
        public string? DatabaseName { get; set; }
        /// <summary>Manual: the database's own SQL user. Null = keep, "" = none (use the API's login).</summary>
        public string? DbUser { get; set; }
        /// <summary>Its password - empty = keep the saved one (same user). Stored encrypted.</summary>
        public string? DbPassword { get; set; }
        /// <summary>True = start provisioning straight away with the new choice (Run again).</summary>
        public bool RunNow { get; set; }
    }

    public class SetBrandingRequest
    {
        /// <summary>Shown in the Business Panel header. Empty = the tenant's name.</summary>
        public string? CompanyName { get; set; }
        /// <summary>Logo as a data URL (data:image/png;base64,...), max 300 KB. Empty = no logo (Bliss Point logo shown).</summary>
        public string? LogoDataUrl { get; set; }
        /// <summary>PDF template per document: classic / modern / elegant / minimal. Null = keep the current one.</summary>
        public string? QuotationTemplate { get; set; }
        public string? InvoiceTemplate { get; set; }
        public string? ReceiptTemplate { get; set; }
    }

    public class BrandingResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public TenantBranding? Branding { get; set; }
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

    [HttpGet("~/api/platform/tenants/database-defaults")]
    public async Task<ActionResult<TenantDatabaseDefaults>> Defaults([FromQuery] string? tenantCode, CancellationToken ct)
        => Ok(await _databases.GetDatabaseDefaultsAsync(tenantCode, ct));

    /// <summary>Automatic / Manual and the database name - only before the database step is done.</summary>
    [HttpPut("database/options")]
    public async Task<ActionResult<DatabaseActionResult>> SetDatabaseOptions(int tenantId, SetDatabaseOptionsRequest request, CancellationToken ct)
        => await RunAsync(tenantId, () => _databases.SetDatabaseOptionsAsync(
            tenantId, new TenantDatabaseOptions(request.DatabaseMode, request.DatabaseName, request.DbUser, request.DbPassword), request.RunNow, ct));

    [HttpGet("branding")]
    public async Task<ActionResult<TenantBranding>> GetBranding(int tenantId, CancellationToken ct)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return NotFound(new { message = "Tenant not found." });
        return Ok(await _databases.GetBrandingAsync(tenantId, ct));
    }

    [HttpPut("branding")]
    [RequestSizeLimit(1_000_000)]   // a 300 KB logo is about 400 KB as a data URL
    public async Task<ActionResult<BrandingResult>> SetBranding(int tenantId, SetBrandingRequest request, CancellationToken ct)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return NotFound(new { message = "Tenant not found." });
        var result = await _databases.SetBrandingAsync(tenantId, request.CompanyName, request.LogoDataUrl, ct,
            request.QuotationTemplate, request.InvoiceTemplate, request.ReceiptTemplate);
        var body = new BrandingResult { Success = result.Success, Message = result.Message, Branding = await _databases.GetBrandingAsync(tenantId, ct) };
        return result.Success ? Ok(body) : BadRequest(body);
    }

    /// <summary>Tenants list: company name + whether a logo is set, for every tenant (logos via GET .../{id}/logo).</summary>
    [HttpGet("~/api/platform/tenants/branding-summary")]
    public async Task<ActionResult<List<TenantBrandingSummary>>> BrandingSummary(CancellationToken ct)
        => Ok(await _databases.GetBrandingSummaryAsync(ct));

    /// <summary>Sample PDF of a template with this tenant's name and logo - Control Center template picker preview.</summary>
    [HttpGet("branding/preview")]
    public async Task<IActionResult> PreviewTemplate(int tenantId, [FromQuery] string? template, [FromQuery] string? document, CancellationToken ct)
    {
        var branding = await _databases.GetBrandingAsync(tenantId, ct);
        var name = string.IsNullOrWhiteSpace(branding.CompanyName) ? branding.DefaultName : branding.CompanyName;
        var pdf = TemplatePreview.Build(DocumentTemplates.Normalize(template), document, name ?? "Company", branding.LogoDataUrl);
        Response.Headers.ContentDisposition = "inline; filename=\"template-preview.pdf\"";
        return File(pdf, "application/pdf");
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
        var defaults = await _databases.GetDatabaseDefaultsAsync(tenant.TenantCode);
        return new TenantControlDto
        {
            TenantId = tenantId,
            DatabaseName = reg?.DatabaseName ?? defaults.SuggestedDatabaseName,
            DatabaseMode = reg?.DatabaseMode ?? defaults.DefaultMode,
            CanChangeDatabase = defaults.Enabled && (reg == null || (reg.ProvisioningStep <= TenantDatabaseService.StepDatabase && reg.Status != "Running")),
            SqlLogin = defaults.SqlLogin,
            DbUser = reg?.DatabaseMode == TenantDatabaseModes.Manual ? reg.DbUser : null,
            HasDbPassword = reg?.DatabaseMode == TenantDatabaseModes.Manual && !string.IsNullOrEmpty(reg.DbPassword),
            CanStoreDbPassword = defaults.CanStoreDbPassword,
            ServerName = reg?.ServerName,
            SchemaVersion = reg?.SchemaVersion,
            SeedVersion = reg?.SeedVersion,
            LastBackupDate = reg?.LastBackupDate,
            DatabaseCreatedDate = reg != null && reg.ProvisioningStep > TenantDatabaseService.StepDatabase ? reg.CreatedDate : null,
            IsDatabaseEnabled = reg?.IsEnabled ?? tenant.IsActive,
            ProvisioningStep = reg?.ProvisioningStep ?? 0,
            // LastError also carries the warnings of a run that finished - those are not a failure.
            ProvisioningError = reg?.Status == "Failed" ? (string.IsNullOrEmpty(reg.LastError) ? "Provisioning failed." : reg.LastError) : null,
            ProvisioningWarning = reg != null && reg.Status != "Failed" && !string.IsNullOrEmpty(reg.LastError) ? reg.LastError : null,
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
