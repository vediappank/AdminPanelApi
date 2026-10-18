using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// Database-per-tenant registry (dbo.TenantDatabases) - one row per tenant, written
/// automatically by TenantDatabaseService: the customer database it created, which
/// numbered scripts from TenantDatabase/Schema and TenantDatabase/MasterData have run,
/// and where provisioning got to. Shown on the Tenant Control Center.
/// </summary>
public class TenantDatabase
{
    [Key]
    public int TenantDatabaseId { get; set; }

    public int TenantId { get; set; }

    [MaxLength(128)]
    public string DatabaseName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? ServerName { get; set; }

    /// <summary>How the database comes into existence: "Automatic" = the API runs CREATE DATABASE itself
    /// (its SQL login needs CREATE DATABASE permission); "Manual" = an operator creates it first
    /// (e.g. Plesk ▸ Databases ▸ Add Database, exact name = DatabaseName, API login as owner) and
    /// provisioning only checks that it exists and can be used, then fills it.</summary>
    [MaxLength(10)]
    public string DatabaseMode { get; set; } = "Automatic";

    /// <summary>Manual mode only: the database's own SQL user (e.g. the user Plesk creates together with the
    /// database). When set, provisioning and the Business Panel open this database with this user instead of
    /// their own SQL logins - for servers where those logins cannot be added to the database.
    /// Null = the API's / Business Panel's own logins are used.</summary>
    [MaxLength(128)]
    public string? DbUser { get; set; }

    /// <summary>Password of <see cref="DbUser"/>, encrypted (TenantDbSecret, key TenantDatabase:CredentialKey).
    /// Never the plain password.</summary>
    [MaxLength(500)]
    public string? DbPassword { get; set; }

    /// <summary>Last Schema script applied, e.g. "003_Add_Quotation_Items.sql".</summary>
    [MaxLength(260)]
    public string? SchemaVersion { get; set; }

    /// <summary>Last MasterData script applied.</summary>
    [MaxLength(260)]
    public string? SeedVersion { get; set; }

    /// <summary>Index of the step in progress (Control Center list): 0 registered, 1 database, 2 schema,
    /// 3 master data, 4 tenant settings, 5 menus &amp; modules, 6 Administrator role, 7 owner user, 8 verify, 9 = complete.
    /// Every step below it is done.</summary>
    public int ProvisioningStep { get; set; }

    /// <summary>Pending (queued) / Running / Ready / Failed.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "Pending";

    public string? LastError { get; set; }

    /// <summary>False = the database is switched off on the Control Center; automatic migration skips it.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedDate { get; set; }

    public DateTime? LastMigratedDate { get; set; }

    public DateTime? LastBackupDate { get; set; }

    public DateTime? ModifiedDate { get; set; }

    // ---- owner login, created inside the tenant database (dbo.Users, Administrator role)

    [MaxLength(100)]
    public string? OwnerFirstName { get; set; }

    [MaxLength(100)]
    public string? OwnerLastName { get; set; }

    [MaxLength(200)]
    public string? OwnerEmail { get; set; }

    /// <summary>BCrypt hash of the admin password from Provision tenant (never the password itself);
    /// cleared once the owner user exists in the tenant database.</summary>
    [MaxLength(200)]
    public string? OwnerPasswordHash { get; set; }

    /// <summary>dbo.Users.UserId of the owner inside the tenant database, once created.</summary>
    public int? OwnerUserId { get; set; }

    /// <summary>Tenant settings from Provision tenant (date format, time zone, currency, ...) waiting to be
    /// written into the tenant's dbo.ApplicationSettings; cleared once written.</summary>
    public string? SettingsJson { get; set; }

    /// <summary>When an operator verified the finished provisioning on the Control Center.</summary>
    public DateTime? VerifiedDate { get; set; }

    [MaxLength(200)]
    public string? VerifiedBy { get; set; }
}
