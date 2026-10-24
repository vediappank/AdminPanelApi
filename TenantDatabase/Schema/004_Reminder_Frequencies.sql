/*
  004 - Reminder frequencies (Notification tracker)
  ==================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-20_reminder_frequencies.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/004_Reminder_Frequencies.sql run automatically on every tenant database (BP_*)

  * dbo.ReminderFrequencies     the list of reminder steps a company can pick from ("60 days", "30 days", ... - add your own)
  * NotificationRules.ReminderDays  one rule can now remind at several steps, e.g. "60,30,20,10" days before the due date
  * usp_Notifications_Run       creates a reminder at each step (once per step); old rules (one DaysBefore) work as before
  * usp_ReminderFrequencies_*   list / save / delete for the frequency setup screen
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.ReminderFrequencies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReminderFrequencies
    (
        ReminderFrequencyId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReminderFrequencies PRIMARY KEY,
        FrequencyName       NVARCHAR(100) NOT NULL,
        DaysBefore          INT           NOT NULL,
        DisplayOrder        INT           NOT NULL CONSTRAINT DF_ReminderFrequencies_Order DEFAULT (0),
        IsActive            BIT           NOT NULL CONSTRAINT DF_ReminderFrequencies_Active DEFAULT (1),
        CreatedDate         DATETIME2(0)  NOT NULL CONSTRAINT DF_ReminderFrequencies_Created DEFAULT (SYSUTCDATETIME()),
        ModifiedDate        DATETIME2(0)  NULL,
        CONSTRAINT UX_ReminderFrequencies_Days UNIQUE (DaysBefore),
        CONSTRAINT CK_ReminderFrequencies_Days CHECK (DaysBefore BETWEEN -365 AND 365)
    );
    INSERT dbo.ReminderFrequencies (FrequencyName, DaysBefore, DisplayOrder)
    VALUES (N'60 days before', 60, 1), (N'30 days before', 30, 2), (N'20 days before', 20, 3), (N'10 days before', 10, 4);
END;
GO

IF COL_LENGTH(N'dbo.NotificationRules', N'ReminderDays') IS NULL
    ALTER TABLE dbo.NotificationRules ADD ReminderDays NVARCHAR(200) NULL;
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

    /* open tracker lines that have a due date */
    SELECT sp.TrackerCategorySubCategorySpecId AS SpecId, sp.TrackerId, sp.CategoryId, sp.CategoryName, sp.SubCategoryId, sp.SubCategoryName,
           sp.PersonName, sp.DueDate, DATEDIFF(DAY, @Today, sp.DueDate) AS DaysLeft,
           t.CustomerId, t.CustomerName,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Email)), N''), NULLIF(LTRIM(RTRIM(c.CustomerEmail)), N'')) AS CustEmail,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Mobile)), N''), NULLIF(LTRIM(RTRIM(c.CustomerPhone)), N'')) AS CustMobile,
           t.AssignedToEmployeeId AS EmployeeId,
           ISNULL(NULLIF(LTRIM(RTRIM(e.FullName)), N''), NULLIF(LTRIM(RTRIM(t.AssignedToEmployeeName)), N'')) AS EmployeeName,
           NULLIF(LTRIM(RTRIM(e.Email)), N'') AS EmpEmail,
           NULLIF(LTRIM(RTRIM(e.Mobile)), N'') AS EmpMobile
    INTO #np_spec
    FROM dbo.TrackerCategorySubCategorySpecs sp
    INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
    LEFT JOIN dbo.Customers c ON c.CustomerId = t.CustomerId
    LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
    WHERE sp.DueDate IS NOT NULL
      AND UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'COMPLETED', N'REJECTED')
      AND sp.DueDate <= DATEADD(DAY, 366, @Today);

    /* the current step of each line: smallest "days before" that is still >= days left, and not too late */
    ;WITH r AS (
        SELECT s.SpecId, ru.RuleId, st.D AS DaysBefore, ru.NotifyEmail, ru.NotifyWhatsApp, ru.NotifyInApp, ru.ToCustomer, ru.ToEmployee,
               ru.[Subject] AS TSubject, ru.Body AS TBody, s.DaysLeft,
               ROW_NUMBER() OVER (PARTITION BY s.SpecId, st.D
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
        SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.SpecId ORDER BY r.DaysBefore) AS rn2
        FROM r
        WHERE r.rn1 = 1
    )
    SELECT r2.SpecId, r2.RuleId, r2.DaysBefore, r2.NotifyEmail, r2.NotifyWhatsApp, r2.NotifyInApp, r2.ToCustomer, r2.ToEmployee,
           ISNULL(r2.TSubject, N'{{Category}} - {{SubCategory}} {{DaysLeft}}') AS TSubject,
           ISNULL(r2.TBody, N'Dear {{Recipient}}, this is a reminder that the {{SubCategory}} ({{Category}}) for {{Person}} is due on {{DueDate}} ({{DaysLeft}}). Please contact {{Employee}} to arrange the renewal.') AS TBody
    INTO #np_step
    FROM r2
    WHERE r2.rn2 = 1 AND r2.DaysBefore - r2.DaysLeft <= @CatchUpDays;

    /* one row per channel x recipient */
    CREATE TABLE #np_cand
    (
        RuleId INT NOT NULL, SpecId INT NOT NULL, TrackerId INT NOT NULL, CustomerId INT NULL, CustomerName NVARCHAR(150) NULL,
        CategoryId INT NULL, CategoryName NVARCHAR(100) NULL, SubCategoryName NVARCHAR(100) NULL, PersonName NVARCHAR(200) NULL,
        DueDate DATE NOT NULL, DaysBefore INT NOT NULL, DaysLeft INT NOT NULL,
        Channel NVARCHAR(10) NOT NULL, RecipientType NVARCHAR(10) NOT NULL, RecipientName NVARCHAR(150) NULL,
        RecipientAddress NVARCHAR(150) NOT NULL, RecipientEmployeeId INT NULL,
        TSubject NVARCHAR(200) NULL, TBody NVARCHAR(2000) NULL,
        [Subject] NVARCHAR(200) NULL, Body NVARCHAR(2000) NULL,
        [Status] NVARCHAR(12) NOT NULL, StatusMessage NVARCHAR(400) NULL
    );

    /* customer: email + whatsapp */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'CUSTOMER', sp.CustomerName, ISNULL(ch.Addr, N''), NULL, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN CASE ch.Channel WHEN N'EMAIL' THEN N'The customer has no email address.' ELSE N'The customer has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    CROSS APPLY (VALUES (N'EMAIL', sp.CustEmail, st.NotifyEmail), (N'WHATSAPP', sp.CustMobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToCustomer = 1 AND ch.Enabled = 1;

    /* assigned employee: email + whatsapp + in-app */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'EMPLOYEE', sp.EmployeeName,
           ISNULL(ch.Addr, N''), sp.EmployeeId, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' WHEN ch.Channel = N'INAPP' THEN N'SENT' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN
                CASE WHEN sp.EmployeeId IS NULL THEN N'No employee is assigned to this tracker.'
                     WHEN ch.Channel = N'EMAIL' THEN N'The employee has no email address.'
                     ELSE N'The employee has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    CROSS APPLY (VALUES (N'EMAIL',    sp.EmpEmail, st.NotifyEmail),
                        (N'WHATSAPP', sp.EmpMobile, st.NotifyWhatsApp),
                        (N'INAPP',    CASE WHEN sp.EmployeeId IS NULL THEN NULL ELSE CONVERT(NVARCHAR(150), sp.EmployeeId) END, st.NotifyInApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToEmployee = 1 AND ch.Enabled = 1;

    /* the fixed list of the rule: email + whatsapp (only the details that were entered) */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'FIXED', fr.RecipientName, ch.Addr, NULL, st.TSubject, st.TBody, N'PENDING', NULL
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    INNER JOIN dbo.NotificationRuleRecipients fr ON fr.RuleId = st.RuleId
    CROSS APPLY (VALUES (N'EMAIL', fr.Email, st.NotifyEmail), (N'WHATSAPP', fr.Mobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE ch.Enabled = 1 AND ch.Addr IS NOT NULL;

    /* same address twice in one run (e.g. two fixed entries) -> keep one */
    ;WITH d AS (
        SELECT ROW_NUMBER() OVER (PARTITION BY RuleId, SpecId, Channel, RecipientType, RecipientAddress, DueDate, DaysBefore ORDER BY (SELECT 1)) AS rn
        FROM #np_cand
    )
    DELETE FROM d WHERE rn > 1;

    /* already created before -> not due again */
    DELETE c
    FROM #np_cand c
    WHERE EXISTS (SELECT 1 FROM dbo.Notifications n
                  WHERE n.RuleId = c.RuleId AND n.SpecId = c.SpecId AND n.Channel = c.Channel
                    AND n.RecipientType = c.RecipientType AND n.RecipientAddress = c.RecipientAddress AND n.DueDate = c.DueDate
                    AND n.DaysBefore = c.DaysBefore);   -- each reminder step once

    /* write the message for each recipient */
    UPDATE c
    SET [Subject] = dbo.fn_Notification_Render(c.TSubject, c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName),
        Body      = dbo.fn_Notification_Render(c.TBody,    c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName)
    FROM #np_cand c
    INNER JOIN #np_spec sp ON sp.SpecId = c.SpecId;

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

CREATE OR ALTER PROCEDURE [dbo].[usp_NotificationRules_Save]
    @RuleId         INT = NULL,
    @CategoryId     INT,
    @SubCategoryId  INT = NULL,
    @DaysBefore     INT,
    @NotifyEmail    BIT,
    @NotifyWhatsApp BIT,
    @NotifyInApp    BIT,
    @ToCustomer     BIT,
    @ToEmployee     BIT,
    @Subject        NVARCHAR(200) = NULL,
    @Body           NVARCHAR(2000) = NULL,
    @IsActive       BIT = 1,
    @RecipientsJson NVARCHAR(MAX) = NULL,
    @UserId         INT = NULL,
    @ReminderDays   NVARCHAR(200) = NULL      -- "60,30,20,10": a reminder at each step; NULL = only @DaysBefore
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RuleId = 0 SET @RuleId = NULL;
    IF @SubCategoryId = 0 SET @SubCategoryId = NULL;
    SET @Subject = NULLIF(LTRIM(RTRIM(@Subject)), N'');
    SET @Body = NULLIF(LTRIM(RTRIM(@Body)), N'');

    IF NOT EXISTS (SELECT 1 FROM dbo.ServiceCategories WHERE CategoryId = @CategoryId)
        THROW 50201, 'Choose a category.', 1;
    IF @SubCategoryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.ServiceSubCategories WHERE SubCategoryId = @SubCategoryId AND CategoryId = @CategoryId)
        THROW 50202, 'That sub category does not belong to the selected category.', 1;
    /* reminder steps: clean list, largest first; DaysBefore = the first (largest) step */
    DECLARE @Steps TABLE (D INT PRIMARY KEY);
    INSERT @Steps (D)
    SELECT DISTINCT TRY_CONVERT(INT, LTRIM(RTRIM(value))) FROM STRING_SPLIT(ISNULL(@ReminderDays, N''), N',')
    WHERE LTRIM(RTRIM(value)) <> N'' AND TRY_CONVERT(INT, LTRIM(RTRIM(value))) IS NOT NULL;
    IF EXISTS (SELECT 1 FROM @Steps)
    BEGIN
        IF EXISTS (SELECT 1 FROM @Steps WHERE D < -365 OR D > 365)
            THROW 50203, 'Reminder days must be between -365 and 365.', 1;
        SET @DaysBefore = (SELECT MAX(D) FROM @Steps);
        SET @ReminderDays = STUFF((SELECT N',' + CONVERT(NVARCHAR(12), D) FROM @Steps ORDER BY D DESC FOR XML PATH(N'')), 1, 1, N'');
    END
    ELSE SET @ReminderDays = NULL;
    IF @DaysBefore IS NULL OR @DaysBefore < -365 OR @DaysBefore > 365
        THROW 50203, 'Days before due date must be between -365 and 365.', 1;
    IF ISNULL(@NotifyEmail, 0) = 0 AND ISNULL(@NotifyWhatsApp, 0) = 0 AND ISNULL(@NotifyInApp, 0) = 0
        THROW 50204, 'Choose at least one channel: Email, WhatsApp or In-app.', 1;
    IF ISNULL(@NotifyInApp, 0) = 1 AND ISNULL(@ToEmployee, 0) = 0
        THROW 50205, 'In-app reminders go to the assigned employee. Tick "Assigned employee" or untick In-app.', 1;

    DECLARE @Rec TABLE (RecipientName NVARCHAR(150) NOT NULL, Email NVARCHAR(150) NULL, Mobile NVARCHAR(30) NULL);
    IF NULLIF(@RecipientsJson, N'') IS NOT NULL
    BEGIN
        IF ISJSON(@RecipientsJson) <> 1 THROW 50206, 'The fixed recipient list is not valid.', 1;
        INSERT @Rec (RecipientName, Email, Mobile)
        SELECT LEFT(LTRIM(RTRIM(j.[name])), 150), NULLIF(LEFT(LTRIM(RTRIM(j.email)), 150), N''), NULLIF(LEFT(LTRIM(RTRIM(j.mobile)), 30), N'')
        FROM OPENJSON(@RecipientsJson) WITH ([name] NVARCHAR(150) '$.name', email NVARCHAR(150) '$.email', mobile NVARCHAR(30) '$.mobile') j
        WHERE NULLIF(LTRIM(RTRIM(j.email)), N'') IS NOT NULL OR NULLIF(LTRIM(RTRIM(j.mobile)), N'') IS NOT NULL;
        IF EXISTS (SELECT 1 FROM @Rec WHERE Email IS NOT NULL AND (Email NOT LIKE N'%_@_%._%' OR Email LIKE N'% %'))
            THROW 50207, 'A fixed recipient has an invalid email address.', 1;
        UPDATE @Rec SET RecipientName = ISNULL(NULLIF(RecipientName, N''), ISNULL(Email, Mobile));
    END;

    IF ISNULL(@ToCustomer, 0) = 0 AND ISNULL(@ToEmployee, 0) = 0 AND NOT EXISTS (SELECT 1 FROM @Rec)
        THROW 50208, 'Choose who is notified: the customer, the assigned employee or a fixed recipient.', 1;

    IF EXISTS (SELECT 1 FROM dbo.NotificationRules
               WHERE CategoryId = @CategoryId AND ISNULL(SubCategoryId, 0) = ISNULL(@SubCategoryId, 0)
                 AND DaysBefore = @DaysBefore AND (@RuleId IS NULL OR RuleId <> @RuleId))
        THROW 50209, 'A rule for this category / sub category and number of days already exists. Edit that rule instead.', 1;
    IF @RuleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.NotificationRules WHERE RuleId = @RuleId)
        THROW 50210, 'The rule was not found. It may have been deleted.', 1;

    BEGIN TRAN;
    IF @RuleId IS NULL
    BEGIN
        INSERT dbo.NotificationRules (CategoryId, SubCategoryId, DaysBefore, ReminderDays, NotifyEmail, NotifyWhatsApp, NotifyInApp, ToCustomer, ToEmployee, [Subject], Body, IsActive, CreatedBy)
        VALUES (@CategoryId, @SubCategoryId, @DaysBefore, @ReminderDays, ISNULL(@NotifyEmail, 0), ISNULL(@NotifyWhatsApp, 0), ISNULL(@NotifyInApp, 0),
                ISNULL(@ToCustomer, 0), ISNULL(@ToEmployee, 0), @Subject, @Body, ISNULL(@IsActive, 1), @UserId);
        SET @RuleId = CONVERT(INT, SCOPE_IDENTITY());
    END
    ELSE
    BEGIN
        UPDATE dbo.NotificationRules
        SET CategoryId = @CategoryId, SubCategoryId = @SubCategoryId, DaysBefore = @DaysBefore, ReminderDays = @ReminderDays,
            NotifyEmail = ISNULL(@NotifyEmail, 0), NotifyWhatsApp = ISNULL(@NotifyWhatsApp, 0), NotifyInApp = ISNULL(@NotifyInApp, 0),
            ToCustomer = ISNULL(@ToCustomer, 0), ToEmployee = ISNULL(@ToEmployee, 0),
            [Subject] = @Subject, Body = @Body, IsActive = ISNULL(@IsActive, 1),
            ModifiedBy = @UserId, ModifiedDate = SYSUTCDATETIME()
        WHERE RuleId = @RuleId;
        DELETE FROM dbo.NotificationRuleRecipients WHERE RuleId = @RuleId;
    END;
    INSERT dbo.NotificationRuleRecipients (RuleId, RecipientName, Email, Mobile)
    SELECT @RuleId, RecipientName, Email, Mobile FROM @Rec;
    COMMIT;

    SELECT @RuleId AS RuleId;
END;
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_NotificationRules_GetAll]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (
        SELECT r.RuleId AS id,
               r.CategoryId AS categoryId, c.CategoryName AS categoryName,
               r.SubCategoryId AS subCategoryId, sc.SubCategoryName AS subCategoryName,
               r.DaysBefore AS daysBefore,
               ISNULL(r.ReminderDays, CONVERT(NVARCHAR(12), r.DaysBefore)) AS reminderDays,
               r.NotifyEmail AS notifyEmail, r.NotifyWhatsApp AS notifyWhatsApp, r.NotifyInApp AS notifyInApp,
               r.ToCustomer AS toCustomer, r.ToEmployee AS toEmployee,
               r.[Subject] AS [subject], r.Body AS body, r.IsActive AS isActive,
               (SELECT COUNT(1)
                FROM dbo.TrackerCategorySubCategorySpecs sp
                INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
                WHERE sp.CategoryId = r.CategoryId
                  AND (r.SubCategoryId IS NULL OR sp.SubCategoryId = r.SubCategoryId)
                  AND sp.DueDate IS NOT NULL
                  AND UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'COMPLETED', N'REJECTED')) AS appliesTo,
               (SELECT COUNT(1) FROM dbo.Notifications n WHERE n.RuleId = r.RuleId) AS created,
               JSON_QUERY(ISNULL((SELECT fr.RecipientId AS id, fr.RecipientName AS name, fr.Email AS email, fr.Mobile AS mobile
                                  FROM dbo.NotificationRuleRecipients fr
                                  WHERE fr.RuleId = r.RuleId
                                  ORDER BY fr.RecipientId
                                  FOR JSON PATH), N'[]')) AS recipients
        FROM dbo.NotificationRules r
        INNER JOIN dbo.ServiceCategories c ON c.CategoryId = r.CategoryId
        LEFT JOIN dbo.ServiceSubCategories sc ON sc.SubCategoryId = r.SubCategoryId
        ORDER BY c.CategoryName, ISNULL(sc.SubCategoryName, N''), r.DaysBefore DESC
        FOR JSON PATH
    ) AS json;
END;
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_Notifications_Lookups]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (
        SELECT (
            SELECT c.CategoryId AS id, c.CategoryName AS name,
                   JSON_QUERY(ISNULL((SELECT s.SubCategoryId AS id, s.SubCategoryName AS name
                                      FROM dbo.ServiceSubCategories s
                                      WHERE s.CategoryId = c.CategoryId AND s.IsActive = 1
                                      ORDER BY s.SubCategoryName
                                      FOR JSON PATH), N'[]')) AS subCategories
            FROM dbo.ServiceCategories c
            WHERE c.IsActive = 1
            ORDER BY c.CategoryName
            FOR JSON PATH
        ) AS categories,
        JSON_QUERY(ISNULL((
            SELECT f.ReminderFrequencyId AS id, f.FrequencyName AS name, f.DaysBefore AS days
            FROM dbo.ReminderFrequencies f
            WHERE f.IsActive = 1
            ORDER BY f.DisplayOrder, f.DaysBefore DESC
            FOR JSON PATH), N'[]')) AS frequencies
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS json;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ReminderFrequencies_GetAll
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (
        SELECT f.ReminderFrequencyId AS id, f.FrequencyName AS name, f.DaysBefore AS days, f.DisplayOrder AS displayOrder, f.IsActive AS isActive,
               (SELECT COUNT(1) FROM dbo.NotificationRules r
                WHERE r.IsActive = 1 AND EXISTS (SELECT 1 FROM STRING_SPLIT(ISNULL(r.ReminderDays, CONVERT(NVARCHAR(12), r.DaysBefore)), N',') x
                                                 WHERE LTRIM(RTRIM(x.value)) <> N'' AND TRY_CONVERT(INT, LTRIM(RTRIM(x.value))) = f.DaysBefore)) AS usedByRules
        FROM dbo.ReminderFrequencies f
        ORDER BY f.DisplayOrder, f.DaysBefore DESC
        FOR JSON PATH
    ) AS json;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ReminderFrequencies_Save
    @Id           INT = NULL,
    @Name         NVARCHAR(100),
    @DaysBefore   INT,
    @DisplayOrder INT = 0,
    @IsActive     BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    IF @Id = 0 SET @Id = NULL;
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    IF @DaysBefore IS NULL OR @DaysBefore < -365 OR @DaysBefore > 365
        THROW 50231, 'Days must be between -365 and 365 (a negative number = after the due date).', 1;
    IF @Name IS NULL SET @Name = CASE WHEN @DaysBefore >= 0 THEN CONCAT(@DaysBefore, N' days before') ELSE CONCAT(-@DaysBefore, N' days after') END;
    IF EXISTS (SELECT 1 FROM dbo.ReminderFrequencies WHERE DaysBefore = @DaysBefore AND (@Id IS NULL OR ReminderFrequencyId <> @Id))
        THROW 50232, 'A frequency with this number of days already exists.', 1;
    IF @Id IS NULL
    BEGIN
        INSERT dbo.ReminderFrequencies (FrequencyName, DaysBefore, DisplayOrder, IsActive) VALUES (@Name, @DaysBefore, ISNULL(@DisplayOrder, 0), ISNULL(@IsActive, 1));
        SET @Id = CONVERT(INT, SCOPE_IDENTITY());
    END
    ELSE
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.ReminderFrequencies WHERE ReminderFrequencyId = @Id) THROW 50233, 'The frequency was not found.', 1;
        UPDATE dbo.ReminderFrequencies
           SET FrequencyName = @Name, DaysBefore = @DaysBefore, DisplayOrder = ISNULL(@DisplayOrder, 0), IsActive = ISNULL(@IsActive, 1), ModifiedDate = SYSUTCDATETIME()
         WHERE ReminderFrequencyId = @Id;
    END;
    SELECT @Id AS Id;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ReminderFrequencies_Delete @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    -- rules keep their own list of days, so a frequency can always be removed from the list
    DELETE dbo.ReminderFrequencies WHERE ReminderFrequencyId = @Id;
    IF @@ROWCOUNT = 0 THROW 50233, 'The frequency was not found.', 1;
END;
GO
