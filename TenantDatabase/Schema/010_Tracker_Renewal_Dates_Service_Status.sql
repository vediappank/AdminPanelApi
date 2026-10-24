/*
  010 - Renewal tracker: Actual Date / Due Date, service status, completion rules
  ===============================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-23_tracker_renewal_dates_and_service_status.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/010_Tracker_Renewal_Dates_Service_Status.sql    run automatically on every tenant database (BP_*)

  * TrackerCategorySubCategorySpecs.ActualDate         - when the renewal of the entry was actually done
                                                          (the entry's DueDate = the new expiry / next renewal).
  * TrackerCategorySubCategorySpecPersons.ActualDate / DueDate - the same per member of an entry.
  * TrackerServiceSpecs.Status = Pending | Incomplete | Completed (was Active / Open).
      Existing services: Completed for trackers that are already Completed, otherwise Pending.
  * dbo.fn_Tracker_RenewalBlocker(@TrackerId) - why a tracker cannot be completed yet (NULL = it can):
      every entry and every member needs an Actual Date and a Due Date, and every service must be Completed.
      The API checks it when a tracker is completed by hand; it is also added to dbo.fn_Tracker_CompletionBlocker
      (automatic completion) by replacing one line inside the CURRENT definition, so other changes are kept.
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecs', N'ActualDate') IS NULL
    ALTER TABLE dbo.TrackerCategorySubCategorySpecs ADD ActualDate DATE NULL;
GO
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecPersons', N'ActualDate') IS NULL
    ALTER TABLE dbo.TrackerCategorySubCategorySpecPersons ADD ActualDate DATE NULL;
GO
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecPersons', N'DueDate') IS NULL
    ALTER TABLE dbo.TrackerCategorySubCategorySpecPersons ADD DueDate DATE NULL;
GO

/* Service status: Pending / Incomplete / Completed. */
UPDATE s SET Status = CASE WHEN UPPER(LTRIM(RTRIM(t.Status))) = N'COMPLETED' THEN N'Completed' ELSE N'Pending' END
FROM dbo.TrackerServiceSpecs s
INNER JOIN dbo.Trackers t ON t.TrackerId = s.TrackerId
WHERE s.Status NOT IN (N'Pending', N'Incomplete', N'Completed', N'Deleted');
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

/* Why the tracker cannot be completed yet - NULL when it can. Reasons start with a space. */
CREATE OR ALTER FUNCTION dbo.fn_Tracker_RenewalBlocker (@TrackerId INT)
RETURNS NVARCHAR(400)
AS
BEGIN
    DECLARE @Entries INT = (SELECT COUNT(1) FROM dbo.TrackerCategorySubCategorySpecs
                            WHERE TrackerId = @TrackerId AND (ActualDate IS NULL OR DueDate IS NULL));
    DECLARE @Members INT = (SELECT COUNT(1) FROM dbo.TrackerCategorySubCategorySpecPersons
                            WHERE TrackerId = @TrackerId AND (ActualDate IS NULL OR DueDate IS NULL));
    DECLARE @Services INT = (SELECT COUNT(1) FROM dbo.TrackerServiceSpecs
                             WHERE TrackerId = @TrackerId AND ISNULL(Status, N'') NOT IN (N'Completed', N'Deleted'));
    DECLARE @Msg NVARCHAR(400) = N'';
    IF @Entries > 0
        SET @Msg = CONCAT(@Msg, N' ', @Entries, CASE WHEN @Entries = 1 THEN N' entry needs' ELSE N' entries need' END, N' an Actual Date and a Due Date.');
    IF @Members > 0
        SET @Msg = CONCAT(@Msg, N' ', @Members, CASE WHEN @Members = 1 THEN N' member needs' ELSE N' members need' END, N' an Actual Date and a Due Date.');
    IF @Services > 0
        SET @Msg = CONCAT(@Msg, N' ', @Services, CASE WHEN @Services = 1 THEN N' service is' ELSE N' services are' END, N' not Completed yet.');
    RETURN NULLIF(@Msg, N'');
END;
GO

/* Add the same rules to the automatic completion check: one line is inserted into the CURRENT definition.
   The header is found as "FUNCTION <name>" (comments above it may also contain the word "function"),
   and everything before it is replaced by ALTER. A failure is reported instead of being hidden. */
DECLARE @Def NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID(N'dbo.fn_Tracker_CompletionBlocker'));
DECLARE @Find NVARCHAR(200) = N'IF @Msg = N'''' RETURN NULL;';
DECLARE @Add NVARCHAR(400) = N'SET @Msg = CONCAT(@Msg, ISNULL(dbo.fn_Tracker_RenewalBlocker(@TrackerId), N''''));';
DECLARE @Pos INT = 0, @At INT = 0, @After NVARCHAR(200);
IF @Def IS NULL
    RAISERROR(N'SKIPPED fn_Tracker_CompletionBlocker - function not found.', 10, 1) WITH NOWAIT;
ELSE IF CHARINDEX(N'fn_Tracker_RenewalBlocker', @Def) > 0
    RAISERROR(N'OK      fn_Tracker_CompletionBlocker - already includes the renewal rules.', 10, 1) WITH NOWAIT;
ELSE IF CHARINDEX(@Find, @Def) = 0
    RAISERROR(N'WARNING fn_Tracker_CompletionBlocker - expected text not found; add the renewal rules by hand.', 10, 1) WITH NOWAIT;
ELSE
BEGIN
    -- the "FUNCTION" that is directly followed by the function's own name (not one inside a comment)
    WHILE 1 = 1
    BEGIN
        SET @Pos = CHARINDEX(N'FUNCTION', @Def, @Pos + 1);
        IF @Pos = 0 BREAK;
        SET @After = SUBSTRING(@Def, @Pos + 8, 120);
        SET @After = LTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@After, N'[', N''), N']', N''), NCHAR(13), N' '), NCHAR(10), N' '), NCHAR(9), N' '));
        IF @After LIKE N'dbo.fn[_]Tracker[_]CompletionBlocker%' OR @After LIKE N'fn[_]Tracker[_]CompletionBlocker%' BEGIN SET @At = @Pos; BREAK; END
    END
    IF @At = 0
        RAISERROR(N'WARNING fn_Tracker_CompletionBlocker - could not find its header; add the renewal rules by hand.', 10, 1) WITH NOWAIT;
    ELSE
    BEGIN
        SET @Def = REPLACE(@Def, @Find, @Add + NCHAR(13) + NCHAR(10) + N'    ' + @Find);
        SET @Def = N'ALTER ' + SUBSTRING(@Def, @At, 2147483647);
        BEGIN TRY
            EXEC sys.sp_executesql @Def;
            RAISERROR(N'PATCHED fn_Tracker_CompletionBlocker', 10, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            DECLARE @Err NVARCHAR(2048) = ERROR_MESSAGE();
            RAISERROR(N'FAILED  fn_Tracker_CompletionBlocker - not changed: %s', 16, 1, @Err);
        END CATCH
    END
END
GO
