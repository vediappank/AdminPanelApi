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
    Task QueueProvisioningAsync(int tenantId, TenantOwnerLogin? owner, TenantSettings? settings = null, CancellationToken ct = default);

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

    /// <summary>Read-only: what the tenant database should have (from the scripts and the template list)
    /// against what it actually has - for the Control Center "Provisioning details" screen.</summary>
    Task<TenantDatabaseProgress?> GetProgressAsync(int tenantId, CancellationToken ct = default);
}

public record TenantDatabaseRunResult(bool Success, string Message);

/// <summary>One provisioning step: 0 registered ... 7 verified. Detail = what it did (e.g. "27 of 40 menus on").</summary>
public record TenantProvisioningStepInfo(int StepNo, string StepName, DateTime? StartedDate, DateTime? CompletedDate, string? Detail);

/// <summary>Tenant configuration entered on Provision tenant (empty = default: dd-MM-yyyy, UTC, USD).</summary>
public record TenantSettings(string? DateFormat, string? TimeZone, string? Currency);

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
