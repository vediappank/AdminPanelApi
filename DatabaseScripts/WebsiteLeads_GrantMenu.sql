/* =====================================================================================
   Website Leads - show the menu for a user (run in SSMS against the SAME database the
   AdminPanelApi you are using is connected to: appsettings.Development.json for local
   runs / ng serve, appsettings.Production.json for adminpanelapi.givingsign.in).

   1. Set @Email to the address you log in to the Admin Panel with.
   2. Run (F5). Read the 4 result grids - they show what was wrong.
   3. Sign OUT of the Admin Panel and sign IN again (the menu list is read at login).
   Safe to re-run.
   ===================================================================================== */
DECLARE @Email NVARCHAR(200) = N'your-login@email.com';   -- <<< change this

SET NOCOUNT ON;
PRINT CONCAT('Database: ', DB_NAME());

DECLARE @UserId INT, @RoleId INT;
SELECT @UserId = PlatformUserId, @RoleId = PlatformRoleId FROM dbo.PlatformUsers WHERE Email = @Email;

IF @UserId IS NULL
BEGIN
    PRINT 'No PlatformUsers row with that email in this database - wrong database or wrong email.';
    SELECT TOP (20) PlatformUserId, Email, Status, PlatformRoleId FROM dbo.PlatformUsers ORDER BY PlatformUserId;
    RETURN;
END
IF @RoleId IS NULL
BEGIN
    PRINT 'This user has no role assigned - assign a role in Admin Panel > Platform users first.';
    RETURN;
END

/* make sure the 5 menu rows exist and are active (in case the install script's menu part did not run here) */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleCode = 'SALES'), 30) + 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE', N'Website Leads', N'Enquiries, CRM requests and CVs from blisspointgroup.com', 1, 0, @Order, @Now, NULL, NULL, 'mail');
ELSE
    UPDATE dbo.Modules SET IsActive = 1, ParentModuleId = NULL, MenuUrl = NULL, ModifiedDate = @Now WHERE ModuleCode = 'WEBSITE';

DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

MERGE dbo.Modules AS t
USING (VALUES
    ('WEBSITE_DASHBOARD', N'Dashboard',        N'Website leads at a glance', 0, '/website/dashboard',        'dashboard'),
    ('WEBSITE_ENQUIRIES', N'Enquiries',        N'Website enquiries, quotes, demos and CRM access requests', 1, '/website/enquiries', 'email'),
    ('WEBSITE_JOBS',      N'Job Applications', N'CVs from the careers page', 2, '/website/job-applications', 'badge'),
    ('WEBSITE_FORMS',     N'Website Forms',    N'Switch forms on/off and set department alert emails', 3, '/website/forms', 'list_alt')
) AS s (ModuleCode, ModuleName, Description, DisplayOrder, MenuUrl, Icon)
ON t.ModuleCode = s.ModuleCode
WHEN MATCHED THEN UPDATE SET ParentModuleId = @Parent, MenuUrl = s.MenuUrl, Icon = s.Icon, DisplayOrder = s.DisplayOrder, IsActive = 1, ModifiedDate = @Now
WHEN NOT MATCHED THEN INSERT (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
                      VALUES (s.ModuleCode, s.ModuleName, s.Description, 1, 0, s.DisplayOrder, @Now, @Parent, s.MenuUrl, s.Icon);

/* grant all 5 to this user's role */
INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT @RoleId, m.ModuleId
FROM dbo.Modules m
WHERE m.ModuleCode IN ('WEBSITE', 'WEBSITE_DASHBOARD', 'WEBSITE_ENQUIRIES', 'WEBSITE_JOBS', 'WEBSITE_FORMS')
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = @RoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Granted ', @@ROWCOUNT, ' new Website Leads module(s) to role id ', @RoleId, '.');

/* ---- results ---- */
-- 1. who you are
SELECT u.PlatformUserId, u.Email, u.Status, r.PlatformRoleId, r.RoleName
FROM dbo.PlatformUsers u LEFT JOIN dbo.PlatformRoles r ON r.PlatformRoleId = u.PlatformRoleId
WHERE u.PlatformUserId = @UserId;

-- 2. the menu rows (WEBSITE must have ParentModuleId NULL; the 4 children must point to it)
SELECT ModuleId, ModuleCode, ModuleName, ParentModuleId, MenuUrl, Icon, DisplayOrder, IsActive
FROM dbo.Modules WHERE ModuleCode LIKE 'WEBSITE%' ORDER BY ParentModuleId, DisplayOrder;

-- 3. your role's Website grants (expect 5 rows)
SELECT m.ModuleCode FROM dbo.PlatformRoleModules rm JOIN dbo.Modules m ON m.ModuleId = rm.ModuleId
WHERE rm.PlatformRoleId = @RoleId AND m.ModuleCode LIKE 'WEBSITE%';

-- 4. procedures installed (expect 18)
SELECT COUNT(*) AS WebsiteLeadProcedures FROM sys.procedures WHERE name LIKE 'usp_Web%';
