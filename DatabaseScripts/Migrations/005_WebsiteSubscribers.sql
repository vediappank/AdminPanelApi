/* =====================================================================================================
   005_WebsiteSubscribers.sql - website sign-up (email + WhatsApp, verified by one-time codes) and the
   automatic "new posts" messages (runs by itself when the Admin API starts)

   dbo.WebsiteSubscribers     one row per person: name, email, WhatsApp number, language, what they agreed to,
                              which channels are verified / switched on
   dbo.SubscriberChallenges   one sign-up or login attempt: the hashed codes sent to email and WhatsApp
   dbo.SubscriberSessions     "My account" logins (hashed token, 90 days)
   dbo.NewsletterSends        every automatic / test send: which posts, how many emails / WhatsApp messages
   dbo.NewsletterPosts        per blog post: send to subscribers yes/no, and when it was sent
                              (posts that exist before this script are marked as already handled - never sent)
   Menu                       Website Leads > Subscribers (/website/subscribers), granted to roles that have WEBSITE

   Safe to run again. SQL Server 2016 compatible.
   ===================================================================================================== */

IF OBJECT_ID(N'dbo.WebsiteSubscribers', N'U') IS NULL
CREATE TABLE dbo.WebsiteSubscribers
(
    SubscriberId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebsiteSubscribers PRIMARY KEY,
    Name                NVARCHAR(120)  NOT NULL,
    Email               NVARCHAR(200)  NOT NULL,          -- lower case
    WhatsApp            VARCHAR(20)    NULL,              -- international digits, no + (971501234567)
    Language            VARCHAR(5)     NOT NULL CONSTRAINT DF_WebsiteSubscribers_Language DEFAULT ('en'),   -- en | ar
    Status              VARCHAR(20)    NOT NULL CONSTRAINT DF_WebsiteSubscribers_Status DEFAULT ('Pending'),-- Pending | Active | Unsubscribed
    EmailVerifiedAt     DATETIME2(0)   NULL,
    WhatsAppVerifiedAt  DATETIME2(0)   NULL,
    EmailOptIn          BIT            NOT NULL CONSTRAINT DF_WebsiteSubscribers_EmailOptIn DEFAULT (1),
    WhatsAppOptIn       BIT            NOT NULL CONSTRAINT DF_WebsiteSubscribers_WhatsAppOptIn DEFAULT (1),
    ConsentAt           DATETIME2(0)   NULL,
    ConsentIp           VARCHAR(64)    NULL,
    ConsentText         NVARCHAR(400)  NULL,              -- the exact wording the person ticked
    UnsubscribeToken    CHAR(32)       NOT NULL,
    EmailFailures       INT            NOT NULL CONSTRAINT DF_WebsiteSubscribers_EmailFailures DEFAULT (0),
    Source              NVARCHAR(300)  NULL,              -- page they signed up on
    CreatedAt           DATETIME2(0)   NOT NULL CONSTRAINT DF_WebsiteSubscribers_CreatedAt DEFAULT (SYSUTCDATETIME()),
    ActivatedAt         DATETIME2(0)   NULL,
    LastLoginAt         DATETIME2(0)   NULL,
    UnsubscribedAt      DATETIME2(0)   NULL,
    UpdatedAt           DATETIME2(0)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WebsiteSubscribers_Email' AND object_id = OBJECT_ID(N'dbo.WebsiteSubscribers'))
    CREATE UNIQUE INDEX UX_WebsiteSubscribers_Email ON dbo.WebsiteSubscribers (Email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WebsiteSubscribers_Unsub' AND object_id = OBJECT_ID(N'dbo.WebsiteSubscribers'))
    CREATE UNIQUE INDEX UX_WebsiteSubscribers_Unsub ON dbo.WebsiteSubscribers (UnsubscribeToken);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WebsiteSubscribers_WhatsApp' AND object_id = OBJECT_ID(N'dbo.WebsiteSubscribers'))
    CREATE INDEX IX_WebsiteSubscribers_WhatsApp ON dbo.WebsiteSubscribers (WhatsApp);
GO

IF OBJECT_ID(N'dbo.SubscriberChallenges', N'U') IS NULL
CREATE TABLE dbo.SubscriberChallenges
(
    ChallengeId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubscriberChallenges PRIMARY KEY,
    Ticket            CHAR(32)       NOT NULL,            -- handed to the browser, identifies this attempt
    SubscriberId      INT            NOT NULL,
    Purpose           VARCHAR(10)    NOT NULL,            -- signup | login | whatsapp (verify WhatsApp from My account)
    EmailCodeHash     CHAR(64)       NULL,
    WhatsAppCodeHash  CHAR(64)       NULL,
    PendingName       NVARCHAR(120)  NULL,                -- sign-up details applied once the codes are confirmed
    PendingWhatsApp   VARCHAR(20)    NULL,
    PendingLanguage   VARCHAR(5)     NULL,
    Attempts          INT            NOT NULL CONSTRAINT DF_SubscriberChallenges_Attempts DEFAULT (0),
    ExpiresAt         DATETIME2(0)   NOT NULL,
    CompletedAt       DATETIME2(0)   NULL,
    Ip                VARCHAR(64)    NULL,
    CreatedAt         DATETIME2(0)   NOT NULL CONSTRAINT DF_SubscriberChallenges_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubscriberChallenges_Ticket' AND object_id = OBJECT_ID(N'dbo.SubscriberChallenges'))
    CREATE UNIQUE INDEX UX_SubscriberChallenges_Ticket ON dbo.SubscriberChallenges (Ticket);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubscriberChallenges_Subscriber' AND object_id = OBJECT_ID(N'dbo.SubscriberChallenges'))
    CREATE INDEX IX_SubscriberChallenges_Subscriber ON dbo.SubscriberChallenges (SubscriberId, CreatedAt);
GO

IF OBJECT_ID(N'dbo.SubscriberSessions', N'U') IS NULL
CREATE TABLE dbo.SubscriberSessions
(
    TokenHash     CHAR(64)      NOT NULL CONSTRAINT PK_SubscriberSessions PRIMARY KEY,
    SubscriberId  INT           NOT NULL,
    ExpiresAt     DATETIME2(0)  NOT NULL,
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_SubscriberSessions_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO

IF OBJECT_ID(N'dbo.NewsletterSends', N'U') IS NULL
CREATE TABLE dbo.NewsletterSends
(
    SendId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NewsletterSends PRIMARY KEY,
    Kind            VARCHAR(10)    NOT NULL,              -- posts | test
    PostIds         VARCHAR(400)   NULL,                  -- comma separated BlogPostId
    PostTitles      NVARCHAR(1000) NULL,
    Recipients      INT            NOT NULL CONSTRAINT DF_NewsletterSends_Recipients DEFAULT (0),
    EmailSent       INT            NOT NULL CONSTRAINT DF_NewsletterSends_EmailSent DEFAULT (0),
    EmailFailed     INT            NOT NULL CONSTRAINT DF_NewsletterSends_EmailFailed DEFAULT (0),
    WhatsAppSent    INT            NOT NULL CONSTRAINT DF_NewsletterSends_WhatsAppSent DEFAULT (0),
    WhatsAppFailed  INT            NOT NULL CONSTRAINT DF_NewsletterSends_WhatsAppFailed DEFAULT (0),
    StartedAt       DATETIME2(0)   NOT NULL CONSTRAINT DF_NewsletterSends_StartedAt DEFAULT (SYSUTCDATETIME()),
    FinishedAt      DATETIME2(0)   NULL,
    RequestedBy     NVARCHAR(200)  NULL
);
GO

IF OBJECT_ID(N'dbo.NewsletterPosts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.NewsletterPosts
    (
        BlogPostId         INT           NOT NULL CONSTRAINT PK_NewsletterPosts PRIMARY KEY,
        SendToSubscribers  BIT           NOT NULL CONSTRAINT DF_NewsletterPosts_Send DEFAULT (1),
        SentAt             DATETIME2(0)  NULL,
        SendId             INT           NULL,
        UpdatedAt          DATETIME2(0)  NULL,
        UpdatedBy          NVARCHAR(200) NULL
    );
    -- posts that already exist were published before sign-ups existed: never send them
    IF OBJECT_ID(N'dbo.BlogPost', N'U') IS NOT NULL
        EXEC (N'INSERT dbo.NewsletterPosts (BlogPostId, SendToSubscribers, SentAt, UpdatedBy)
                SELECT BlogPostId, 0, NULL, N''existing before sign-ups'' FROM dbo.BlogPost;');
END
GO

/* Menu: Website Leads > Subscribers */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

IF @Parent IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_SUBSCRIBERS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE_SUBSCRIBERS', N'Subscribers', N'People who signed up on the website for new posts by email and WhatsApp', 1, 0, 7, @Now, @Parent, '/website/subscribers', 'mark_email_read');

DECLARE @SubsId INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_SUBSCRIBERS');

IF @SubsId IS NOT NULL
    INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
    SELECT DISTINCT rm.PlatformRoleId, @SubsId
    FROM dbo.PlatformRoleModules rm
    WHERE rm.ModuleId = @Parent
      AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = @SubsId);
GO
