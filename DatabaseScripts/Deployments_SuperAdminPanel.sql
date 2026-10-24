/* =====================================================================================
   Settings > Deployments - SuperAdminPanel (safe to run again)

   Shows which build of the website, Admin Panel, Admin API, Business Panel and Business API
   is live, and every build seen before.

     Admin Panel > Settings > Deployments      the screen
     GET api/platform/deployments              checks the five sites, records new builds
     dbo.DeploymentHistory                     one row per build first seen live (also created by the API)

   1. Table   dbo.DeploymentHistory
   2. Menu    DEPLOYMENTS (/settings/deployments) in the Settings group, granted to the roles that
              manage the Admin Panel (roles with the Roles screen, always including Administrator)

   Sign out and in again to the Admin Panel afterwards.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. TABLE --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.DeploymentHistory', N'U') IS NULL
CREATE TABLE dbo.DeploymentHistory
(
    DeploymentHistoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeploymentHistory PRIMARY KEY,
    App           VARCHAR(40)    NOT NULL,          -- website | adminpanel | adminpanelapi | businesspanel | businesspanelapi
    Version       VARCHAR(40)    NOT NULL,          -- yyyy.MM.dd.HHmm of the publish
    CommitHash    VARCHAR(40)    NULL,
    Branch        NVARCHAR(100)  NULL,
    Dirty         BIT            NOT NULL CONSTRAINT DF_DeploymentHistory_Dirty DEFAULT (0),   -- built with uncommitted changes
    CommitMessage NVARCHAR(300)  NULL,
    BuiltAt       DATETIME2(0)   NULL,              -- UTC
    BuiltBy       NVARCHAR(100)  NULL,
    FirstSeenAt   DATETIME2(0)   NOT NULL CONSTRAINT DF_DeploymentHistory_FirstSeen DEFAULT (SYSUTCDATETIME()),   -- ~ when it went live
    CONSTRAINT UQ_DeploymentHistory_AppVersion UNIQUE (App, Version)
);
GO

/* 2. MENU - Settings > Deployments ----------------------------------------------------- */
DECLARE @Now   DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Roles INT = COALESCE((SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'ROLES' ORDER BY ModuleId),
                              (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE MenuUrl IN ('/roles', '/settings/roles') ORDER BY ModuleId));
DECLARE @SettingsParent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Roles);
DECLARE @Admin INT = (SELECT TOP (1) PlatformRoleId FROM dbo.PlatformRoles WHERE RoleName = N'Administrator' ORDER BY PlatformRoleId);

IF @SettingsParent IS NULL
    THROW 50000, 'Settings menu group not found (the Roles screen has no parent module).', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'DEPLOYMENTS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('DEPLOYMENTS', N'Deployments', N'Which build of the website, panels and APIs is live, and the history of builds', 1, 0, 95, @Now, @SettingsParent, '/settings/deployments', 'settings');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @SettingsParent, MenuUrl = '/settings/deployments', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'DEPLOYMENTS';

DECLARE @Managers TABLE (PlatformRoleId INT PRIMARY KEY);
INSERT @Managers SELECT DISTINCT PlatformRoleId FROM dbo.PlatformRoleModules WHERE ModuleId = @Roles;
IF @Admin IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Managers WHERE PlatformRoleId = @Admin) INSERT @Managers VALUES (@Admin);

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT g.PlatformRoleId, m.ModuleId
FROM @Managers g CROSS JOIN dbo.Modules m
WHERE m.ModuleCode = 'DEPLOYMENTS'
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = g.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Deployments granted to ', @@ROWCOUNT, ' role(s).');
GO

SELECT m.ModuleId, m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup,
       (SELECT COUNT(*) FROM dbo.PlatformRoleModules x WHERE x.ModuleId = m.ModuleId) AS Roles
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'DEPLOYMENTS';
PRINT 'Done. Sign out and in again to the Admin Panel to see Settings > Deployments.';
GO
