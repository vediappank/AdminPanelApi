/* =====================================================================================
   Website Leads module – SuperAdminPanel database
   -------------------------------------------------------------------------------------
   Every form on blisspointgroup.com posts to AdminPanelApi (PublicWebFormsController),
   which calls these procedures, then alerts everyone in the Admin Panel instantly
   (SignalR "WebLeadReceived").

   Contents
     1. Tables     WebForm, WebEnquiry, WebEnquiryTravel, WebEnquiryCrm, WebEnquiryModule,
                   WebJobApplication, WebStatusHistory
     2. Procedures usp_WebEnquiry_Insert / _List / _Get / _UpdateStatus / _Assign / _MarkRead /
                   _MarkAllRead / _ConvertToSalesEnquiry
                   usp_WebJobApplication_Insert / _List / _Get / _UpdateStatus / _MarkRead
                   usp_WebLead_Summary, usp_WebLead_Dashboard, usp_WebLead_Assignees, usp_WebForm_List, usp_WebForm_Update
     3. Seed       43 website forms, incl. the home page Corporate PRO proposal form (Travel & Tourism removed Oct 2026)
     4. Menu       Modules: WEBSITE (Website Leads) + 4 screens, granted to every role that has SALES

   Safe to re-run (CREATE IF missing, CREATE OR ALTER for procedures, MERGE for seed rows).
   Requires SQL Server 2017+ (STRING_AGG, CONCAT_WS, CREATE OR ALTER) and database
   compatibility level 130+ (STRING_SPLIT) - check: SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =====================================================================================
   1. TABLES
   ===================================================================================== */
IF OBJECT_ID('dbo.WebForm', 'U') IS NULL
CREATE TABLE dbo.WebForm
(
    WebFormId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebForm PRIMARY KEY,
    FormCode     VARCHAR(60)       NOT NULL CONSTRAINT UQ_WebForm_FormCode UNIQUE,
    FormName     NVARCHAR(150)     NOT NULL,
    PagePath     NVARCHAR(300)     NOT NULL,
    Category     VARCHAR(40)       NOT NULL,
    FormType     VARCHAR(40)       NOT NULL,
    TargetTable  VARCHAR(40)       NOT NULL,
    NotifyEmail  NVARCHAR(400)     NULL,           -- comma-separated department inboxes (optional)
    IsActive     BIT               NOT NULL CONSTRAINT DF_WebForm_IsActive DEFAULT (1),
    CreatedAt    DATETIME2(0)      NOT NULL CONSTRAINT DF_WebForm_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt    DATETIME2(0)      NULL,
    UpdatedBy    NVARCHAR(100)     NULL
);
GO

/* Upgrade: WebForm created by an earlier version of this script */
IF COL_LENGTH('dbo.WebForm', 'UpdatedAt') IS NULL ALTER TABLE dbo.WebForm ADD UpdatedAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebForm', 'UpdatedBy') IS NULL ALTER TABLE dbo.WebForm ADD UpdatedBy NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.WebForm', 'NotifyEmail') IS NULL ALTER TABLE dbo.WebForm ADD NotifyEmail NVARCHAR(400) NULL;
ELSE IF COL_LENGTH('dbo.WebForm', 'NotifyEmail') < 800 ALTER TABLE dbo.WebForm ALTER COLUMN NotifyEmail NVARCHAR(400) NULL;
GO

IF OBJECT_ID('dbo.WebEnquiry', 'U') IS NULL
CREATE TABLE dbo.WebEnquiry
(
    WebEnquiryId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebEnquiry PRIMARY KEY,
    ReferenceNo       VARCHAR(20)     NOT NULL CONSTRAINT UQ_WebEnquiry_Ref UNIQUE,
    WebFormId         INT             NOT NULL CONSTRAINT FK_WebEnquiry_WebForm REFERENCES dbo.WebForm(WebFormId),
    RequestType       VARCHAR(20)     NOT NULL CONSTRAINT CK_WebEnquiry_RequestType CHECK (RequestType IN ('Enquiry','Quote','Demo','Access')),
    Category          VARCHAR(40)     NOT NULL,
    ServiceInterest   NVARCHAR(200)   NULL,
    FullName          NVARCHAR(120)   NOT NULL,
    CompanyName       NVARCHAR(200)   NULL,
    Email             NVARCHAR(200)   NOT NULL,
    Phone             NVARCHAR(30)    NOT NULL,
    Message           NVARCHAR(2000)  NULL,
    ConsentGiven      BIT             NOT NULL CONSTRAINT DF_WebEnquiry_Consent DEFAULT (0),
    SourcePage        NVARCHAR(300)   NULL,
    Referrer          NVARCHAR(500)   NULL,
    UtmSource         NVARCHAR(100)   NULL,
    UtmMedium         NVARCHAR(100)   NULL,
    UtmCampaign       NVARCHAR(150)   NULL,
    IpAddress         VARCHAR(45)     NULL,
    UserAgent         NVARCHAR(400)   NULL,
    IsSpam            BIT             NOT NULL CONSTRAINT DF_WebEnquiry_IsSpam DEFAULT (0),
    Status            VARCHAR(20)     NOT NULL CONSTRAINT DF_WebEnquiry_Status DEFAULT ('New')
                                      CONSTRAINT CK_WebEnquiry_Status CHECK (Status IN ('New','Contacted','Quoted','Converted','Closed','Rejected')),
    AssignedToUserId  INT             NULL,             -- dbo.PlatformUsers.PlatformUserId (no FK, same "id + snapshot" convention as SalesEnquiries)
    AssignedToName    NVARCHAR(150)   NULL,
    StatusNote        NVARCHAR(500)   NULL,
    IsRead            BIT             NOT NULL CONSTRAINT DF_WebEnquiry_IsRead DEFAULT (0),
    ReadAt            DATETIME2(0)    NULL,
    ReadBy            NVARCHAR(100)   NULL,
    SalesEnquiryId    INT             NULL,             -- set by usp_WebEnquiry_ConvertToSalesEnquiry
    ConvertedTenantId INT             NULL,
    CreatedAt         DATETIME2(0)    NOT NULL CONSTRAINT DF_WebEnquiry_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt         DATETIME2(0)    NULL,
    UpdatedBy         NVARCHAR(100)   NULL
);
GO
/* Upgrade: WebEnquiry created by an earlier version of this script */
IF COL_LENGTH('dbo.WebEnquiry', 'AssignedToName') IS NULL ALTER TABLE dbo.WebEnquiry ADD AssignedToName NVARCHAR(150) NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'IsRead') IS NULL ALTER TABLE dbo.WebEnquiry ADD IsRead BIT NOT NULL CONSTRAINT DF_WebEnquiry_IsRead DEFAULT (0) WITH VALUES;
IF COL_LENGTH('dbo.WebEnquiry', 'ReadAt') IS NULL ALTER TABLE dbo.WebEnquiry ADD ReadAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'ReadBy') IS NULL ALTER TABLE dbo.WebEnquiry ADD ReadBy NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'SalesEnquiryId') IS NULL ALTER TABLE dbo.WebEnquiry ADD SalesEnquiryId INT NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'ConvertedTenantId') IS NULL ALTER TABLE dbo.WebEnquiry ADD ConvertedTenantId INT NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'UpdatedAt') IS NULL ALTER TABLE dbo.WebEnquiry ADD UpdatedAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebEnquiry', 'UpdatedBy') IS NULL ALTER TABLE dbo.WebEnquiry ADD UpdatedBy NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebEnquiry_Status_CreatedAt' AND object_id = OBJECT_ID('dbo.WebEnquiry'))
    CREATE INDEX IX_WebEnquiry_Status_CreatedAt ON dbo.WebEnquiry(Status, CreatedAt DESC) INCLUDE (Category, FullName, Phone, IsRead);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebEnquiry_Category_CreatedAt' AND object_id = OBJECT_ID('dbo.WebEnquiry'))
    CREATE INDEX IX_WebEnquiry_Category_CreatedAt ON dbo.WebEnquiry(Category, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebEnquiry_Unread' AND object_id = OBJECT_ID('dbo.WebEnquiry'))
    CREATE INDEX IX_WebEnquiry_Unread ON dbo.WebEnquiry(IsRead, IsSpam, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebEnquiry_Email' AND object_id = OBJECT_ID('dbo.WebEnquiry'))
    CREATE INDEX IX_WebEnquiry_Email ON dbo.WebEnquiry(Email, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebEnquiry_Phone' AND object_id = OBJECT_ID('dbo.WebEnquiry'))
    CREATE INDEX IX_WebEnquiry_Phone ON dbo.WebEnquiry(Phone);
GO

IF OBJECT_ID('dbo.WebEnquiryTravel', 'U') IS NULL
CREATE TABLE dbo.WebEnquiryTravel
(
    WebEnquiryId     BIGINT        NOT NULL CONSTRAINT PK_WebEnquiryTravel PRIMARY KEY
                                   CONSTRAINT FK_WebEnquiryTravel_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId) ON DELETE CASCADE,
    Destination      NVARCHAR(150) NULL,
    TravelDatesText  NVARCHAR(100) NULL,
    DepartureDate    DATE          NULL,
    ArrivalDate      DATE          NULL,
    Travellers       SMALLINT      NULL,
    Pilgrims         SMALLINT      NULL,
    RoomType         NVARCHAR(60)  NULL,
    VisaType         NVARCHAR(60)  NULL
);
GO

IF OBJECT_ID('dbo.WebEnquiryCrm', 'U') IS NULL
CREATE TABLE dbo.WebEnquiryCrm
(
    WebEnquiryId   BIGINT       NOT NULL CONSTRAINT PK_WebEnquiryCrm PRIMARY KEY
                                CONSTRAINT FK_WebEnquiryCrm_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId) ON DELETE CASCADE,
    TeamSize       VARCHAR(20)  NULL,
    BusinessType   NVARCHAR(60) NULL,
    ProvisionedAt  DATETIME2(0) NULL
);
GO

IF OBJECT_ID('dbo.WebEnquiryModule', 'U') IS NULL
CREATE TABLE dbo.WebEnquiryModule
(
    WebEnquiryId  BIGINT       NOT NULL CONSTRAINT FK_WebEnquiryModule_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId) ON DELETE CASCADE,
    ModuleName    NVARCHAR(60) NOT NULL,
    CONSTRAINT PK_WebEnquiryModule PRIMARY KEY (WebEnquiryId, ModuleName)
);
GO

IF OBJECT_ID('dbo.WebJobApplication', 'U') IS NULL
CREATE TABLE dbo.WebJobApplication
(
    WebJobApplicationId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebJobApplication PRIMARY KEY,
    ReferenceNo     VARCHAR(20)    NOT NULL CONSTRAINT UQ_WebJobApplication_Ref UNIQUE,
    FullName        NVARCHAR(120)  NOT NULL,
    Email           NVARCHAR(200)  NOT NULL,
    Phone           NVARCHAR(30)   NOT NULL,
    RoleApplied     NVARCHAR(80)   NOT NULL,
    Experience      VARCHAR(20)    NULL,
    CoverNote       NVARCHAR(2000) NULL,
    CvFileName      NVARCHAR(260)  NOT NULL,     -- original name shown to staff
    CvFilePath      NVARCHAR(500)  NOT NULL,     -- stored file name inside Website:CvStoragePath (never a public URL)
    CvContentType   VARCHAR(100)   NULL,
    CvSizeBytes     INT            NULL CONSTRAINT CK_WebJobApplication_CvSize CHECK (CvSizeBytes <= 5242880),
    SourcePage      NVARCHAR(300)  NULL,
    IpAddress       VARCHAR(45)    NULL,
    UserAgent       NVARCHAR(400)  NULL,
    IsSpam          BIT            NOT NULL CONSTRAINT DF_WebJobApplication_IsSpam DEFAULT (0),
    Status          VARCHAR(20)    NOT NULL CONSTRAINT DF_WebJobApplication_Status DEFAULT ('New')
                                   CONSTRAINT CK_WebJobApplication_Status CHECK (Status IN ('New','Shortlisted','Interview','Offered','Hired','Rejected')),
    StatusNote      NVARCHAR(500)  NULL,
    IsRead          BIT            NOT NULL CONSTRAINT DF_WebJobApplication_IsRead DEFAULT (0),
    ReadAt          DATETIME2(0)   NULL,
    ReadBy          NVARCHAR(100)  NULL,
    CreatedAt       DATETIME2(0)   NOT NULL CONSTRAINT DF_WebJobApplication_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt       DATETIME2(0)   NULL,
    UpdatedBy       NVARCHAR(100)  NULL
);
GO
/* Upgrade: WebJobApplication created by an earlier version of this script */
IF COL_LENGTH('dbo.WebJobApplication', 'UserAgent') IS NULL ALTER TABLE dbo.WebJobApplication ADD UserAgent NVARCHAR(400) NULL;
IF COL_LENGTH('dbo.WebJobApplication', 'IsRead') IS NULL ALTER TABLE dbo.WebJobApplication ADD IsRead BIT NOT NULL CONSTRAINT DF_WebJobApplication_IsRead DEFAULT (0) WITH VALUES;
IF COL_LENGTH('dbo.WebJobApplication', 'ReadAt') IS NULL ALTER TABLE dbo.WebJobApplication ADD ReadAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebJobApplication', 'ReadBy') IS NULL ALTER TABLE dbo.WebJobApplication ADD ReadBy NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.WebJobApplication', 'UpdatedAt') IS NULL ALTER TABLE dbo.WebJobApplication ADD UpdatedAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebJobApplication', 'UpdatedBy') IS NULL ALTER TABLE dbo.WebJobApplication ADD UpdatedBy NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebJobApplication_Status_CreatedAt' AND object_id = OBJECT_ID('dbo.WebJobApplication'))
    CREATE INDEX IX_WebJobApplication_Status_CreatedAt ON dbo.WebJobApplication(Status, CreatedAt DESC) INCLUDE (IsRead);
GO

IF OBJECT_ID('dbo.WebStatusHistory', 'U') IS NULL
CREATE TABLE dbo.WebStatusHistory
(
    WebStatusHistoryId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebStatusHistory PRIMARY KEY,
    EntityType   VARCHAR(20)   NOT NULL CONSTRAINT CK_WebStatusHistory_EntityType CHECK (EntityType IN ('Enquiry','JobApplication')),
    EntityId     BIGINT        NOT NULL,
    FromStatus   VARCHAR(20)   NULL,
    ToStatus     VARCHAR(20)   NOT NULL,
    Note         NVARCHAR(500) NULL,
    ChangedBy    NVARCHAR(100) NOT NULL,
    ChangedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_WebStatusHistory_ChangedAt DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebStatusHistory_Entity' AND object_id = OBJECT_ID('dbo.WebStatusHistory'))
    CREATE INDEX IX_WebStatusHistory_Entity ON dbo.WebStatusHistory(EntityType, EntityId, ChangedAt);
GO

/* =====================================================================================
   2. PROCEDURES – website (called by the anonymous public endpoint)
   ===================================================================================== */

/* Saves one enquiry / quote / demo / access request with its travel / CRM extras.
   Unknown form codes are registered automatically (so a new page works without a DB change);
   an inactive form is refused. Same email + form + message within 10 minutes = duplicate:
   the existing reference is returned and no new row (or alert) is created. */
CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_Insert
    @FormCode         VARCHAR(60),
    @PagePath         NVARCHAR(300)  = NULL,
    @PageTitle        NVARCHAR(150)  = NULL,
    @RequestType      VARCHAR(20)    = 'Enquiry',
    @ServiceInterest  NVARCHAR(200)  = NULL,
    @FullName         NVARCHAR(120),
    @CompanyName      NVARCHAR(200)  = NULL,
    @Email            NVARCHAR(200),
    @Phone            NVARCHAR(30),
    @Message          NVARCHAR(2000) = NULL,
    @ConsentGiven     BIT            = 0,
    @SourcePage       NVARCHAR(300)  = NULL,
    @Referrer         NVARCHAR(500)  = NULL,
    @UtmSource        NVARCHAR(100)  = NULL,
    @UtmMedium        NVARCHAR(100)  = NULL,
    @UtmCampaign      NVARCHAR(150)  = NULL,
    @IpAddress        VARCHAR(45)    = NULL,
    @UserAgent        NVARCHAR(400)  = NULL,
    @IsSpam           BIT            = 0,
    -- travel extras
    @Destination      NVARCHAR(150)  = NULL,
    @TravelDatesText  NVARCHAR(100)  = NULL,
    @DepartureDate    DATE           = NULL,
    @ArrivalDate      DATE           = NULL,
    @Travellers       SMALLINT       = NULL,
    @Pilgrims         SMALLINT       = NULL,
    @RoomType         NVARCHAR(60)   = NULL,
    @VisaType         NVARCHAR(60)   = NULL,
    -- CRM extras
    @TeamSize         VARCHAR(20)    = NULL,
    @BusinessType     NVARCHAR(60)   = NULL,
    @ModulesCsv       NVARCHAR(600)  = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RequestType NOT IN ('Enquiry','Quote','Demo','Access') SET @RequestType = 'Enquiry';

    DECLARE @WebFormId INT, @IsActive BIT, @Category VARCHAR(40), @FormName NVARCHAR(150), @NotifyEmail NVARCHAR(400);

    SELECT @WebFormId = WebFormId, @IsActive = IsActive, @Category = Category, @FormName = FormName, @NotifyEmail = NotifyEmail
    FROM dbo.WebForm WHERE FormCode = @FormCode;

    IF @WebFormId IS NULL
    BEGIN
        BEGIN TRY
            INSERT dbo.WebForm (FormCode, FormName, PagePath, Category, FormType, TargetTable)
            VALUES (@FormCode, COALESCE(NULLIF(@PageTitle, N''), @FormCode), COALESCE(@PagePath, @FormCode), 'General',
                    CASE WHEN @RequestType IN ('Demo','Access') THEN 'CRM request' ELSE 'Service enquiry' END, 'WebEnquiry');
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;   -- another request registered it a moment ago
        END CATCH;
        SELECT @WebFormId = WebFormId, @IsActive = IsActive, @Category = Category, @FormName = FormName, @NotifyEmail = NotifyEmail
        FROM dbo.WebForm WHERE FormCode = @FormCode;
    END

    IF @IsActive = 0
        THROW 50010, 'This form is currently switched off.', 1;

    -- duplicate guard (double click / resubmit)
    DECLARE @DupId BIGINT =
        (SELECT TOP (1) WebEnquiryId FROM dbo.WebEnquiry
          WHERE Email = @Email AND WebFormId = @WebFormId
            AND ISNULL(Message, N'') = ISNULL(@Message, N'')
            AND CreatedAt > DATEADD(MINUTE, -10, SYSUTCDATETIME())
          ORDER BY WebEnquiryId DESC);
    IF @DupId IS NOT NULL
    BEGIN
        SELECT e.WebEnquiryId, e.ReferenceNo, CAST(1 AS BIT) AS IsDuplicate, f.FormName, e.Category, e.RequestType,
               e.ServiceInterest, e.FullName, e.CompanyName, e.Phone, e.Email, e.IsSpam, e.CreatedAt, f.NotifyEmail
        FROM dbo.WebEnquiry e JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
        WHERE e.WebEnquiryId = @DupId;
        RETURN;
    END

    BEGIN TRAN;

    INSERT dbo.WebEnquiry (ReferenceNo, WebFormId, RequestType, Category, ServiceInterest, FullName, CompanyName, Email, Phone,
                           Message, ConsentGiven, SourcePage, Referrer, UtmSource, UtmMedium, UtmCampaign, IpAddress, UserAgent,
                           IsSpam, Status, IsRead)
    VALUES (LEFT(CONCAT('T', REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')), 20), @WebFormId, @RequestType, @Category, NULLIF(LTRIM(RTRIM(@ServiceInterest)), N''),
            LTRIM(RTRIM(@FullName)), NULLIF(LTRIM(RTRIM(@CompanyName)), N''), LOWER(LTRIM(RTRIM(@Email))), LTRIM(RTRIM(@Phone)),
            NULLIF(LTRIM(RTRIM(@Message)), N''), ISNULL(@ConsentGiven, 0), @SourcePage, @Referrer, @UtmSource, @UtmMedium, @UtmCampaign,
            @IpAddress, @UserAgent, ISNULL(@IsSpam, 0), CASE WHEN @IsSpam = 1 THEN 'Rejected' ELSE 'New' END, ISNULL(@IsSpam, 0));

    DECLARE @Id BIGINT = SCOPE_IDENTITY();
    DECLARE @Ref VARCHAR(20) = CONCAT('WE-', FORMAT(SYSUTCDATETIME(), 'yyMM'), '-', CASE WHEN @Id > 999999 THEN CAST(@Id AS VARCHAR(19)) ELSE RIGHT(CONCAT('000000', @Id), 6) END);
    UPDATE dbo.WebEnquiry SET ReferenceNo = @Ref WHERE WebEnquiryId = @Id;

    IF COALESCE(@Destination, @TravelDatesText, @RoomType, @VisaType) IS NOT NULL
       OR @DepartureDate IS NOT NULL OR @ArrivalDate IS NOT NULL OR @Travellers IS NOT NULL OR @Pilgrims IS NOT NULL
        INSERT dbo.WebEnquiryTravel (WebEnquiryId, Destination, TravelDatesText, DepartureDate, ArrivalDate, Travellers, Pilgrims, RoomType, VisaType)
        VALUES (@Id, NULLIF(@Destination, N''), NULLIF(@TravelDatesText, N''), @DepartureDate, @ArrivalDate,
                @Travellers, @Pilgrims, NULLIF(@RoomType, N''), NULLIF(@VisaType, N''));

    IF COALESCE(@TeamSize, @BusinessType, @ModulesCsv) IS NOT NULL
        INSERT dbo.WebEnquiryCrm (WebEnquiryId, TeamSize, BusinessType)
        VALUES (@Id, NULLIF(@TeamSize, ''), NULLIF(@BusinessType, N''));

    IF NULLIF(@ModulesCsv, N'') IS NOT NULL
        INSERT dbo.WebEnquiryModule (WebEnquiryId, ModuleName)
        SELECT DISTINCT @Id, LEFT(LTRIM(RTRIM(value)), 60)
        FROM STRING_SPLIT(@ModulesCsv, N',')
        WHERE LTRIM(RTRIM(value)) <> N'';

    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('Enquiry', @Id, NULL, CASE WHEN @IsSpam = 1 THEN 'Rejected' ELSE 'New' END,
            CASE WHEN @IsSpam = 1 THEN N'Spam trap filled - hidden from the inbox' ELSE CONCAT(N'Received from ', COALESCE(@SourcePage, @FormCode)) END,
            N'website');

    COMMIT;

    SELECT e.WebEnquiryId, e.ReferenceNo, CAST(0 AS BIT) AS IsDuplicate, @FormName AS FormName, e.Category, e.RequestType,
           e.ServiceInterest, e.FullName, e.CompanyName, e.Phone, e.Email, e.IsSpam, e.CreatedAt, @NotifyEmail AS NotifyEmail
    FROM dbo.WebEnquiry e WHERE e.WebEnquiryId = @Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebJobApplication_Insert
    @FullName       NVARCHAR(120),
    @Email          NVARCHAR(200),
    @Phone          NVARCHAR(30),
    @RoleApplied    NVARCHAR(80),
    @Experience     VARCHAR(20)    = NULL,
    @CoverNote      NVARCHAR(2000) = NULL,
    @CvFileName     NVARCHAR(260),
    @CvFilePath     NVARCHAR(500),
    @CvContentType  VARCHAR(100)   = NULL,
    @CvSizeBytes    INT            = NULL,
    @SourcePage     NVARCHAR(300)  = NULL,
    @IpAddress      VARCHAR(45)    = NULL,
    @UserAgent      NVARCHAR(400)  = NULL,
    @IsSpam         BIT            = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRAN;

    INSERT dbo.WebJobApplication (ReferenceNo, FullName, Email, Phone, RoleApplied, Experience, CoverNote, CvFileName, CvFilePath,
                                  CvContentType, CvSizeBytes, SourcePage, IpAddress, UserAgent, IsSpam, Status, IsRead)
    VALUES (LEFT(CONCAT('T', REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')), 20), LTRIM(RTRIM(@FullName)), LOWER(LTRIM(RTRIM(@Email))), LTRIM(RTRIM(@Phone)), LTRIM(RTRIM(@RoleApplied)),
            NULLIF(@Experience, ''), NULLIF(LTRIM(RTRIM(@CoverNote)), N''), @CvFileName, @CvFilePath, @CvContentType, @CvSizeBytes,
            @SourcePage, @IpAddress, @UserAgent, ISNULL(@IsSpam, 0), CASE WHEN @IsSpam = 1 THEN 'Rejected' ELSE 'New' END, ISNULL(@IsSpam, 0));

    DECLARE @Id BIGINT = SCOPE_IDENTITY();
    DECLARE @Ref VARCHAR(20) = CONCAT('JA-', FORMAT(SYSUTCDATETIME(), 'yyMM'), '-', CASE WHEN @Id > 999999 THEN CAST(@Id AS VARCHAR(19)) ELSE RIGHT(CONCAT('000000', @Id), 6) END);
    UPDATE dbo.WebJobApplication SET ReferenceNo = @Ref WHERE WebJobApplicationId = @Id;

    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('JobApplication', @Id, NULL, CASE WHEN @IsSpam = 1 THEN 'Rejected' ELSE 'New' END, N'CV received from careers page', N'website');

    COMMIT;

    SELECT WebJobApplicationId, ReferenceNo, FullName, Email, Phone, RoleApplied, Experience, IsSpam, CreatedAt
    FROM dbo.WebJobApplication WHERE WebJobApplicationId = @Id;
END
GO

/* =====================================================================================
   2b. PROCEDURES – Admin Panel (staff)
   ===================================================================================== */
CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_List
    @Status           VARCHAR(20)   = NULL,
    @Category         VARCHAR(40)   = NULL,
    @RequestType      VARCHAR(20)   = NULL,
    @WebFormId        INT           = NULL,
    @AssignedToUserId INT           = NULL,
    @Search           NVARCHAR(100) = NULL,
    @FromDate         DATE          = NULL,
    @ToDate           DATE          = NULL,
    @UnreadOnly       BIT           = 0,
    @IncludeSpam      BIT           = 0,
    @PageNumber       INT           = 1,
    @PageSize         INT           = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize NOT BETWEEN 5 AND 200 SET @PageSize = 25;
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    SELECT e.WebEnquiryId, e.ReferenceNo, e.RequestType, e.Category, f.FormName, f.PagePath, e.ServiceInterest,
           e.FullName, e.CompanyName, e.Email, e.Phone, LEFT(e.Message, 160) AS MessagePreview,
           e.Status, e.AssignedToUserId, e.AssignedToName, e.IsRead, e.IsSpam, e.SalesEnquiryId, e.CreatedAt,
           COUNT(*) OVER () AS TotalCount
    FROM dbo.WebEnquiry e
    JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
    WHERE (@IncludeSpam = 1 OR e.IsSpam = 0)
      AND (@Status IS NULL OR e.Status = @Status)
      AND (@Category IS NULL OR e.Category = @Category)
      AND (@RequestType IS NULL OR e.RequestType = @RequestType)
      AND (@WebFormId IS NULL OR e.WebFormId = @WebFormId)
      AND (@AssignedToUserId IS NULL OR e.AssignedToUserId = @AssignedToUserId)
      AND (@UnreadOnly = 0 OR e.IsRead = 0)
      AND (@FromDate IS NULL OR e.CreatedAt >= @FromDate)
      AND (@ToDate IS NULL OR e.CreatedAt < DATEADD(DAY, 1, CAST(@ToDate AS DATETIME2(0))))
      AND (@Search IS NULL
           OR e.ReferenceNo = @Search
           OR e.FullName LIKE N'%' + @Search + N'%'
           OR e.CompanyName LIKE N'%' + @Search + N'%'
           OR e.Email LIKE N'%' + @Search + N'%'
           OR e.Phone LIKE N'%' + @Search + N'%'
           OR e.ServiceInterest LIKE N'%' + @Search + N'%'
           OR f.FormName LIKE N'%' + @Search + N'%')
    ORDER BY e.CreatedAt DESC, e.WebEnquiryId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END
GO

/* 3 result sets: the enquiry (+ form, travel, CRM), its modules, its status history */
CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_Get
    @WebEnquiryId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT e.*, f.FormCode, f.FormName, f.PagePath, f.FormType,
           t.Destination, t.TravelDatesText, t.DepartureDate, t.ArrivalDate, t.Travellers, t.Pilgrims, t.RoomType, t.VisaType,
           c.TeamSize, c.BusinessType, c.ProvisionedAt
    FROM dbo.WebEnquiry e
    JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
    LEFT JOIN dbo.WebEnquiryTravel t ON t.WebEnquiryId = e.WebEnquiryId
    LEFT JOIN dbo.WebEnquiryCrm c ON c.WebEnquiryId = e.WebEnquiryId
    WHERE e.WebEnquiryId = @WebEnquiryId;

    SELECT ModuleName FROM dbo.WebEnquiryModule WHERE WebEnquiryId = @WebEnquiryId ORDER BY ModuleName;

    SELECT WebStatusHistoryId, FromStatus, ToStatus, Note, ChangedBy, ChangedAt
    FROM dbo.WebStatusHistory
    WHERE EntityType = 'Enquiry' AND EntityId = @WebEnquiryId
    ORDER BY ChangedAt DESC, WebStatusHistoryId DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_UpdateStatus
    @WebEnquiryId BIGINT,
    @Status       VARCHAR(20),
    @Note         NVARCHAR(500) = NULL,
    @ChangedBy    NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Status NOT IN ('New','Contacted','Quoted','Converted','Closed','Rejected')
        THROW 50001, 'Invalid status.', 1;

    DECLARE @From VARCHAR(20) = (SELECT Status FROM dbo.WebEnquiry WHERE WebEnquiryId = @WebEnquiryId);
    IF @From IS NULL THROW 50404, 'Enquiry not found.', 1;

    BEGIN TRAN;
    UPDATE dbo.WebEnquiry
       SET Status = @Status, StatusNote = COALESCE(NULLIF(@Note, N''), StatusNote),
           IsRead = 1, ReadAt = COALESCE(ReadAt, SYSUTCDATETIME()), ReadBy = COALESCE(ReadBy, @ChangedBy),
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE WebEnquiryId = @WebEnquiryId;
    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('Enquiry', @WebEnquiryId, @From, @Status, NULLIF(@Note, N''), @ChangedBy);
    COMMIT;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_Assign
    @WebEnquiryId     BIGINT,
    @AssignedToUserId INT = NULL,       -- NULL = unassign
    @ChangedBy        NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Status VARCHAR(20) = (SELECT Status FROM dbo.WebEnquiry WHERE WebEnquiryId = @WebEnquiryId);
    IF @Status IS NULL THROW 50404, 'Enquiry not found.', 1;

    DECLARE @Name NVARCHAR(150) =
        (SELECT LTRIM(RTRIM(CONCAT(FirstName, N' ', ISNULL(LastName, N'')))) FROM dbo.PlatformUsers WHERE PlatformUserId = @AssignedToUserId);
    IF @AssignedToUserId IS NOT NULL AND @Name IS NULL THROW 50002, 'User not found.', 1;

    BEGIN TRAN;
    UPDATE dbo.WebEnquiry
       SET AssignedToUserId = @AssignedToUserId, AssignedToName = @Name,
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE WebEnquiryId = @WebEnquiryId;
    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('Enquiry', @WebEnquiryId, @Status, @Status, CASE WHEN @Name IS NULL THEN N'Unassigned' ELSE CONCAT(N'Assigned to ', @Name) END, @ChangedBy);
    COMMIT;

    SELECT @AssignedToUserId AS AssignedToUserId, @Name AS AssignedToName;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_MarkRead
    @WebEnquiryId BIGINT,
    @ReadBy       NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.WebEnquiry SET IsRead = 1, ReadAt = SYSUTCDATETIME(), ReadBy = @ReadBy
    WHERE WebEnquiryId = @WebEnquiryId AND IsRead = 0;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_MarkAllRead
    @ReadBy NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.WebEnquiry SET IsRead = 1, ReadAt = SYSUTCDATETIME(), ReadBy = @ReadBy WHERE IsRead = 0;
    UPDATE dbo.WebJobApplication SET IsRead = 1, ReadAt = SYSUTCDATETIME(), ReadBy = @ReadBy WHERE IsRead = 0;
END
GO

/* Copies a website enquiry into the existing Sales pipeline (dbo.SalesEnquiries, Source = 'Website').
   Idempotent: a second call returns the Sales Enquiry created the first time. */
CREATE OR ALTER PROCEDURE dbo.usp_WebEnquiry_ConvertToSalesEnquiry
    @WebEnquiryId BIGINT,
    @ChangedBy    NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Existing INT, @Status VARCHAR(20);
    SELECT @Existing = SalesEnquiryId, @Status = Status FROM dbo.WebEnquiry WHERE WebEnquiryId = @WebEnquiryId;
    IF @Status IS NULL THROW 50404, 'Enquiry not found.', 1;
    IF @Existing IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.SalesEnquiries WHERE SalesEnquiryId = @Existing)
    BEGIN
        SELECT @Existing AS SalesEnquiryId, CAST(1 AS BIT) AS AlreadyConverted;
        RETURN;
    END

    -- dbo.SalesEnquiries may carry CHECK constraints on CustomerType / Source (as it does on Status):
    -- only use 'Company' / 'Website' when the table accepts them, otherwise fall back to its defaults.
    DECLARE @AllowCompany BIT = CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                                                  WHERE parent_object_id = OBJECT_ID('dbo.SalesEnquiries')
                                                    AND definition LIKE '%CustomerType%' AND definition NOT LIKE '%Company%')
                                     THEN 0 ELSE 1 END;
    DECLARE @Source NVARCHAR(50) = CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                                                     WHERE parent_object_id = OBJECT_ID('dbo.SalesEnquiries')
                                                       AND definition LIKE '%Source%' AND definition NOT LIKE '%Website%')
                                        THEN NULL ELSE N'Website' END;

    BEGIN TRAN;

    INSERT dbo.SalesEnquiries (EnquiryNo, EnquiryDate, CustomerNameSnapshot, ContactPerson, Mobile, WhatsApp, Email, CustomerType,
                               Source, Requirement, EstimatedAmount, AssignedToEmployeeId, AssignedToEmployeeNameSnapshot,
                               Priority, Status, Notes, IsActive, CreatedDate, CreatedBy)
    SELECT e.ReferenceNo,
           CAST(e.CreatedAt AS DATE),
           LEFT(COALESCE(e.CompanyName, e.FullName), 150),
           CASE WHEN e.CompanyName IS NOT NULL THEN LEFT(e.FullName, 150) END,
           LEFT(e.Phone, 30),
           LEFT(e.Phone, 30),
           e.Email,
           CASE WHEN e.CompanyName IS NOT NULL AND @AllowCompany = 1 THEN 'Company' ELSE 'Individual' END,
           @Source,
           CONCAT_WS(CHAR(13) + CHAR(10),
                     CONCAT(N'Website form: ', f.FormName),
                     CASE WHEN e.ServiceInterest IS NOT NULL THEN CONCAT(N'Interested in: ', e.ServiceInterest) END,
                     CASE WHEN c.TeamSize IS NOT NULL THEN CONCAT(N'Team size: ', c.TeamSize) END,
                     CASE WHEN c.BusinessType IS NOT NULL THEN CONCAT(N'Business type: ', c.BusinessType) END,
                     CASE WHEN m.Modules IS NOT NULL THEN CONCAT(N'Modules: ', m.Modules) END,
                     CASE WHEN t.Destination IS NOT NULL THEN CONCAT(N'Destination: ', t.Destination) END,
                     CASE WHEN t.TravelDatesText IS NOT NULL THEN CONCAT(N'Travel dates: ', t.TravelDatesText) END,
                     CASE WHEN t.Travellers IS NOT NULL THEN CONCAT(N'Travellers: ', t.Travellers) END,
                     e.Message),
           0,
           e.AssignedToUserId,
           e.AssignedToName,
           'Normal',
           'New',
           CONCAT(N'Created from website enquiry ', e.ReferenceNo),
           1,
           SYSUTCDATETIME(),
           @ChangedBy
    FROM dbo.WebEnquiry e
    JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
    LEFT JOIN dbo.WebEnquiryCrm c ON c.WebEnquiryId = e.WebEnquiryId
    LEFT JOIN dbo.WebEnquiryTravel t ON t.WebEnquiryId = e.WebEnquiryId
    OUTER APPLY (SELECT STRING_AGG(ModuleName, N', ') AS Modules FROM dbo.WebEnquiryModule WHERE WebEnquiryId = e.WebEnquiryId) m
    WHERE e.WebEnquiryId = @WebEnquiryId;

    DECLARE @SalesId INT = SCOPE_IDENTITY();

    UPDATE dbo.WebEnquiry
       SET SalesEnquiryId = @SalesId, Status = 'Converted', IsRead = 1,
           ReadAt = COALESCE(ReadAt, SYSUTCDATETIME()), ReadBy = COALESCE(ReadBy, @ChangedBy),
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE WebEnquiryId = @WebEnquiryId;

    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('Enquiry', @WebEnquiryId, @Status, 'Converted', CONCAT(N'Moved to Sales pipeline (Sales Enquiry #', @SalesId, N')'), @ChangedBy);

    COMMIT;
    SELECT @SalesId AS SalesEnquiryId, CAST(0 AS BIT) AS AlreadyConverted;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebJobApplication_List
    @Status       VARCHAR(20)   = NULL,
    @RoleApplied  NVARCHAR(80)  = NULL,
    @Search       NVARCHAR(100) = NULL,
    @FromDate     DATE          = NULL,
    @ToDate       DATE          = NULL,
    @UnreadOnly   BIT           = 0,
    @IncludeSpam  BIT           = 0,
    @PageNumber   INT           = 1,
    @PageSize     INT           = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize NOT BETWEEN 5 AND 200 SET @PageSize = 25;
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    SELECT j.WebJobApplicationId, j.ReferenceNo, j.FullName, j.Email, j.Phone, j.RoleApplied, j.Experience,
           j.CvFileName, j.CvSizeBytes, j.Status, j.IsRead, j.IsSpam, j.CreatedAt,
           COUNT(*) OVER () AS TotalCount
    FROM dbo.WebJobApplication j
    WHERE (@IncludeSpam = 1 OR j.IsSpam = 0)
      AND (@Status IS NULL OR j.Status = @Status)
      AND (@RoleApplied IS NULL OR j.RoleApplied = @RoleApplied)
      AND (@UnreadOnly = 0 OR j.IsRead = 0)
      AND (@FromDate IS NULL OR j.CreatedAt >= @FromDate)
      AND (@ToDate IS NULL OR j.CreatedAt < DATEADD(DAY, 1, CAST(@ToDate AS DATETIME2(0))))
      AND (@Search IS NULL OR j.ReferenceNo = @Search OR j.FullName LIKE N'%' + @Search + N'%'
           OR j.Email LIKE N'%' + @Search + N'%' OR j.Phone LIKE N'%' + @Search + N'%')
    ORDER BY j.CreatedAt DESC, j.WebJobApplicationId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END
GO

/* 2 result sets: the application, its status history */
CREATE OR ALTER PROCEDURE dbo.usp_WebJobApplication_Get
    @WebJobApplicationId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM dbo.WebJobApplication WHERE WebJobApplicationId = @WebJobApplicationId;

    SELECT WebStatusHistoryId, FromStatus, ToStatus, Note, ChangedBy, ChangedAt
    FROM dbo.WebStatusHistory
    WHERE EntityType = 'JobApplication' AND EntityId = @WebJobApplicationId
    ORDER BY ChangedAt DESC, WebStatusHistoryId DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebJobApplication_UpdateStatus
    @WebJobApplicationId BIGINT,
    @Status              VARCHAR(20),
    @Note                NVARCHAR(500) = NULL,
    @ChangedBy           NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Status NOT IN ('New','Shortlisted','Interview','Offered','Hired','Rejected')
        THROW 50001, 'Invalid status.', 1;

    DECLARE @From VARCHAR(20) = (SELECT Status FROM dbo.WebJobApplication WHERE WebJobApplicationId = @WebJobApplicationId);
    IF @From IS NULL THROW 50404, 'Job application not found.', 1;

    BEGIN TRAN;
    UPDATE dbo.WebJobApplication
       SET Status = @Status, StatusNote = COALESCE(NULLIF(@Note, N''), StatusNote),
           IsRead = 1, ReadAt = COALESCE(ReadAt, SYSUTCDATETIME()), ReadBy = COALESCE(ReadBy, @ChangedBy),
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE WebJobApplicationId = @WebJobApplicationId;
    INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
    VALUES ('JobApplication', @WebJobApplicationId, @From, @Status, NULLIF(@Note, N''), @ChangedBy);
    COMMIT;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebJobApplication_MarkRead
    @WebJobApplicationId BIGINT,
    @ReadBy              NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.WebJobApplication SET IsRead = 1, ReadAt = SYSUTCDATETIME(), ReadBy = @ReadBy
    WHERE WebJobApplicationId = @WebJobApplicationId AND IsRead = 0;
END
GO

/* Bell + dashboard: 3 result sets – counters, counts by category (New), latest 10 unread items */
CREATE OR ALTER PROCEDURE dbo.usp_WebLead_Summary
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Today DATETIME2(0) = CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2(0));

    SELECT
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsRead = 0 AND IsSpam = 0)                      AS UnreadEnquiries,
        (SELECT COUNT(*) FROM dbo.WebJobApplication WHERE IsRead = 0 AND IsSpam = 0)               AS UnreadJobApplications,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE Status = 'New' AND IsSpam = 0)                  AS NewEnquiries,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE CreatedAt >= @Today AND IsSpam = 0)             AS EnquiriesToday,
        (SELECT COUNT(*) FROM dbo.WebJobApplication WHERE CreatedAt >= @Today AND IsSpam = 0)      AS JobApplicationsToday,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE CreatedAt >= DATEADD(DAY, -30, @Today) AND IsSpam = 0) AS EnquiriesLast30Days;

    SELECT Category, COUNT(*) AS NewCount
    FROM dbo.WebEnquiry WHERE Status = 'New' AND IsSpam = 0
    GROUP BY Category ORDER BY COUNT(*) DESC;

    SELECT TOP (10) x.Kind, x.Id, x.ReferenceNo, x.Title, x.SubTitle, x.Category, x.CreatedAt
    FROM (
        SELECT 'Enquiry' AS Kind, e.WebEnquiryId AS Id, e.ReferenceNo,
               COALESCE(e.CompanyName, e.FullName) AS Title,
               COALESCE(e.ServiceInterest, f.FormName) AS SubTitle, e.Category, e.CreatedAt
        FROM dbo.WebEnquiry e JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
        WHERE e.IsRead = 0 AND e.IsSpam = 0
        UNION ALL
        SELECT 'JobApplication', j.WebJobApplicationId, j.ReferenceNo, j.FullName, CONCAT(N'CV: ', j.RoleApplied), 'Careers', j.CreatedAt
        FROM dbo.WebJobApplication j
        WHERE j.IsRead = 0 AND j.IsSpam = 0
    ) x
    ORDER BY x.CreatedAt DESC;
END
GO

/* Website Leads dashboard - 9 result sets for the selected period (@Days = 7 / 30 / 90 ...):
   1 KPIs  2 daily trend  3 by business  4 by status  5 by request type  6 top forms
   7 lead sources  8 team workload  9 latest leads */
CREATE OR ALTER PROCEDURE dbo.usp_WebLead_Dashboard
    @Days INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    IF @Days IS NULL OR @Days < 1 SET @Days = 30;
    IF @Days > 366 SET @Days = 366;

    DECLARE @Today DATETIME2(0) = CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2(0));
    DECLARE @From  DATETIME2(0) = DATEADD(DAY, 1 - @Days, @Today);           -- period = last @Days days incl. today
    DECLARE @PrevFrom DATETIME2(0) = DATEADD(DAY, -@Days, @From);            -- the equal period before it

    /* 1. KPIs */
    ;WITH firstResponse AS (
        SELECT e.WebEnquiryId, e.CreatedAt,
               (SELECT MIN(h.ChangedAt) FROM dbo.WebStatusHistory h
                 WHERE h.EntityType = 'Enquiry' AND h.EntityId = e.WebEnquiryId
                   AND h.ChangedBy <> N'website' AND h.ToStatus <> 'New') AS RespondedAt
        FROM dbo.WebEnquiry e
        WHERE e.IsSpam = 0 AND e.CreatedAt >= @From
    )
    SELECT
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From)                                  AS Enquiries,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @PrevFrom AND CreatedAt < @From)        AS EnquiriesPrevious,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @Today)                                 AS EnquiriesToday,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND Status = 'New')                                      AS OpenNew,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND IsRead = 0)                                          AS UnreadEnquiries,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From AND Status = 'Converted')         AS Converted,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From AND Status IN ('Contacted','Quoted','Converted','Closed')) AS Responded,
        (SELECT AVG(CAST(DATEDIFF(MINUTE, CreatedAt, RespondedAt) AS FLOAT)) FROM firstResponse WHERE RespondedAt IS NOT NULL) AS AvgFirstResponseMinutes,
        (SELECT COUNT(*) FROM dbo.WebJobApplication WHERE IsSpam = 0 AND CreatedAt >= @From)                           AS JobApplications,
        (SELECT COUNT(*) FROM dbo.WebJobApplication WHERE IsSpam = 0 AND IsRead = 0)                                   AS UnreadJobApplications,
        (SELECT COUNT(*) FROM dbo.WebEnquiry WHERE IsSpam = 1 AND CreatedAt >= @From)
          + (SELECT COUNT(*) FROM dbo.WebJobApplication WHERE IsSpam = 1 AND CreatedAt >= @From)                       AS SpamBlocked,
        @Days AS Days;

    /* 2. daily trend - every day in the period, zero-filled */
    ;WITH n AS (SELECT TOP (@Days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_objects),
          d AS (SELECT CAST(DATEADD(DAY, i, @From) AS DATE) AS [Day] FROM n)
    SELECT d.[Day],
           (SELECT COUNT(*) FROM dbo.WebEnquiry e WHERE e.IsSpam = 0 AND CAST(e.CreatedAt AS DATE) = d.[Day])        AS Enquiries,
           (SELECT COUNT(*) FROM dbo.WebJobApplication j WHERE j.IsSpam = 0 AND CAST(j.CreatedAt AS DATE) = d.[Day]) AS JobApplications
    FROM d ORDER BY d.[Day];

    /* 3. by business */
    SELECT Category, COUNT(*) AS Total,
           SUM(CASE WHEN Status = 'Converted' THEN 1 ELSE 0 END) AS Converted,
           SUM(CASE WHEN Status = 'New' THEN 1 ELSE 0 END) AS OpenNew
    FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From
    GROUP BY Category ORDER BY COUNT(*) DESC;

    /* 4. by status (pipeline) */
    SELECT s.Status, ISNULL(c.Total, 0) AS Total
    FROM (VALUES ('New',1),('Contacted',2),('Quoted',3),('Converted',4),('Closed',5),('Rejected',6)) s(Status, Ord)
    LEFT JOIN (SELECT Status, COUNT(*) AS Total FROM dbo.WebEnquiry
               WHERE IsSpam = 0 AND CreatedAt >= @From GROUP BY Status) c ON c.Status = s.Status
    ORDER BY s.Ord;

    /* 5. by request type */
    SELECT RequestType, COUNT(*) AS Total
    FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From
    GROUP BY RequestType ORDER BY COUNT(*) DESC;

    /* 6. top forms */
    SELECT TOP (10) f.WebFormId, f.FormName, f.PagePath, f.Category, COUNT(*) AS Total,
           SUM(CASE WHEN e.Status = 'Converted' THEN 1 ELSE 0 END) AS Converted
    FROM dbo.WebEnquiry e JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
    WHERE e.IsSpam = 0 AND e.CreatedAt >= @From
    GROUP BY f.WebFormId, f.FormName, f.PagePath, f.Category
    ORDER BY COUNT(*) DESC, f.FormName;

    /* 7. lead sources: UTM source, else referrer host, else Direct */
    ;WITH src AS (
        SELECT CASE
                 WHEN NULLIF(LTRIM(RTRIM(UtmSource)), N'') IS NOT NULL THEN LOWER(LTRIM(RTRIM(UtmSource)))
                 WHEN NULLIF(Referrer, N'') IS NULL THEN N'direct'
                 ELSE LOWER(REPLACE(
                        SUBSTRING(Referrer,
                                  CHARINDEX(N'//', Referrer + N'//') + 2,
                                  CHARINDEX(N'/', Referrer + N'/', CHARINDEX(N'//', Referrer + N'//') + 2) - CHARINDEX(N'//', Referrer + N'//') - 2),
                        N'www.', N''))
               END AS Source
        FROM dbo.WebEnquiry WHERE IsSpam = 0 AND CreatedAt >= @From
    )
    SELECT TOP (8) LEFT(Source, 100) AS Source, COUNT(*) AS Total FROM src GROUP BY Source ORDER BY COUNT(*) DESC;

    /* 8. team workload (open = New / Contacted / Quoted) */
    SELECT ISNULL(AssignedToName, N'Unassigned') AS AssignedTo, AssignedToUserId,
           SUM(CASE WHEN Status IN ('New','Contacted','Quoted') THEN 1 ELSE 0 END) AS OpenCount,
           SUM(CASE WHEN Status = 'Converted' AND CreatedAt >= @From THEN 1 ELSE 0 END) AS ConvertedInPeriod,
           COUNT(*) AS Total
    FROM dbo.WebEnquiry WHERE IsSpam = 0
    GROUP BY AssignedToName, AssignedToUserId
    HAVING SUM(CASE WHEN Status IN ('New','Contacted','Quoted') THEN 1 ELSE 0 END) > 0
        OR SUM(CASE WHEN CreatedAt >= @From THEN 1 ELSE 0 END) > 0
    ORDER BY OpenCount DESC, AssignedTo;

    /* 9. latest leads (read or not) */
    SELECT TOP (8) x.Kind, x.Id, x.ReferenceNo, x.Title, x.SubTitle, x.Category, x.Status, x.IsRead, x.CreatedAt
    FROM (
        SELECT 'Enquiry' AS Kind, e.WebEnquiryId AS Id, e.ReferenceNo, COALESCE(e.CompanyName, e.FullName) AS Title,
               COALESCE(e.ServiceInterest, f.FormName) AS SubTitle, e.Category, e.Status, e.IsRead, e.CreatedAt
        FROM dbo.WebEnquiry e JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId WHERE e.IsSpam = 0
        UNION ALL
        SELECT 'JobApplication', j.WebJobApplicationId, j.ReferenceNo, j.FullName, CONCAT(N'CV: ', j.RoleApplied), 'Careers', j.Status, j.IsRead, j.CreatedAt
        FROM dbo.WebJobApplication j WHERE j.IsSpam = 0
    ) x
    ORDER BY x.CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebLead_Assignees
AS
BEGIN
    SET NOCOUNT ON;
    SELECT PlatformUserId, LTRIM(RTRIM(CONCAT(FirstName, N' ', ISNULL(LastName, N'')))) AS FullName, Email
    FROM dbo.PlatformUsers
    WHERE Status = 'Active'
    ORDER BY FirstName, LastName;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebForm_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT f.WebFormId, f.FormCode, f.FormName, f.PagePath, f.Category, f.FormType, f.TargetTable, f.NotifyEmail, f.IsActive,
           ISNULL(s.Total, 0) AS TotalSubmissions, ISNULL(s.Last30Days, 0) AS Last30Days, s.LastSubmittedAt
    FROM dbo.WebForm f
    OUTER APPLY (
        SELECT COUNT(*) AS Total,
               SUM(CASE WHEN e.CreatedAt >= DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Last30Days,
               MAX(e.CreatedAt) AS LastSubmittedAt
        FROM dbo.WebEnquiry e WHERE e.WebFormId = f.WebFormId AND e.IsSpam = 0
    ) s
    WHERE f.Category <> 'Travel & Tourism'          -- Travel & Tourism discontinued (Oct 2026)
    ORDER BY f.Category, f.FormName;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebForm_Update
    @WebFormId   INT,
    @IsActive    BIT,
    @NotifyEmail NVARCHAR(400) = NULL,
    @ChangedBy   NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.WebForm
       SET IsActive = @IsActive, NotifyEmail = NULLIF(LTRIM(RTRIM(@NotifyEmail)), N''),
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE WebFormId = @WebFormId;
    IF @@ROWCOUNT = 0 THROW 50404, 'Form not found.', 1;
END
GO

/* =====================================================================================
   3. SEED – every form found on blisspointgroup.com
   ===================================================================================== */
MERGE dbo.WebForm AS t
USING (VALUES
    ('accounting-bookkeeping', N'Accounting Services Dubai', N'accounting-bookkeeping.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-aml-compliance', N'AML Compliance', N'accounting/services/aml-compliance.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-audit-support', N'Audit Support Services Dubai', N'accounting/services/audit-support.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-bank-reconciliation', N'Bank Reconciliation', N'accounting/services/bank-reconciliation.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-bookkeeping', N'Bookkeeping Services Dubai', N'accounting/services/bookkeeping.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-corporate-tax-filing', N'Corporate Tax Filing Dubai', N'accounting/services/corporate-tax-filing.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-corporate-tax-registration', N'Corporate Tax Registration UAE', N'accounting/services/corporate-tax-registration.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-corporate-tax', N'Corporate Tax Services Dubai', N'accounting/services/corporate-tax.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-excise-tax', N'Excise Tax', N'accounting/services/excise-tax.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-financial-consultancy-advisory-services', N'Financial Consultancy UAE', N'accounting/services/financial-consultancy-advisory-services.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-financial-reporting', N'Financial Reporting Services Dubai', N'accounting/services/financial-reporting.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-fta-compliance', N'FTA Compliance', N'accounting/services/fta-compliance.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-internal-audit', N'Internal Audit', N'accounting/services/internal-audit.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-payroll-management', N'Payroll Management Services Dubai', N'accounting/services/payroll-management.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-ubo-compliance', N'UBO Compliance', N'accounting/services/ubo-compliance.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-vat-filing', N'VAT Filing Services Dubai', N'accounting/services/vat-filing.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('accounting-services-vat-registration', N'VAT Registration Dubai', N'accounting/services/vat-registration.html', 'Accounting', 'Service enquiry', 'WebEnquiry'),
    ('business-setup', N'Business Setup in Dubai', N'business-setup.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-company-liquidation', N'Company Liquidation Services Dubai', N'business/services/company-liquidation.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-free-zone-setup', N'Free Zone Company Setup Dubai', N'business/services/free-zone-setup.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-golden-visa', N'Golden Visa Services Dubai', N'business/services/golden-visa.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-mainland-setup', N'Mainland Company Setup Dubai', N'business/services/mainland-setup.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-moa-poa', N'MOA & POA Drafting & Attestation', N'business/services/moa-poa.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-offshore-setup', N'Offshore Company Setup UAE', N'business/services/offshore-setup.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-pro-services', N'PRO Services Dubai', N'business/services/pro-services.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-special-approval', N'External Approval Services Dubai', N'business/services/special-approval.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-trade-licence-amendment', N'Trade Licence Amendment Dubai', N'business/services/trade-licence-amendment.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-trade-license', N'Dubai Trade License Services', N'business/services/trade-license.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-virtual-office', N'Virtual Office Dubai for Business Setup', N'business/services/virtual-office.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-visa-services', N'Visa Services Dubai', N'business/services/visa-services.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('business-services-will-drafting', N'Will Drafting & Registration Dubai', N'business/services/will-drafting.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('careers', N'Careers', N'careers.html', 'Careers', 'Job application', 'WebJobApplication'),
    ('contact', N'Contact Us', N'contact.html', 'General', 'Service enquiry', 'WebEnquiry'),
    ('crm-solutions', N'CRM for Document Clearing & Typing Centres in UAE', N'crm-solutions.html', 'IT Services', 'CRM demo request', 'WebEnquiry'),
    ('digital-marketing', N'Digital Marketing Agency Dubai: SEO, Google Ads & Social Media', N'digital-marketing.html', 'IT Services', 'IT services enquiry', 'WebEnquiry'),
    ('get-access', N'Get Access to Bliss Point CRM', N'get-access.html', 'IT Services', 'CRM access request', 'WebEnquiry'),
    ('it-services', N'IT Services in Dubai: CRM, Digital Marketing & Websites', N'it-services.html', 'IT Services', 'IT services enquiry', 'WebEnquiry'),
    ('service-commercial-license', N'Commercial license', N'service-commercial-license.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('service-global-visa', N'Global Visa Services', N'service-global-visa.html', 'Typing Center', 'Service enquiry', 'WebEnquiry'),
    ('service-professional-license', N'Professional license', N'service-professional-license.html', 'Business Setup', 'Service enquiry', 'WebEnquiry'),
    ('typing-center', N'Typing Center', N'typing-center.html', 'Typing Center', 'Service enquiry', 'WebEnquiry'),
    ('pro-proposal', N'Corporate PRO Proposal (home page)', N'index.html#pro-proposal', 'PRO Services', 'PRO proposal request', 'WebEnquiry'),
    ('website-development', N'Website Development Company Dubai', N'website-development.html', 'IT Services', 'IT services enquiry', 'WebEnquiry')
) AS s (FormCode, FormName, PagePath, Category, FormType, TargetTable)
ON t.FormCode = s.FormCode
WHEN MATCHED THEN UPDATE SET FormName = s.FormName, PagePath = s.PagePath, Category = s.Category, FormType = s.FormType, TargetTable = s.TargetTable
WHEN NOT MATCHED THEN INSERT (FormCode, FormName, PagePath, Category, FormType, TargetTable)
                      VALUES (s.FormCode, s.FormName, s.PagePath, s.Category, s.FormType, s.TargetTable);
GO

/* =====================================================================================
   4. MENU – data-driven sidebar (dbo.Modules) + role access (dbo.PlatformRoleModules)
      Website Leads ▸ Dashboard / Enquiries / Job Applications / Website Forms
      Granted to every role that already has SALES. Users must sign out and in again
      to see the new menu (their module list is read at login).
   ===================================================================================== */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleCode = 'SALES'), 30) + 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE', N'Website Leads', N'Enquiries, CRM requests and CVs from blisspointgroup.com', 1, 0, @Order, @Now, NULL, NULL, 'mail');

DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

MERGE dbo.Modules AS t
USING (VALUES
    ('WEBSITE_DASHBOARD', N'Dashboard',        N'Website leads at a glance - trend, sources, conversion, team workload', 0, '/website/dashboard', 'dashboard'),
    ('WEBSITE_ENQUIRIES', N'Enquiries',        N'Website enquiries, quotes, demos and CRM access requests', 1, '/website/enquiries',       'email'),
    ('WEBSITE_JOBS',      N'Job Applications', N'CVs from the careers page',                                2, '/website/job-applications', 'badge'),
    ('WEBSITE_FORMS',     N'Website Forms',    N'Switch forms on/off and set department alert emails',      3, '/website/forms',            'list_alt')
) AS s (ModuleCode, ModuleName, Description, DisplayOrder, MenuUrl, Icon)
ON t.ModuleCode = s.ModuleCode
WHEN MATCHED THEN UPDATE SET ParentModuleId = @Parent, MenuUrl = s.MenuUrl, Icon = s.Icon, DisplayOrder = s.DisplayOrder, IsActive = 1, ModifiedDate = @Now
WHEN NOT MATCHED THEN INSERT (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
                      VALUES (s.ModuleCode, s.ModuleName, s.Description, 1, 0, s.DisplayOrder, @Now, @Parent, s.MenuUrl, s.Icon);

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT rm.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoleModules rm
JOIN dbo.Modules sales ON sales.ModuleId = rm.ModuleId AND sales.ModuleCode = 'SALES'
JOIN dbo.Modules m ON m.ModuleCode IN ('WEBSITE', 'WEBSITE_DASHBOARD', 'WEBSITE_ENQUIRIES', 'WEBSITE_JOBS', 'WEBSITE_FORMS')
WHERE NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = m.ModuleId);
GO

PRINT 'Website Leads module installed.';
GO
