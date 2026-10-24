/* =====================================================================================
   Branding - SuperAdminPanel (safe to run again)
   Adds the Admin Panel menu "Branding" (Settings ▸ Branding, route /settings/branding):
   the Admin Panel's own name and logo (sidebar, login page, footer).

   Company branding for each tenant (name + logo in the Business Panel) is NOT here - it is
   on Tenants ▸ Control Center ▸ Business Panel branding, stored in each tenant's database.

   1. Table   dbo.PlatformBranding (the API also creates it on first use)
   2. Menu    PLATFORM_BRANDING - placed in the same menu group as Roles (or top level),
              granted to every role that has the Roles screen
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. TABLE ----------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.PlatformBranding', N'U') IS NULL
CREATE TABLE dbo.PlatformBranding
(
    BrandingId   INT            NOT NULL CONSTRAINT PK_PlatformBranding PRIMARY KEY CONSTRAINT CK_PlatformBranding_OneRow CHECK (BrandingId = 1),
    CompanyName  NVARCHAR(150)  NULL,
    LogoDataUrl  NVARCHAR(MAX)  NULL,
    UpdatedAt    DATETIME2(0)   NULL,
    UpdatedBy    NVARCHAR(200)  NULL
);
GO

/* 2. MENU ------------------------------------------------------------------------------ */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Roles INT = (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE MenuUrl = '/roles' ORDER BY ModuleId);
DECLARE @Parent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Roles);   -- NULL = top level

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'PLATFORM_BRANDING')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('PLATFORM_BRANDING', N'Branding', N'Admin Panel name and logo', 1, 0, 90, @Now, @Parent, '/settings/branding', 'settings');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/settings/branding', Icon = 'settings', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'PLATFORM_BRANDING';

-- Who may change it: every role that can open the Roles screen (or, if there is no Roles screen, every role).
INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT r.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoles r
JOIN dbo.Modules m ON m.ModuleCode = 'PLATFORM_BRANDING'
WHERE (@Roles IS NULL OR EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = @Roles))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Branding granted to ', @@ROWCOUNT, ' role(s).');
GO

SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'PLATFORM_BRANDING';
PRINT 'Branding installed. Sign out and in again to see Settings > Branding in the menu.';
GO
