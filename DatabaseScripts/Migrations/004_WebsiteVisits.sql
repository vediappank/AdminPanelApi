/* =====================================================================================================
   004_WebsiteVisits.sql - website visitor counter (runs by itself when the Admin API starts)

   dbo.WebsiteVisits      one row per visitor per day (visitor = random id kept in the browser;
                          no IP address, name or other personal data is stored)
   dbo.WebsitePageViews   page views per page per day
   Menu                   Website Leads > Visitors  (/website/visitors), granted to every role that has
                          Website Leads (WEBSITE)

   Safe to run again (IF NOT EXISTS everywhere). SQL Server 2016 compatible.
   ===================================================================================================== */

IF OBJECT_ID(N'dbo.WebsiteVisits', N'U') IS NULL
CREATE TABLE dbo.WebsiteVisits
(
    VisitDate    DATE          NOT NULL,
    VisitorId    CHAR(32)      NOT NULL,                 -- random id from the browser (hex)
    LandingPath  NVARCHAR(300) NOT NULL,                 -- first page of the day
    PageViews    INT           NOT NULL CONSTRAINT DF_WebsiteVisits_PageViews DEFAULT (1),
    Source       NVARCHAR(100) NULL,                     -- where they came from: google.com, facebook.com, (direct) ...
    Device       VARCHAR(10)   NULL,                     -- mobile | desktop
    FirstSeenAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_WebsiteVisits_First DEFAULT (SYSUTCDATETIME()),
    LastSeenAt   DATETIME2(0)  NOT NULL CONSTRAINT DF_WebsiteVisits_Last DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_WebsiteVisits PRIMARY KEY CLUSTERED (VisitDate, VisitorId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebsiteVisits_Visitor' AND object_id = OBJECT_ID(N'dbo.WebsiteVisits'))
    CREATE INDEX IX_WebsiteVisits_Visitor ON dbo.WebsiteVisits (VisitorId, VisitDate);
GO

IF OBJECT_ID(N'dbo.WebsitePageViews', N'U') IS NULL
CREATE TABLE dbo.WebsitePageViews
(
    ViewDate  DATE          NOT NULL,
    Path      NVARCHAR(300) NOT NULL,
    Views     INT           NOT NULL CONSTRAINT DF_WebsitePageViews_Views DEFAULT (0),
    CONSTRAINT PK_WebsitePageViews PRIMARY KEY CLUSTERED (ViewDate, Path)
);
GO

/* Menu: Website Leads > Visitors */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE', N'Website Leads', N'Enquiries, CVs, blog and settings for the website', 1, 0, 31, @Now, NULL, NULL, 'mail');

DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_VISITS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE_VISITS', N'Visitors', N'Website visitors per day, top pages and where visitors come from', 1, 0, 6, @Now, @Parent, '/website/visitors', 'insights');

DECLARE @VisitsId INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_VISITS');

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT rm.PlatformRoleId, @VisitsId
FROM dbo.PlatformRoleModules rm
WHERE rm.ModuleId = @Parent
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = @VisitsId);
GO
