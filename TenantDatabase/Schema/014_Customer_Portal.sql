/*
  014 - Customer Portal: employee logins, company managers, support tickets
  ==========================================================================
  Same script in two places:
    CustomerPanel/sql/003_customer_portal_employees_managers_tickets.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/014_Customer_Portal.sql               run automatically on every tenant database (BP_*)
  Includes everything of CustomerPanel/sql/001 (so a tenant database gets the whole portal schema).
  Needs 009 (tracker members from customer contacts) for employee / family scoping.

  * CustomerContacts (= the company's employees): portal login columns
      PortalLoginEmail, PortalPasswordHash, PortalAccessEnabled (default off), IsPortalManager (default off),
      PortalLastLoginDate. Family members never get a login - they are tracker members, not contacts.
  * CustomerPortalMessages = support tickets: + Category, TrackerId, ModifiedDate (ticket number = TK-<MessageId>).
  * CustomerPortalMessageReplies - the conversation of a ticket (staff and employee replies).
  * Business Panel menu item "Portal Tickets" (/portal-tickets), with the same role permissions as "customers".
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
    RAISERROR(N'SKIPPED - dbo.CustomerContacts does not exist yet (customer management script first).', 10, 1) WITH NOWAIT;
GO

/* ---- 1. Login columns on the employees (customer contacts) ---- */
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'PortalLoginEmail') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD PortalLoginEmail NVARCHAR(254) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'PortalPasswordHash') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD PortalPasswordHash NVARCHAR(200) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'PortalAccessEnabled') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD PortalAccessEnabled BIT NOT NULL
        CONSTRAINT DF_CustomerContacts_PortalAccessEnabled DEFAULT (0) WITH VALUES;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'IsPortalManager') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD IsPortalManager BIT NOT NULL
        CONSTRAINT DF_CustomerContacts_IsPortalManager DEFAULT (0) WITH VALUES;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'PortalLastLoginDate') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD PortalLastLoginDate DATETIME2(7) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerContacts_PortalLoginEmail' AND object_id = OBJECT_ID(N'dbo.CustomerContacts'))
    EXEC sys.sp_executesql N'CREATE INDEX IX_CustomerContacts_PortalLoginEmail ON dbo.CustomerContacts (PortalLoginEmail) WHERE PortalLoginEmail IS NOT NULL;';
GO

/* ---- 2. Support tickets ---- */
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.CustomerPortalMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPortalMessages
    (
        MessageId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerPortalMessages PRIMARY KEY,
        CustomerId   INT NOT NULL,
        ContactId    INT NOT NULL,
        Subject      NVARCHAR(200) NOT NULL,
        MessageText  NVARCHAR(4000) NOT NULL,
        StaffReply   NVARCHAR(4000) NULL,
        Status       NVARCHAR(30) NOT NULL CONSTRAINT DF_CustomerPortalMessages_Status DEFAULT (N'Open'),
        CreatedDate  DATETIME2(7) NOT NULL CONSTRAINT DF_CustomerPortalMessages_CreatedDate DEFAULT (SYSUTCDATETIME()),
        RepliedDate  DATETIME2(7) NULL,
        CONSTRAINT FK_CustomerPortalMessages_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId),
        CONSTRAINT FK_CustomerPortalMessages_Contacts FOREIGN KEY (ContactId) REFERENCES dbo.CustomerContacts (ContactId)
    );
    CREATE INDEX IX_CustomerPortalMessages_CustomerContact_Created ON dbo.CustomerPortalMessages (CustomerId, ContactId, CreatedDate DESC);
END
GO
IF OBJECT_ID(N'dbo.CustomerPortalMessages', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerPortalMessages', N'Category') IS NULL
    ALTER TABLE dbo.CustomerPortalMessages ADD Category NVARCHAR(50) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerPortalMessages', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerPortalMessages', N'TrackerId') IS NULL
    ALTER TABLE dbo.CustomerPortalMessages ADD TrackerId INT NULL;
GO
IF OBJECT_ID(N'dbo.CustomerPortalMessages', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerPortalMessages', N'ModifiedDate') IS NULL
    ALTER TABLE dbo.CustomerPortalMessages ADD ModifiedDate DATETIME2(7) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerPortalMessages', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.CustomerPortalMessageReplies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPortalMessageReplies
    (
        ReplyId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerPortalMessageReplies PRIMARY KEY,
        MessageId    INT NOT NULL,
        AuthorType   NVARCHAR(10) NOT NULL,              -- Staff | Employee
        AuthorName   NVARCHAR(200) NULL,
        UserId       INT NULL,                           -- Business Panel user (staff replies)
        ContactId    INT NULL,                           -- portal employee (employee replies)
        ReplyText    NVARCHAR(4000) NOT NULL,
        CreatedDate  DATETIME2(7) NOT NULL CONSTRAINT DF_CustomerPortalMessageReplies_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_CustomerPortalMessageReplies_Messages FOREIGN KEY (MessageId) REFERENCES dbo.CustomerPortalMessages (MessageId) ON DELETE CASCADE,
        CONSTRAINT CK_CustomerPortalMessageReplies_AuthorType CHECK (AuthorType IN (N'Staff', N'Employee'))
    );
    CREATE INDEX IX_CustomerPortalMessageReplies_Message ON dbo.CustomerPortalMessageReplies (MessageId, CreatedDate);
    /* keep the single staff reply of older tickets as the first reply */
    INSERT dbo.CustomerPortalMessageReplies (MessageId, AuthorType, AuthorName, ReplyText, CreatedDate)
    SELECT MessageId, N'Staff', N'Support team', StaffReply, ISNULL(RepliedDate, CreatedDate)
    FROM dbo.CustomerPortalMessages WHERE NULLIF(LTRIM(RTRIM(StaffReply)), N'') IS NOT NULL;
END
GO

/* ---- 3. Faster employee / family lookups ---- */
IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'SponsorContactId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TrackerFamilyMembers_SponsorContactId' AND object_id = OBJECT_ID(N'dbo.TrackerFamilyMembers'))
    EXEC sys.sp_executesql N'CREATE INDEX IX_TrackerFamilyMembers_SponsorContactId ON dbo.TrackerFamilyMembers (SponsorContactId) WHERE SponsorContactId IS NOT NULL;';
GO

/* ---- 4. Business Panel menu: Portal Tickets (next to Customers), same permissions as Customers ---- */
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'portal-tickets')
BEGIN
    DECLARE @CustomersParent INT, @App NVARCHAR(30), @Group NVARCHAR(100), @Order INT;
    SELECT TOP (1) @CustomersParent = ParentMenuItemId, @App = ApplicationCode, @Group = MenuGroup, @Order = DisplayOrder
    FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'customers' ORDER BY MenuItemId;
    INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
    VALUES (N'portal-tickets', N'Portal Tickets', N'message', NULL, 1, @Group, N'/portal-tickets', ISNULL(@Order, 0) + 1, ISNULL(@App, N'TRACKER'), 0, @CustomersParent, SYSUTCDATETIME());
    RAISERROR(N'CREATED menu item portal-tickets', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'portal-tickets', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'portal-tickets');
GO
