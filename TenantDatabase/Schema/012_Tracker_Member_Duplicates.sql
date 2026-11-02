/*
  012 - Renewal tracker: no duplicate members, services start as Pending
  ======================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-30_tracker_member_duplicates.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/012_Tracker_Member_Duplicates.sql   run automatically on every tenant database (BP_*)
  Needs 009 (2026-10-23_tracker_members_from_customer_contacts.sql) first - it adds CustomerContactId / SponsorContactId.

  * UX_TrackerFamilyMembers_Tracker_Contact  - the same employee (customer contact) only once per tracker.
  * UX_TrackerFamilyMembers_Tracker_Passport - the same passport number only once per tracker.
      Both are filtered unique indexes (empty values are not checked).
      If a database ALREADY has duplicates, that index is NOT created and the duplicates are listed
      (message severity 10 - the script does not fail). Remove the duplicates on the tracker screen,
      then run this script again to add the index. The API checks the same rules either way
      (TrackerFamilyMembersController), including name + date of birth + relationship under one employee.
  * TrackerServiceSpecs.Status default = Pending (already set by 010; re-checked here) and any
    remaining old Active / Open / empty values become Pending (Completed for completed trackers).
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

/* ---- 1. Same employee twice in one tracker ---- */
IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'CustomerContactId') IS NULL
    RAISERROR(N'SKIPPED UX_TrackerFamilyMembers_Tracker_Contact - run 009 (tracker members from customer contacts) first.', 10, 1) WITH NOWAIT;
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TrackerFamilyMembers_Tracker_Contact' AND object_id = OBJECT_ID(N'dbo.TrackerFamilyMembers'))
    RAISERROR(N'OK      UX_TrackerFamilyMembers_Tracker_Contact - already there.', 10, 1) WITH NOWAIT;
ELSE
BEGIN
    /* dynamic SQL: the batch must also compile where CustomerContactId does not exist yet */
    DECLARE @dupContacts NVARCHAR(MAX) = N'', @msg1 NVARCHAR(1800);
    EXEC sys.sp_executesql N'
        SELECT @out = @out + CONCAT(N'' TrackerId '', TrackerId, N'' / ContactId '', CustomerContactId, N'' ('', COUNT(1), N'' rows);'')
        FROM dbo.TrackerFamilyMembers
        WHERE CustomerContactId IS NOT NULL
        GROUP BY TrackerId, CustomerContactId
        HAVING COUNT(1) > 1;', N'@out NVARCHAR(MAX) OUTPUT', @out = @dupContacts OUTPUT;

    IF @dupContacts <> N''
    BEGIN
        SET @msg1 = LEFT(@dupContacts, 1800);
        RAISERROR(N'WARNING UX_TrackerFamilyMembers_Tracker_Contact NOT created - the same employee is in a tracker more than once:%s', 10, 1, @msg1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_TrackerFamilyMembers_Tracker_Contact
            ON dbo.TrackerFamilyMembers (TrackerId, CustomerContactId) WHERE CustomerContactId IS NOT NULL;';
        RAISERROR(N'CREATED UX_TrackerFamilyMembers_Tracker_Contact', 10, 1) WITH NOWAIT;
    END
END
GO

/* ---- 2. Same passport twice in one tracker ---- */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TrackerFamilyMembers_Tracker_Passport' AND object_id = OBJECT_ID(N'dbo.TrackerFamilyMembers'))
    RAISERROR(N'OK      UX_TrackerFamilyMembers_Tracker_Passport - already there.', 10, 1) WITH NOWAIT;
ELSE
BEGIN
    /* stored values with spaces around them are trimmed first, so the index compares like the API does */
    UPDATE dbo.TrackerFamilyMembers SET PassportNo = NULLIF(LTRIM(RTRIM(PassportNo)), N'')
    WHERE PassportNo IS NOT NULL AND PassportNo <> NULLIF(LTRIM(RTRIM(PassportNo)), N'');
    UPDATE dbo.TrackerFamilyMembers SET PassportNo = NULL WHERE PassportNo = N'';

    DECLARE @dupPassports NVARCHAR(MAX) = N'', @msg2 NVARCHAR(1800);
    SELECT @dupPassports = @dupPassports + CONCAT(N' TrackerId ', TrackerId, N' / passport ', PassportNo, N' (', COUNT(1), N' rows);')
    FROM dbo.TrackerFamilyMembers
    WHERE PassportNo IS NOT NULL
    GROUP BY TrackerId, PassportNo
    HAVING COUNT(1) > 1;

    IF @dupPassports <> N''
    BEGIN
        SET @msg2 = LEFT(@dupPassports, 1800);
        RAISERROR(N'WARNING UX_TrackerFamilyMembers_Tracker_Passport NOT created - a passport number is in a tracker more than once:%s', 10, 1, @msg2) WITH NOWAIT;
    END
    ELSE
    BEGIN
        EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_TrackerFamilyMembers_Tracker_Passport
            ON dbo.TrackerFamilyMembers (TrackerId, PassportNo) WHERE PassportNo IS NOT NULL;';
        RAISERROR(N'CREATED UX_TrackerFamilyMembers_Tracker_Passport', 10, 1) WITH NOWAIT;
    END
END
GO

/* ---- 3. Family members entered twice (same name + date of birth + relationship under the same employee) - report only ---- */
IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'SponsorContactId') IS NOT NULL
BEGIN
    DECLARE @dupFamily NVARCHAR(MAX) = N'', @msg3 NVARCHAR(1800);
    EXEC sys.sp_executesql N'
        SELECT @out = @out + CONCAT(N'' TrackerId '', TrackerId, N'' / '', MIN(Name), N'' ('', COUNT(1), N'' rows);'')
        FROM dbo.TrackerFamilyMembers
        WHERE CustomerContactId IS NULL
        GROUP BY TrackerId, LOWER(LTRIM(RTRIM(Name))), RelationshipId, DateOfBirth, ISNULL(SponsorContactId, 0)
        HAVING COUNT(1) > 1;', N'@out NVARCHAR(MAX) OUTPUT', @out = @dupFamily OUTPUT;
    IF @dupFamily <> N''
    BEGIN
        SET @msg3 = LEFT(@dupFamily, 1800);
        RAISERROR(N'NOTE    family members entered twice (the API now refuses new ones; please review):%s', 10, 1, @msg3) WITH NOWAIT;
    END
END
GO

/* ---- 4. Services start as Pending ---- */
UPDATE s SET Status = CASE WHEN UPPER(LTRIM(RTRIM(t.Status))) = N'COMPLETED' THEN N'Completed' ELSE N'Pending' END
FROM dbo.TrackerServiceSpecs s
INNER JOIN dbo.Trackers t ON t.TrackerId = s.TrackerId
WHERE ISNULL(s.Status, N'') NOT IN (N'Pending', N'Incomplete', N'Completed', N'Deleted');
GO
DECLARE @df SYSNAME = (SELECT dc.name FROM sys.default_constraints dc
                       INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                       WHERE dc.parent_object_id = OBJECT_ID(N'dbo.TrackerServiceSpecs') AND c.name = N'Status');
DECLARE @dropSql NVARCHAR(400);
IF @df IS NOT NULL AND OBJECT_DEFINITION(OBJECT_ID(N'dbo.' + QUOTENAME(@df))) NOT LIKE N'%Pending%'
BEGIN
    SET @dropSql = N'ALTER TABLE dbo.TrackerServiceSpecs DROP CONSTRAINT ' + QUOTENAME(@df);
    EXEC sys.sp_executesql @dropSql;
    SET @df = NULL;
END
IF @df IS NULL
    ALTER TABLE dbo.TrackerServiceSpecs ADD CONSTRAINT DF_TrackerServiceSpecs_Status DEFAULT (N'Pending') FOR Status;
GO
