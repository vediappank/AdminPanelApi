/* =====================================================================================
   Email & WhatsApp settings - SuperAdminPanel (safe to run again)
   Settings > Email & WhatsApp: the mailbox every email goes out from (Admin Panel and Business Panel), the
   WhatsApp account reminders are sent through, and the daily automatic reminder run. Control Center > Reminders:
   each tenant's automatic email / WhatsApp switches.

   1. Tables  dbo.PlatformMessagingSettings (one row, incl. the 24/7 support button), dbo.TenantMessagingSettings
              (one row per tenant), dbo.SupportRequests (requests sent from the Business Panel support button)
              (the Admin Panel API also creates them on first use)
   2. Menu    MESSAGING_SETTINGS (/settings/messaging) next to Branding, granted to every role that has Branding
   ===================================================================================== */
SET NOCOUNT ON;
GO

/* 1. TABLES ---------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.PlatformMessagingSettings', N'U') IS NULL
CREATE TABLE dbo.PlatformMessagingSettings
(
    SettingsId               INT            NOT NULL CONSTRAINT PK_PlatformMessagingSettings PRIMARY KEY,
    EmailEnabled             BIT            NOT NULL CONSTRAINT DF_PMS_EmailEnabled DEFAULT (0),
    SmtpHost                 NVARCHAR(200)  NULL,
    SmtpPort                 INT            NOT NULL CONSTRAINT DF_PMS_SmtpPort DEFAULT (587),
    SmtpEnableSsl            BIT            NOT NULL CONSTRAINT DF_PMS_SmtpSsl DEFAULT (1),
    SmtpUserName             NVARCHAR(200)  NULL,
    SmtpPassword             NVARCHAR(1000) NULL,
    FromEmail                NVARCHAR(200)  NULL,
    FromName                 NVARCHAR(200)  NULL,
    WhatsAppEnabled          BIT            NOT NULL CONSTRAINT DF_PMS_WaEnabled DEFAULT (0),
    WhatsAppProvider         NVARCHAR(20)   NOT NULL CONSTRAINT DF_PMS_WaProvider DEFAULT (N'meta'),
    WhatsAppApiUrl           NVARCHAR(400)  NULL,
    WhatsAppAccountId        NVARCHAR(200)  NULL,
    WhatsAppApiKey           NVARCHAR(2000) NULL,
    WhatsAppSender           NVARCHAR(30)   NULL,
    WhatsAppTemplate         NVARCHAR(200)  NULL,
    WhatsAppTemplateLanguage NVARCHAR(20)   NULL,
    DefaultCountryCode       NVARCHAR(5)    NULL,
    RemindersEnabled         BIT            NOT NULL CONSTRAINT DF_PMS_Reminders DEFAULT (0),
    RemindersRunHour         INT            NOT NULL CONSTRAINT DF_PMS_RunHour DEFAULT (8),
    ModifiedDate             DATETIME2(0)   NULL,
    ModifiedBy               NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.TenantMessagingSettings', N'U') IS NULL
CREATE TABLE dbo.TenantMessagingSettings
(
    TenantId     INT           NOT NULL CONSTRAINT PK_TenantMessagingSettings PRIMARY KEY
                               CONSTRAINT FK_TenantMessagingSettings_Tenants REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE,
    AutoEmail    BIT           NOT NULL CONSTRAINT DF_TMS_AutoEmail DEFAULT (0),
    AutoWhatsApp BIT           NOT NULL CONSTRAINT DF_TMS_AutoWhatsApp DEFAULT (0),
    ModifiedDate DATETIME2(0)  NULL,
    ModifiedBy   NVARCHAR(200) NULL
);
-- 24/7 support button of the Business Panel
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportEnabled') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportEnabled BIT NOT NULL CONSTRAINT DF_PMS_SupportEnabled DEFAULT (0);
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportWhatsApp') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportWhatsApp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportEmail') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportEmail NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportHours') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportHours NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportGreeting') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportGreeting NVARCHAR(300) NULL;
-- requests sent from the support button (e-mailed to SupportEmail and kept here)
IF OBJECT_ID(N'dbo.SupportRequests', N'U') IS NULL
CREATE TABLE dbo.SupportRequests
(
    SupportRequestId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportRequests PRIMARY KEY,
    TenantId         INT            NULL,
    CompanyName      NVARCHAR(200)  NULL,
    UserName         NVARCHAR(200)  NULL,
    UserEmail        NVARCHAR(200)  NULL,
    Phone            NVARCHAR(30)   NULL,
    Subject          NVARCHAR(200)  NOT NULL,
    Message          NVARCHAR(4000) NOT NULL,
    PageUrl          NVARCHAR(400)  NULL,
    Status           NVARCHAR(20)   NOT NULL CONSTRAINT DF_SupportRequests_Status DEFAULT (N'Open'),
    EmailSent        BIT            NOT NULL CONSTRAINT DF_SupportRequests_Emailed DEFAULT (0),
    CreatedDate      DATETIME2(0)   NOT NULL CONSTRAINT DF_SupportRequests_Created DEFAULT (SYSUTCDATETIME()),
    ClosedDate       DATETIME2(0)   NULL,
    ClosedBy         NVARCHAR(200)  NULL,
    Resolution       NVARCHAR(2000) NULL
);
GO

/* 2. MENU ------------------------------------------------------------------------------ */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Branding INT = COALESCE((SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'PLATFORM_BRANDING' ORDER BY ModuleId),
                                 (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'ROLES' ORDER BY ModuleId));   -- no Branding yet: same roles as the Roles screen
DECLARE @Parent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Branding);   -- NULL = top level
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleId = @Branding), 90) + 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'MESSAGING_SETTINGS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('MESSAGING_SETTINGS', N'Email & WhatsApp', N'Mailbox, WhatsApp account and automatic reminders', 1, 0, @Order, @Now, @Parent, '/settings/messaging', 'settings');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/settings/messaging', Icon = 'settings', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'MESSAGING_SETTINGS';

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT r.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoles r
JOIN dbo.Modules m ON m.ModuleCode = 'MESSAGING_SETTINGS'
WHERE (@Branding IS NULL OR EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = @Branding))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Email & WhatsApp granted to ', @@ROWCOUNT, ' role(s).');
GO

/* 3. BUSINESS PANEL MENU "Reminders" (route /notifications) ---------------------------------
   Added to the Business Panel menu catalogue next to Follow-ups. Tick it for each tenant (Control Center >
   Manage menus) or in a plan's modules (Billing > Subscriptions) - the tenant database gets it on the next run. */
IF OBJECT_ID(N'dbo.TenantMenuItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.TenantMenuItems WHERE MenuKey = N'notifications')
BEGIN
    DECLARE @FollowUps INT = (SELECT TOP (1) MenuItemId FROM dbo.TenantMenuItems WHERE Route IN (N'/follow-ups', N'follow-ups') ORDER BY MenuItemId);
    INSERT dbo.TenantMenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
    SELECT N'notifications', N'Reminders', N'notifications', NULL, 1, f.MenuGroup, N'/notifications', ISNULL(f.DisplayOrder, 50) + 1,
           ISNULL(f.ApplicationCode, N'TRACKER'), 0, f.ParentMenuItemId, SYSUTCDATETIME()
    FROM (SELECT 1 AS x) one
    LEFT JOIN dbo.TenantMenuItems f ON f.MenuItemId = @FollowUps;
    PRINT 'Business Panel menu "Reminders" (/notifications) added to the catalogue.';
END;
GO

SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'MESSAGING_SETTINGS';
PRINT 'Email & WhatsApp installed. Sign out and in again to see Settings > Email & WhatsApp in the menu.';
GO
