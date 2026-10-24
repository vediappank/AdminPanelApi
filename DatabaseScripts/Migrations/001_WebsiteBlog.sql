/* =====================================================================================
   Website Blog - SuperAdminPanel
   Blog posts and categories for the public website (blisspointworld.com), written by staff
   in the Admin Panel instead of being hand-coded as HTML files.

     Admin Panel ▸ Website Leads ▸ Blog            write / publish posts, manage categories
     GET  api/public/blog/categories                 website: category chips with post counts
     GET  api/public/blog/posts?category=&page=      website: blog.html list (+ the govt-updates bell)
     GET  api/public/blog/posts/{slug}               website: blog/post.html?slug=...
     GET  api/public/blog/images/{file}              cover + in-article images
     api/platform/blog/...                           Admin Panel (module WEBSITE_BLOG)

   1. Tables    dbo.BlogCategory, dbo.BlogPost
   2. Seed      starter categories (Government Updates feeds the website's notification bell)
   3. Menu      WEBSITE_BLOG under Website Leads, granted to every role that has WEBSITE

   Set @Email in section 3 to your Admin Panel login, then run (F5) against the database your
   AdminPanelApi connects to (appsettings.Development.json when running locally). Safe to re-run.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. TABLES ------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.BlogCategory', 'U') IS NULL
CREATE TABLE dbo.BlogCategory
(
    BlogCategoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BlogCategory PRIMARY KEY,
    Name           NVARCHAR(80)   NOT NULL,
    Slug           VARCHAR(90)    NOT NULL,
    Description    NVARCHAR(300)  NULL,
    DisplayOrder   INT            NOT NULL CONSTRAINT DF_BlogCategory_DisplayOrder DEFAULT (0),
    IsActive       BIT            NOT NULL CONSTRAINT DF_BlogCategory_IsActive DEFAULT (1),
    CreatedAt      DATETIME2(0)   NOT NULL CONSTRAINT DF_BlogCategory_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt      DATETIME2(0)   NULL,
    CONSTRAINT UQ_BlogCategory_Slug UNIQUE (Slug)
);
GO

IF OBJECT_ID('dbo.BlogPost', 'U') IS NULL
CREATE TABLE dbo.BlogPost
(
    BlogPostId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BlogPost PRIMARY KEY,
    Title           NVARCHAR(200)  NOT NULL,
    Slug            VARCHAR(220)   NOT NULL,
    Summary         NVARCHAR(400)  NULL,                 -- shown on the blog list and as meta description
    ContentHtml     NVARCHAR(MAX)  NOT NULL,
    CoverImage      NVARCHAR(200)  NULL,                 -- file name in Website:BlogImagePath
    BlogCategoryId  INT            NULL CONSTRAINT FK_BlogPost_Category REFERENCES dbo.BlogCategory (BlogCategoryId),
    Tags            NVARCHAR(300)  NULL,                 -- comma separated
    Status          VARCHAR(20)    NOT NULL CONSTRAINT DF_BlogPost_Status DEFAULT ('Draft'),   -- Draft | Published
    PublishedAt     DATETIME2(0)   NULL,                 -- UTC; a future date = scheduled
    AuthorName      NVARCHAR(100)  NULL,
    MetaTitle       NVARCHAR(200)  NULL,
    MetaDescription NVARCHAR(320)  NULL,
    IsFeatured      BIT            NOT NULL CONSTRAINT DF_BlogPost_IsFeatured DEFAULT (0),
    ViewCount       INT            NOT NULL CONSTRAINT DF_BlogPost_ViewCount DEFAULT (0),
    IsDeleted       BIT            NOT NULL CONSTRAINT DF_BlogPost_IsDeleted DEFAULT (0),
    CreatedAt       DATETIME2(0)   NOT NULL CONSTRAINT DF_BlogPost_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy       NVARCHAR(100)  NULL,
    UpdatedAt       DATETIME2(0)   NULL,
    UpdatedBy       NVARCHAR(100)  NULL,
    CONSTRAINT CK_BlogPost_Status CHECK (Status IN ('Draft', 'Published'))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_BlogPost_Slug' AND object_id = OBJECT_ID('dbo.BlogPost'))
    CREATE UNIQUE INDEX UX_BlogPost_Slug ON dbo.BlogPost (Slug) WHERE IsDeleted = 0;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BlogPost_Public' AND object_id = OBJECT_ID('dbo.BlogPost'))
    CREATE INDEX IX_BlogPost_Public ON dbo.BlogPost (Status, IsDeleted, PublishedAt DESC) INCLUDE (BlogCategoryId);
GO

/* 2. SEED - starter categories (edit / add more in the Admin Panel) ---------------------- */
MERGE dbo.BlogCategory AS t
USING (VALUES
    (N'Government Updates', 'government-updates', N'New UAE rules and announcements - also shown in the website''s notification bell.', 1),
    (N'Business Setup',     'business-setup',     N'Mainland, free zone and offshore company formation.', 2),
    (N'Visas',              'visas',              N'Residence, golden, family, employment and visit visas.', 3),
    (N'Accounting & Tax',   'accounting-tax',     N'VAT, corporate tax, bookkeeping and compliance.', 4),
    (N'Typing & Documents', 'typing-documents',   N'Typing, attestation, translation and Emirates ID.', 5),
    (N'IT & CRM',           'it-crm',             N'Websites, digital marketing and Bliss Point CRM.', 6)
) AS s (Name, Slug, Description, DisplayOrder)
ON t.Slug = s.Slug
WHEN NOT MATCHED THEN INSERT (Name, Slug, Description, DisplayOrder) VALUES (s.Name, s.Slug, s.Description, s.DisplayOrder);
GO

/* 3. MENU - Website Leads > Blog ------------------------------------------------------------
   Creates the Blog menu row and grants it. The sidebar only shows a group whose TOP-LEVEL module
   (WEBSITE) is granted to your role, so both WEBSITE and WEBSITE_BLOG are granted below.
   Set @Email to your Admin Panel login to make sure YOUR role gets it (leave as is to skip). */
DECLARE @Email NVARCHAR(200) = N'administrator@blisspointgroup.com';   -- Admin Panel login

DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
PRINT CONCAT('Database: ', DB_NAME());

-- Website Leads parent (created here if the Website Leads script never ran on this database)
IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE', N'Website Leads', N'Enquiries, CVs, blog and settings for the website', 1, 0, 31, @Now, NULL, NULL, 'mail');
ELSE
    UPDATE dbo.Modules SET IsActive = 1, ParentModuleId = NULL, MenuUrl = NULL, ModifiedDate = @Now WHERE ModuleCode = 'WEBSITE';

DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_BLOG')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE_BLOG', N'Blog', N'Write and publish blog posts and government updates on the website', 1, 0, 5, @Now, @Parent, '/website/blog', 'description');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/website/blog', Icon = 'description', DisplayOrder = 5, IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'WEBSITE_BLOG';

DECLARE @BlogId INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_BLOG');

-- a) every role that already has Website Leads gets Blog
INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT rm.PlatformRoleId, @BlogId
FROM dbo.PlatformRoleModules rm
WHERE rm.ModuleId = @Parent
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = @BlogId);
PRINT CONCAT('Blog granted to ', @@ROWCOUNT, ' role(s) that have Website Leads.');

-- b) your own role gets Website Leads + Blog
DECLARE @RoleId INT = (SELECT PlatformRoleId FROM dbo.PlatformUsers WHERE Email = @Email);
IF @RoleId IS NOT NULL
BEGIN
    INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
    SELECT @RoleId, m.ModuleId FROM dbo.Modules m
    WHERE m.ModuleCode IN ('WEBSITE', 'WEBSITE_BLOG')
      AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = @RoleId AND x.ModuleId = m.ModuleId);
    PRINT CONCAT('Granted ', @@ROWCOUNT, ' module(s) to role id ', @RoleId, ' (', @Email, ').');
END
ELSE IF @Email <> N'your-login@email.com'
    PRINT CONCAT('No PlatformUsers row for ', @Email, ' in ', DB_NAME(), ' - wrong database or email.');
GO

/* ---- check ---- */
-- 1. menu rows: WEBSITE has ParentModuleId NULL, WEBSITE_BLOG points to it with MenuUrl /website/blog
SELECT ModuleId, ModuleCode, ModuleName, ParentModuleId, MenuUrl, IsActive
FROM dbo.Modules WHERE ModuleCode IN ('WEBSITE', 'WEBSITE_BLOG');

-- 2. roles that can see Blog (your role must be here, and must also have WEBSITE)
SELECT r.PlatformRoleId, r.RoleName,
       MAX(CASE WHEN m.ModuleCode = 'WEBSITE' THEN 1 ELSE 0 END)      AS HasWebsite,
       MAX(CASE WHEN m.ModuleCode = 'WEBSITE_BLOG' THEN 1 ELSE 0 END) AS HasBlog
FROM dbo.PlatformRoles r
JOIN dbo.PlatformRoleModules rm ON rm.PlatformRoleId = r.PlatformRoleId
JOIN dbo.Modules m ON m.ModuleId = rm.ModuleId AND m.ModuleCode IN ('WEBSITE', 'WEBSITE_BLOG')
GROUP BY r.PlatformRoleId, r.RoleName;

-- 3. categories
SELECT BlogCategoryId, Name, Slug FROM dbo.BlogCategory ORDER BY DisplayOrder;
PRINT 'Website Blog installed. Restart the API, then sign OUT and IN again in the Admin Panel to see Website Leads > Blog.';
GO
