/*
  019 - Customer feedback (Customer Portal -> Business Panel -> website)
  ======================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-11-02_customer_feedback.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/019_Customer_Feedback.sql   run automatically on every tenant database (BP_*)

  * dbo.CustomerFeedback - one row per employee (portal login) and service entry (tracker + sub-category):
      Rating 1-5 and a comment, given in the Customer Portal when the tracker is Completed.
      Skipped = 1: the employee chose "Don't ask again" (no rating).
      AllowPublic = 1: the employee agreed that Bliss Point may show the feedback, with DisplayName, on its website.
      IsPublished = 1: a Business Panel user chose to show it on the website (only possible when AllowPublic = 1).
  * Business Panel menu item "Customer Feedback" (/customer-feedback) next to Customers, same role permissions as Customers.
  Nothing existing is changed. Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.CustomerFeedback', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerFeedback
    (
        FeedbackId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerFeedback PRIMARY KEY,
        CustomerId      INT            NOT NULL,
        ContactId       INT            NOT NULL,          -- the employee (customer contact) who gave it
        TrackerId       INT            NOT NULL,
        SpecId          INT            NOT NULL,          -- TrackerCategorySubCategorySpecId (the sub-category entry)
        CategoryName    NVARCHAR(200)  NULL,
        SubCategoryName NVARCHAR(200)  NULL,
        PersonNames     NVARCHAR(500)  NULL,              -- whose services they were (the employee / family members)
        Rating          TINYINT        NULL,              -- 1-5; NULL when Skipped
        Comment         NVARCHAR(1000) NULL,
        Skipped         BIT            NOT NULL CONSTRAINT DF_CustomerFeedback_Skipped DEFAULT (0),
        AllowPublic     BIT            NOT NULL CONSTRAINT DF_CustomerFeedback_AllowPublic DEFAULT (0),
        DisplayName     NVARCHAR(150)  NULL,              -- how the employee agreed to be named on the website
        IsPublished     BIT            NOT NULL CONSTRAINT DF_CustomerFeedback_IsPublished DEFAULT (0),
        PublishedDate   DATETIME2(0)   NULL,
        PublishedBy     NVARCHAR(150)  NULL,
        StaffNote       NVARCHAR(500)  NULL,
        CreatedDate     DATETIME2(0)   NOT NULL CONSTRAINT DF_CustomerFeedback_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate    DATETIME2(0)   NULL,
        CONSTRAINT CK_CustomerFeedback_Rating CHECK (Rating IS NULL OR Rating BETWEEN 1 AND 5),
        CONSTRAINT CK_CustomerFeedback_Publish CHECK (IsPublished = 0 OR (AllowPublic = 1 AND Rating IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_CustomerFeedback_Contact_Entry ON dbo.CustomerFeedback (ContactId, TrackerId, SpecId);
    CREATE INDEX IX_CustomerFeedback_Customer ON dbo.CustomerFeedback (CustomerId, CreatedDate);
    CREATE INDEX IX_CustomerFeedback_Published ON dbo.CustomerFeedback (IsPublished, Rating) INCLUDE (PublishedDate);
    RAISERROR(N'CREATED dbo.CustomerFeedback', 10, 1) WITH NOWAIT;
END
GO

/* ---- Business Panel menu: Customer Feedback (next to Customers), same permissions as Customers ---- */
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'customer-feedback')
BEGIN
    DECLARE @CustomersParent INT, @App NVARCHAR(30), @Group NVARCHAR(100), @Order INT;
    SELECT TOP (1) @CustomersParent = ParentMenuItemId, @App = ApplicationCode, @Group = MenuGroup, @Order = DisplayOrder
    FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'customers' ORDER BY MenuItemId;
    INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
    VALUES (N'customer-feedback', N'Customer Feedback', N'star', NULL, 1, @Group, N'/customer-feedback', ISNULL(@Order, 0) + 2, ISNULL(@App, N'TRACKER'), 0, @CustomersParent, SYSUTCDATETIME());
    RAISERROR(N'CREATED menu item customer-feedback', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'customer-feedback', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'customer-feedback');
GO
