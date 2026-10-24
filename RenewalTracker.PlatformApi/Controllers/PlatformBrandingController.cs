using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Services;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// The Admin Panel's own name and logo (sidebar, login page, footer) - Settings ▸ Branding.
/// Stored in SuperAdminPanel.dbo.PlatformBranding (one row, created here on first use).
///   GET api/platform/branding   anyone, also before login (the login page shows the logo)
///   PUT api/platform/branding   { companyName, logoDataUrl } - PLATFORM_BRANDING module
///                               (DatabaseScripts/Branding_SuperAdminPanel.sql adds the menu and grants it)
/// The logo is kept as a data URL (max 300 KB, see BrandingLogo), so no file hosting is needed.
/// </summary>
[ApiController]
[Route("api/platform/branding")]
public class PlatformBrandingController : ControllerBase
{
    public const string DefaultName = "Bliss Point Group";

    private readonly IConfiguration _config;
    private readonly ICurrentPlatformUserService _currentUser;
    private readonly ILogger<PlatformBrandingController> _logger;

    public PlatformBrandingController(IConfiguration config, ICurrentPlatformUserService currentUser, ILogger<PlatformBrandingController> logger)
    {
        _config = config;
        _currentUser = currentUser;
        _logger = logger;
    }

    public class BrandingDto
    {
        /// <summary>Shown in the footer, login page and browser tab. Never empty (default "Bliss Point Group").</summary>
        public string CompanyName { get; set; } = DefaultName;
        /// <summary>Data URL of the uploaded logo; null = the built-in Bliss Point logo.</summary>
        public string? LogoDataUrl { get; set; }
        /// <summary>PDF templates for the Admin Panel's own quotations / invoices / payment receipts (classic, modern, elegant, minimal).</summary>
        public string QuotationTemplate { get; set; } = DocumentTemplates.Classic;
        public string InvoiceTemplate { get; set; } = DocumentTemplates.Classic;
        public string ReceiptTemplate { get; set; } = DocumentTemplates.Classic;
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class SaveBrandingRequest
    {
        public string? CompanyName { get; set; }
        public string? LogoDataUrl { get; set; }
        /// <summary>Null = keep the current choice.</summary>
        public string? QuotationTemplate { get; set; }
        public string? InvoiceTemplate { get; set; }
        public string? ReceiptTemplate { get; set; }
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<BrandingDto>> Get(CancellationToken ct)
    {
        try
        {
            return Ok(await ReadAsync(ct));
        }
        catch (SqlException ex)
        {
            // never break the login page over branding
            _logger.LogWarning(ex, "Platform branding could not be read - showing the defaults");
            return Ok(new BrandingDto());
        }
    }

    [HttpPut]
    [RequirePlatformModule("PLATFORM_BRANDING")]
    [RequestSizeLimit(1_000_000)]   // a 300 KB logo is about 400 KB as a data URL
    public async Task<ActionResult<BrandingDto>> Save(SaveBrandingRequest request, CancellationToken ct)
    {
        if (BrandingLogo.NameError(request.CompanyName) is string nameError) return BadRequest(new { message = nameError });
        if (BrandingLogo.Error(request.LogoDataUrl) is string logoError) return BadRequest(new { message = logoError });
        foreach (var tpl in new[] { request.QuotationTemplate, request.InvoiceTemplate, request.ReceiptTemplate })
            if (!string.IsNullOrWhiteSpace(tpl) && !DocumentTemplates.IsKnown(tpl))
                return BadRequest(new { message = "Template must be classic, modern, elegant or minimal." });

        var by = string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
EXEC(N'
UPDATE dbo.PlatformBranding SET CompanyName = @name, LogoDataUrl = @logo, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @by,
       QuotationTemplate = COALESCE(@tq, QuotationTemplate), InvoiceTemplate = COALESCE(@ti, InvoiceTemplate), ReceiptTemplate = COALESCE(@tr, ReceiptTemplate)
 WHERE BrandingId = 1;
IF @@ROWCOUNT = 0
    INSERT dbo.PlatformBranding (BrandingId, CompanyName, LogoDataUrl, UpdatedAt, UpdatedBy, QuotationTemplate, InvoiceTemplate, ReceiptTemplate)
    VALUES (1, @name, @logo, SYSUTCDATETIME(), @by, @tq, @ti, @tr);',
     N'@name NVARCHAR(150), @logo NVARCHAR(MAX), @by NVARCHAR(200), @tq NVARCHAR(20), @ti NVARCHAR(20), @tr NVARCHAR(20)',
     @name = @name, @logo = @logo, @by = @by, @tq = @tq, @ti = @ti, @tr = @tr);", conn);
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, BrandingLogo.MaxNameLength).Value = (object?)BrandingLogo.CleanName(request.CompanyName) ?? DBNull.Value;
        cmd.Parameters.Add("@logo", System.Data.SqlDbType.NVarChar, -1).Value = (object?)BrandingLogo.CleanLogo(request.LogoDataUrl) ?? DBNull.Value;
        cmd.Parameters.Add("@by", System.Data.SqlDbType.NVarChar, 200).Value = by.Length > 200 ? by[..200] : by;
        cmd.Parameters.Add("@tq", System.Data.SqlDbType.NVarChar, 20).Value = Tpl(request.QuotationTemplate);
        cmd.Parameters.Add("@ti", System.Data.SqlDbType.NVarChar, 20).Value = Tpl(request.InvoiceTemplate);
        cmd.Parameters.Add("@tr", System.Data.SqlDbType.NVarChar, 20).Value = Tpl(request.ReceiptTemplate);
        await cmd.ExecuteNonQueryAsync(ct);
        return Ok(await ReadAsync(ct));
    }

    /// <summary>Sample PDF of a template with the Admin Panel's name and logo (Settings > Branding template picker).</summary>
    [HttpGet("preview")]
    [RequirePlatformModule("PLATFORM_BRANDING")]
    public async Task<IActionResult> Preview([FromQuery] string? template, [FromQuery] string? document, CancellationToken ct)
    {
        var branding = await ReadAsync(ct);
        var pdf = TemplatePreview.Build(DocumentTemplates.Normalize(template), document, branding.CompanyName, branding.LogoDataUrl, "AED");
        Response.Headers.ContentDisposition = "inline; filename=\"template-preview.pdf\"";
        return File(pdf, "application/pdf");
    }

    private async Task<BrandingDto> ReadAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("EXEC(N'SELECT CompanyName, LogoDataUrl, UpdatedAt, UpdatedBy, QuotationTemplate, InvoiceTemplate, ReceiptTemplate FROM dbo.PlatformBranding WHERE BrandingId = 1')", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new BrandingDto();
        return new BrandingDto
        {
            CompanyName = r.IsDBNull(0) || string.IsNullOrWhiteSpace(r.GetString(0)) ? DefaultName : r.GetString(0),
            LogoDataUrl = r.IsDBNull(1) ? null : r.GetString(1),
            UpdatedAt = r.IsDBNull(2) ? null : r.GetDateTime(2),
            UpdatedBy = r.IsDBNull(3) ? null : r.GetString(3),
            QuotationTemplate = DocumentTemplates.Normalize(r.IsDBNull(4) ? null : r.GetString(4)),
            InvoiceTemplate = DocumentTemplates.Normalize(r.IsDBNull(5) ? null : r.GetString(5)),
            ReceiptTemplate = DocumentTemplates.Normalize(r.IsDBNull(6) ? null : r.GetString(6)),
        };
    }

    /// <summary>Opens SuperAdminPanel and creates dbo.PlatformBranding the first time (no script needed for the table).</summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing."));
        await conn.OpenAsync(ct);
        // table + the template columns (QuotationTemplate / InvoiceTemplate / ReceiptTemplate)
        await using var ensure = new SqlCommand(PlatformDocumentBranding.EnsureSql, conn);
        await ensure.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static object Tpl(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : DocumentTemplates.Normalize(value);
}
