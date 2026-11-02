/*
  017 - Renewal lifecycle: Dates Pending, renewal status per item, Renew, family at customer level, validity per Sub Category
  ==========================================================================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-11-02_renewal_lifecycle.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/017_Renewal_Lifecycle.sql   run automatically on every tenant database (BP_*)
  Needs 009 (tracker members linked to customer contacts), 010 (Actual / Due Dates), 015 (services per employee), 016 (reminders).

  1. Tracker status DATES_PENDING ("Dates Pending", not terminal), just before Completed.
     All work is done (services Completed, invoiced, paid, no document Pending) but an Actual / Due Date is missing:
     the tracker moves to Dates Pending instead of being refused; when the last date is saved it becomes Completed.
       * dbo.fn_Tracker_DatesBlocker / fn_Tracker_RenewalBlocker (same text as before) / fn_Tracker_OnlyDatesMissing
       * usp_Tracker_ApplyStatus, usp_Tracker_SyncDocumentHold, usp_Tracker_AutoProgress: one line each is changed inside
         their CURRENT definition (as script 010 did), so other changes are kept. A failure is reported, not hidden.
       * one time: open trackers whose work is done and only dates are missing move to Dates Pending.
  2. Renewal status of each item (one service for the company, an employee or a family member) - dbo.vw_RenewalItems:
       ACTIVE / DUE_SOON (inside the rule's first reminder step; 60 days without a rule) / EXPIRED / LAPSED (expired longer
       than the "Renewals.LapsedDays" setting, default 30) / NOT_RENEWING (set with a reason, or the employee exited, or the
       family member left) / RENEWAL_IN_PROGRESS / RENEWED (from the renewal tracker's status).
       Stored: RenewalStatus / RenewalNote / RenewalStatusDate / RenewalStatusBy on entries and members.
  3. Renew (stage 2): dbo.usp_Tracker_Renew makes a new tracker with the Due Soon / Expired items (or the ones picked),
     their members and services at today's prices; Trackers.RenewalOfTrackerId + dbo.TrackerRenewalLinks link old and new.
  4. Family members at customer level (stage 3): dbo.CustomerFamilyMembers (under their employee, with passport expiry,
     active flag); TrackerFamilyMembers.CustomerFamilyMemberId; dbo.usp_CustomerFamily_SyncTracker (the API runs it after a
     tracker's members are saved). One time: existing trackers' family members are moved up.
  5. ServiceSubCategories.ValidityMonths (stage 4): the Due Date suggested from the Actual Date.
  6. usp_Notifications_Run (reminders): only Active / Due Soon / Expired items; the employee themself also gets the
     reminder (recipient type MEMBER) when the rule reminds the customer and they have their own email / mobile.
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecPersons', N'DueDate') IS NULL
   OR COL_LENGTH(N'dbo.TrackerFamilyMembers', N'CustomerContactId') IS NULL
   OR COL_LENGTH(N'dbo.TrackerFamilyMembers', N'SponsorContactId') IS NULL
   OR OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
   OR OBJECT_ID(N'dbo.fn_Tracker_CompletionBlocker') IS NULL
   OR COL_LENGTH(N'dbo.TrackerServiceSpecs', N'EmployeeContactId') IS NULL
   OR OBJECT_ID(N'dbo.NotificationRules', N'U') IS NULL
BEGIN
    RAISERROR(N'Run scripts 009, 010, 015 and 016 first (tracker members from customer contacts, renewal dates, services per employee, reminders). Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

/* ---------- 1. Dates Pending status ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TrackerStatuses WHERE UPPER(StatusCode) = N'DATES_PENDING')
BEGIN
    DECLARE @Order INT = (SELECT TOP (1) DisplayOrder FROM dbo.TrackerStatuses WHERE UPPER(StatusCode) = N'COMPLETED');
    IF @Order IS NULL
        SET @Order = ISNULL((SELECT MAX(DisplayOrder) FROM dbo.TrackerStatuses), 0) + 1;
    ELSE
        UPDATE dbo.TrackerStatuses SET DisplayOrder = DisplayOrder + 1, ModifiedDate = SYSUTCDATETIME() WHERE DisplayOrder >= @Order;
    INSERT dbo.TrackerStatuses (StatusCode, StatusName, DisplayOrder, IsDefault, IsTerminal, IsActive, CreatedDate)
    VALUES (N'DATES_PENDING', N'Dates Pending', @Order, 0, 0, 1, SYSUTCDATETIME());
    RAISERROR(N'ADDED   tracker status DATES_PENDING (Dates Pending)', 10, 1) WITH NOWAIT;
END
GO

/* ---------- 2. Renewal status columns + Lapsed setting ---------- */
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecs', N'RenewalStatus') IS NULL
    ALTER TABLE dbo.TrackerCategorySubCategorySpecs ADD RenewalStatus NVARCHAR(30) NULL, RenewalNote NVARCHAR(500) NULL,
        RenewalStatusDate DATETIME2(0) NULL, RenewalStatusBy INT NULL;
GO
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecPersons', N'RenewalStatus') IS NULL
    ALTER TABLE dbo.TrackerCategorySubCategorySpecPersons ADD RenewalStatus NVARCHAR(30) NULL, RenewalNote NVARCHAR(500) NULL,
        RenewalStatusDate DATETIME2(0) NULL, RenewalStatusBy INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM dbo.ApplicationSettings WHERE SettingKey = N'Renewals.LapsedDays')
    INSERT dbo.ApplicationSettings (SettingKey, SettingValue, IsActive, ModifiedDate) VALUES (N'Renewals.LapsedDays', N'30', 1, SYSUTCDATETIME());
GO

/* ---------- 3. Completion rules split: dates / everything else ---------- */
CREATE OR ALTER FUNCTION dbo.fn_Tracker_DatesBlocker (@TrackerId INT)
RETURNS NVARCHAR(400)
AS
BEGIN
    DECLARE @Entries INT = (SELECT COUNT(1) FROM dbo.TrackerCategorySubCategorySpecs
                            WHERE TrackerId = @TrackerId AND (ActualDate IS NULL OR DueDate IS NULL));
    DECLARE @Members INT = (SELECT COUNT(1) FROM dbo.TrackerCategorySubCategorySpecPersons
                            WHERE TrackerId = @TrackerId AND (ActualDate IS NULL OR DueDate IS NULL));
    DECLARE @Msg NVARCHAR(400) = N'';
    IF @Entries > 0
        SET @Msg = CONCAT(@Msg, N' ', @Entries, CASE WHEN @Entries = 1 THEN N' entry needs' ELSE N' entries need' END, N' an Actual Date and a Due Date.');
    IF @Members > 0
        SET @Msg = CONCAT(@Msg, N' ', @Members, CASE WHEN @Members = 1 THEN N' member needs' ELSE N' members need' END, N' an Actual Date and a Due Date.');
    RETURN NULLIF(@Msg, N'');
END;
GO

/* Same text as script 010 (dates first, then services). */
CREATE OR ALTER FUNCTION dbo.fn_Tracker_RenewalBlocker (@TrackerId INT)
RETURNS NVARCHAR(400)
AS
BEGIN
    DECLARE @Services INT = (SELECT COUNT(1) FROM dbo.TrackerServiceSpecs
                             WHERE TrackerId = @TrackerId AND ISNULL(Status, N'') NOT IN (N'Completed', N'Deleted'));
    DECLARE @Msg NVARCHAR(400) = ISNULL(dbo.fn_Tracker_DatesBlocker(@TrackerId), N'');
    IF @Services > 0
        SET @Msg = CONCAT(@Msg, N' ', @Services, CASE WHEN @Services = 1 THEN N' service is' ELSE N' services are' END, N' not Completed yet.');
    RETURN NULLIF(@Msg, N'');
END;
GO

/* 1 = the only thing stopping completion is missing Actual / Due Dates. */
CREATE OR ALTER FUNCTION dbo.fn_Tracker_OnlyDatesMissing (@TrackerId INT)
RETURNS BIT
AS
BEGIN
    DECLARE @Dates NVARCHAR(400) = dbo.fn_Tracker_DatesBlocker(@TrackerId);
    IF @Dates IS NULL RETURN 0;
    DECLARE @All NVARCHAR(4000) = ISNULL(dbo.fn_Tracker_CompletionBlocker(@TrackerId), N'');
    IF LEN(LTRIM(RTRIM(REPLACE(@All, @Dates, N'')))) = 0 RETURN 1;
    RETURN 0;
END;
GO

/* ---------- 4. One-line changes inside the current status procedures ---------- */
IF OBJECT_ID(N'tempdb..#PatchModule') IS NOT NULL DROP PROCEDURE #PatchModule;
GO
CREATE PROCEDURE #PatchModule
    @Name SYSNAME, @Kind NVARCHAR(20), @Find NVARCHAR(1000), @Replace NVARCHAR(2000), @Marker NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Def NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID(N'dbo.' + @Name));
    DECLARE @Pos INT = 0, @At INT = 0, @After NVARCHAR(200), @Err NVARCHAR(2048);
    IF @Def IS NULL BEGIN RAISERROR(N'SKIPPED %s - not found.', 10, 1, @Name) WITH NOWAIT; RETURN; END
    IF CHARINDEX(@Marker, @Def) > 0 BEGIN RAISERROR(N'OK      %s - already changed.', 10, 1, @Name) WITH NOWAIT; RETURN; END
    IF CHARINDEX(@Find, @Def) = 0 BEGIN RAISERROR(N'WARNING %s - expected line not found; change it by hand (see script 017).', 10, 1, @Name) WITH NOWAIT; RETURN; END
    -- the header: "<kind> <name>" (comments above it may contain the same word)
    WHILE 1 = 1
    BEGIN
        SET @Pos = CHARINDEX(@Kind, @Def, @Pos + 1);
        IF @Pos = 0 BREAK;
        SET @After = SUBSTRING(@Def, @Pos + LEN(@Kind), 160);
        SET @After = LTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@After, N'[', N''), N']', N''), NCHAR(13), N' '), NCHAR(10), N' '), NCHAR(9), N' '));
        IF @After LIKE N'dbo.' + REPLACE(@Name, N'_', N'[_]') + N'%' OR @After LIKE REPLACE(@Name, N'_', N'[_]') + N'%' BEGIN SET @At = @Pos; BREAK; END
    END
    IF @At = 0 BEGIN RAISERROR(N'WARNING %s - header not found; change it by hand (see script 017).', 10, 1, @Name) WITH NOWAIT; RETURN; END
    SET @Def = REPLACE(@Def, @Find, @Replace);
    SET @Def = N'ALTER ' + SUBSTRING(@Def, @At, 2147483647);
    BEGIN TRY
        EXEC sys.sp_executesql @Def;
        RAISERROR(N'PATCHED %s', 10, 1, @Name) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        SET @Err = ERROR_MESSAGE();
        RAISERROR(N'FAILED  %s - not changed: %s', 16, 1, @Name, @Err);
    END CATCH
END
GO
DECLARE @NL NCHAR(2) = NCHAR(13) + NCHAR(10);

/* ApplyStatus (a): Completed asked, only dates missing -> Dates Pending instead of nothing. */
DECLARE @Find NVARCHAR(1000) = N'IF @Code = N''COMPLETED'' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL RETURN;';
DECLARE @Repl NVARCHAR(2000) = N'IF @Code = N''COMPLETED'' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL' + @NL
    + N'    BEGIN' + @NL
    + N'        -- 017: all work done and only Actual / Due Dates missing -> Dates Pending (Completed when the last date is saved)' + @NL
    + N'        IF dbo.fn_Tracker_OnlyDatesMissing(@TrackerId) = 0 RETURN;' + @NL
    + N'        SELECT @Code = N''DATES_PENDING'', @TargetTerminal = 0, @Note = N''Work done - waiting for the Actual / Due Dates'';' + @NL
    + N'    END;';
EXEC #PatchModule N'usp_Tracker_ApplyStatus', N'PROCEDURE', @Find, @Repl, N'fn_Tracker_OnlyDatesMissing';

/* ApplyStatus (b): a Dates Pending tracker is not moved back by invoice / payment / supporting-document events
   (only Completed, Pending documents, or a terminal status move it). */
SET @Find = N'IF EXISTS (SELECT 1 FROM dbo.TrackerStatuses WHERE StatusCode = @Current AND IsTerminal = 1) RETURN;';
SET @Repl = @Find + @NL
    + N'    IF @Current = N''DATES_PENDING'' AND @Code NOT IN (N''COMPLETED'', N''PENDING'') AND @TargetTerminal = 0 RETURN;  -- 017';
EXEC #PatchModule N'usp_Tracker_ApplyStatus', N'PROCEDURE', @Find, @Repl, N'@Current = N''DATES_PENDING''';

/* SyncDocumentHold: documents back to Success, tracker was to be Completed but only dates are missing -> Dates Pending. */
SET @Find = N'IF @Resume = N''COMPLETED'' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL SET @Resume = N''ACCEPTED'';';
SET @Repl = N'IF @Resume = N''COMPLETED'' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL' + @NL
    + N'        SET @Resume = CASE WHEN dbo.fn_Tracker_OnlyDatesMissing(@TrackerId) = 1 THEN N''DATES_PENDING'' ELSE N''ACCEPTED'' END;  -- 017';
EXEC #PatchModule N'usp_Tracker_SyncDocumentHold', N'PROCEDURE', @Find, @Repl, N'fn_Tracker_OnlyDatesMissing';

/* AutoProgress: leaves a Dates Pending tracker alone (like Completed). */
SET @Find = N'IF @Current IN (N''COMPLETED'', N''REJECTED'') RETURN;';
SET @Repl = N'IF @Current IN (N''COMPLETED'', N''REJECTED'', N''DATES_PENDING'') RETURN;  -- 017';
EXEC #PatchModule N'usp_Tracker_AutoProgress', N'PROCEDURE', @Find, @Repl, N'DATES_PENDING';
GO
DROP PROCEDURE #PatchModule;
GO

/* ---------- 5. Renew: link between trackers (stage 2) ---------- */
IF COL_LENGTH(N'dbo.Trackers', N'RenewalOfTrackerId') IS NULL
    ALTER TABLE dbo.Trackers ADD RenewalOfTrackerId INT NULL;
GO
/* one row per item that a renewal tracker renews: the old item (entry or member) and where it went */
IF OBJECT_ID(N'dbo.TrackerRenewalLinks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TrackerRenewalLinks
    (
        LinkId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrackerRenewalLinks PRIMARY KEY,
        OldTrackerId INT NOT NULL,
        OldItemType  NVARCHAR(10) NOT NULL,          -- ENTRY | MEMBER
        OldItemId    INT NOT NULL,                   -- TrackerCategorySubCategorySpecId | TrackerCategorySubCategorySpecPersonId
        NewTrackerId INT NOT NULL,
        NewEntryId   INT NULL,
        NewPersonId  INT NULL,
        CreatedBy    INT NULL,
        CreatedDate  DATETIME2(0) NOT NULL CONSTRAINT DF_TrackerRenewalLinks_CreatedDate DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_TrackerRenewalLinks_Old ON dbo.TrackerRenewalLinks (OldItemType, OldItemId);
    CREATE INDEX IX_TrackerRenewalLinks_New ON dbo.TrackerRenewalLinks (NewTrackerId);
END
GO

/* ---------- 6. Family members at customer level (stage 3) ---------- */
IF OBJECT_ID(N'dbo.CustomerFamilyMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerFamilyMembers
    (
        FamilyMemberId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerFamilyMembers PRIMARY KEY,
        CustomerId       INT NOT NULL,
        SponsorContactId INT NULL,                   -- the employee (customer contact) they belong to
        Name             NVARCHAR(200) NOT NULL,
        RelationshipId   INT NULL,
        DateOfBirth      DATE NULL,
        PassportNo       NVARCHAR(100) NULL,
        PassportExpiry   DATE NULL,
        VisaType         NVARCHAR(100) NULL,
        IsActive         BIT NOT NULL CONSTRAINT DF_CustomerFamilyMembers_IsActive DEFAULT (1),   -- 0 = left (no reminders, not renewed)
        CreatedDate      DATETIME2(0) NOT NULL CONSTRAINT DF_CustomerFamilyMembers_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate     DATETIME2(0) NULL,
        CONSTRAINT FK_CustomerFamilyMembers_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId) ON DELETE CASCADE
    );
    CREATE INDEX IX_CustomerFamilyMembers_Customer ON dbo.CustomerFamilyMembers (CustomerId, SponsorContactId);
END
GO
IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'CustomerFamilyMemberId') IS NULL
    ALTER TABLE dbo.TrackerFamilyMembers ADD CustomerFamilyMemberId INT NULL;
GO

/* Family members of one tracker -> the customer's family list (found by passport, or by employee + name + date of birth;
   created when new). The tracker keeps its own copy; the customer record keeps the latest details. */
CREATE OR ALTER PROCEDURE dbo.usp_CustomerFamily_SyncTracker
    @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Cust INT = (SELECT CustomerId FROM dbo.Trackers WHERE TrackerId = @TrackerId);
    IF @Cust IS NULL RETURN;
    DECLARE @EmpRel INT = (SELECT TOP (1) RelationshipId FROM dbo.Relationships WHERE LOWER(LTRIM(RTRIM(RelationshipName))) = N'employee');

    SELECT fm.TrackerFamilyMemberId, fm.Name, fm.SponsorContactId, fm.RelationshipId, fm.DateOfBirth, fm.PassportNo, fm.VisaType,
           UPPER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(fm.PassportNo, N''))), N' ', N''), N'-', N'')) AS PKey,
           LOWER(LTRIM(RTRIM(fm.Name))) AS NKey
    INTO #fam
    FROM dbo.TrackerFamilyMembers fm
    WHERE fm.TrackerId = @TrackerId AND fm.CustomerContactId IS NULL
      AND (@EmpRel IS NULL OR ISNULL(fm.RelationshipId, 0) <> @EmpRel)
      AND LTRIM(RTRIM(ISNULL(fm.Name, N''))) <> N'';

    DECLARE @pass INT = 0;
    WHILE @pass < 2
    BEGIN
        /* link to an existing customer family member */
        UPDATE fm SET CustomerFamilyMemberId = m.FamilyMemberId
        FROM dbo.TrackerFamilyMembers fm
        INNER JOIN #fam f ON f.TrackerFamilyMemberId = fm.TrackerFamilyMemberId
        CROSS APPLY (SELECT TOP (1) cf.FamilyMemberId
                     FROM dbo.CustomerFamilyMembers cf
                     WHERE cf.CustomerId = @Cust
                       AND ((f.PKey <> N'' AND UPPER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(cf.PassportNo, N''))), N' ', N''), N'-', N'')) = f.PKey)
                         OR (ISNULL(cf.SponsorContactId, 0) = ISNULL(f.SponsorContactId, 0) AND LOWER(LTRIM(RTRIM(cf.Name))) = f.NKey
                             AND (cf.DateOfBirth IS NULL OR f.DateOfBirth IS NULL OR cf.DateOfBirth = f.DateOfBirth)))
                     ORDER BY CASE WHEN f.PKey <> N'' AND UPPER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(cf.PassportNo, N''))), N' ', N''), N'-', N'')) = f.PKey THEN 0 ELSE 1 END,
                              cf.FamilyMemberId) m
        WHERE fm.CustomerFamilyMemberId IS NULL;

        IF @pass = 1 BREAK;
        /* create the ones not found (one per person, even if the tracker has them twice) */
        INSERT dbo.CustomerFamilyMembers (CustomerId, SponsorContactId, Name, RelationshipId, DateOfBirth, PassportNo, VisaType, IsActive, CreatedDate)
        SELECT @Cust, x.SponsorContactId, LTRIM(RTRIM(x.Name)), x.RelationshipId, x.DateOfBirth, NULLIF(LTRIM(RTRIM(x.PassportNo)), N''), NULLIF(LTRIM(RTRIM(x.VisaType)), N''), 1, SYSUTCDATETIME()
        FROM (SELECT f.*, ROW_NUMBER() OVER (PARTITION BY CASE WHEN f.PKey <> N'' THEN f.PKey ELSE CONCAT(ISNULL(f.SponsorContactId, 0), N'|', f.NKey) END
                                             ORDER BY f.TrackerFamilyMemberId) AS rn
              FROM #fam f
              INNER JOIN dbo.TrackerFamilyMembers fm ON fm.TrackerFamilyMemberId = f.TrackerFamilyMemberId
              WHERE fm.CustomerFamilyMemberId IS NULL) x
        WHERE x.rn = 1;
        SET @pass += 1;
    END

    /* the customer record keeps the latest details from the tracker */
    UPDATE cf SET Name = LTRIM(RTRIM(f.Name)), SponsorContactId = f.SponsorContactId, RelationshipId = ISNULL(f.RelationshipId, cf.RelationshipId),
                  DateOfBirth = ISNULL(f.DateOfBirth, cf.DateOfBirth), PassportNo = ISNULL(NULLIF(LTRIM(RTRIM(f.PassportNo)), N''), cf.PassportNo),
                  VisaType = ISNULL(NULLIF(LTRIM(RTRIM(f.VisaType)), N''), cf.VisaType), ModifiedDate = SYSUTCDATETIME()
    FROM dbo.CustomerFamilyMembers cf
    INNER JOIN dbo.TrackerFamilyMembers fm ON fm.CustomerFamilyMemberId = cf.FamilyMemberId
    INNER JOIN #fam f ON f.TrackerFamilyMemberId = fm.TrackerFamilyMemberId;
END;
GO

/* one-time: existing trackers' family members -> customer level (oldest tracker first, so the newest details win) */
IF EXISTS (SELECT 1 FROM dbo.TrackerFamilyMembers WHERE CustomerFamilyMemberId IS NULL AND CustomerContactId IS NULL)
BEGIN
    DECLARE @t INT, @n INT = 0;
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT TrackerId FROM dbo.TrackerFamilyMembers WHERE CustomerFamilyMemberId IS NULL AND CustomerContactId IS NULL ORDER BY TrackerId;
    OPEN c; FETCH NEXT FROM c INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.usp_CustomerFamily_SyncTracker @TrackerId = @t;
        SET @n += 1;
        FETCH NEXT FROM c INTO @t;
    END
    CLOSE c; DEALLOCATE c;
    DECLARE @msg NVARCHAR(200) = CONCAT(N'MOVED   family members of ', @n, N' tracker(s) to customer level (', (SELECT COUNT(1) FROM dbo.CustomerFamilyMembers), N' people).');
    RAISERROR(@msg, 10, 1) WITH NOWAIT;
END
GO

/* ---------- 7. Validity period per Sub Category (stage 4): Due Date = Actual Date + months ---------- */
IF COL_LENGTH(N'dbo.ServiceSubCategories', N'ValidityMonths') IS NULL
    ALTER TABLE dbo.ServiceSubCategories ADD ValidityMonths INT NULL;
GO

/* ---------- 8. Renewal items and their status ---------- */
CREATE OR ALTER VIEW dbo.vw_RenewalItems
AS
WITH base AS (
    /* the company itself, or an entry whose members have no Due Date: the entry's own date */
    SELECT CAST(N'ENTRY' AS NVARCHAR(10)) AS ItemType, sp.TrackerCategorySubCategorySpecId AS ItemId,
           sp.TrackerCategorySubCategorySpecId AS EntryId, sp.TrackerId,
           sp.CategoryId, sp.CategoryName, sp.SubCategoryId, sp.SubCategoryName, sp.PersonName, sp.ActualDate, sp.DueDate,
           sp.RenewalStatus AS StoredStatus, sp.RenewalNote AS StoredNote, sp.RenewalStatusDate AS StoredDate,
           CAST(N'COMPANY' AS NVARCHAR(10)) AS HolderType, CAST(NULL AS INT) AS HolderContactId, CAST(NULL AS INT) AS FamilyMemberId,
           CAST(0 AS BIT) AS Exited, CAST(NULL AS DATE) AS PassportExpiry
    FROM dbo.TrackerCategorySubCategorySpecs sp
    WHERE NOT EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecPersons mp
                      WHERE mp.TrackerCategorySubCategorySpecId = sp.TrackerCategorySubCategorySpecId AND mp.DueDate IS NOT NULL)
    UNION ALL
    /* each employee / family member with a Due Date; a status set on the whole entry applies to all its members */
    SELECT N'MEMBER', mp.TrackerCategorySubCategorySpecPersonId, sp.TrackerCategorySubCategorySpecId, sp.TrackerId,
           sp.CategoryId, sp.CategoryName, sp.SubCategoryId, sp.SubCategoryName, mp.PersonName, mp.ActualDate, mp.DueDate,
           COALESCE(sp.RenewalStatus, mp.RenewalStatus),
           CASE WHEN sp.RenewalStatus IS NOT NULL THEN sp.RenewalNote ELSE mp.RenewalNote END,
           CASE WHEN sp.RenewalStatus IS NOT NULL THEN sp.RenewalStatusDate ELSE mp.RenewalStatusDate END,
           CASE WHEN h.CustomerContactId IS NOT NULL THEN N'EMPLOYEE' WHEN h.TrackerFamilyMemberId IS NOT NULL THEN N'FAMILY' ELSE N'PERSON' END,
           h.EmployeeContactId, h.CustomerFamilyMemberId,
           /* exited: the employee (or the employee who sponsors the family member) is an inactive contact, or the family member left */
           CAST(CASE WHEN ec.IsActive = 0 OR cf.IsActive = 0 THEN 1 ELSE 0 END AS BIT),
           CASE WHEN h.CustomerContactId IS NOT NULL THEN ec.PassportExpiry ELSE cf.PassportExpiry END
    FROM dbo.TrackerCategorySubCategorySpecPersons mp
    INNER JOIN dbo.TrackerCategorySubCategorySpecs sp ON sp.TrackerCategorySubCategorySpecId = mp.TrackerCategorySubCategorySpecId
    OUTER APPLY (SELECT TOP (1) fm.TrackerFamilyMemberId, fm.CustomerContactId, fm.CustomerFamilyMemberId,
                        COALESCE(fm.CustomerContactId, fm.SponsorContactId) AS EmployeeContactId
                 FROM dbo.TrackerFamilyMembers fm
                 WHERE fm.TrackerId = sp.TrackerId AND LOWER(LTRIM(RTRIM(fm.Name))) = LOWER(LTRIM(RTRIM(mp.PersonName)))
                 ORDER BY fm.TrackerFamilyMemberId) h
    LEFT JOIN dbo.CustomerContacts ec ON ec.ContactId = h.EmployeeContactId
    LEFT JOIN dbo.CustomerFamilyMembers cf ON cf.FamilyMemberId = h.CustomerFamilyMemberId
    WHERE mp.DueDate IS NOT NULL
)
SELECT b.ItemType, b.ItemId, b.EntryId, b.TrackerId, t.[Status] AS TrackerStatus, t.CustomerId, t.CustomerName,
       b.CategoryId, b.CategoryName, b.SubCategoryId, b.SubCategoryName, b.PersonName, b.ActualDate, b.DueDate,
       DATEDIFF(DAY, d.Today, b.DueDate) AS DaysLeft, ISNULL(rs.FirstStep, 60) AS DueSoonDays, s.LapsedDays, b.Exited,
       b.HolderType, b.HolderContactId, b.FamilyMemberId, b.PassportExpiry,
       /* the same holder across trackers: company / employee contact / customer family member / name */
       CASE WHEN b.ItemType = N'ENTRY' THEN CONCAT(N'C', t.CustomerId)
            WHEN b.HolderType = N'EMPLOYEE' THEN CONCAT(N'E', b.HolderContactId)
            WHEN b.FamilyMemberId IS NOT NULL THEN CONCAT(N'F', b.FamilyMemberId)
            ELSE CONCAT(N'N', t.CustomerId, N':', LOWER(LTRIM(RTRIM(b.PersonName)))) END AS HolderKey,
       rl.NewTrackerId AS RenewalTrackerId,
       CASE WHEN b.StoredStatus IS NOT NULL THEN b.StoredStatus
            WHEN rl.NewTrackerId IS NOT NULL AND UPPER(rl.NewStatus) = N'COMPLETED' THEN N'RENEWED'
            WHEN rl.NewTrackerId IS NOT NULL THEN N'RENEWAL_IN_PROGRESS'
            WHEN b.Exited = 1 THEN N'NOT_RENEWING'
            WHEN b.DueDate IS NULL THEN N'NO_DATE'
            WHEN b.DueDate < DATEADD(DAY, -s.LapsedDays, d.Today) THEN N'LAPSED'
            WHEN b.DueDate < d.Today THEN N'EXPIRED'
            WHEN DATEDIFF(DAY, d.Today, b.DueDate) <= ISNULL(rs.FirstStep, 60) THEN N'DUE_SOON'
            ELSE N'ACTIVE' END AS RenewalStatus,
       CASE WHEN b.StoredStatus IS NULL AND rl.NewTrackerId IS NULL AND b.Exited = 1
            THEN CASE WHEN b.HolderType = N'FAMILY' THEN N'Family member left or their employee has exited' ELSE N'Employee has exited (inactive contact)' END
            ELSE b.StoredNote END AS RenewalNote,
       b.StoredStatus, b.StoredDate AS RenewalStatusDate
FROM base b
INNER JOIN dbo.Trackers t ON t.TrackerId = b.TrackerId
CROSS JOIN (SELECT CAST(GETDATE() AS DATE) AS Today) d
CROSS JOIN (SELECT ISNULL((SELECT TOP (1) TRY_CONVERT(INT, SettingValue) FROM dbo.ApplicationSettings
                           WHERE SettingKey = N'Renewals.LapsedDays' AND IsActive = 1 AND TRY_CONVERT(INT, SettingValue) >= 0), 30) AS LapsedDays) s
/* Due Soon window = the first (largest) reminder step of the rule that reminds this item */
OUTER APPLY (SELECT TOP (1) (SELECT MAX(TRY_CONVERT(INT, LTRIM(RTRIM(x.value))))
                             FROM STRING_SPLIT(ISNULL(NULLIF(ru.ReminderDays, N''), CONVERT(NVARCHAR(12), ru.DaysBefore)), N',') x) AS FirstStep
             FROM dbo.NotificationRules ru
             WHERE ru.IsActive = 1 AND ru.CategoryId = b.CategoryId AND (ru.SubCategoryId IS NULL OR ru.SubCategoryId = b.SubCategoryId)
             ORDER BY CASE WHEN ru.SubCategoryId IS NULL THEN 1 ELSE 0 END, ru.RuleId) rs
/* the latest renewal tracker of this item that is still alive (not rejected / cancelled / deleted) */
OUTER APPLY (SELECT TOP (1) l.NewTrackerId, nt.[Status] AS NewStatus
             FROM dbo.TrackerRenewalLinks l
             INNER JOIN dbo.Trackers nt ON nt.TrackerId = l.NewTrackerId
             WHERE l.OldItemType = b.ItemType AND l.OldItemId = b.ItemId
               AND UPPER(LTRIM(RTRIM(nt.[Status]))) NOT IN (N'REJECTED', N'NOT_COMPLETED', N'CANCELLED')
             ORDER BY l.LinkId DESC) rl;
GO

/* ---------- 9. Reminders: only Active / Due Soon / Expired items; also to the employee themself ---------- */
CREATE OR ALTER PROCEDURE [dbo].[usp_Notifications_Run]
    @Mode        NVARCHAR(10) = N'PREVIEW',     -- PREVIEW (nothing saved) or GENERATE
    @CatchUpDays INT = 7,
    @UserId      INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Mode = UPPER(LTRIM(RTRIM(ISNULL(@Mode, N'PREVIEW'))));
    IF @Mode NOT IN (N'PREVIEW', N'GENERATE') THROW 50220, 'Mode must be PREVIEW or GENERATE.', 1;
    IF @CatchUpDays IS NULL OR @CatchUpDays < 0 SET @CatchUpDays = 7;
    IF @CatchUpDays > 60 SET @CatchUpDays = 60;
    DECLARE @Today DATE = CONVERT(DATE, SYSDATETIME());

    /* open tracker lines that have a due date (017: from dbo.vw_RenewalItems):
       - an entry whose members have their own Due Date (Members tab) -> one line per member, with the member's name and date;
       - otherwise the entry's own Due Date;
       - only items that still need renewing: Active, Due Soon, Expired. Not Renewing (also an exited employee or a family
         member who left), Lapsed (expired longer than the Renewals.LapsedDays setting), Renewal in Progress and Renewed get nothing. */
    ;WITH lines AS (
        SELECT v.EntryId AS SpecId, v.PersonName, v.DueDate, v.HolderContactId
        FROM dbo.vw_RenewalItems v
        WHERE v.DueDate IS NOT NULL AND v.RenewalStatus IN (N'ACTIVE', N'DUE_SOON', N'EXPIRED')
    )
    SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY l.SpecId, l.DueDate, l.PersonName)) AS RowKey,
           sp.TrackerCategorySubCategorySpecId AS SpecId, sp.TrackerId, sp.CategoryId, sp.CategoryName, sp.SubCategoryId, sp.SubCategoryName,
           l.PersonName, l.DueDate, DATEDIFF(DAY, @Today, l.DueDate) AS DaysLeft,
           t.CustomerId, t.CustomerName,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Email)), N''), NULLIF(LTRIM(RTRIM(c.CustomerEmail)), N'')) AS CustEmail,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Mobile)), N''), NULLIF(LTRIM(RTRIM(c.CustomerPhone)), N'')) AS CustMobile,
           t.AssignedToEmployeeId AS EmployeeId,
           ISNULL(NULLIF(LTRIM(RTRIM(e.FullName)), N''), NULLIF(LTRIM(RTRIM(t.AssignedToEmployeeName)), N'')) AS EmployeeName,
           NULLIF(LTRIM(RTRIM(e.Email)), N'') AS EmpEmail,
           NULLIF(LTRIM(RTRIM(e.Mobile)), N'') AS EmpMobile,
           -- 017: the employee themself (for a family member: the employee they belong to)
           NULLIF(LTRIM(RTRIM(hc.ContactName)), N'') AS MemName,
           NULLIF(LTRIM(RTRIM(hc.Email)), N'') AS MemEmail,
           NULLIF(LTRIM(RTRIM(hc.Mobile)), N'') AS MemMobile
    INTO #np_spec
    FROM lines l
    INNER JOIN dbo.TrackerCategorySubCategorySpecs sp ON sp.TrackerCategorySubCategorySpecId = l.SpecId
    INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
    LEFT JOIN dbo.Customers c ON c.CustomerId = t.CustomerId
    LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
    LEFT JOIN dbo.CustomerContacts hc ON hc.ContactId = l.HolderContactId AND hc.IsActive = 1
    -- a Completed tracker still gets reminders: its Due Date is the NEXT renewal. Only rejected / not completed ones are skipped.
    WHERE UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'REJECTED', N'NOT_COMPLETED', N'CANCELLED')
      AND l.DueDate <= DATEADD(DAY, 366, @Today);

    /* the current step of each line: smallest "days before" that is still >= days left, and not too late */
    ;WITH r AS (
        SELECT s.RowKey, s.SpecId, ru.RuleId, st.D AS DaysBefore, ru.NotifyEmail, ru.NotifyWhatsApp, ru.NotifyInApp, ru.ToCustomer, ru.ToEmployee,
               ru.[Subject] AS TSubject, ru.Body AS TBody, s.DaysLeft,
               ROW_NUMBER() OVER (PARTITION BY s.RowKey, st.D
                                  ORDER BY CASE WHEN ru.SubCategoryId IS NULL THEN 1 ELSE 0 END, ru.RuleId) AS rn1
        FROM #np_spec s
        INNER JOIN dbo.NotificationRules ru
                ON ru.IsActive = 1 AND ru.CategoryId = s.CategoryId
               AND (ru.SubCategoryId IS NULL OR ru.SubCategoryId = s.SubCategoryId)
        -- each reminder step of the rule (ReminderDays "60,30,20,10"; empty = the single DaysBefore)
        CROSS APPLY (SELECT DISTINCT TRY_CONVERT(INT, LTRIM(RTRIM(x.value))) AS D
                     FROM STRING_SPLIT(ISNULL(NULLIF(ru.ReminderDays, N''), CONVERT(NVARCHAR(12), ru.DaysBefore)), N',') x
                     WHERE LTRIM(RTRIM(x.value)) <> N'' AND TRY_CONVERT(INT, LTRIM(RTRIM(x.value))) IS NOT NULL) st
        WHERE st.D >= s.DaysLeft
    ),
    r2 AS (
        SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.RowKey ORDER BY r.DaysBefore) AS rn2
        FROM r
        WHERE r.rn1 = 1
    )
    SELECT r2.RowKey, r2.SpecId, r2.RuleId, r2.DaysBefore, r2.NotifyEmail, r2.NotifyWhatsApp, r2.NotifyInApp, r2.ToCustomer, r2.ToEmployee,
           ISNULL(r2.TSubject, N'{{Category}} - {{SubCategory}} {{DaysLeft}}') AS TSubject,
           ISNULL(r2.TBody, N'Dear {{Recipient}}, this is a reminder that the {{SubCategory}} ({{Category}}) for {{Person}} is due on {{DueDate}} ({{DaysLeft}}). Please contact {{Employee}} to arrange the renewal.') AS TBody
    INTO #np_step
    FROM r2
    WHERE r2.rn2 = 1 AND r2.DaysBefore - r2.DaysLeft <= @CatchUpDays;

    /* one row per channel x recipient */
    CREATE TABLE #np_cand
    (
        RowKey INT NOT NULL, RuleId INT NOT NULL, SpecId INT NOT NULL, TrackerId INT NOT NULL, CustomerId INT NULL, CustomerName NVARCHAR(150) NULL,
        CategoryId INT NULL, CategoryName NVARCHAR(100) NULL, SubCategoryName NVARCHAR(100) NULL, PersonName NVARCHAR(200) NULL,
        DueDate DATE NOT NULL, DaysBefore INT NOT NULL, DaysLeft INT NOT NULL,
        Channel NVARCHAR(10) NOT NULL, RecipientType NVARCHAR(10) NOT NULL, RecipientName NVARCHAR(150) NULL,
        RecipientAddress NVARCHAR(150) NOT NULL, RecipientEmployeeId INT NULL,
        TSubject NVARCHAR(200) NULL, TBody NVARCHAR(2000) NULL,
        [Subject] NVARCHAR(200) NULL, Body NVARCHAR(2000) NULL,
        [Status] NVARCHAR(12) NOT NULL, StatusMessage NVARCHAR(400) NULL
    );

    /* customer: email + whatsapp */
    INSERT #np_cand (RowKey, RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT sp.RowKey, st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'CUSTOMER', sp.CustomerName, ISNULL(ch.Addr, N''), NULL, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN CASE ch.Channel WHEN N'EMAIL' THEN N'The customer has no email address.' ELSE N'The customer has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.RowKey = st.RowKey
    CROSS APPLY (VALUES (N'EMAIL', sp.CustEmail, st.NotifyEmail), (N'WHATSAPP', sp.CustMobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToCustomer = 1 AND ch.Enabled = 1;

    /* assigned employee: email + whatsapp + in-app */
    INSERT #np_cand (RowKey, RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT sp.RowKey, st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'EMPLOYEE', sp.EmployeeName,
           ISNULL(ch.Addr, N''), sp.EmployeeId, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' WHEN ch.Channel = N'INAPP' THEN N'SENT' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN
                CASE WHEN sp.EmployeeId IS NULL THEN N'No employee is assigned to this tracker.'
                     WHEN ch.Channel = N'EMAIL' THEN N'The employee has no email address.'
                     ELSE N'The employee has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.RowKey = st.RowKey
    CROSS APPLY (VALUES (N'EMAIL',    sp.EmpEmail, st.NotifyEmail),
                        (N'WHATSAPP', sp.EmpMobile, st.NotifyWhatsApp),
                        (N'INAPP',    CASE WHEN sp.EmployeeId IS NULL THEN NULL ELSE CONVERT(NVARCHAR(150), sp.EmployeeId) END, st.NotifyInApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToEmployee = 1 AND ch.Enabled = 1;

    /* 017: the employee themself - when the rule reminds the customer, and only when they have their own email / mobile
       (different from the company's) */
    INSERT #np_cand (RowKey, RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT sp.RowKey, st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'MEMBER', sp.MemName, ch.Addr, NULL, st.TSubject, st.TBody, N'PENDING', NULL
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.RowKey = st.RowKey
    CROSS APPLY (VALUES (N'EMAIL', sp.MemEmail, st.NotifyEmail, sp.CustEmail), (N'WHATSAPP', sp.MemMobile, st.NotifyWhatsApp, sp.CustMobile)) ch (Channel, Addr, Enabled, CustAddr)
    WHERE st.ToCustomer = 1 AND ch.Enabled = 1 AND ch.Addr IS NOT NULL AND ch.Addr <> ISNULL(ch.CustAddr, N'');

    /* the fixed list of the rule: email + whatsapp (only the details that were entered) */
    INSERT #np_cand (RowKey, RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT sp.RowKey, st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'FIXED', fr.RecipientName, ch.Addr, NULL, st.TSubject, st.TBody, N'PENDING', NULL
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.RowKey = st.RowKey
    INNER JOIN dbo.NotificationRuleRecipients fr ON fr.RuleId = st.RuleId
    CROSS APPLY (VALUES (N'EMAIL', fr.Email, st.NotifyEmail), (N'WHATSAPP', fr.Mobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE ch.Enabled = 1 AND ch.Addr IS NOT NULL;

    /* same address twice in one run (e.g. two fixed entries) -> keep one */
    ;WITH d AS (
        SELECT ROW_NUMBER() OVER (PARTITION BY RuleId, SpecId, ISNULL(PersonName, N''), Channel, RecipientType, RecipientAddress, DueDate, DaysBefore ORDER BY (SELECT 1)) AS rn
        FROM #np_cand
    )
    DELETE FROM d WHERE rn > 1;

    /* already created before -> not due again */
    DELETE c
    FROM #np_cand c
    WHERE EXISTS (SELECT 1 FROM dbo.Notifications n
                  WHERE n.RuleId = c.RuleId AND n.SpecId = c.SpecId AND n.Channel = c.Channel
                    AND n.RecipientType = c.RecipientType AND n.RecipientAddress = c.RecipientAddress AND n.DueDate = c.DueDate
                    AND n.DaysBefore = c.DaysBefore
                    AND ISNULL(n.PersonName, N'') = ISNULL(c.PersonName, N''));   -- each reminder step once (per member)

    /* write the message for each recipient */
    UPDATE c
    SET [Subject] = dbo.fn_Notification_Render(c.TSubject, c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName),
        Body      = dbo.fn_Notification_Render(c.TBody,    c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName)
    FROM #np_cand c
    INNER JOIN #np_spec sp ON sp.RowKey = c.RowKey;

    DECLARE @Pending INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'PENDING');
    DECLARE @Skipped INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'SKIPPED');
    DECLARE @InApp   INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'SENT');
    DECLARE @Created INT = 0;

    IF @Mode = N'GENERATE'
    BEGIN
        INSERT dbo.Notifications (RuleId, TrackerId, SpecId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore,
                                  Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, [Subject], Body, [Status], StatusMessage, SentDate, ModifiedBy)
        SELECT RuleId, TrackerId, SpecId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore,
               Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, [Subject], Body, [Status], StatusMessage,
               CASE WHEN [Status] = N'SENT' THEN SYSUTCDATETIME() END, @UserId
        FROM #np_cand;
        SET @Created = @@ROWCOUNT;
    END;

    DECLARE @Rows NVARCHAR(MAX) = (
        SELECT c.RuleId AS ruleId, c.SpecId AS specId, c.TrackerId AS trackerId, c.CustomerName AS customer,
               c.CategoryName AS category, c.SubCategoryName AS subCategory, c.PersonName AS person,
               c.DueDate AS dueDate, c.DaysLeft AS daysLeft, c.DaysBefore AS daysBefore,
               c.Channel AS channel, c.RecipientType AS recipientType, c.RecipientName AS recipient, c.RecipientAddress AS address,
               c.[Subject] AS [subject], c.Body AS body, c.[Status] AS [status], c.StatusMessage AS [message]
        FROM #np_cand c
        ORDER BY c.DueDate, c.CustomerName, c.SpecId, c.Channel, c.RecipientType
        FOR JSON PATH);

    SELECT (
        SELECT @Mode AS mode, @Today AS today, @CatchUpDays AS catchUpDays,
               @Pending AS pending, @Skipped AS skipped, @InApp AS inApp, @Created AS created,
               JSON_QUERY(ISNULL(CASE WHEN @Mode = N'PREVIEW' THEN @Rows END, N'[]')) AS [rows]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS json;
END;
GO

/* ---------- 10. Renew: a new tracker from an old one (stage 2) ----------
   @Items: NULL = every Due Soon / Expired item of the tracker; or a list such as N'E12,M34' (E = entry, M = member).
   The new tracker gets the same customer and assigned staff, the entries of those items, those members (with their
   tracker member details and links), and the entries' services at TODAY's Services price (count follows the people).
   Each old item is linked to the new tracker: Renewal in Progress now, Renewed when the new tracker is Completed. */
CREATE OR ALTER PROCEDURE dbo.usp_Tracker_Renew
    @TrackerId INT,
    @Items     NVARCHAR(MAX) = NULL,
    @UserId    INT = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId) THROW 50404, 'Tracker not found.', 1;

    SELECT v.ItemType, v.ItemId, v.EntryId, v.PersonName, v.HolderContactId, v.StoredStatus
    INTO #it
    FROM dbo.vw_RenewalItems v
    WHERE v.TrackerId = @TrackerId
      AND v.RenewalStatus NOT IN (N'RENEWAL_IN_PROGRESS', N'RENEWED')
      AND ((@Items IS NULL AND v.RenewalStatus IN (N'DUE_SOON', N'EXPIRED'))
        OR (@Items IS NOT NULL AND CONCAT(LEFT(v.ItemType, 1), v.ItemId) IN (SELECT UPPER(LTRIM(RTRIM(value))) FROM STRING_SPLIT(@Items, N','))));
    IF NOT EXISTS (SELECT 1 FROM #it)
        THROW 50001, 'Nothing to renew: no item of this tracker is Due Soon or Expired (or they are already being renewed).', 1;

    DECLARE @Status NVARCHAR(30) = ISNULL((SELECT TOP (1) StatusCode FROM dbo.TrackerStatuses WHERE IsDefault = 1 AND IsActive = 1), N'NEW');
    DECLARE @New INT, @Note NVARCHAR(200) = CONCAT(N'Renewal of tracker #', @TrackerId);
    CREATE TABLE #emap (OldId INT NOT NULL, NewId INT NOT NULL);
    CREATE TABLE #pmap (OldId INT NOT NULL, NewId INT NOT NULL);

    BEGIN TRANSACTION;

    INSERT dbo.Trackers (CustomerId, CustomerCode, CustomerName, Mobile, Email, AssignedToEmployeeId, AssignedToEmployeeName, [Status],
                         CreatedBy, CreatedDate, FollowUpFrequencyDays, FollowUpFrequencyId, RenewalOfTrackerId)
    SELECT t.CustomerId, t.CustomerCode, t.CustomerName, t.Mobile, t.Email, t.AssignedToEmployeeId, t.AssignedToEmployeeName, @Status,
           @UserId, SYSUTCDATETIME(), t.FollowUpFrequencyDays, t.FollowUpFrequencyId, @TrackerId
    FROM dbo.Trackers t WHERE t.TrackerId = @TrackerId;
    SET @New = CONVERT(INT, SCOPE_IDENTITY());

    /* entries (Category / Sub Category) of the items - without dates, they are filled when this renewal is done */
    MERGE dbo.TrackerCategorySubCategorySpecs AS tgt
    USING (SELECT s.* FROM dbo.TrackerCategorySubCategorySpecs s WHERE s.TrackerCategorySubCategorySpecId IN (SELECT EntryId FROM #it)) AS src
    ON 1 = 0
    WHEN NOT MATCHED THEN
        INSERT (TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName, IssueDate, DueDate, [Status], Notes, CreatedBy, CreatedDate, PersonName)
        VALUES (@New, src.CategoryId, src.CategoryName, src.SubCategoryId, src.SubCategoryName, NULL, NULL, src.[Status], @Note, @UserId, SYSUTCDATETIME(), src.PersonName)
    OUTPUT src.TrackerCategorySubCategorySpecId, inserted.TrackerCategorySubCategorySpecId INTO #emap (OldId, NewId);

    /* the members being renewed */
    MERGE dbo.TrackerCategorySubCategorySpecPersons AS tgt
    USING (SELECT p.TrackerCategorySubCategorySpecPersonId, p.PersonName, m.NewId
           FROM dbo.TrackerCategorySubCategorySpecPersons p
           INNER JOIN #it i ON i.ItemType = N'MEMBER' AND i.ItemId = p.TrackerCategorySubCategorySpecPersonId
           INNER JOIN #emap m ON m.OldId = p.TrackerCategorySubCategorySpecId) AS src
    ON 1 = 0
    WHEN NOT MATCHED THEN
        INSERT (TrackerCategorySubCategorySpecId, TrackerId, PersonName, CreatedBy, CreatedDate)
        VALUES (src.NewId, @New, src.PersonName, @UserId, SYSUTCDATETIME())
    OUTPUT src.TrackerCategorySubCategorySpecPersonId, inserted.TrackerCategorySubCategorySpecPersonId INTO #pmap (OldId, NewId);

    /* their tracker member records (employee / family links kept) - one per person */
    INSERT dbo.TrackerFamilyMembers (TrackerId, Name, RelationshipId, DateOfBirth, PassportNo, VisaType, [Status], CreatedBy, CreatedDate,
                                     CustomerContactId, SponsorContactId, CustomerFamilyMemberId)
    SELECT @New, fm.Name, fm.RelationshipId, fm.DateOfBirth, fm.PassportNo, fm.VisaType, fm.[Status], @UserId, SYSUTCDATETIME(),
           fm.CustomerContactId, fm.SponsorContactId, fm.CustomerFamilyMemberId
    FROM (SELECT f.*, ROW_NUMBER() OVER (PARTITION BY LOWER(LTRIM(RTRIM(f.Name))) ORDER BY f.TrackerFamilyMemberId) AS rn
          FROM dbo.TrackerFamilyMembers f
          WHERE f.TrackerId = @TrackerId
            AND LOWER(LTRIM(RTRIM(f.Name))) IN (SELECT LOWER(LTRIM(RTRIM(PersonName))) FROM #it WHERE ItemType = N'MEMBER')) fm
    WHERE fm.rn = 1;

    /* services of the entries: the company's own list, and each renewed employee's list; today's price */
    SELECT ts.TrackerServiceSpecId, m.NewId AS NewEntryId, ts.EmployeeContactId,
           (SELECT COUNT(1) FROM dbo.vw_RenewalItems v WHERE v.EntryId = ts.TrackerCategorySubCategorySpecId AND v.ItemType = N'MEMBER'
                                                        AND v.HolderContactId = ts.EmployeeContactId) AS OldPeople,
           (SELECT COUNT(1) FROM #it i WHERE i.EntryId = ts.TrackerCategorySubCategorySpecId AND i.ItemType = N'MEMBER'
                                       AND i.HolderContactId = ts.EmployeeContactId) AS NewPeople
    INTO #svc
    FROM dbo.TrackerServiceSpecs ts
    INNER JOIN #emap m ON m.OldId = ts.TrackerCategorySubCategorySpecId
    WHERE ISNULL(ts.[Status], N'') <> N'Deleted'
      AND (ts.EmployeeContactId IS NULL
           OR EXISTS (SELECT 1 FROM #it i WHERE i.EntryId = ts.TrackerCategorySubCategorySpecId AND i.HolderContactId = ts.EmployeeContactId));

    DECLARE @unit NVARCHAR(400) = CASE WHEN COL_LENGTH(N'dbo.Services', N'PricingUnit') IS NOT NULL
        THEN N'ISNULL((SELECT TOP (1) sv.PricingUnit FROM dbo.Services sv WHERE sv.ServiceId = ts.ServiceId), N''PerPerson'')'
        ELSE N'N''PerPerson''' END;
    DECLARE @sql NVARCHAR(MAX) = CAST(N'' AS NVARCHAR(MAX)) + N'
INSERT dbo.TrackerServiceSpecs (TrackerCategorySubCategorySpecId, TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName,
    ServiceId, ServiceName, Amount, VAT, [Status], CreatedBy, CreatedDate, Quantity, [Count], EmployeeContactId)
SELECT s.NewEntryId, @New, ts.CategoryId, ts.CategoryName, ts.SubCategoryId, ts.SubCategoryName,
       ts.ServiceId, ts.ServiceName, ISNULL(cur.Amount, ts.Amount), ISNULL(cur.VAT, ts.VAT), N''Pending'', @UserId, SYSUTCDATETIME(), ts.Quantity,
       CASE WHEN ts.EmployeeContactId IS NOT NULL AND ' + @unit + N' = N''PerPerson'' AND s.OldPeople > 0 AND s.NewPeople > 0
            THEN CASE WHEN ts.[Count] * s.NewPeople / s.OldPeople < 1 THEN 1 ELSE ts.[Count] * s.NewPeople / s.OldPeople END
            ELSE ts.[Count] END,
       ts.EmployeeContactId
FROM #svc s
INNER JOIN dbo.TrackerServiceSpecs ts ON ts.TrackerServiceSpecId = s.TrackerServiceSpecId
OUTER APPLY (SELECT TOP (1) sv.Amount, sv.VAT FROM dbo.Services sv WHERE sv.ServiceId = ts.ServiceId AND sv.IsActive = 1) cur
ORDER BY ts.TrackerServiceSpecId;';
    EXEC sys.sp_executesql @sql, N'@New INT, @UserId INT', @New = @New, @UserId = @UserId;

    /* the links: old item -> new tracker */
    INSERT dbo.TrackerRenewalLinks (OldTrackerId, OldItemType, OldItemId, NewTrackerId, NewEntryId, NewPersonId, CreatedBy)
    SELECT @TrackerId, i.ItemType, i.ItemId, @New, m.NewId, pm.NewId, @UserId
    FROM #it i
    INNER JOIN #emap m ON m.OldId = i.EntryId
    LEFT JOIN #pmap pm ON i.ItemType = N'MEMBER' AND pm.OldId = i.ItemId;

    /* an item chosen by hand that was marked Not Renewing is being renewed after all */
    UPDATE s SET RenewalStatus = NULL, RenewalNote = NULL, RenewalStatusDate = SYSUTCDATETIME(), RenewalStatusBy = @UserId
    FROM dbo.TrackerCategorySubCategorySpecs s INNER JOIN #it i ON i.ItemType = N'ENTRY' AND i.ItemId = s.TrackerCategorySubCategorySpecId
    WHERE s.RenewalStatus = N'NOT_RENEWING';
    UPDATE p SET RenewalStatus = NULL, RenewalNote = NULL, RenewalStatusDate = SYSUTCDATETIME(), RenewalStatusBy = @UserId
    FROM dbo.TrackerCategorySubCategorySpecPersons p INNER JOIN #it i ON i.ItemType = N'MEMBER' AND i.ItemId = p.TrackerCategorySubCategorySpecPersonId
    WHERE p.RenewalStatus = N'NOT_RENEWING';

    COMMIT TRANSACTION;

    SELECT @New AS NewTrackerId, (SELECT COUNT(1) FROM #it) AS Items, (SELECT COUNT(1) FROM #emap) AS Entries, (SELECT COUNT(1) FROM #pmap) AS Members;
END;
GO

/* ---------- 11. One time: open trackers whose work is done and only dates are missing -> Dates Pending ---------- */
DECLARE @t INT, @n INT = 0;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.TrackerId FROM dbo.Trackers t
    WHERE UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'COMPLETED', N'DATES_PENDING', N'PENDING', N'REJECTED', N'NOT_COMPLETED', N'CANCELLED')
      AND NOT EXISTS (SELECT 1 FROM dbo.TrackerStatuses s WHERE s.StatusCode = t.[Status] AND s.IsTerminal = 1)
      AND dbo.fn_Tracker_OnlyDatesMissing(t.TrackerId) = 1;
OPEN c; FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @t, @StatusCode = N'DATES_PENDING', @Note = N'Work done - waiting for the Actual / Due Dates (script 017)';
    SET @n += 1;
    FETCH NEXT FROM c INTO @t;
END
CLOSE c; DEALLOCATE c;
IF @n > 0
BEGIN
    DECLARE @msg NVARCHAR(200) = CONCAT(N'MOVED   ', @n, N' tracker(s) to Dates Pending (work done, dates missing).');
    RAISERROR(@msg, 10, 1) WITH NOWAIT;
END
GO

SET NOEXEC OFF;
GO
