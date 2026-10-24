/* =====================================================================================
   Fix Admin Panel menus and access - SuperAdminPanel (safe to run again)
   Found on production (21 Oct 2026):
     1. Settings > Branding (PLATFORM_BRANDING) did not exist - the screen was missing and saving
        the Admin Panel name / logo was refused for everybody.
     2. Reports > Sales & Collections (SALES_CONSOLIDATED_REPORT) was granted to no role - the report
        answered "You don't have access to this section." for everybody.
     3. Email & WhatsApp (MESSAGING_SETTINGS) and Chatbot (CHATBOT_SETTINGS) were granted to all 14 roles,
        Viewer and Support Staff included (they hold the mailbox password) - because Branding was missing
        when those scripts ran. Now only roles that can open the Roles screen keep them.
   Sign out and in again to the Admin Panel afterwards.
   ===================================================================================== */
USE [SuperAdminPanel];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Now   DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Roles INT = COALESCE((SELECT TOP (1) ModuleId FROM dbo.Modules WHERE ModuleCode = 'ROLES' ORDER BY ModuleId),
                              (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE MenuUrl IN ('/roles', '/settings/roles') ORDER BY ModuleId));
DECLARE @Admin INT = (SELECT TOP (1) PlatformRoleId FROM dbo.PlatformRoles WHERE RoleName = N'Administrator' ORDER BY PlatformRoleId);

-- roles that manage the Admin Panel = roles with the Roles screen (always including Administrator)
DECLARE @Managers TABLE (PlatformRoleId INT PRIMARY KEY);
INSERT @Managers SELECT DISTINCT PlatformRoleId FROM dbo.PlatformRoleModules WHERE ModuleId = @Roles;
IF @Admin IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Managers WHERE PlatformRoleId = @Admin) INSERT @Managers VALUES (@Admin);

/* 1. Settings > Branding ---------------------------------------------------------------- */
DECLARE @SettingsParent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Roles);
IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'PLATFORM_BRANDING')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('PLATFORM_BRANDING', N'Branding', N'Admin Panel name and logo', 1, 0, 90, @Now, @SettingsParent, '/settings/branding', 'settings');

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT g.PlatformRoleId, m.ModuleId
FROM @Managers g CROSS JOIN dbo.Modules m
WHERE m.ModuleCode = 'PLATFORM_BRANDING'
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = g.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Branding granted to ', @@ROWCOUNT, ' role(s).');

-- Email & WhatsApp and Chatbot sit next to Branding in the same menu group
UPDATE dbo.Modules SET ParentModuleId = @SettingsParent, ModifiedDate = @Now
WHERE ModuleCode IN ('MESSAGING_SETTINGS', 'CHATBOT_SETTINGS') AND ISNULL(ParentModuleId, -1) <> ISNULL(@SettingsParent, -1);

/* 2. Reports > Sales & Collections ------------------------------------------------------ */
INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT x.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoleModules x
JOIN dbo.Modules mm ON mm.ModuleId = x.ModuleId
CROSS JOIN dbo.Modules m
WHERE m.ModuleCode = 'SALES_CONSOLIDATED_REPORT'
  AND (mm.ModuleCode IN ('REPORTS', 'REPORT_SALES', 'REPORT_PAYMENTS', 'BILLING_PAYMENTS')
       OR mm.MenuUrl IN ('/reports/sales', '/reports/payments', '/billing/payments'))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules y WHERE y.PlatformRoleId = x.PlatformRoleId AND y.ModuleId = m.ModuleId);
PRINT CONCAT('Sales & Collections granted to ', @@ROWCOUNT, ' role(s).');

/* 3. Email & WhatsApp / Chatbot: only roles that manage the Admin Panel ----------------- */
DELETE x
FROM dbo.PlatformRoleModules x
JOIN dbo.Modules m ON m.ModuleId = x.ModuleId
WHERE m.ModuleCode IN ('MESSAGING_SETTINGS', 'CHATBOT_SETTINGS')
  AND NOT EXISTS (SELECT 1 FROM @Managers g WHERE g.PlatformRoleId = x.PlatformRoleId);
PRINT CONCAT('Email & WhatsApp / Chatbot removed from ', @@ROWCOUNT, ' grant(s) of other roles.');

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT g.PlatformRoleId, m.ModuleId
FROM @Managers g CROSS JOIN dbo.Modules m
WHERE m.ModuleCode IN ('MESSAGING_SETTINGS', 'CHATBOT_SETTINGS')
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = g.PlatformRoleId AND x.ModuleId = m.ModuleId);

COMMIT TRANSACTION;
GO

/* Result: who has the new screens now */
SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, r.RoleName
FROM dbo.Modules m
LEFT JOIN dbo.PlatformRoleModules x ON x.ModuleId = m.ModuleId
LEFT JOIN dbo.PlatformRoles r ON r.PlatformRoleId = x.PlatformRoleId
WHERE m.ModuleCode IN ('PLATFORM_BRANDING', 'MESSAGING_SETTINGS', 'CHATBOT_SETTINGS', 'SALES_CONSOLIDATED_REPORT', 'SUBSCRIPTIONS')
ORDER BY m.ModuleCode, r.RoleName;
GO
