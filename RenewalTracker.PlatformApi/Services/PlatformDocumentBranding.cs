using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// The Admin Panel's own name, logo and document templates (Settings ▸ Branding, dbo.PlatformBranding) as the
/// quotation / invoice / receipt PDFs need them. Never throws - a missing table or row gives the defaults
/// (Bliss Point Group, no logo, Classic), so a PDF is never blocked by branding.
/// </summary>
public sealed record PlatformDocumentBranding(string CompanyName, string? LogoDataUrl, string QuotationTemplate, string InvoiceTemplate, string ReceiptTemplate)
{
    public const string DefaultName = "Bliss Point Group";

    public static PlatformDocumentBranding Default => new(DefaultName, null, DocumentTemplates.Classic, DocumentTemplates.Classic, DocumentTemplates.Classic);

    /// <summary>Creates dbo.PlatformBranding and its template columns when missing (safe to run every time).</summary>
    public const string EnsureSql = @"
IF OBJECT_ID(N'dbo.PlatformBranding', N'U') IS NULL
CREATE TABLE dbo.PlatformBranding
(
    BrandingId   INT            NOT NULL CONSTRAINT PK_PlatformBranding PRIMARY KEY CONSTRAINT CK_PlatformBranding_OneRow CHECK (BrandingId = 1),
    CompanyName  NVARCHAR(150)  NULL,
    LogoDataUrl  NVARCHAR(MAX)  NULL,
    UpdatedAt    DATETIME2(0)   NULL,
    UpdatedBy    NVARCHAR(200)  NULL
);
IF COL_LENGTH(N'dbo.PlatformBranding', N'QuotationTemplate') IS NULL ALTER TABLE dbo.PlatformBranding ADD QuotationTemplate NVARCHAR(20) NULL;
IF COL_LENGTH(N'dbo.PlatformBranding', N'InvoiceTemplate') IS NULL ALTER TABLE dbo.PlatformBranding ADD InvoiceTemplate NVARCHAR(20) NULL;
IF COL_LENGTH(N'dbo.PlatformBranding', N'ReceiptTemplate') IS NULL ALTER TABLE dbo.PlatformBranding ADD ReceiptTemplate NVARCHAR(20) NULL;";

    public static async Task<PlatformDocumentBranding> ReadAsync(PlatformDbContext db, CancellationToken ct = default)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == ConnectionState.Open;
            if (!wasOpen) await conn.OpenAsync(ct);
            try
            {
                await using (var ensure = conn.CreateCommand())
                {
                    ensure.CommandText = EnsureSql;
                    if (db.Database.CurrentTransaction != null) ensure.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
                    await ensure.ExecuteNonQueryAsync(ct);
                }
                await using var cmd = conn.CreateCommand();
                if (db.Database.CurrentTransaction != null) cmd.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
                // the template columns may have just been added - read them through dynamic SQL so the batch compiles
                cmd.CommandText = "EXEC(N'SELECT CompanyName, LogoDataUrl, QuotationTemplate, InvoiceTemplate, ReceiptTemplate FROM dbo.PlatformBranding WHERE BrandingId = 1')";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Default;
                return new PlatformDocumentBranding(
                    Str(r, 0) is { Length: > 0 } name ? name : DefaultName,
                    Str(r, 1),
                    DocumentTemplates.Normalize(Str(r, 2)),
                    DocumentTemplates.Normalize(Str(r, 3)),
                    DocumentTemplates.Normalize(Str(r, 4)));
            }
            finally
            {
                if (!wasOpen) await conn.CloseAsync();
            }
        }
        catch (DbException)
        {
            return Default;
        }
    }

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
}
