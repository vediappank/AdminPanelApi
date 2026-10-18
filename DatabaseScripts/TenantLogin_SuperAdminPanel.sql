/*
  Tenant login directory - run on the SuperAdminPanel database (safe to run again)
  ==================================================================================
  The Business Panel API uses these objects to find a user's company at login:

    dbo.TenantUsers            e-mail -> TenantId (one row per Business Panel user, no passwords)
    usp_TenantLogin_FindByEmail    login:        e-mail -> company, its database, status, licence
    usp_TenantLogin_GetById        every request: TenantId (from the token) -> the same details
    usp_TenantLogin_GetByCode      public PDF links: TenantCode -> the same details
    usp_TenantLogin_List           daily reminder run: every company
    usp_TenantUsers_Add            Business Panel "add user"          (refuses an e-mail of another company / over the user limit)
    usp_TenantUsers_ChangeEmail    Business Panel "change user e-mail"
    usp_TenantUsers_Remove         Business Panel "delete user"
    usp_TenantUsers_SyncFromTenantDatabases   fills TenantUsers from every tenant database's dbo.Users (run once now, then any time)
    usp_TenantUsers_CheckSync      lists differences between TenantUsers and the tenant databases

  Uses the existing tables: dbo.Tenants (IsActive), dbo.TenantDatabases (DatabaseName, IsEnabled,
  ProvisioningStep, DatabaseMode, DbUser, DbPassword) and dbo.Licenses (IsActive, StartDate, ExpiryDate, UserLimit).
  Nothing existing is changed except dbo.TenantUsers (unique e-mail, index, defaults).

  At the end: GRANT EXECUTE for the SQL login the Business Panel API uses (edit the name).
*/
SET NOCOUNT ON;
GO
-- draft procedure from the earlier explanation (it referenced columns Tenants does not have)
DROP PROCEDURE IF EXISTS dbo.usp_FindTenantForLogin;
GO

/* ------------------------------------------------------------------ 1. dbo.TenantUsers */
IF OBJECT_ID(N'dbo.TenantUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantUsers
    (
        Email     NVARCHAR(200) NOT NULL,
        TenantId  INT           NOT NULL,
        CreatedOn DATETIME2(7)  NOT NULL CONSTRAINT DF_TenantUsers_CreatedOn DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_TenantUsers_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE
    );
    PRINT N'Created dbo.TenantUsers';
END;
GO
IF COL_LENGTH(N'dbo.TenantUsers', N'CreatedOn') IS NULL
    ALTER TABLE dbo.TenantUsers ADD CreatedOn DATETIME2(7) NOT NULL CONSTRAINT DF_TenantUsers_CreatedOn DEFAULT (SYSUTCDATETIME());
GO
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.TenantUsers')
               AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.TenantUsers'), N'CreatedOn', 'ColumnId'))
    ALTER TABLE dbo.TenantUsers ADD CONSTRAINT DF_TenantUsers_CreatedOn DEFAULT (SYSUTCDATETIME()) FOR CreatedOn;
GO
-- e-mails are stored trimmed and lower-case
UPDATE dbo.TenantUsers SET Email = LOWER(LTRIM(RTRIM(Email))) WHERE Email <> LOWER(LTRIM(RTRIM(Email))) COLLATE Latin1_General_CS_AS;
GO
IF EXISTS (SELECT Email FROM dbo.TenantUsers GROUP BY Email HAVING COUNT(*) > 1)
BEGIN
    SELECT Email, COUNT(*) AS Copies, MIN(TenantId) AS FirstTenantId, MAX(TenantId) AS LastTenantId   -- (no STRING_AGG: the server is SQL 2016)
    FROM dbo.TenantUsers GROUP BY Email HAVING COUNT(*) > 1;
    RAISERROR(N'Stopped: the e-mails listed above are in dbo.TenantUsers more than once. Keep one row each, then run this script again.', 16, 1);
    SET NOEXEC ON;
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TenantUsers') AND name = N'UX_TenantUsers_Email')
    CREATE UNIQUE INDEX UX_TenantUsers_Email ON dbo.TenantUsers (Email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TenantUsers') AND name = N'IX_TenantUsers_TenantId')
    CREATE INDEX IX_TenantUsers_TenantId ON dbo.TenantUsers (TenantId);
GO

/* ------------------------------------------------------------------ 1b. database's own SQL user (Manual mode, e.g. Plesk)
   Also added by the Admin Panel API on start-up; here too so this script can run first. */
IF COL_LENGTH(N'dbo.TenantDatabases', N'DatabaseMode') IS NULL ALTER TABLE dbo.TenantDatabases ADD DatabaseMode NVARCHAR(10) NOT NULL CONSTRAINT DF_TenantDatabases_DatabaseMode DEFAULT (N'Automatic');
IF COL_LENGTH(N'dbo.TenantDatabases', N'DbUser') IS NULL ALTER TABLE dbo.TenantDatabases ADD DbUser NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.TenantDatabases', N'DbPassword') IS NULL ALTER TABLE dbo.TenantDatabases ADD DbPassword NVARCHAR(500) NULL;
GO

/* ------------------------------------------------------------------ 2. one view of a tenant's login state */
CREATE OR ALTER VIEW dbo.vw_TenantLogin
AS
SELECT
    t.TenantId,
    t.TenantCode,
    t.TenantName,
    t.IsActive                                   AS TenantIsActive,
    d.DatabaseName,
    d.Status                                     AS DatabaseStatus,
    CONVERT(BIT, ISNULL(d.IsEnabled, 0))         AS DatabaseEnabled,
    -- usable once provisioning got past the Administrator-role step (6 -> ProvisioningStep 7+);
    -- the 10-minute automatic migration ("Running") does not lock anyone out
    CONVERT(BIT, CASE WHEN d.TenantDatabaseId IS NOT NULL AND d.IsEnabled = 1 AND d.ProvisioningStep >= 7 THEN 1 ELSE 0 END) AS DatabaseReady,
    ISNULL(l.LicenseCount, 0)                    AS LicenseCount,
    lv.ExpiryDate                                AS LicenseValidUntil,
    ISNULL(lv.UserLimit, 0)                      AS UserLimit,
    (SELECT COUNT(*) FROM dbo.TenantUsers u WHERE u.TenantId = t.TenantId) AS UserCount,
    -- Manual databases with their own SQL user: the Business Panel opens the database with this user.
    -- DbPassword is encrypted (key: MultiTenant:CredentialKey = the Admin Panel's TenantDatabase:CredentialKey).
    CASE WHEN d.DatabaseMode = N'Manual' THEN NULLIF(LTRIM(RTRIM(d.DbUser)), N'') END AS DbUser,
    CASE WHEN d.DatabaseMode = N'Manual' AND NULLIF(LTRIM(RTRIM(d.DbUser)), N'') IS NOT NULL THEN d.DbPassword END AS DbPassword
FROM dbo.Tenants t
LEFT JOIN dbo.TenantDatabases d ON d.TenantId = t.TenantId
OUTER APPLY (SELECT COUNT(*) AS LicenseCount FROM dbo.Licenses x WHERE x.TenantId = t.TenantId) l
OUTER APPLY (
    -- the active licence that runs longest and has started
    SELECT TOP (1) x.ExpiryDate, x.UserLimit
    FROM dbo.Licenses x
    WHERE x.TenantId = t.TenantId AND x.IsActive = 1 AND CONVERT(DATE, x.StartDate) <= CONVERT(DATE, GETDATE())
    ORDER BY x.ExpiryDate DESC
) lv;
GO

/* ------------------------------------------------------------------ 3. lookups used by the Business Panel API */
CREATE OR ALTER PROCEDURE dbo.usp_TenantLogin_FindByEmail
    @Email NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT v.*
    FROM dbo.TenantUsers u
    JOIN dbo.vw_TenantLogin v ON v.TenantId = u.TenantId
    WHERE u.Email = LOWER(LTRIM(RTRIM(@Email)));
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantLogin_GetById
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT v.* FROM dbo.vw_TenantLogin v WHERE v.TenantId = @TenantId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantLogin_GetByCode
    @TenantCode NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT v.* FROM dbo.vw_TenantLogin v WHERE v.TenantCode = LTRIM(RTRIM(@TenantCode));
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantLogin_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT v.* FROM dbo.vw_TenantLogin v ORDER BY v.TenantCode;
END;
GO

/* ------------------------------------------------------------------ 4. keeping TenantUsers in step with the Business Panel */
CREATE OR ALTER PROCEDURE dbo.usp_TenantUsers_Add
    @TenantId INT,
    @Email    NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @e NVARCHAR(200) = LOWER(LTRIM(RTRIM(@Email)));
    IF @e = N'' THROW 50010, N'E-mail is required.', 1;

    BEGIN TRANSACTION;
    DECLARE @owner INT = (SELECT TenantId FROM dbo.TenantUsers WITH (UPDLOCK, HOLDLOCK) WHERE Email = @e);
    IF @owner = @TenantId BEGIN COMMIT; RETURN; END;            -- already registered for this company
    IF @owner IS NOT NULL
        THROW 50011, N'This e-mail is already used by another company. Use a different e-mail address.', 1;

    DECLARE @limit INT, @count INT;
    SELECT @limit = UserLimit, @count = UserCount FROM dbo.vw_TenantLogin WHERE TenantId = @TenantId;
    IF @limit IS NULL THROW 50012, N'Company not found.', 1;
    IF @limit > 0 AND @count >= @limit
    BEGIN
        DECLARE @msg NVARCHAR(300) = CONCAT(N'Your plan allows ', @limit, N' users and all of them are in use. Remove a user or upgrade the plan.');
        THROW 50013, @msg, 1;
    END;

    INSERT dbo.TenantUsers (Email, TenantId) VALUES (@e, @TenantId);
    COMMIT;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantUsers_ChangeEmail
    @TenantId INT,
    @OldEmail NVARCHAR(200),
    @NewEmail NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @old NVARCHAR(200) = LOWER(LTRIM(RTRIM(@OldEmail))), @new NVARCHAR(200) = LOWER(LTRIM(RTRIM(@NewEmail)));
    IF @new = N'' THROW 50010, N'E-mail is required.', 1;
    IF @old = @new RETURN;

    BEGIN TRANSACTION;
    DECLARE @owner INT = (SELECT TenantId FROM dbo.TenantUsers WITH (UPDLOCK, HOLDLOCK) WHERE Email = @new);
    IF @owner IS NOT NULL AND @owner <> @TenantId
        THROW 50011, N'This e-mail is already used by another company. Use a different e-mail address.', 1;
    IF @owner IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.TenantUsers WHERE Email = @old AND TenantId = @TenantId)
            UPDATE dbo.TenantUsers SET Email = @new WHERE Email = @old AND TenantId = @TenantId;
        ELSE
            INSERT dbo.TenantUsers (Email, TenantId) VALUES (@new, @TenantId);
    END
    ELSE
        DELETE dbo.TenantUsers WHERE Email = @old AND TenantId = @TenantId;   -- new e-mail already registered here
    COMMIT;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantUsers_Remove
    @TenantId INT,
    @Email    NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.TenantUsers WHERE TenantId = @TenantId AND Email = LOWER(LTRIM(RTRIM(@Email)));
END;
GO

/* ------------------------------------------------------------------ 5. fill / check from the tenant databases */
CREATE OR ALTER PROCEDURE dbo.usp_TenantUsers_SyncFromTenantDatabases
    @TenantId INT = NULL      -- NULL = every tenant
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @result TABLE (TenantId INT, TenantCode NVARCHAR(50), DatabaseName SYSNAME, Email NVARCHAR(200), Outcome NVARCHAR(100));
    DECLARE @tid INT, @code NVARCHAR(50), @db SYSNAME, @sql NVARCHAR(MAX);

    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT TenantId, TenantCode, DatabaseName FROM dbo.vw_TenantLogin
        WHERE DatabaseName IS NOT NULL AND (@TenantId IS NULL OR TenantId = @TenantId);
    OPEN c; FETCH NEXT FROM c INTO @tid, @code, @db;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF DB_ID(@db) IS NULL
            INSERT @result VALUES (@tid, @code, @db, NULL, N'Database not found');
        ELSE
        BEGIN
            DECLARE @emails TABLE (Email NVARCHAR(200));
            DELETE @emails;
            SET @sql = N'SELECT DISTINCT LOWER(LTRIM(RTRIM(Email))) FROM ' + QUOTENAME(@db) + N'.dbo.Users WHERE Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> N''''';
            BEGIN TRY
                INSERT @emails EXEC sys.sp_executesql @sql;

                INSERT @result
                SELECT @tid, @code, @db, e.Email, N'Used by another company: ' + o.TenantCode
                FROM @emails e JOIN dbo.TenantUsers u ON u.Email = e.Email JOIN dbo.Tenants o ON o.TenantId = u.TenantId
                WHERE u.TenantId <> @tid;

                INSERT dbo.TenantUsers (Email, TenantId)
                OUTPUT @tid, @code, @db, inserted.Email, N'Added' INTO @result
                SELECT e.Email, @tid FROM @emails e
                WHERE NOT EXISTS (SELECT 1 FROM dbo.TenantUsers u WHERE u.Email = e.Email);
            END TRY
            BEGIN CATCH
                INSERT @result VALUES (@tid, @code, @db, NULL, LEFT(N'Failed: ' + ERROR_MESSAGE(), 100));
            END CATCH;
        END;
        FETCH NEXT FROM c INTO @tid, @code, @db;
    END;
    CLOSE c; DEALLOCATE c;

    SELECT * FROM @result ORDER BY TenantCode, Outcome, Email;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TenantUsers_CheckSync
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @result TABLE (TenantCode NVARCHAR(50), Email NVARCHAR(200), Problem NVARCHAR(100));
    DECLARE @tid INT, @code NVARCHAR(50), @db SYSNAME, @sql NVARCHAR(MAX);

    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT TenantId, TenantCode, DatabaseName FROM dbo.vw_TenantLogin WHERE DatabaseName IS NOT NULL AND DB_ID(DatabaseName) IS NOT NULL;
    OPEN c; FETCH NEXT FROM c INTO @tid, @code, @db;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @emails TABLE (Email NVARCHAR(200));
        DELETE @emails;
        SET @sql = N'SELECT DISTINCT LOWER(LTRIM(RTRIM(Email))) FROM ' + QUOTENAME(@db) + N'.dbo.Users WHERE Email IS NOT NULL';
        BEGIN TRY
            INSERT @emails EXEC sys.sp_executesql @sql;
            INSERT @result SELECT @code, e.Email, N'User exists in ' + @db + N' but is not in TenantUsers (cannot log in)'
                FROM @emails e WHERE NOT EXISTS (SELECT 1 FROM dbo.TenantUsers u WHERE u.Email = e.Email AND u.TenantId = @tid);
            INSERT @result SELECT @code, u.Email, N'In TenantUsers but no such user in ' + @db
                FROM dbo.TenantUsers u WHERE u.TenantId = @tid AND NOT EXISTS (SELECT 1 FROM @emails e WHERE e.Email = u.Email);
        END TRY
        BEGIN CATCH
            INSERT @result VALUES (@code, NULL, LEFT(N'Could not read ' + @db + N': ' + ERROR_MESSAGE(), 100));
        END CATCH;
        FETCH NEXT FROM c INTO @tid, @code, @db;
    END;
    CLOSE c; DEALLOCATE c;

    INSERT @result SELECT t.TenantCode, u.Email, N'Company has no database'
    FROM dbo.TenantUsers u JOIN dbo.vw_TenantLogin t ON t.TenantId = u.TenantId WHERE t.DatabaseName IS NULL;

    SELECT * FROM @result ORDER BY TenantCode, Email;
END;
GO

/* ------------------------------------------------------------------ 6. let the Business Panel API's SQL login into every tenant database
   New tenant databases get this automatically (Admin Panel API, setting TenantDatabase:BusinessApiLogin).
   For databases made before that: EXEC dbo.usp_TenantDatabases_GrantBusinessApiLogin N'businesspanel'; */
CREATE OR ALTER PROCEDURE dbo.usp_TenantDatabases_GrantBusinessApiLogin
    @LoginName SYSNAME,
    @TenantId  INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF SUSER_ID(@LoginName) IS NULL THROW 50020, N'That SQL login does not exist on this server.', 1;
    DECLARE @result TABLE (DatabaseName SYSNAME, Outcome NVARCHAR(300));
    DECLARE @db SYSNAME, @sql NVARCHAR(MAX);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT DatabaseName FROM dbo.TenantDatabases WHERE DB_ID(DatabaseName) IS NOT NULL AND (@TenantId IS NULL OR TenantId = @TenantId);
    OPEN c; FETCH NEXT FROM c INTO @db;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'USE ' + QUOTENAME(@db) + N';
IF DATABASE_PRINCIPAL_ID(@l) IS NULL EXEC (N''CREATE USER '' + @ql + N'' FOR LOGIN '' + @ql);
EXEC (N''ALTER ROLE db_datareader ADD MEMBER '' + @ql);
EXEC (N''ALTER ROLE db_datawriter ADD MEMBER '' + @ql);
EXEC (N''GRANT EXECUTE TO '' + @ql);';
        BEGIN TRY
            EXEC sys.sp_executesql @sql, N'@l SYSNAME, @ql NVARCHAR(300)', @l = @LoginName, @ql = QUOTENAME(@LoginName);
            INSERT @result VALUES (@db, N'OK');
        END TRY
        BEGIN CATCH
            INSERT @result VALUES (@db, LEFT(N'Failed: ' + ERROR_MESSAGE(), 300));
        END CATCH;
        FETCH NEXT FROM c INTO @db;
    END;
    CLOSE c; DEALLOCATE c;
    SELECT * FROM @result ORDER BY DatabaseName;
END;
GO

/* ------------------------------------------------------------------ 7. permissions for the Business Panel API's SQL login
   The Business Panel API reads these procedures with its own SQL login. Put that login's name
   below (live server: the login in the Business API's ConnectionStrings:TenantDirectory).
   Leave it as is on your PC when the API uses Windows login (Trusted_Connection). */
DECLARE @BusinessApiLogin SYSNAME = N'businesspanel';
IF SUSER_ID(@BusinessApiLogin) IS NOT NULL
BEGIN
    DECLARE @g NVARCHAR(MAX) = N'';
    IF DATABASE_PRINCIPAL_ID(@BusinessApiLogin) IS NULL
        SET @g += N'CREATE USER ' + QUOTENAME(@BusinessApiLogin) + N' FOR LOGIN ' + QUOTENAME(@BusinessApiLogin) + N';';
    SELECT @g += N'GRANT EXECUTE ON dbo.' + QUOTENAME(p) + N' TO ' + QUOTENAME(@BusinessApiLogin) + N';'
    FROM (VALUES (N'usp_TenantLogin_FindByEmail'), (N'usp_TenantLogin_GetById'), (N'usp_TenantLogin_GetByCode'), (N'usp_TenantLogin_List'),
                 (N'usp_TenantUsers_Add'), (N'usp_TenantUsers_ChangeEmail'), (N'usp_TenantUsers_Remove')) x(p);
    EXEC (@g);
    PRINT N'Granted the tenant login procedures to ' + @BusinessApiLogin;
END
ELSE
    PRINT N'SQL login ' + @BusinessApiLogin + N' not found on this server - no permissions granted (fine when the API uses Windows login).';
GO
SET NOEXEC OFF;
GO
PRINT N'Tenant login directory ready. Next: EXEC dbo.usp_TenantUsers_SyncFromTenantDatabases;';
GO
