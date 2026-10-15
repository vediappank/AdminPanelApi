/* =====================================================================================
   Website Settings - SuperAdminPanel
   Values the public website (blisspointgroup.com) reads at runtime instead of having
   them hard-coded in the HTML. First group: social media links (Facebook, LinkedIn,
   X, Instagram, YouTube) shown in the header, footer, contact page and mobile menu.

     Admin Panel ▸ Website Leads ▸ Website Settings   edit the values
     GET  api/public/web-settings                       website reads them (js/bp-social.js)
     GET/PUT api/platform/web-settings                  Admin Panel

   1. Table     dbo.WebSetting
   2. Procs     usp_WebSetting_List, usp_WebSetting_Save
   3. Seed      the 5 social links (current addresses kept; Instagram / YouTube empty = hidden)
   4. Menu      WEBSITE_SETTINGS under Website Leads, granted to every role that has WEBSITE

   Run AFTER WebsiteLeads_SuperAdminPanel.sql. Safe to re-run: values already edited in
   the Admin Panel are never overwritten by the seed.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. TABLE ----------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.WebSetting', 'U') IS NULL
CREATE TABLE dbo.WebSetting
(
    SettingKey    VARCHAR(60)    NOT NULL CONSTRAINT PK_WebSetting PRIMARY KEY,
    GroupName     VARCHAR(40)    NOT NULL,
    Label         NVARCHAR(100)  NOT NULL,
    SettingValue  NVARCHAR(500)  NULL,                 -- NULL / empty = not set (website hides the icon)
    ValueType     VARCHAR(20)    NOT NULL CONSTRAINT DF_WebSetting_ValueType DEFAULT ('url'),   -- url | text
    Placeholder   NVARCHAR(200)  NULL,
    HelpText      NVARCHAR(300)  NULL,
    DisplayOrder  INT            NOT NULL CONSTRAINT DF_WebSetting_DisplayOrder DEFAULT (0),
    IsPublic      BIT            NOT NULL CONSTRAINT DF_WebSetting_IsPublic DEFAULT (1),        -- returned to the website
    UpdatedAt     DATETIME2(0)   NULL,
    UpdatedBy     NVARCHAR(100)  NULL
);
GO

/* 2. PROCEDURES ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_WebSetting_List
    @PublicOnly BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SettingKey, GroupName, Label, SettingValue, ValueType, Placeholder, HelpText,
           DisplayOrder, IsPublic, UpdatedAt, UpdatedBy
    FROM dbo.WebSetting
    WHERE @PublicOnly = 0 OR IsPublic = 1
    ORDER BY GroupName, DisplayOrder, SettingKey;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_WebSetting_Save
    @SettingKey   VARCHAR(60),
    @SettingValue NVARCHAR(500) = NULL,
    @ChangedBy    NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.WebSetting
       SET SettingValue = NULLIF(LTRIM(RTRIM(@SettingValue)), N''),
           UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
     WHERE SettingKey = @SettingKey;
    IF @@ROWCOUNT = 0 THROW 50404, 'Setting not found.', 1;
END
GO

/* 3. SEED - labels / order are refreshed, values are only set on first insert ----------- */
MERGE dbo.WebSetting AS t
USING (VALUES
    ('social.facebook',  'Social media', N'Facebook',  N'https://www.facebook.com/share/v9NpmiCtNbXsPsaZ/',     N'https://www.facebook.com/your-page',      1),
    ('social.x',         'Social media', N'X (Twitter)', N'https://x.com/BlissPoint5',                         N'https://x.com/your-handle',               2),
    ('social.linkedin',  'Social media', N'LinkedIn',  N'https://www.linkedin.com/in/bliss-point-group-05b9751b4', N'https://www.linkedin.com/company/your-company', 3),
    ('social.instagram', 'Social media', N'Instagram', NULL,                                                   N'https://www.instagram.com/your-handle/',  4),
    ('social.youtube',   'Social media', N'YouTube',   NULL,                                                   N'https://www.youtube.com/@your-channel',   5)
) AS s (SettingKey, GroupName, Label, SettingValue, Placeholder, DisplayOrder)
ON t.SettingKey = s.SettingKey
WHEN MATCHED THEN UPDATE SET GroupName = s.GroupName, Label = s.Label, Placeholder = s.Placeholder, DisplayOrder = s.DisplayOrder,
                             HelpText = N'Leave empty to hide this icon on the website.'
WHEN NOT MATCHED THEN INSERT (SettingKey, GroupName, Label, SettingValue, ValueType, Placeholder, HelpText, DisplayOrder, IsPublic)
                      VALUES (s.SettingKey, s.GroupName, s.Label, s.SettingValue, 'url', s.Placeholder,
                              N'Leave empty to hide this icon on the website.', s.DisplayOrder, 1);
GO

/* 4. MENU - Website Leads ▸ Website Settings ------------------------------------------------ */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

IF @Parent IS NULL
    THROW 50000, 'Run WebsiteLeads_SuperAdminPanel.sql first (Website Leads menu not found).', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_SETTINGS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE_SETTINGS', N'Website Settings', N'Social media links and other values shown on the website', 1, 0, 4, @Now, @Parent, '/website/settings', 'settings');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/website/settings', Icon = 'settings', DisplayOrder = 4, IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'WEBSITE_SETTINGS';

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT rm.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoleModules rm
JOIN dbo.Modules w ON w.ModuleId = rm.ModuleId AND w.ModuleCode = 'WEBSITE'
JOIN dbo.Modules m ON m.ModuleCode = 'WEBSITE_SETTINGS'
WHERE NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Website Settings granted to ', @@ROWCOUNT, ' role(s).');
GO

SELECT SettingKey, Label, SettingValue FROM dbo.WebSetting ORDER BY GroupName, DisplayOrder;
PRINT 'Website Settings installed. Sign out and in again to see the menu.';
GO
