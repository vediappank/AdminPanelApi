/*
  011 - Renewal tracker: history of Actual Date / Due Date changes (who changed what, and when)
  =============================================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-25_tracker_renewal_date_history.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/011_Tracker_Renewal_Date_History.sql    run automatically on every tenant database (BP_*)
  Needs 010 (2026-10-23_tracker_renewal_dates_and_service_status.sql) first - it adds the ActualDate / DueDate columns.

  * dbo.TrackerRenewalDateHistory - one row per entry or member whose Actual Date or Due Date changed:
      old and new dates, the tracker status at that moment, an optional note, the user and the time (UTC).
  * Two triggers fill it, so every way the dates are saved is recorded (tracker edit screen, the
    "Update dates" dialog for completed trackers, scripts):
      trg_TrackerSpecs_RenewalDateHistory        on dbo.TrackerCategorySubCategorySpecs
      trg_TrackerSpecPersons_RenewalDateHistory  on dbo.TrackerCategorySubCategorySpecPersons
    The user is SESSION_CONTEXT('BP_UserId') (set by the API), else the row's ModifiedBy.
    The note is SESSION_CONTEXT('BP_DateNote') (the reason typed in the dialog).
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.TrackerRenewalDateHistory', N'U') IS NULL
CREATE TABLE dbo.TrackerRenewalDateHistory
(
    HistoryId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrackerRenewalDateHistory PRIMARY KEY,
    TrackerId      INT            NOT NULL,
    TargetType     NVARCHAR(10)   NOT NULL,               -- Entry | Member
    TargetId       INT            NOT NULL,               -- TrackerCategorySubCategorySpecId / ...PersonId
    TargetName     NVARCHAR(300)  NULL,                   -- "Category / Sub Category" or the member's name
    OldActualDate  DATE           NULL,
    NewActualDate  DATE           NULL,
    OldDueDate     DATE           NULL,
    NewDueDate     DATE           NULL,
    TrackerStatus  NVARCHAR(50)   NULL,                   -- tracker status when the change was made
    Note           NVARCHAR(500)  NULL,
    ChangedBy      INT            NULL,                   -- dbo.Users.UserId
    ChangedByName  NVARCHAR(210)  NULL,                   -- name at the time of the change
    ChangedAt      DATETIME2(0)   NOT NULL CONSTRAINT DF_TrackerRenewalDateHistory_ChangedAt DEFAULT (SYSUTCDATETIME())
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TrackerRenewalDateHistory_Tracker' AND object_id = OBJECT_ID(N'dbo.TrackerRenewalDateHistory'))
    CREATE INDEX IX_TrackerRenewalDateHistory_Tracker ON dbo.TrackerRenewalDateHistory (TrackerId, ChangedAt DESC);
GO

/* Entries (company entries; for members entries the API keeps the entry dates in step with its members). */
CREATE OR ALTER TRIGGER dbo.trg_TrackerSpecs_RenewalDateHistory
ON dbo.TrackerCategorySubCategorySpecs
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(ActualDate) OR UPDATE(DueDate)) RETURN;
    DECLARE @User INT = TRY_CAST(SESSION_CONTEXT(N'BP_UserId') AS INT);
    DECLARE @Note NVARCHAR(500) = TRY_CAST(SESSION_CONTEXT(N'BP_DateNote') AS NVARCHAR(500));

    INSERT dbo.TrackerRenewalDateHistory
        (TrackerId, TargetType, TargetId, TargetName, OldActualDate, NewActualDate, OldDueDate, NewDueDate, TrackerStatus, Note, ChangedBy, ChangedByName)
    SELECT i.TrackerId, N'Entry', i.TrackerCategorySubCategorySpecId,
           LEFT(CONCAT(i.CategoryName, N' / ', i.SubCategoryName), 300),
           d.ActualDate, i.ActualDate, d.DueDate, i.DueDate, t.Status, NULLIF(@Note, N''),
           COALESCE(@User, i.ModifiedBy),
           (SELECT TOP (1) LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) FROM dbo.Users u WHERE u.UserId = COALESCE(@User, i.ModifiedBy))
    FROM inserted i
    INNER JOIN deleted d ON d.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId
    LEFT JOIN dbo.Trackers t ON t.TrackerId = i.TrackerId
    WHERE ISNULL(d.ActualDate, '19000101') <> ISNULL(i.ActualDate, '19000101')
       OR ISNULL(d.DueDate, '19000101') <> ISNULL(i.DueDate, '19000101');
END;
GO

/* Members of an entry. */
CREATE OR ALTER TRIGGER dbo.trg_TrackerSpecPersons_RenewalDateHistory
ON dbo.TrackerCategorySubCategorySpecPersons
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(ActualDate) OR UPDATE(DueDate)) RETURN;
    DECLARE @User INT = TRY_CAST(SESSION_CONTEXT(N'BP_UserId') AS INT);
    DECLARE @Note NVARCHAR(500) = TRY_CAST(SESSION_CONTEXT(N'BP_DateNote') AS NVARCHAR(500));

    INSERT dbo.TrackerRenewalDateHistory
        (TrackerId, TargetType, TargetId, TargetName, OldActualDate, NewActualDate, OldDueDate, NewDueDate, TrackerStatus, Note, ChangedBy, ChangedByName)
    SELECT i.TrackerId, N'Member', i.TrackerCategorySubCategorySpecPersonId,
           LEFT(CONCAT(i.PersonName, N' (', s.CategoryName, N' / ', s.SubCategoryName, N')'), 300),
           d.ActualDate, i.ActualDate, d.DueDate, i.DueDate, t.Status, NULLIF(@Note, N''),
           COALESCE(@User, i.ModifiedBy),
           (SELECT TOP (1) LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) FROM dbo.Users u WHERE u.UserId = COALESCE(@User, i.ModifiedBy))
    FROM inserted i
    INNER JOIN deleted d ON d.TrackerCategorySubCategorySpecPersonId = i.TrackerCategorySubCategorySpecPersonId
    LEFT JOIN dbo.TrackerCategorySubCategorySpecs s ON s.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId
    LEFT JOIN dbo.Trackers t ON t.TrackerId = i.TrackerId
    WHERE ISNULL(d.ActualDate, '19000101') <> ISNULL(i.ActualDate, '19000101')
       OR ISNULL(d.DueDate, '19000101') <> ISNULL(i.DueDate, '19000101');
END;
GO

PRINT N'OK - dbo.TrackerRenewalDateHistory and its two triggers are in place.';
GO
