/*
  018 - Reports and dashboard for renewals, reminders and follow-ups
  ===================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-11-02_renewal_reports_dashboard.sql       run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/018_Renewal_Reports_Dashboard.sql    run automatically on every tenant database (BP_*)
  Needs 017 (dbo.vw_RenewalItems). Nothing existing is changed - two new procedures only:

  * dbo.usp_Reports_Renewals  - five more reports, in the same JSON shape as usp_Reports_GetDetailed
      renewal-status      every renewal item with its renewal status (Due Soon, Expired, Lapsed, Not Renewing, ...)
      renewal-forecast    items due in the next 12 months and the expected revenue at today's prices
      renewal-history     renewals done (Actual Date) by month, per company / employee / family member
      reminder-log        reminders created: sent, failed, pending, skipped - by channel and recipient
      follow-up-activity  follow-ups recorded: calls, WhatsApp, emails (manual / automatic), by employee
    The API sends these keys here and every other report to usp_Reports_GetDetailed.
  * dbo.usp_Dashboard_GetRenewals - extra dashboard widgets (one row of JSON columns, merged by the API into the
      dashboard): renewalKpis, renewalStatus, renewalForecast, renewalsDone, reminderStats, renewalItems.
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.vw_RenewalItems', N'V') IS NULL
BEGIN
    RAISERROR(N'Run script 017 (renewal lifecycle) first - dbo.vw_RenewalItems is missing. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

/* Today's price of one renewal item's services (before VAT): the entry's list for the company, one of each service of
   the employee's own list for a member (or of the entry's list when the employee has none). */
CREATE OR ALTER FUNCTION dbo.fn_RenewalItem_Price (@ItemType NVARCHAR(10), @EntryId INT, @HolderContactId INT)
RETURNS DECIMAL(18,2)
AS
BEGIN
    RETURN ISNULL((
        SELECT SUM(ISNULL(cur.Amount, ts.Amount) * CASE WHEN @ItemType = N'MEMBER' THEN 1 WHEN ts.[Count] > 0 THEN ts.[Count] ELSE 1 END)
        FROM dbo.TrackerServiceSpecs ts
        OUTER APPLY (SELECT TOP (1) sv.Amount FROM dbo.Services sv WHERE sv.ServiceId = ts.ServiceId AND sv.IsActive = 1) cur
        WHERE ts.TrackerCategorySubCategorySpecId = @EntryId AND ISNULL(ts.[Status], N'') <> N'Deleted'
          AND (@ItemType = N'ENTRY'
               OR ts.EmployeeContactId = @HolderContactId
               OR (ts.EmployeeContactId IS NULL AND NOT EXISTS (SELECT 1 FROM dbo.TrackerServiceSpecs x
                       WHERE x.TrackerCategorySubCategorySpecId = @EntryId AND x.EmployeeContactId = @HolderContactId)))), 0);
END;
GO

CREATE OR ALTER FUNCTION dbo.fn_RenewalStatusName (@Code NVARCHAR(30))
RETURNS NVARCHAR(40)
AS
BEGIN
    RETURN CASE @Code WHEN N'ACTIVE' THEN N'Active' WHEN N'DUE_SOON' THEN N'Due Soon' WHEN N'EXPIRED' THEN N'Expired'
                      WHEN N'LAPSED' THEN N'Lapsed' WHEN N'NOT_RENEWING' THEN N'Not Renewing'
                      WHEN N'RENEWAL_IN_PROGRESS' THEN N'Renewal in Progress' WHEN N'RENEWED' THEN N'Renewed'
                      WHEN N'NO_DATE' THEN N'No date' ELSE ISNULL(@Code, N'') END;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Reports_Renewals
    @ReportKey  NVARCHAR(50),
    @CustomerId INT = NULL,
    @EmployeeId INT = NULL,
    @CategoryId INT = NULL,
    @Status     NVARCHAR(50) = NULL,
    @FromDate   DATE = NULL,
    @ToDate     DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Today DATE = CONVERT(DATE, GETDATE());
    DECLARE @k NVARCHAR(50) = LOWER(LTRIM(RTRIM(ISNULL(@ReportKey, N''))));
    SET @Status = NULLIF(LTRIM(RTRIM(ISNULL(@Status, N''))), N'');

    DECLARE @Title NVARCHAR(200) = N'', @Desc NVARCHAR(500) = N'';
    DECLARE @Summary NVARCHAR(MAX) = N'[]', @Chart NVARCHAR(MAX) = NULL, @Insights NVARCHAR(MAX) = N'[]',
            @Cols NVARCHAR(MAX) = N'[]', @Rows NVARCHAR(MAX) = N'[]', @StatusList NVARCHAR(MAX) = N'[]';
    DECLARE @i1 INT, @i2 INT, @i3 INT, @i4 INT, @i5 INT, @i6 INT;
    DECLARE @d1 DECIMAL(18,2), @d2 DECIMAL(18,2);
    DECLARE @t1 NVARCHAR(300);

    /* items of open (not rejected / cancelled) trackers with the tracker's assigned employee */
    IF @k IN (N'renewal-status', N'renewal-forecast', N'renewal-history')
        SELECT v.*, t.AssignedToEmployeeId AS EmployeeId,
               ISNULL(NULLIF(LTRIM(RTRIM(e.FullName)), N''), t.AssignedToEmployeeName) AS EmployeeName,
               dbo.fn_RenewalStatusName(v.RenewalStatus) AS StatusName
        INTO #items
        FROM dbo.vw_RenewalItems v
        INNER JOIN dbo.Trackers t ON t.TrackerId = v.TrackerId
        LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
        WHERE UPPER(LTRIM(RTRIM(v.TrackerStatus))) NOT IN (N'REJECTED', N'NOT_COMPLETED', N'CANCELLED')
          AND (@CustomerId IS NULL OR v.CustomerId = @CustomerId)
          AND (@EmployeeId IS NULL OR t.AssignedToEmployeeId = @EmployeeId)
          AND (@CategoryId IS NULL OR v.CategoryId = @CategoryId);

    /* ===================================================== renewal-status */
    IF @k = N'renewal-status'
    BEGIN
        DELETE FROM #items WHERE DueDate IS NULL
            OR (@Status IS NOT NULL AND StatusName <> @Status)
            OR (@FromDate IS NOT NULL AND DueDate < @FromDate) OR (@ToDate IS NOT NULL AND DueDate > @ToDate);
        SELECT @i1 = COUNT(*),
               @i2 = ISNULL(SUM(CASE WHEN RenewalStatus = N'DUE_SOON' THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN RenewalStatus = N'EXPIRED' THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN RenewalStatus IN (N'RENEWAL_IN_PROGRESS', N'RENEWED') THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN RenewalStatus IN (N'LAPSED', N'NOT_RENEWING') THEN 1 ELSE 0 END), 0),
               @i6 = ISNULL(SUM(CASE WHEN RenewalStatus = N'EXPIRED' AND DaysLeft < -14 THEN 1 ELSE 0 END), 0)
        FROM #items;
        SET @Title = N'Renewal status';
        SET @Desc = N'Every visa, licence and service for the company, its employees and their family, with its renewal status.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Items', CONVERT(NVARCHAR(40), @i1), N'with a due date', N'', 1),
              (N'Due soon', CONVERT(NVARCHAR(40), @i2), N'inside the first reminder step', N'amber', 2),
              (N'Expired', CONVERT(NVARCHAR(40), @i3), N'due date passed, not renewed', N'red', 3),
              (N'Renewing / renewed', CONVERT(NVARCHAR(40), @i4), N'renewal tracker started or done', N'green', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Items by renewal status' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT s.name AS label, COUNT(i.ItemId) AS value
                        FROM (VALUES (N'Active', 1), (N'Due Soon', 2), (N'Expired', 3), (N'Lapsed', 4), (N'Not Renewing', 5), (N'Renewal in Progress', 6), (N'Renewed', 7)) s(name, ord)
                        LEFT JOIN #items i ON i.StatusName = s.name
                        GROUP BY s.name, s.ord ORDER BY s.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i3 > 0 THEN CONCAT(@i3, N' items have expired without a renewal; ', @i6, N' of them more than 14 days ago.') ELSE N'No item is expired without a renewal.' END,
               CASE WHEN @i3 > 0 THEN N'bad' ELSE N'good' END, 1),
              (CASE WHEN @i2 > 0 THEN CONCAT(@i2, N' items are due soon - start their renewal trackers.') ELSE N'' END, N'warn', 2),
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' items are Lapsed or Not Renewing and get no more reminders.') ELSE N'' END, N'info', 3)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"service","label":"Service","type":"text"},{"key":"person","label":"For whom","type":"text"},{"key":"holder","label":"Holder","type":"text"},{"key":"actualDate","label":"Last renewed","type":"date"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"daysLeft","label":"Days left","type":"days"},{"key":"status","label":"Renewal status","type":"status"},{"key":"note","label":"Note","type":"text"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT CustomerName AS customer, CONCAT(N'#', TrackerId) AS tracker, CONCAT(CategoryName, N' / ', SubCategoryName) AS service,
                            ISNULL(PersonName, N'Company') AS person,
                            CASE HolderType WHEN N'COMPANY' THEN N'Company' WHEN N'EMPLOYEE' THEN N'Employee' WHEN N'FAMILY' THEN N'Family member' ELSE N'Member' END AS holder,
                            ActualDate AS actualDate, DueDate AS dueDate, DaysLeft AS daysLeft, StatusName AS status,
                            CASE WHEN RenewalTrackerId IS NOT NULL THEN CONCAT(N'Renewal tracker #', RenewalTrackerId) ELSE RenewalNote END AS note,
                            EmployeeName AS employee
                     FROM #items ORDER BY DueDate, CustomerName, PersonName FOR JSON PATH);
        SET @StatusList = N'["Active","Due Soon","Expired","Lapsed","Not Renewing","Renewal in Progress","Renewed"]';
    END
    /* ===================================================== renewal-forecast */
    ELSE IF @k = N'renewal-forecast'
    BEGIN
        DECLARE @FFrom DATE = ISNULL(@FromDate, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1));
        DECLARE @FTo DATE = ISNULL(@ToDate, DATEADD(DAY, -1, DATEADD(MONTH, 12, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1))));
        DELETE FROM #items WHERE DueDate IS NULL OR DueDate > @FTo
            OR RenewalStatus NOT IN (N'ACTIVE', N'DUE_SOON', N'EXPIRED', N'LAPSED')
            OR (DueDate < @FFrom AND RenewalStatus NOT IN (N'EXPIRED', N'LAPSED'));
        SELECT i.*, dbo.fn_RenewalItem_Price(i.ItemType, i.EntryId, i.HolderContactId) AS Price,
               CASE WHEN i.DueDate < @FFrom THEN NULL ELSE DATEFROMPARTS(YEAR(i.DueDate), MONTH(i.DueDate), 1) END AS M
        INTO #fc FROM #items i;
        SELECT @i1 = COUNT(*), @i2 = COUNT(DISTINCT CustomerId), @i3 = ISNULL(SUM(CASE WHEN M IS NULL THEN 1 ELSE 0 END), 0),
               @d1 = ISNULL(SUM(Price), 0), @d2 = ISNULL(SUM(CASE WHEN M IS NULL THEN Price ELSE 0 END), 0)
        FROM #fc;
        SELECT TOP (1) @t1 = CONCAT(FORMAT(M, N'MMMM yyyy', N'en-US'), N' (', COUNT(*), N' items)') FROM #fc WHERE M IS NOT NULL GROUP BY M ORDER BY COUNT(*) DESC, M;
        SET @Title = N'Renewals forecast';
        SET @Desc = CONCAT(N'Items due from ', FORMAT(@FFrom, N'dd/MM/yyyy'), N' to ', FORMAT(@FTo, N'dd/MM/yyyy'),
                           N' (plus overdue ones still to renew) and the expected revenue at today''s Services prices, before VAT.');
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Items to renew', CONVERT(NVARCHAR(40), @i1), N'in the period', N'', 1),
              (N'Customers', CONVERT(NVARCHAR(40), @i2), N'with renewals due', N'', 2),
              (N'Expected revenue', dbo.fn_BP_Money(@d1), N'before VAT, today''s prices', N'green', 3),
              (N'Overdue', CONVERT(NVARCHAR(40), @i3), CONCAT(dbo.fn_BP_Money(@d2), N' not renewed yet'), N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Expected revenue by month' AS title, N'cols' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT CASE WHEN M IS NULL THEN N'Overdue' ELSE FORMAT(M, N'MMM yy', N'en-US') END AS label, SUM(Price) AS value
                        FROM #fc GROUP BY M ORDER BY CASE WHEN M IS NULL THEN 0 ELSE 1 END, M FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(N'Busiest month: ', @t1, N'.') END, N'info', 1),
              (CASE WHEN @i3 > 0 THEN CONCAT(@i3, N' items are already overdue (', dbo.fn_BP_Money(@d2), N') - renew them first.') ELSE N'' END, N'warn', 2),
              (N'Prices come from the Services list today; the quotation may differ (count of people, discounts).', N'info', 3)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"month","label":"Month","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"service","label":"Service","type":"text"},{"key":"person","label":"For whom","type":"text"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"status","label":"Renewal status","type":"status"},{"key":"employee","label":"Employee","type":"text"},{"key":"value","label":"Expected (AED)","type":"money","total":true}]';
        SET @Rows = (SELECT CASE WHEN M IS NULL THEN N'Overdue' ELSE FORMAT(M, N'MMM yyyy', N'en-US') END AS [month], CustomerName AS customer,
                            CONCAT(CategoryName, N' / ', SubCategoryName) AS service, ISNULL(PersonName, N'Company') AS person,
                            DueDate AS dueDate, StatusName AS status, EmployeeName AS employee, Price AS value
                     FROM #fc ORDER BY DueDate, CustomerName FOR JSON PATH);
    END
    /* ===================================================== renewal-history */
    ELSE IF @k = N'renewal-history'
    BEGIN
        DECLARE @HFrom DATE = ISNULL(@FromDate, DATEADD(MONTH, -12, @Today)), @HTo DATE = ISNULL(@ToDate, @Today);
        DELETE FROM #items WHERE ActualDate IS NULL OR ActualDate < @HFrom OR ActualDate > @HTo;
        SELECT i.*, (SELECT COUNT(1) FROM dbo.vw_RenewalItems h WHERE h.HolderKey = i.HolderKey AND h.SubCategoryId = i.SubCategoryId AND h.ActualDate IS NOT NULL) AS TimesRenewed
        INTO #hi FROM #items i;
        SELECT @i1 = COUNT(*), @i2 = COUNT(DISTINCT HolderKey), @i3 = COUNT(DISTINCT CustomerId),
               @i4 = ISNULL(SUM(CASE WHEN TimesRenewed > 1 THEN 1 ELSE 0 END), 0) FROM #hi;
        SET @Title = N'Renewal history';
        SET @Desc = CONCAT(N'Renewals done (Actual Date) from ', FORMAT(@HFrom, N'dd/MM/yyyy'), N' to ', FORMAT(@HTo, N'dd/MM/yyyy'),
                           N', and how many times each person / company has renewed that service.');
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Renewals done', CONVERT(NVARCHAR(40), @i1), N'in the period', N'green', 1),
              (N'People / companies', CONVERT(NVARCHAR(40), @i2), N'renewed', N'', 2),
              (N'Customers', CONVERT(NVARCHAR(40), @i3), N'served', N'', 3),
              (N'Repeat renewals', CONVERT(NVARCHAR(40), @i4), N'renewed before too', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Renewals done by month' AS title, N'cols' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT FORMAT(DATEFROMPARTS(YEAR(ActualDate), MONTH(ActualDate), 1), N'MMM yy', N'en-US') AS label, COUNT(*) AS value
                        FROM #hi GROUP BY DATEFROMPARTS(YEAR(ActualDate), MONTH(ActualDate), 1)
                        ORDER BY DATEFROMPARTS(YEAR(ActualDate), MONTH(ActualDate), 1) FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i1 = 0 THEN N'No renewal was completed in this period.' ELSE CONCAT(@i1, N' renewals were completed for ', @i3, N' customers.') END, N'info', 1),
              (CASE WHEN @i4 > 0 THEN CONCAT(@i4, N' of them were repeat renewals of the same person and service.') ELSE N'' END, N'good', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"actualDate","label":"Renewed on","type":"date"},{"key":"customer","label":"Customer","type":"text"},{"key":"person","label":"For whom","type":"text"},{"key":"holder","label":"Holder","type":"text"},{"key":"service","label":"Service","type":"text"},{"key":"dueDate","label":"Valid until","type":"date"},{"key":"times","label":"Times renewed","type":"num"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT ActualDate AS actualDate, CustomerName AS customer, ISNULL(PersonName, N'Company') AS person,
                            CASE HolderType WHEN N'COMPANY' THEN N'Company' WHEN N'EMPLOYEE' THEN N'Employee' WHEN N'FAMILY' THEN N'Family member' ELSE N'Member' END AS holder,
                            CONCAT(CategoryName, N' / ', SubCategoryName) AS service, DueDate AS dueDate, TimesRenewed AS times,
                            CONCAT(N'#', TrackerId) AS tracker, EmployeeName AS employee
                     FROM #hi ORDER BY ActualDate DESC, CustomerName FOR JSON PATH);
    END
    /* ===================================================== reminder-log */
    ELSE IF @k = N'reminder-log'
    BEGIN
        DECLARE @RFrom DATE = ISNULL(@FromDate, DATEADD(DAY, -30, @Today)), @RTo DATE = ISNULL(@ToDate, @Today);
        SELECT n.NotificationId, n.CreatedDate, n.CustomerName, n.CategoryName, n.SubCategoryName, n.PersonName, n.DueDate, n.DaysBefore,
               n.Channel, n.RecipientType, n.RecipientName, n.RecipientAddress, n.[Status], n.StatusMessage, n.SentDate, t.AssignedToEmployeeName AS EmployeeName,
               CASE n.[Status] WHEN N'SENT' THEN N'Sent' WHEN N'FAILED' THEN N'Failed' WHEN N'PENDING' THEN N'Pending' WHEN N'SKIPPED' THEN N'Skipped' WHEN N'DISMISSED' THEN N'Dismissed' ELSE n.[Status] END AS StatusName
        INTO #nl
        FROM dbo.Notifications n
        LEFT JOIN dbo.Trackers t ON t.TrackerId = n.TrackerId
        WHERE CONVERT(DATE, n.CreatedDate) BETWEEN @RFrom AND @RTo
          AND (@CustomerId IS NULL OR n.CustomerId = @CustomerId)
          AND (@EmployeeId IS NULL OR t.AssignedToEmployeeId = @EmployeeId)
          AND (@CategoryId IS NULL OR n.CategoryId = @CategoryId);
        IF @Status IS NOT NULL DELETE FROM #nl WHERE StatusName <> @Status;
        SELECT @i1 = COUNT(*),
               @i2 = ISNULL(SUM(CASE WHEN [Status] = N'SENT' THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN [Status] = N'FAILED' THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN [Status] = N'PENDING' THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN [Status] = N'SKIPPED' THEN 1 ELSE 0 END), 0)
        FROM #nl;
        SELECT TOP (1) @t1 = StatusMessage FROM #nl WHERE [Status] IN (N'FAILED', N'SKIPPED') AND StatusMessage IS NOT NULL
        GROUP BY StatusMessage ORDER BY COUNT(*) DESC;
        SET @Title = N'Reminder log';
        SET @Desc = CONCAT(N'Reminders created from ', FORMAT(@RFrom, N'dd/MM/yyyy'), N' to ', FORMAT(@RTo, N'dd/MM/yyyy'), N': who was reminded, how, and whether it went out.');
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Reminders', CONVERT(NVARCHAR(40), @i1), N'created', N'', 1),
              (N'Sent', CONVERT(NVARCHAR(40), @i2), N'email / WhatsApp / in app', N'green', 2),
              (N'Failed', CONVERT(NVARCHAR(40), @i3), N'could not be sent', N'red', 3),
              (N'Pending / skipped', CONVERT(NVARCHAR(40), @i4 + @i5), CONCAT(@i5, N' skipped - no email or mobile'), N'amber', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Reminders by channel' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT CASE c.ch WHEN N'EMAIL' THEN N'Email' WHEN N'WHATSAPP' THEN N'WhatsApp' ELSE N'In app' END AS label, COUNT(n.NotificationId) AS value
                        FROM (VALUES (N'EMAIL', 1), (N'WHATSAPP', 2), (N'INAPP', 3)) c(ch, ord) LEFT JOIN #nl n ON n.Channel = c.ch
                        GROUP BY c.ch, c.ord ORDER BY c.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i1 = 0 THEN N'No reminder was created in this period.' ELSE CONCAT(@i2, N' of ', @i1, N' reminders were sent (', CONVERT(INT, ROUND(@i2 * 100.0 / NULLIF(@i1, 0), 0)), N'%).') END, CASE WHEN @i3 > 0 THEN N'warn' ELSE N'good' END, 1),
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(N'Most common problem: ', @t1) END, N'bad', 2),
              (CASE WHEN @i4 > 0 THEN CONCAT(@i4, N' reminders are still waiting to be sent - Reminders > Send due reminders.') ELSE N'' END, N'warn', 3)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"created","label":"Created","type":"date"},{"key":"customer","label":"Customer","type":"text"},{"key":"service","label":"Service","type":"text"},{"key":"person","label":"For whom","type":"text"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"step","label":"Step","type":"text"},{"key":"channel","label":"Channel","type":"text"},{"key":"recipient","label":"Sent to","type":"text"},{"key":"status","label":"Status","type":"status"},{"key":"message","label":"Message","type":"text"}]';
        SET @Rows = (SELECT CONVERT(DATE, CreatedDate) AS created, CustomerName AS customer, CONCAT(CategoryName, N' / ', SubCategoryName) AS service,
                            PersonName AS person, DueDate AS dueDate,
                            CASE WHEN DaysBefore > 0 THEN CONCAT(DaysBefore, N' days before') WHEN DaysBefore = 0 THEN N'On the due date' ELSE CONCAT(-DaysBefore, N' days after') END AS step,
                            CASE Channel WHEN N'EMAIL' THEN N'Email' WHEN N'WHATSAPP' THEN N'WhatsApp' ELSE N'In app' END AS channel,
                            CONCAT(CASE RecipientType WHEN N'CUSTOMER' THEN N'Company' WHEN N'MEMBER' THEN N'Employee' WHEN N'EMPLOYEE' THEN N'Staff' ELSE N'Fixed' END, N': ', ISNULL(RecipientName, RecipientAddress)) AS recipient,
                            StatusName AS status, StatusMessage AS message
                     FROM #nl ORDER BY CreatedDate DESC, NotificationId DESC FOR JSON PATH);
        SET @StatusList = N'["Sent","Failed","Pending","Skipped","Dismissed"]';
    END
    /* ===================================================== follow-up-activity */
    ELSE IF @k = N'follow-up-activity'
    BEGIN
        DECLARE @AFrom DATE = ISNULL(@FromDate, DATEADD(DAY, -30, @Today)), @ATo DATE = ISNULL(@ToDate, @Today);
        SELECT f.FollowUpId, COALESCE(f.FollowUpDate, CONVERT(DATE, f.CreatedDate)) AS ActDate, t.CustomerName, f.TrackerId,
               f.CommunicationType, f.AutoGenerated, f.SentStatus, f.Notes, f.EmailSubject, f.SenderInfo, f.NextFollowUpDate,
               t.AssignedToEmployeeId, ISNULL(NULLIF(LTRIM(RTRIM(e.FullName)), N''), t.AssignedToEmployeeName) AS EmployeeName
        INTO #fa
        FROM dbo.FollowUps f
        INNER JOIN dbo.Trackers t ON t.TrackerId = f.TrackerId
        LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
        WHERE COALESCE(f.FollowUpDate, CONVERT(DATE, f.CreatedDate)) BETWEEN @AFrom AND @ATo
          AND (@CustomerId IS NULL OR t.CustomerId = @CustomerId)
          AND (@EmployeeId IS NULL OR t.AssignedToEmployeeId = @EmployeeId)
          AND (@Status IS NULL OR f.CommunicationType = @Status);
        SELECT @i1 = COUNT(*),
               @i2 = ISNULL(SUM(CASE WHEN CommunicationType = N'Call' THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN CommunicationType = N'Email' THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN AutoGenerated = 1 THEN 1 ELSE 0 END), 0),
               @i5 = COUNT(DISTINCT TrackerId)
        FROM #fa;
        SELECT TOP (1) @t1 = CONCAT(EmployeeName, N' (', COUNT(*), N')') FROM #fa WHERE EmployeeName IS NOT NULL GROUP BY EmployeeName ORDER BY COUNT(*) DESC, EmployeeName;
        SET @i6 = (SELECT COUNT(*) FROM dbo.TrackerQuotationDocuments d INNER JOIN dbo.Trackers t ON t.TrackerId = d.TrackerId
                   WHERE UPPER(LTRIM(RTRIM(d.[Status]))) = N'PENDING' AND (@CustomerId IS NULL OR t.CustomerId = @CustomerId)
                     AND (@EmployeeId IS NULL OR t.AssignedToEmployeeId = @EmployeeId));
        SET @Title = N'Follow-up activity';
        SET @Desc = CONCAT(N'Calls, WhatsApp messages, emails and meetings recorded from ', FORMAT(@AFrom, N'dd/MM/yyyy'), N' to ', FORMAT(@ATo, N'dd/MM/yyyy'), N'.');
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Follow-ups', CONVERT(NVARCHAR(40), @i1), CONCAT(N'on ', @i5, N' trackers'), N'', 1),
              (N'Calls', CONVERT(NVARCHAR(40), @i2), N'phone calls', N'', 2),
              (N'Emails', CONVERT(NVARCHAR(40), @i3), CONCAT(@i4, N' sent automatically'), N'green', 3),
              (N'Documents pending', CONVERT(NVARCHAR(40), @i6), N'still waiting from customers', N'amber', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Follow-ups by channel' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT c.ch AS label, COUNT(f.FollowUpId) AS value
                        FROM (VALUES (N'Call', 1), (N'WhatsApp', 2), (N'Email', 3), (N'Meeting', 4)) c(ch, ord) LEFT JOIN #fa f ON f.CommunicationType = c.ch
                        GROUP BY c.ch, c.ord ORDER BY c.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'No follow-up was recorded in this period.' ELSE CONCAT(N'Most follow-ups: ', @t1, N'.') END, N'info', 1),
              (CASE WHEN @i6 > 0 THEN CONCAT(@i6, N' documents are still Pending - keep following up.') ELSE N'No document is waiting from a customer.' END, CASE WHEN @i6 > 0 THEN N'warn' ELSE N'good' END, 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"date","label":"Date","type":"date"},{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"channel","label":"How","type":"text"},{"key":"mode","label":"By","type":"text"},{"key":"notes","label":"What was discussed / sent","type":"text"},{"key":"next","label":"Next follow-up","type":"date"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT ActDate AS [date], CustomerName AS customer, CONCAT(N'#', TrackerId) AS tracker, CommunicationType AS channel,
                            CASE WHEN AutoGenerated = 1 THEN N'Automatic' ELSE ISNULL(NULLIF(SenderInfo, N''), N'Staff') END AS mode,
                            LEFT(COALESCE(NULLIF(Notes, N''), EmailSubject, N''), 160) AS notes, NextFollowUpDate AS [next], EmployeeName AS employee
                     FROM #fa ORDER BY ActDate DESC, FollowUpId DESC FOR JSON PATH);
        SET @StatusList = N'["Call","WhatsApp","Email","Meeting"]';
    END
    ELSE
    BEGIN
        SET @Title = N'Report';
        SET @Desc = N'No report definition was found.';
    END;

    SELECT (SELECT @k AS [key], @Title AS title, @Desc AS description,
                   JSON_QUERY(ISNULL(@Summary, N'[]')) AS summary,
                   JSON_QUERY(@Chart) AS chart,
                   JSON_QUERY(ISNULL(@Insights, N'[]')) AS insights,
                   JSON_QUERY(ISNULL(@Cols, N'[]')) AS columns,
                   JSON_QUERY(ISNULL(@Rows, N'[]')) AS rows,
                   JSON_QUERY(ISNULL(@StatusList, N'[]')) AS statuses
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS json;
END;
GO

/* ---------- dashboard: renewals, reminders, follow-ups ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_Dashboard_GetRenewals
    @Months INT = 6
AS
BEGIN
    SET NOCOUNT ON;
    IF @Months NOT IN (3, 6, 12) SET @Months = 6;
    DECLARE @Today DATE = CONVERT(DATE, GETDATE());
    DECLARE @MonthStart DATE = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1);
    DECLARE @From DATE = DATEADD(MONTH, 1 - @Months, @MonthStart);

    SELECT v.*, t.AssignedToEmployeeName AS EmployeeName
    INTO #it
    FROM dbo.vw_RenewalItems v
    INNER JOIN dbo.Trackers t ON t.TrackerId = v.TrackerId
    WHERE UPPER(LTRIM(RTRIM(v.TrackerStatus))) NOT IN (N'REJECTED', N'NOT_COMPLETED', N'CANCELLED') AND v.DueDate IS NOT NULL;

    DECLARE @Kpis NVARCHAR(MAX), @Status NVARCHAR(MAX), @Forecast NVARCHAR(MAX), @Done NVARCHAR(MAX), @Rem NVARCHAR(MAX), @Items NVARCHAR(MAX);

    SET @Kpis = (SELECT
        (SELECT COUNT(*) FROM #it WHERE RenewalStatus = N'DUE_SOON') AS dueSoon,
        (SELECT COUNT(*) FROM #it WHERE RenewalStatus = N'EXPIRED') AS expired,
        (SELECT COUNT(*) FROM #it WHERE RenewalStatus = N'LAPSED') AS lapsed,
        (SELECT COUNT(*) FROM #it WHERE RenewalStatus = N'NOT_RENEWING') AS notRenewing,
        (SELECT COUNT(*) FROM #it WHERE RenewalStatus = N'RENEWAL_IN_PROGRESS') AS inProgress,
        (SELECT COUNT(*) FROM #it WHERE ActualDate >= @From AND ActualDate <= @Today) AS renewedInPeriod,
        (SELECT COUNT(*) FROM dbo.Trackers WHERE UPPER(LTRIM(RTRIM([Status]))) = N'DATES_PENDING') AS datesPending,
        (SELECT COUNT(*) FROM dbo.Notifications WHERE [Status] = N'PENDING' AND Channel <> N'INAPP') AS remindersToSend,
        (SELECT COUNT(*) FROM dbo.Notifications WHERE [Status] = N'FAILED') AS remindersFailed,
        (SELECT COUNT(*) FROM dbo.Notifications WHERE [Status] = N'SENT' AND SentDate >= DATEADD(DAY, -30, @Today)) AS remindersSent30,
        (SELECT COUNT(*) FROM dbo.FollowUps WHERE COALESCE(FollowUpDate, CONVERT(DATE, CreatedDate)) >= DATEADD(DAY, -30, @Today)) AS followUps30,
        (SELECT COUNT(*) FROM dbo.TrackerQuotationDocuments WHERE UPPER(LTRIM(RTRIM([Status]))) = N'PENDING') AS docsPending
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    SET @Status = (SELECT s.code, s.name AS label, COUNT(i.ItemId) AS [count]
                   FROM (VALUES (N'ACTIVE', N'Active', 1), (N'DUE_SOON', N'Due Soon', 2), (N'EXPIRED', N'Expired', 3), (N'LAPSED', N'Lapsed', 4),
                                (N'NOT_RENEWING', N'Not Renewing', 5), (N'RENEWAL_IN_PROGRESS', N'Renewal in Progress', 6), (N'RENEWED', N'Renewed', 7)) s(code, name, ord)
                   LEFT JOIN #it i ON i.RenewalStatus = s.code
                   GROUP BY s.code, s.name, s.ord ORDER BY s.ord FOR JSON PATH);

    /* next 6 months, overdue first */
    SET @Forecast = (SELECT x.label, x.[month], COUNT(*) AS [count], SUM(x.Price) AS value
                     FROM (SELECT CASE WHEN i.DueDate < @MonthStart THEN N'Overdue' ELSE FORMAT(i.DueDate, N'MMM', N'en-US') END AS label,
                                  CASE WHEN i.DueDate < @MonthStart THEN N'0000-00' ELSE CONVERT(VARCHAR(7), i.DueDate, 126) END AS [month],
                                  dbo.fn_RenewalItem_Price(i.ItemType, i.EntryId, i.HolderContactId) AS Price
                           FROM #it i
                           WHERE i.RenewalStatus IN (N'ACTIVE', N'DUE_SOON', N'EXPIRED')
                             AND i.DueDate < DATEADD(MONTH, 6, @MonthStart)) x
                     GROUP BY x.label, x.[month] ORDER BY x.[month] FOR JSON PATH);

    SET @Done = (SELECT FORMAT(m.d, N'MMM', N'en-US') AS label, CONVERT(VARCHAR(7), m.d, 126) AS [month],
                        (SELECT COUNT(*) FROM #it i WHERE i.ActualDate >= m.d AND i.ActualDate < DATEADD(MONTH, 1, m.d)) AS renewed,
                        (SELECT COUNT(*) FROM #it i WHERE i.DueDate >= m.d AND i.DueDate < DATEADD(MONTH, 1, m.d)
                                                       AND i.RenewalStatus IN (N'EXPIRED', N'LAPSED', N'NOT_RENEWING')) AS lost
                 FROM (SELECT DATEADD(MONTH, v.n, @From) AS d FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11)) v(n) WHERE v.n < @Months) m
                 ORDER BY m.d FOR JSON PATH);

    SET @Rem = (SELECT c.label AS channel,
                       SUM(CASE WHEN n.[Status] = N'SENT' THEN 1 ELSE 0 END) AS sent,
                       SUM(CASE WHEN n.[Status] = N'FAILED' THEN 1 ELSE 0 END) AS failed,
                       SUM(CASE WHEN n.[Status] IN (N'PENDING', N'SKIPPED') THEN 1 ELSE 0 END) AS pending
                FROM (VALUES (N'EMAIL', N'Email', 1), (N'WHATSAPP', N'WhatsApp', 2), (N'INAPP', N'In app', 3)) c(code, label, ord)
                LEFT JOIN dbo.Notifications n ON n.Channel = c.code AND n.CreatedDate >= DATEADD(DAY, -30, @Today)
                GROUP BY c.label, c.ord ORDER BY c.ord FOR JSON PATH);

    SET @Items = (SELECT TOP (10) i.CustomerName AS customer, i.TrackerId AS trackerId, CONCAT(i.CategoryName, N' / ', i.SubCategoryName) AS service,
                         ISNULL(i.PersonName, N'Company') AS person, i.DueDate AS dueDate, i.DaysLeft AS days, i.RenewalStatus AS status,
                         dbo.fn_RenewalStatusName(i.RenewalStatus) AS statusName, i.EmployeeName AS employee
                  FROM #it i WHERE i.RenewalStatus IN (N'DUE_SOON', N'EXPIRED')
                  ORDER BY i.DueDate, i.CustomerName FOR JSON PATH);

    SELECT ISNULL(@Kpis, N'{}')     AS renewalKpis,
           ISNULL(@Status, N'[]')   AS renewalStatus,
           ISNULL(@Forecast, N'[]') AS renewalForecast,
           ISNULL(@Done, N'[]')     AS renewalsDone,
           ISNULL(@Rem, N'[]')      AS reminderStats,
           ISNULL(@Items, N'[]')    AS renewalItems;
END;
GO

SET NOEXEC OFF;
GO
