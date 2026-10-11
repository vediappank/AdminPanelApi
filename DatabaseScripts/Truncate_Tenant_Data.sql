/*
  ============================================================================
  Start fresh: clear ALL tenant data and ALL sales workflow data
  from the Admin Panel database (SuperAdminPanel)
  ============================================================================
  TENANT data (always cleared):
    dbo.TenantDatabases            registry of each tenant's own database
    dbo.TenantProvisioningSteps    start / finish time of each provisioning step
    dbo.TenantMenuAssignments      Business Panel menus given to the tenant
    dbo.DeviceActivations, dbo.AppConfigs, dbo.ApplicationConfiguration
    dbo.TenantLicenses, dbo.TenantModules
    dbo.Payments                   tenant / licence payments (ALL payments when @ClearSalesWorkflow = 1)
    dbo.Licenses, dbo.Tenants

  SALES WORKFLOW data (cleared when @ClearSalesWorkflow = 1, the default):
    dbo.SalesInvoices              invoices
    dbo.QuotationServiceLines      quotation service lines
    dbo.QuotationProducts          quotation products
    dbo.Quotations                 quotations
    dbo.SalesLeads                 leads
    dbo.SalesFollowUps             follow-ups
    dbo.EnquiryRequirementProducts enquiry requirements (+ older EnquiryRequirementServices / EnquiryRequirements if present)
    dbo.SalesEnquiries             enquiries
    -> Enquiry -> Follow-up -> Quotation -> Invoice -> Lead -> Tenant all start from zero,
       and quotation / invoice / enquiry numbers start again at 1.

  KEPT (master data / setup):
    TenantMenuItems (menu master), TenantModuleCatalog, Modules, Permissions,
    PlatformRoles, PlatformRoleModules, PlatformUsers (your logins), Applications,
    ProductServices, ServiceCategories, ServiceSubCategories, Services, SalesPicklists.

  Options:
    @ClearSalesWorkflow   1 (default) = also clear enquiries, follow-ups, leads, quotations,
                          invoices and all payments. 0 = tenants only; leads can then be
                          converted again (see @ResetLeadConversions).
    @ResetLeadConversions 1 (default) = with @ClearSalesWorkflow = 0, remove the automatic
                          "Lead to Tenant" follow-ups and re-sync each enquiry's status.
    @DropTenantDatabases  1 (default) = also DROP every tenant database (BP_<CODE>) listed in
                          dbo.TenantDatabases, so a new tenant with the same code starts
                          empty. With @Backup = 1 each one is first backed up to
                          <name>_bak_<yyyyMMdd_HHmm>.bak in SQL Server's backup folder.

  Why DELETE + RESEED instead of TRUNCATE TABLE:
    SQL Server refuses TRUNCATE on any table referenced by a foreign key, even when
    empty. DELETE in child -> parent order, then DBCC CHECKIDENT, gives the same end
    result: empty tables, ids restart at 1.

  Safety:
    1. Nothing runs unless @Confirm is set to 'DELETE-ALL-TENANTS'.
    2. @Backup = 1 (default) copies every affected table into schema
       [bak_<yyyyMMdd_HHmm>] first, so the rows can be restored.
    3. All deletes run in ONE transaction - any error rolls it all back.

  Not covered here: the shared BusinessPanel database (tenants provisioned before
  database-per-tenant may have rows there).
  ============================================================================
*/
/* Run the WHOLE script (F5 with nothing selected). Only change the value on the @Confirm line below. */
USE [SuperAdminPanel];

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Confirm NVARCHAR(50)       = N'DELETE-ALL-TENANTS'  -- set to N'DELETE-ALL-TENANTS' to run
DECLARE @Backup  BIT                = 1;     -- 1 = copy affected tables to a backup schema first
DECLARE @ClearSalesWorkflow BIT     = 1;     -- 1 = also clear enquiries, follow-ups, leads, quotations, invoices, payments
DECLARE @ResetLeadConversions BIT   = 1;     -- only used when @ClearSalesWorkflow = 0
DECLARE @DropTenantDatabases BIT    = 1;     -- 1 = also DROP every tenant database (BP_<CODE>); with @Backup = 1 a .bak is taken first

IF @Confirm <> N'DELETE-ALL-TENANTS'
BEGIN
    RAISERROR(N'Nothing deleted. Set @Confirm = N''DELETE-ALL-TENANTS'' to run this script.', 16, 1);
    RETURN;
END;

-- Tables in delete order (children first). Payments is handled separately.
DECLARE @Tables TABLE (Ord INT PRIMARY KEY, Name SYSNAME, Workflow BIT NOT NULL);
INSERT @Tables (Ord, Name, Workflow) VALUES
    ( 0, N'TenantProvisioningSteps',    0),
    ( 1, N'TenantDatabases',            0),
    ( 2, N'TenantMenuAssignments',      0),
    ( 3, N'DeviceActivations',          0),
    ( 4, N'AppConfigs',                 0),
    ( 5, N'ApplicationConfiguration',   0),
    ( 6, N'TenantLicenses',             0),
    ( 7, N'TenantModules',              0),
    ( 8, N'Payments',                   0),
    ( 9, N'Licenses',                   0),
    (10, N'Tenants',                    0),
    (11, N'SalesInvoices',              1),
    (12, N'QuotationServiceLines',      1),
    (13, N'QuotationProducts',          1),
    (14, N'Quotations',                 1),
    (15, N'SalesLeads',                 1),
    (16, N'SalesFollowUps',             1),
    (17, N'EnquiryRequirementServices', 1),   -- older table, skipped if not there
    (18, N'EnquiryRequirementProducts', 1),
    (19, N'EnquiryRequirements',        1),   -- older table, skipped if not there
    (20, N'SalesEnquiries',             1);

IF @ClearSalesWorkflow = 0
    DELETE FROM @Tables WHERE Workflow = 1;

DECLARE @sql NVARCHAR(MAX), @name SYSNAME, @ord INT, @rows INT;

-- ---------------------------------------------------------------- backup
IF @Backup = 1
BEGIN
    DECLARE @schema SYSNAME = N'bak_' + FORMAT(SYSDATETIME(), 'yyyyMMdd_HHmm');
    IF SCHEMA_ID(@schema) IS NULL
        EXEC (N'CREATE SCHEMA ' + N'[' + @schema + N']');

    DECLARE b CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @Tables ORDER BY Ord;
    OPEN b; FETCH NEXT FROM b INTO @name;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(N'dbo.' + QUOTENAME(@name), N'U') IS NOT NULL
           AND OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name), N'U') IS NULL
        BEGIN
            SET @sql = N'SELECT * INTO ' + QUOTENAME(@schema) + N'.' + QUOTENAME(@name) + N' FROM dbo.' + QUOTENAME(@name) + N';';
            EXEC sp_executesql @sql;
            PRINT N'Backed up dbo.' + @name + N' -> ' + @schema + N'.' + @name;
        END;
        FETCH NEXT FROM b INTO @name;
    END;
    CLOSE b; DEALLOCATE b;
END;

-- Tenant databases to drop (read before the registry rows are deleted).
DECLARE @DropDbs TABLE (DatabaseName SYSNAME PRIMARY KEY);
IF @DropTenantDatabases = 1 AND OBJECT_ID(N'dbo.TenantDatabases', N'U') IS NOT NULL
    INSERT @DropDbs (DatabaseName)
        SELECT DISTINCT DatabaseName FROM dbo.TenantDatabases
         WHERE DatabaseName IS NOT NULL AND DB_ID(DatabaseName) IS NOT NULL
           AND DatabaseName NOT IN (N'master', N'model', N'msdb', N'tempdb', N'BusinessPanel', DB_NAME());

-- ---------------------------------------------------------------- delete
BEGIN TRANSACTION;

DECLARE d CURSOR LOCAL FAST_FORWARD FOR SELECT Ord, Name FROM @Tables ORDER BY Ord;
OPEN d; FETCH NEXT FROM d INTO @ord, @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + QUOTENAME(@name), N'U') IS NOT NULL
    BEGIN
        IF @name = N'Payments' AND @ClearSalesWorkflow = 0
            -- Tenants only: quotation payments taken before a tenant existed stay.
            SET @sql = N'DELETE FROM dbo.Payments WHERE TenantId IS NOT NULL OR LicenseId IS NOT NULL; SET @r = @@ROWCOUNT;';
        ELSE
            SET @sql = N'DELETE FROM dbo.' + QUOTENAME(@name) + N'; SET @r = @@ROWCOUNT;';

        EXEC sp_executesql @sql, N'@r INT OUTPUT', @r = @rows OUTPUT;
        PRINT N'dbo.' + @name + N': ' + CAST(@rows AS NVARCHAR(20)) + N' row(s) deleted';

        -- Restart identity numbering (empty table -> next id 1; Payments kept rows -> next after the highest).
        -- (Only when rows were deleted: reseeding a never-used table to 0 would make its first id 0.)
        IF @rows > 0 AND OBJECTPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME(@name)), N'TableHasIdentity') = 1
        BEGIN
            SET @sql = N'DECLARE @max INT = (SELECT ISNULL(MAX($IDENTITY), 0) FROM dbo.' + QUOTENAME(@name) + N');
                         DBCC CHECKIDENT (''dbo.' + @name + N''', RESEED, @max) WITH NO_INFOMSGS;';
            EXEC sp_executesql @sql;
        END;
    END
    ELSE
        PRINT N'dbo.' + @name + N': table not found, skipped';

    FETCH NEXT FROM d INTO @ord, @name;
END;
CLOSE d; DEALLOCATE d;

-- ---------------------------------------------------------------- lead conversions (tenants-only mode)
IF @ClearSalesWorkflow = 0 AND @ResetLeadConversions = 1 AND OBJECT_ID(N'dbo.SalesFollowUps', N'U') IS NOT NULL
BEGIN
    DECLARE @Touched TABLE (SalesEnquiryId INT PRIMARY KEY);
    INSERT @Touched (SalesEnquiryId)
        SELECT DISTINCT SalesEnquiryId FROM dbo.SalesFollowUps WHERE Status = N'Lead to Tenant';

    DELETE FROM dbo.SalesFollowUps WHERE Status = N'Lead to Tenant';
    PRINT N'dbo.SalesFollowUps: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' "Lead to Tenant" entries removed';

    -- Enquiry.Status = Status of its latest remaining follow-up (latest NextFollowUpDate/FollowUpDate, then highest id).
    UPDATE e
       SET e.Status = x.Status, e.ModifiedDate = SYSUTCDATETIME()
      FROM dbo.SalesEnquiries e
      JOIN @Touched t ON t.SalesEnquiryId = e.SalesEnquiryId
     CROSS APPLY (SELECT TOP (1) f.Status
                    FROM dbo.SalesFollowUps f
                   WHERE f.SalesEnquiryId = e.SalesEnquiryId
                   ORDER BY COALESCE(f.NextFollowUpDate, f.FollowUpDate) DESC, f.SalesFollowUpId DESC) x;
    PRINT N'dbo.SalesEnquiries: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' enquiry status(es) re-synced';
END;

COMMIT TRANSACTION;
PRINT CASE WHEN @ClearSalesWorkflow = 1
           THEN N'Done. All tenant and sales workflow data removed from SuperAdminPanel.'
           ELSE N'Done. All tenant data removed from SuperAdminPanel.' END;

-- ---------------------------------------------------------------- drop tenant databases
-- (DROP DATABASE cannot run inside a transaction, so this runs after the commit.)
DECLARE x CURSOR LOCAL FAST_FORWARD FOR SELECT DatabaseName FROM @DropDbs;
OPEN x; FETCH NEXT FROM x INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        IF @Backup = 1
        BEGIN
            SET @sql = N'BACKUP DATABASE ' + QUOTENAME(@name) + N' TO DISK = N''' + REPLACE(@name, N'''', N'''''')
                     + N'_bak_' + FORMAT(SYSDATETIME(), 'yyyyMMdd_HHmm') + N'.bak'' WITH COPY_ONLY, INIT;';
            EXEC (@sql);
            PRINT N'Backed up database ' + @name;
        END;
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
        EXEC (@sql);
        PRINT N'Dropped database ' + @name;
    END TRY
    BEGIN CATCH
        PRINT N'Could not drop ' + @name + N': ' + ERROR_MESSAGE();
    END CATCH;
    FETCH NEXT FROM x INTO @name;
END;
CLOSE x; DEALLOCATE x;
GO

/*
  ---------------------------------------------------------------------------
  Verify (expect 0 everywhere)
  ---------------------------------------------------------------------------
SELECT 'Tenants' T, COUNT(*) N FROM dbo.Tenants
UNION ALL SELECT 'TenantDatabases', COUNT(*) FROM dbo.TenantDatabases
UNION ALL SELECT 'TenantMenuAssignments', COUNT(*) FROM dbo.TenantMenuAssignments
UNION ALL SELECT 'TenantModules', COUNT(*) FROM dbo.TenantModules
UNION ALL SELECT 'TenantLicenses', COUNT(*) FROM dbo.TenantLicenses
UNION ALL SELECT 'Licenses', COUNT(*) FROM dbo.Licenses
UNION ALL SELECT 'Payments', COUNT(*) FROM dbo.Payments
UNION ALL SELECT 'DeviceActivations', COUNT(*) FROM dbo.DeviceActivations
UNION ALL SELECT 'AppConfigs', COUNT(*) FROM dbo.AppConfigs
UNION ALL SELECT 'ApplicationConfiguration', COUNT(*) FROM dbo.ApplicationConfiguration
UNION ALL SELECT 'SalesInvoices', COUNT(*) FROM dbo.SalesInvoices
UNION ALL SELECT 'QuotationServiceLines', COUNT(*) FROM dbo.QuotationServiceLines
UNION ALL SELECT 'QuotationProducts', COUNT(*) FROM dbo.QuotationProducts
UNION ALL SELECT 'Quotations', COUNT(*) FROM dbo.Quotations
UNION ALL SELECT 'SalesLeads', COUNT(*) FROM dbo.SalesLeads
UNION ALL SELECT 'SalesFollowUps', COUNT(*) FROM dbo.SalesFollowUps
UNION ALL SELECT 'EnquiryRequirementProducts', COUNT(*) FROM dbo.EnquiryRequirementProducts
UNION ALL SELECT 'SalesEnquiries', COUNT(*) FROM dbo.SalesEnquiries;

  ---------------------------------------------------------------------------
  Restore from the backup schema (replace bak_yyyyMMdd_HHmm with the real name).
  Restore parents first: SalesEnquiries, SalesFollowUps, SalesLeads, Quotations,
  QuotationProducts, QuotationServiceLines, SalesInvoices, Tenants, Licenses, then the rest.
  Example for one table:
  ---------------------------------------------------------------------------
SET IDENTITY_INSERT dbo.Tenants ON;
INSERT dbo.Tenants (TenantId, TenantCode, TenantName, ContactName, ContactEmail, ContactMobile, IsActive, CreatedDate, ModifiedDate, LeadId)
SELECT TenantId, TenantCode, TenantName, ContactName, ContactEmail, ContactMobile, IsActive, CreatedDate, ModifiedDate, LeadId
FROM [bak_yyyyMMdd_HHmm].Tenants;
SET IDENTITY_INSERT dbo.Tenants OFF;

  When you no longer need the backup, drop every table in the schema, then the schema:
SELECT 'DROP TABLE [bak_yyyyMMdd_HHmm].' + QUOTENAME(name) + ';' FROM sys.tables WHERE schema_id = SCHEMA_ID('bak_yyyyMMdd_HHmm');
DROP SCHEMA [bak_yyyyMMdd_HHmm];
*/
