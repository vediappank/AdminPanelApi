/* =====================================================================================
   Business Panel help chatbot - SuperAdminPanel (safe to run again)
   The chatbot is inside the support button at the bottom right of every Business Panel page. Its questions and
   answers are managed in the Admin Panel: Settings > Chatbot (/settings/chatbot) - see PlatformChatbotController.

   1. Tables  dbo.ChatbotSettings, dbo.ChatbotCategories, dbo.ChatbotQuestions, dbo.ChatbotUnanswered
              (the Admin Panel API also creates them at start-up and adds about 35 starter questions)
   2. Menu    CHATBOT_SETTINGS (/settings/chatbot) next to Email & WhatsApp, granted to every role that has it
   ===================================================================================== */
SET NOCOUNT ON;
GO

/* 1. TABLES ---------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.ChatbotSettings', N'U') IS NULL
CREATE TABLE dbo.ChatbotSettings
(
    SettingsId      INT            NOT NULL CONSTRAINT PK_ChatbotSettings PRIMARY KEY,
    Enabled         BIT            NOT NULL CONSTRAINT DF_ChatbotSettings_Enabled DEFAULT (1),
    BotName         NVARCHAR(60)   NULL,
    Welcome         NVARCHAR(400)  NULL,
    Fallback        NVARCHAR(400)  NULL,
    ShowSuggestions BIT            NOT NULL CONSTRAINT DF_ChatbotSettings_Suggest DEFAULT (1),
    ModifiedDate    DATETIME2(0)   NULL,
    ModifiedBy      NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.ChatbotCategories', N'U') IS NULL
CREATE TABLE dbo.ChatbotCategories
(
    CategoryId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotCategories PRIMARY KEY,
    Name         NVARCHAR(100)  NOT NULL,
    Icon         NVARCHAR(30)   NULL,
    DisplayOrder INT            NOT NULL CONSTRAINT DF_ChatbotCategories_Order DEFAULT (0),
    IsActive     BIT            NOT NULL CONSTRAINT DF_ChatbotCategories_Active DEFAULT (1)
);
IF OBJECT_ID(N'dbo.ChatbotQuestions', N'U') IS NULL
CREATE TABLE dbo.ChatbotQuestions
(
    QuestionId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotQuestions PRIMARY KEY,
    CategoryId      INT            NOT NULL CONSTRAINT FK_ChatbotQuestions_Categories REFERENCES dbo.ChatbotCategories (CategoryId),
    Question        NVARCHAR(300)  NOT NULL,
    Keywords        NVARCHAR(1000) NULL,
    Answer          NVARCHAR(4000) NOT NULL,
    LinkLabel       NVARCHAR(60)   NULL,
    LinkRoute       NVARCHAR(200)  NULL,
    IsSuggested     BIT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Suggested DEFAULT (0),
    DisplayOrder    INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Order DEFAULT (0),
    IsActive        BIT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Active DEFAULT (1),
    AskCount        INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Asked DEFAULT (0),
    HelpfulCount    INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Helpful DEFAULT (0),
    NotHelpfulCount INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_NotHelpful DEFAULT (0),
    CreatedDate     DATETIME2(0)   NOT NULL CONSTRAINT DF_ChatbotQuestions_Created DEFAULT (SYSUTCDATETIME()),
    ModifiedDate    DATETIME2(0)   NULL,
    ModifiedBy      NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.ChatbotUnanswered', N'U') IS NULL
CREATE TABLE dbo.ChatbotUnanswered
(
    UnansweredId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotUnanswered PRIMARY KEY,
    TenantId     INT            NULL,
    CompanyName  NVARCHAR(200)  NULL,
    UserName     NVARCHAR(200)  NULL,
    AskedText    NVARCHAR(500)  NOT NULL,
    Status       NVARCHAR(20)   NOT NULL CONSTRAINT DF_ChatbotUnanswered_Status DEFAULT (N'Open'),
    QuestionId   INT            NULL,
    CreatedDate  DATETIME2(0)   NOT NULL CONSTRAINT DF_ChatbotUnanswered_Created DEFAULT (SYSUTCDATETIME()),
    ClosedDate   DATETIME2(0)   NULL,
    ClosedBy     NVARCHAR(200)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ChatbotUnanswered_Status' AND object_id = OBJECT_ID(N'dbo.ChatbotUnanswered'))
    CREATE INDEX IX_ChatbotUnanswered_Status ON dbo.ChatbotUnanswered (Status, CreatedDate DESC);
GO

/* 2. ADMIN PANEL MENU ------------------------------------------------------------------ */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Messaging INT = COALESCE((SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'MESSAGING_SETTINGS' ORDER BY ModuleId),
                                  (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'ROLES' ORDER BY ModuleId));   -- same roles as the Roles screen
DECLARE @Parent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Messaging);   -- NULL = top level
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleId = @Messaging), 90) + 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'CHATBOT_SETTINGS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('CHATBOT_SETTINGS', N'Chatbot', N'Questions and answers of the Business Panel help chatbot', 1, 0, @Order, @Now, @Parent, '/settings/chatbot', 'settings');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/settings/chatbot', Icon = 'settings', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'CHATBOT_SETTINGS';

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT r.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoles r
JOIN dbo.Modules m ON m.ModuleCode = 'CHATBOT_SETTINGS'
WHERE (@Messaging IS NULL OR EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = @Messaging))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Chatbot granted to ', @@ROWCOUNT, ' role(s).');
GO

SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'CHATBOT_SETTINGS';
PRINT 'Chatbot installed. Sign out and in again to see Settings > Chatbot in the menu.';
GO
