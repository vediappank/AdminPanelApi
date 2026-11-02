/*
  016 - Reminders per member
  ==========================
  Same script in two places:
    BusinessPanelApi/database/2026-11-01_reminders_per_member.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/016_Reminders_Per_Member.sql run automatically on every tenant database (BP_*)

  Tracker entries now take their dates per member (Members tab: Actual Date / Due Date, script 010). The reminder run
  (usp_Notifications_Run) only read the entry's own Due Date, so those members never got a reminder.
  Now:
    * an entry whose members have a Due Date  -> one reminder line per member, with the member's name ({{Person}}) and date;
    * an entry without member dates           -> the entry's Due Date, exactly as before;
    * "already sent" is checked per member too, so each member gets each step (60 / 30 / 10 days ...) once;
    * Completed trackers get reminders too - after a renewal the Due Date is the NEXT renewal date
      (only Rejected / Not Completed trackers are skipped);
    * reminder steps "On the due date" (0) and "7 days after" (-7) are added to the list a rule can pick from,
      so a rule can also remind when the date expires.
  Everything else in the procedure (rules, steps, channels, recipients, catch-up days, preview / generate) is unchanged.
  Needs script 010 (TrackerCategorySubCategorySpecPersons.DueDate). Safe to run more than once.
*/
SET NOCOUNT ON;
GO
IF COL_LENGTH(N'dbo.TrackerCategorySubCategorySpecPersons', N'DueDate') IS NULL
BEGIN
    RAISERROR(N'Run script 010 (2026-10-23_tracker_renewal_dates_and_service_status.sql) first - TrackerCategorySubCategorySpecPersons.DueDate is missing. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO
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

    /* open tracker lines that have a due date:
       - an entry whose members have their own Due Date (Members tab) -> one line per member, with the member's name and date;
       - otherwise the entry's own Due Date, as before. */
    ;WITH lines AS (
        SELECT sp.TrackerCategorySubCategorySpecId AS SpecId, sp.PersonName, sp.DueDate
        FROM dbo.TrackerCategorySubCategorySpecs sp
        WHERE sp.DueDate IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecPersons mp
                          WHERE mp.TrackerCategorySubCategorySpecId = sp.TrackerCategorySubCategorySpecId AND mp.DueDate IS NOT NULL)
        UNION ALL
        SELECT mp.TrackerCategorySubCategorySpecId, mp.PersonName, mp.DueDate
        FROM dbo.TrackerCategorySubCategorySpecPersons mp
        WHERE mp.DueDate IS NOT NULL
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
           NULLIF(LTRIM(RTRIM(e.Mobile)), N'') AS EmpMobile
    INTO #np_spec
    FROM lines l
    INNER JOIN dbo.TrackerCategorySubCategorySpecs sp ON sp.TrackerCategorySubCategorySpecId = l.SpecId
    INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
    LEFT JOIN dbo.Customers c ON c.CustomerId = t.CustomerId
    LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
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
PRINT N'OK - usp_Notifications_Run now creates reminders per member, including Completed trackers.';
GO
/* reminder steps on / after the due date (rules pick them on the Reminder rules screen) */
IF OBJECT_ID(N'dbo.ReminderFrequencies', N'U') IS NOT NULL
BEGIN
    DECLARE @Order INT = ISNULL((SELECT MAX(DisplayOrder) FROM dbo.ReminderFrequencies), 0);
    IF NOT EXISTS (SELECT 1 FROM dbo.ReminderFrequencies WHERE DaysBefore = 0)
        INSERT dbo.ReminderFrequencies (FrequencyName, DaysBefore, DisplayOrder) VALUES (N'On the due date', 0, @Order + 1);
    IF NOT EXISTS (SELECT 1 FROM dbo.ReminderFrequencies WHERE DaysBefore = -7)
        INSERT dbo.ReminderFrequencies (FrequencyName, DaysBefore, DisplayOrder) VALUES (N'7 days after (expired)', -7, @Order + 2);
    PRINT N'OK - reminder steps "On the due date" and "7 days after" are available.';
END
GO
SET NOEXEC OFF;
GO
