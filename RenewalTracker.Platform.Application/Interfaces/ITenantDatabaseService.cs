using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Creates and maintains each tenant's own database automatically - see
/// TenantDatabaseService and TenantDatabase/README.md.
/// </summary>
public interface ITenantDatabaseService
{
    /// <summary>Creates the tenant database if missing, then applies every pending Schema and MasterData script. Never throws - failures are stored on dbo.TenantDatabases.</summary>
    Task<TenantDatabaseRunResult> EnsureUpToDateAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Provision tenant / Complete provisioning: records the owner login (password stored only as a
    /// BCrypt hash) and queues the tenant for the background worker, which creates the database,
    /// schema, master data and the owner user straight away. Returns at once - progress is shown live
    /// on the Control Center.
    /// </summary>
    Task QueueProvisioningAsync(int tenantId, TenantOwnerLogin? owner, TenantSettings? settings = null, TenantDatabaseOptions? database = null, CancellationToken ct = default);

    /// <summary>
    /// Changes how the tenant's database is created (Automatic / Manual) and its name - only while the
    /// database step has not been done yet. runNow = queue the provisioning run straight away.
    /// </summary>
    Task<TenantDatabaseRunResult> SetDatabaseOptionsAsync(int tenantId, TenantDatabaseOptions options, bool runNow, CancellationToken ct = default);

    /// <summary>Checks a mode / name before anything is created (Provision tenant). forTenantId = the tenant being
    /// completed (its own database name is fine). Returns an error text, or null when fine.</summary>
    Task<string?> ValidateDatabaseOptionsAsync(TenantDatabaseOptions? options, int? forTenantId, CancellationToken ct = default);

    /// <summary>Default mode, suggested database name and the API's SQL login - for the Provision tenant form.</summary>
    Task<TenantDatabaseDefaults> GetDatabaseDefaultsAsync(string? tenantCode, CancellationToken ct = default);

    /// <summary>Re-runs every MasterData script (they must be safe to run twice).</summary>
    Task<TenantDatabaseRunResult> ReseedAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Full COPY_ONLY backup of the tenant database.</summary>
    Task<TenantDatabaseRunResult> BackupAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Runs EnsureUpToDateAsync for every tenant whose database is enabled (used by the background worker).
    /// registryOnly = just create / upgrade SuperAdminPanel.dbo.TenantDatabases.</summary>
    Task MigrateAllAsync(CancellationToken ct = default, bool registryOnly = false);

    /// <summary>Business-side setup finished (only when the Business API call is on) - logged.</summary>
    Task MarkActiveAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Operator confirms that every step is done: provisioning complete (step 8).</summary>
    Task<TenantDatabaseRunResult> VerifyAsync(int tenantId, string? verifiedBy, CancellationToken ct = default);

    /// <summary>Start / finish time of each provisioning step (dbo.TenantProvisioningSteps).</summary>
    Task<List<TenantProvisioningStepInfo>> GetStepsAsync(int tenantId, CancellationToken ct = default);

    /// <summary>The company name and logo the Business Panel shows after this tenant's users log in
    /// (tenant database dbo.ApplicationSettings "Tenant.Name" / "Tenant.Logo").</summary>
    Task<TenantBranding> GetBrandingAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Saves the name (empty = the tenant's name) and logo (data URL; empty = no logo, the Bliss Point logo is shown),
    /// and the PDF template of each document (classic / modern / elegant / minimal; null = keep).</summary>
    Task<TenantDatabaseRunResult> SetBrandingAsync(int tenantId, string? companyName, string? logoDataUrl, CancellationToken ct = default,
        string? quotationTemplate = null, string? invoiceTemplate = null, string? receiptTemplate = null);

    /// <summary>Company name / logo of every tenant for the Tenants list - from the SuperAdminPanel copy
    /// (dbo.TenantBrandingCache, refreshed whenever branding is read or saved; tenants never seen are read once).</summary>
    Task<List<TenantBrandingSummary>> GetBrandingSummaryAsync(CancellationToken ct = default);

    /// <summary>One tenant's logo bytes from the SuperAdminPanel copy, or null.</summary>
    Task<(byte[] Bytes, string ContentType, DateTime UpdatedAt)?> GetCachedLogoAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Read-only: what the tenant database should have (from the scripts and the template list)
    /// against what it actually has - for the Control Center "Provisioning details" screen.</summary>
    Task<TenantDatabaseProgress?> GetProgressAsync(int tenantId, CancellationToken ct = default);
}

public record TenantDatabaseRunResult(bool Success, string Message);

/// <summary>One provisioning step: 0 registered ... 7 verified. Detail = what it did (e.g. "27 of 40 menus on").</summary>
public record TenantProvisioningStepInfo(int StepNo, string StepName, DateTime? StartedDate, DateTime? CompletedDate, string? Detail);

/// <summary>Tenant configuration entered on Provision tenant (empty = default: dd-MM-yyyy, UTC, USD).</summary>
public record TenantSettings(string? DateFormat, string? TimeZone, string? Currency);

/// <summary>Database creation modes - see TenantDatabase.DatabaseMode.</summary>
public static class TenantDatabaseModes
{
    public const string Automatic = "Automatic";
    public const string Manual = "Manual";

    /// <summary>"manual" / "Manual" -> Manual, anything else (or empty) -> null.</summary>
    public static string? Normalize(string? mode) =>
        string.Equals(mode?.Trim(), Manual, StringComparison.OrdinalIgnoreCase) ? Manual
        : string.Equals(mode?.Trim(), Automatic, StringComparison.OrdinalIgnoreCase) ? Automatic
        : null;
}

/// <summary>Chosen on Provision tenant / the provisioning screen. Null values = keep / use the default
/// (mode: TenantDatabase:DefaultMode, name: prefix + tenant code).
/// Manual mode: DbUser / DbPassword = the database's own SQL user (the one Plesk creates with the database).
/// DbUser null = keep, "" = none (use the API's own login). DbPassword empty = keep the saved one (same user only).</summary>
public record TenantDatabaseOptions(string? Mode, string? DatabaseName, string? DbUser = null, string? DbPassword = null);

/// <summary>What the Provision tenant form pre-selects. SqlLogin = the login the API connects with -
/// in Manual mode either that login owns the database you create, or you enter the database's own user.
/// CanStoreDbPassword = TenantDatabase:CredentialKey is set (needed to save a database user's password).</summary>
public record TenantDatabaseDefaults(string DefaultMode, string SuggestedDatabaseName, string Prefix, string? SqlLogin, bool Enabled, bool CanStoreDbPassword);

/// <summary>Business Panel branding of one tenant. Available = false while its database has no settings table yet
/// (Message says why); DefaultName = the tenant's name, used when no company name is set.</summary>
public record TenantBranding(bool Available, string? Message, string? CompanyName, string? LogoDataUrl, string DefaultName,
    string QuotationTemplate = "classic", string InvoiceTemplate = "classic", string ReceiptTemplate = "classic");

/// <summary>Tenants list: the company name the Business Panel shows and whether a logo is set (GET .../{id}/logo serves it).</summary>
public record TenantBrandingSummary(int TenantId, string? CompanyName, bool HasLogo, DateTime? UpdatedAt);

/// <summary>The tenant's first admin (owner) login entered on Provision tenant.</summary>
public record TenantOwnerLogin(string FirstName, string? LastName, string Email, string Password);

/// <summary>Provisioning details of one tenant database. Null DatabaseName = no database registered yet.</summary>
public record TenantDatabaseProgress(
    int TenantId,
    string? DatabaseName,
    bool DatabaseExists,
    string? TemplateDatabase,
    bool TemplateExists,
    int ProvisioningStep,
    string? Status,
    string? LastError,
    DateTime? LastMigratedDate,
    DateTime CheckedDate,
    List<TenantDatabaseObjectProgress> Objects,
    List<TenantMasterDataProgress> MasterData,
    List<TenantScriptProgress> Scripts);

/// <summary>One table / view / procedure / function the Schema scripts create. Rows only for tables.</summary>
public record TenantDatabaseObjectProgress(string Name, string Type, bool Exists, long? Rows);

/// <summary>One master-data table copied from the template database.</summary>
public record TenantMasterDataProgress(string Table, bool Copied, DateTime? CopiedDate, long? TenantRows, long? TemplateRows);

/// <summary>One script file. Status: Applied, Pending, Changed (MasterData: re-runs on the next run; Schema: never re-run).</summary>
public record TenantScriptProgress(string Kind, string Name, string Status, DateTime? AppliedDate);
