/*
  015 - Tracker services per employee
  ===================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-31_tracker_services_per_employee.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/015_Tracker_Services_Per_Employee.sql   run automatically on every tenant database (BP_*)
  Needs 009 (TrackerFamilyMembers.CustomerContactId / SponsorContactId). 013 (Services.PricingUnit) is used when present.

  * TrackerServiceSpecs.EmployeeContactId - on a Members entry, the employee (customer contact) whose service list
      this row belongs to. The list covers the employee and the family members they sponsor.
      NULL = the entry's own list: a "Company itself" entry, or members not linked to any employee.
      No foreign key (a second SET NULL path from CustomerContacts is not allowed); a removed contact's id is cleared below.
  * Count is now the TOTAL quantity of the row - the quotation uses it as typed.

  One-time conversion of existing Members entries (only entries where no row has an employee yet):
    every service row of the entry is copied to each employee group of the entry, keeping the price the old
    quotation would have charged: Count x people of the group for per-person services, Count for per-family / fixed.
    The original row goes to the first employee (so quotation lines that point at it stay valid).
    Members not linked to an employee keep a copy with the original Count (each of them is quoted alone).
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.TrackerServiceSpecs', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.TrackerServiceSpecs', N'EmployeeContactId') IS NULL
    ALTER TABLE dbo.TrackerServiceSpecs ADD EmployeeContactId INT NULL;
GO

IF COL_LENGTH(N'dbo.TrackerServiceSpecs', N'EmployeeContactId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TrackerServiceSpecs_Spec_Employee' AND object_id = OBJECT_ID(N'dbo.TrackerServiceSpecs'))
    CREATE INDEX IX_TrackerServiceSpecs_Spec_Employee ON dbo.TrackerServiceSpecs (TrackerCategorySubCategorySpecId, EmployeeContactId);
GO

/* A contact that no longer exists: the row becomes the entry's own list again. */
IF COL_LENGTH(N'dbo.TrackerServiceSpecs', N'EmployeeContactId') IS NOT NULL AND OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
    EXEC sp_executesql N'UPDATE ts SET EmployeeContactId = NULL FROM dbo.TrackerServiceSpecs ts
WHERE ts.EmployeeContactId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.CustomerContacts c WHERE c.ContactId = ts.EmployeeContactId);';
GO

/* ---- One-time conversion of the common lists of Members entries ---- */
IF COL_LENGTH(N'dbo.TrackerServiceSpecs', N'EmployeeContactId') IS NULL
   OR COL_LENGTH(N'dbo.TrackerFamilyMembers', N'CustomerContactId') IS NULL
   OR COL_LENGTH(N'dbo.TrackerFamilyMembers', N'SponsorContactId') IS NULL
   OR OBJECT_ID(N'dbo.TrackerCategorySubCategorySpecPersons', N'U') IS NULL
BEGIN
    RAISERROR(N'SKIPPED conversion - run 009 (tracker members from customer contacts) first, then this script again.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    DECLARE @countColumn sysname = CASE WHEN COL_LENGTH(N'dbo.TrackerServiceSpecs', N'Count') IS NOT NULL THEN N'Count' ELSE N'Quantity' END;
    DECLARE @unitSql nvarchar(400) = CASE WHEN COL_LENGTH(N'dbo.Services', N'PricingUnit') IS NOT NULL
        THEN N'ISNULL((SELECT TOP (1) sv.PricingUnit FROM dbo.Services sv WHERE sv.ServiceId = ts.ServiceId), N''PerPerson'')'
        ELSE N'N''PerPerson''' END;
    -- CAST to nvarchar(max) first: otherwise the text is cut at 4000 characters when it is joined with @unitSql
    DECLARE @sql nvarchar(max) = CAST(N'' AS nvarchar(max)) + N'
SET XACT_ABORT ON;
/* people of each entry, grouped by their employee (the employee themself, or the employee who sponsors them) */
SELECT sp.TrackerCategorySubCategorySpecId AS SpecId,
       COALESCE(m.CustomerContactId, m.SponsorContactId) AS EmployeeContactId,
       COUNT(DISTINCT m.TrackerFamilyMemberId) AS People
INTO #grp
FROM dbo.TrackerCategorySubCategorySpecPersons sp
INNER JOIN dbo.TrackerCategorySubCategorySpecs s ON s.TrackerCategorySubCategorySpecId = sp.TrackerCategorySubCategorySpecId
INNER JOIN dbo.TrackerFamilyMembers m ON m.TrackerId = s.TrackerId AND LOWER(LTRIM(RTRIM(m.Name))) = LOWER(LTRIM(RTRIM(sp.PersonName)))
WHERE COALESCE(m.CustomerContactId, m.SponsorContactId) IS NOT NULL
GROUP BY sp.TrackerCategorySubCategorySpecId, COALESCE(m.CustomerContactId, m.SponsorContactId);

/* entries still on one common list. An entry whose list has the same service more than once was already split per
   employee by the new screen before this script ran (the employee could not be stored): it is NOT copied again -
   it is listed below and has to be fixed by hand (or the tracker reset). */
SELECT DISTINCT g.SpecId INTO #todo FROM #grp g
WHERE EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = g.SpecId)
  AND NOT EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = g.SpecId AND x.EmployeeContactId IS NOT NULL)
  AND NOT EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = g.SpecId
                  GROUP BY LOWER(LTRIM(RTRIM(x.ServiceName))) HAVING COUNT(1) > 1);

SELECT s.TrackerId, s.TrackerCategorySubCategorySpecId AS EntryId, s.CategoryName, s.SubCategoryName,
       (SELECT COUNT(1) FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = s.TrackerCategorySubCategorySpecId) AS ServiceRows,
       N''NOT converted - same service listed more than once'' AS Note
FROM dbo.TrackerCategorySubCategorySpecs s
WHERE s.TrackerCategorySubCategorySpecId IN (SELECT SpecId FROM #grp)
  AND s.TrackerCategorySubCategorySpecId NOT IN (SELECT SpecId FROM #todo)
  AND NOT EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = s.TrackerCategorySubCategorySpecId AND x.EmployeeContactId IS NOT NULL)
  AND EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x WHERE x.TrackerCategorySubCategorySpecId = s.TrackerCategorySubCategorySpecId
              GROUP BY LOWER(LTRIM(RTRIM(x.ServiceName))) HAVING COUNT(1) > 1);

/* people of those entries linked to no employee */
SELECT sp.TrackerCategorySubCategorySpecId AS SpecId, COUNT(1) AS Loose
INTO #loose
FROM dbo.TrackerCategorySubCategorySpecPersons sp
INNER JOIN dbo.TrackerCategorySubCategorySpecs s ON s.TrackerCategorySubCategorySpecId = sp.TrackerCategorySubCategorySpecId
LEFT JOIN dbo.TrackerFamilyMembers m ON m.TrackerId = s.TrackerId AND LOWER(LTRIM(RTRIM(m.Name))) = LOWER(LTRIM(RTRIM(sp.PersonName)))
WHERE sp.TrackerCategorySubCategorySpecId IN (SELECT SpecId FROM #todo)
  AND COALESCE(m.CustomerContactId, m.SponsorContactId) IS NULL
GROUP BY sp.TrackerCategorySubCategorySpecId;

SELECT g.SpecId, g.EmployeeContactId, g.People,
       ROW_NUMBER() OVER (PARTITION BY g.SpecId ORDER BY g.EmployeeContactId) AS Rn
INTO #g FROM #grp g WHERE g.SpecId IN (SELECT SpecId FROM #todo);

SELECT ts.TrackerServiceSpecId, ts.TrackerCategorySubCategorySpecId AS SpecId, ts.[{CNT}] AS OldCount, ' + @unitSql + N' AS Unit
INTO #orig
FROM dbo.TrackerServiceSpecs ts
WHERE ts.TrackerCategorySubCategorySpecId IN (SELECT SpecId FROM #todo) AND ts.EmployeeContactId IS NULL;

BEGIN TRANSACTION;

/* copies for the 2nd, 3rd ... employee of each entry */
INSERT dbo.TrackerServiceSpecs (TrackerCategorySubCategorySpecId, TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName,
    ServiceId, ServiceName, [{CNT}], Amount, VAT, Status, CreatedBy, CreatedDate, EmployeeContactId)
SELECT ts.TrackerCategorySubCategorySpecId, ts.TrackerId, ts.CategoryId, ts.CategoryName, ts.SubCategoryId, ts.SubCategoryName,
    ts.ServiceId, ts.ServiceName, o.OldCount * CASE WHEN o.Unit = N''PerPerson'' THEN g.People ELSE 1 END,
    ts.Amount, ts.VAT, ts.Status, ts.CreatedBy, SYSUTCDATETIME(), g.EmployeeContactId
FROM #orig o
INNER JOIN dbo.TrackerServiceSpecs ts ON ts.TrackerServiceSpecId = o.TrackerServiceSpecId
INNER JOIN #g g ON g.SpecId = o.SpecId AND g.Rn > 1;

/* members linked to no employee keep the old list (each of them is quoted alone) */
INSERT dbo.TrackerServiceSpecs (TrackerCategorySubCategorySpecId, TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName,
    ServiceId, ServiceName, [{CNT}], Amount, VAT, Status, CreatedBy, CreatedDate, EmployeeContactId)
SELECT ts.TrackerCategorySubCategorySpecId, ts.TrackerId, ts.CategoryId, ts.CategoryName, ts.SubCategoryId, ts.SubCategoryName,
    ts.ServiceId, ts.ServiceName, o.OldCount, ts.Amount, ts.VAT, ts.Status, ts.CreatedBy, SYSUTCDATETIME(), NULL
FROM #orig o
INNER JOIN dbo.TrackerServiceSpecs ts ON ts.TrackerServiceSpecId = o.TrackerServiceSpecId
WHERE EXISTS (SELECT 1 FROM #loose l WHERE l.SpecId = o.SpecId AND l.Loose > 0);

/* the original row becomes the first employee''s */
UPDATE ts SET EmployeeContactId = g.EmployeeContactId,
    [{CNT}] = o.OldCount * CASE WHEN o.Unit = N''PerPerson'' THEN g.People ELSE 1 END
FROM dbo.TrackerServiceSpecs ts
INNER JOIN #orig o ON o.TrackerServiceSpecId = ts.TrackerServiceSpecId
INNER JOIN #g g ON g.SpecId = o.SpecId AND g.Rn = 1;

COMMIT TRANSACTION;

DECLARE @entries int = (SELECT COUNT(1) FROM #todo), @rows int = (SELECT COUNT(1) FROM #orig);
DECLARE @msg nvarchar(200) = CONCAT(N''Converted '', @entries, N'' members entr'', CASE WHEN @entries = 1 THEN N''y'' ELSE N''ies'' END, N'' ('', @rows, N'' common service rows) to per-employee lists.'');
RAISERROR(@msg, 10, 1) WITH NOWAIT;';
    SET @sql = REPLACE(@sql, N'{CNT}', @countColumn);
    EXEC sp_executesql @sql;
END
GO
