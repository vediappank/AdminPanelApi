/*
  022 - Leads and Sales (Bliss CRM v1.1, requirements A2 and A3)
  ==============================================================
  Same script in two places:
    BusinessPanelApi/database/2026-11-17_leads_and_sales.sql               run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/022_Leads_And_Sales.sql           run automatically on every tenant database (BP_*)

  Run AFTER 2026-11-17_employee_vendor_masters.sql (leads and sales point at Employees and Customers).

  * dbo.Leads         LD-0001 (automatic), lead date (automatic), customer link (optional), mobile required, source, assigned
                      employee (required), status, next follow-up, lost reason and customer response.
  * dbo.LeadServices  the services a lead is interested in (one or several, from the Service master).
  * dbo.LeadFollowUps call / WhatsApp / email / meeting notes with the next follow-up date.
  * dbo.SalesOpportunities  SL-0001 (automatic), linked to the lead, the customer (Company master) and the salesperson.
  * dbo.SalesOpportunityServices  the services in a sale (copied from the lead on conversion).
  * usp_Leads_ConvertToSales  turns a lead into a sales opportunity with the same customer; safe to call twice.
  Nothing existing is changed. Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

/* Leads */
IF OBJECT_ID(N'dbo.Leads', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Leads
    (
        LeadId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Leads PRIMARY KEY,
        LeadCode NVARCHAR(20) NULL,
        CustomerId int NULL,
        CompanyName nvarchar(200) NOT NULL,
        ContactPerson nvarchar(150) NULL,
        Mobile nvarchar(20) NOT NULL,
        WhatsApp nvarchar(20) NULL,
        Email nvarchar(150) NULL,
        CompanyLocation nvarchar(100) NULL,
        LeadSource nvarchar(30) NOT NULL,
        LeadSourceOther nvarchar(100) NULL,
        AssignedEmployeeId int NOT NULL,
        LeadStatus nvarchar(40) NOT NULL CONSTRAINT DF_Leads_LeadStatus DEFAULT (N'New'),
        Remarks nvarchar(1000) NULL,
        NextFollowUpAt datetime2(7) NULL,
        CustomerResponse nvarchar(500) NULL,
        LostReason nvarchar(500) NULL,
        LeadDate datetime2(7) NOT NULL CONSTRAINT DF_Leads_LeadDate DEFAULT (SYSUTCDATETIME()),
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_Leads_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_Leads_CustomerId FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId),
        CONSTRAINT FK_Leads_AssignedEmployeeId FOREIGN KEY (AssignedEmployeeId) REFERENCES dbo.Employees (EmployeeId)
    );
    CREATE UNIQUE INDEX UX_Leads_LeadCode ON dbo.Leads (LeadCode) WHERE LeadCode IS NOT NULL;
    CREATE INDEX IX_Leads_Status ON dbo.Leads (LeadStatus);
    CREATE INDEX IX_Leads_AssignedEmployeeId ON dbo.Leads (AssignedEmployeeId);
    RAISERROR(N'CREATED dbo.Leads', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_Leads_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_GetPaged;
GO
CREATE PROCEDURE dbo.usp_Leads_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.Leads;

    SELECT
        LeadId,
        LeadCode,
        CustomerId,
        CompanyName,
        ContactPerson,
        Mobile,
        WhatsApp,
        Email,
        CompanyLocation,
        LeadSource,
        LeadSourceOther,
        AssignedEmployeeId,
        LeadStatus,
        Remarks,
        NextFollowUpAt,
        CustomerResponse,
        LostReason,
        LeadDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.Leads
    ORDER BY LeadId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_Leads_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_GetById;
GO
CREATE PROCEDURE dbo.usp_Leads_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        LeadId,
        LeadCode,
        CustomerId,
        CompanyName,
        ContactPerson,
        Mobile,
        WhatsApp,
        Email,
        CompanyLocation,
        LeadSource,
        LeadSourceOther,
        AssignedEmployeeId,
        LeadStatus,
        Remarks,
        NextFollowUpAt,
        CustomerResponse,
        LostReason,
        LeadDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.Leads
    WHERE LeadId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_Leads_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_Insert;
GO
CREATE PROCEDURE dbo.usp_Leads_Insert
    @CustomerId int = NULL,
    @CompanyName nvarchar(200),
    @ContactPerson nvarchar(150) = NULL,
    @Mobile nvarchar(20),
    @WhatsApp nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @CompanyLocation nvarchar(100) = NULL,
    @LeadSource nvarchar(30),
    @LeadSourceOther nvarchar(100) = NULL,
    @AssignedEmployeeId int,
    @LeadStatus nvarchar(40) = N'New',
    @Remarks nvarchar(1000) = NULL,
    @NextFollowUpAt datetime2(7) = NULL,
    @CustomerResponse nvarchar(500) = NULL,
    @LostReason nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Leads
    (
        CustomerId,
        CompanyName,
        ContactPerson,
        Mobile,
        WhatsApp,
        Email,
        CompanyLocation,
        LeadSource,
        LeadSourceOther,
        AssignedEmployeeId,
        LeadStatus,
        Remarks,
        NextFollowUpAt,
        CustomerResponse,
        LostReason
    )
    VALUES
    (
        @CustomerId,
        @CompanyName,
        @ContactPerson,
        @Mobile,
        @WhatsApp,
        @Email,
        @CompanyLocation,
        @LeadSource,
        @LeadSourceOther,
        @AssignedEmployeeId,
        @LeadStatus,
        @Remarks,
        @NextFollowUpAt,
        @CustomerResponse,
        @LostReason
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    UPDATE dbo.Leads SET LeadCode = N'LD-' + RIGHT(N'0000' + CAST(LeadId AS nvarchar(10)), 4) WHERE LeadId = @NewId;

    SELECT
        LeadId,
        LeadCode,
        CustomerId,
        CompanyName,
        ContactPerson,
        Mobile,
        WhatsApp,
        Email,
        CompanyLocation,
        LeadSource,
        LeadSourceOther,
        AssignedEmployeeId,
        LeadStatus,
        Remarks,
        NextFollowUpAt,
        CustomerResponse,
        LostReason,
        LeadDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.Leads
    WHERE LeadId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_Leads_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_Update;
GO
CREATE PROCEDURE dbo.usp_Leads_Update
    @Id int,
    @CustomerId int = NULL,
    @CompanyName nvarchar(200),
    @ContactPerson nvarchar(150) = NULL,
    @Mobile nvarchar(20),
    @WhatsApp nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @CompanyLocation nvarchar(100) = NULL,
    @LeadSource nvarchar(30),
    @LeadSourceOther nvarchar(100) = NULL,
    @AssignedEmployeeId int,
    @LeadStatus nvarchar(40) = N'New',
    @Remarks nvarchar(1000) = NULL,
    @NextFollowUpAt datetime2(7) = NULL,
    @CustomerResponse nvarchar(500) = NULL,
    @LostReason nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Leads
    SET
        CustomerId = @CustomerId,
        CompanyName = @CompanyName,
        ContactPerson = @ContactPerson,
        Mobile = @Mobile,
        WhatsApp = @WhatsApp,
        Email = @Email,
        CompanyLocation = @CompanyLocation,
        LeadSource = @LeadSource,
        LeadSourceOther = @LeadSourceOther,
        AssignedEmployeeId = @AssignedEmployeeId,
        LeadStatus = @LeadStatus,
        Remarks = @Remarks,
        NextFollowUpAt = @NextFollowUpAt,
        CustomerResponse = @CustomerResponse,
        LostReason = @LostReason,
        ModifiedDate = SYSUTCDATETIME()
    WHERE LeadId = @Id;

    SELECT
        LeadId,
        LeadCode,
        CustomerId,
        CompanyName,
        ContactPerson,
        Mobile,
        WhatsApp,
        Email,
        CompanyLocation,
        LeadSource,
        LeadSourceOther,
        AssignedEmployeeId,
        LeadStatus,
        Remarks,
        NextFollowUpAt,
        CustomerResponse,
        LostReason,
        LeadDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.Leads
    WHERE LeadId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_Leads_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_Delete;
GO
CREATE PROCEDURE dbo.usp_Leads_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.Leads WHERE LeadId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* LeadServices */
IF OBJECT_ID(N'dbo.LeadServices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LeadServices
    (
        LeadServiceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LeadServices PRIMARY KEY,
        LeadId int NOT NULL,
        ServiceId int NOT NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_LeadServices_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_LeadServices_Leads FOREIGN KEY (LeadId) REFERENCES dbo.Leads (LeadId) ON DELETE CASCADE,
        CONSTRAINT FK_LeadServices_Services FOREIGN KEY (ServiceId) REFERENCES dbo.Services (ServiceId)
    );
    CREATE INDEX IX_LeadServices_LeadId ON dbo.LeadServices (LeadId);
    CREATE UNIQUE INDEX UX_LeadServices_LeadService ON dbo.LeadServices (LeadId, ServiceId);
    RAISERROR(N'CREATED dbo.LeadServices', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadServices_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadServices_GetPaged;
GO
CREATE PROCEDURE dbo.usp_LeadServices_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.LeadServices;

    SELECT
        LeadServiceId,
        LeadId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadServices
    ORDER BY LeadServiceId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadServices_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadServices_GetById;
GO
CREATE PROCEDURE dbo.usp_LeadServices_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        LeadServiceId,
        LeadId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadServices
    WHERE LeadServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadServices_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadServices_Insert;
GO
CREATE PROCEDURE dbo.usp_LeadServices_Insert
    @LeadId int,
    @ServiceId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.LeadServices
    (
        LeadId,
        ServiceId
    )
    VALUES
    (
        @LeadId,
        @ServiceId
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        LeadServiceId,
        LeadId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadServices
    WHERE LeadServiceId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadServices_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadServices_Update;
GO
CREATE PROCEDURE dbo.usp_LeadServices_Update
    @Id int,
    @LeadId int,
    @ServiceId int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.LeadServices
    SET
        LeadId = @LeadId,
        ServiceId = @ServiceId,
        ModifiedDate = SYSUTCDATETIME()
    WHERE LeadServiceId = @Id;

    SELECT
        LeadServiceId,
        LeadId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadServices
    WHERE LeadServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadServices_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadServices_Delete;
GO
CREATE PROCEDURE dbo.usp_LeadServices_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.LeadServices WHERE LeadServiceId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* LeadFollowUps */
IF OBJECT_ID(N'dbo.LeadFollowUps', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LeadFollowUps
    (
        LeadFollowUpId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LeadFollowUps PRIMARY KEY,
        LeadId int NOT NULL,
        ContactType nvarchar(20) NOT NULL CONSTRAINT DF_LeadFollowUps_ContactType DEFAULT (N'Call'),
        Notes nvarchar(1000) NOT NULL,
        ContactedAt datetime2(7) NOT NULL,
        NextFollowUpAt datetime2(7) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_LeadFollowUps_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_LeadFollowUps_Leads FOREIGN KEY (LeadId) REFERENCES dbo.Leads (LeadId) ON DELETE CASCADE
    );
    CREATE INDEX IX_LeadFollowUps_LeadId ON dbo.LeadFollowUps (LeadId);
    RAISERROR(N'CREATED dbo.LeadFollowUps', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadFollowUps_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadFollowUps_GetPaged;
GO
CREATE PROCEDURE dbo.usp_LeadFollowUps_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.LeadFollowUps;

    SELECT
        LeadFollowUpId,
        LeadId,
        ContactType,
        Notes,
        ContactedAt,
        NextFollowUpAt,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadFollowUps
    ORDER BY LeadFollowUpId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadFollowUps_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadFollowUps_GetById;
GO
CREATE PROCEDURE dbo.usp_LeadFollowUps_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        LeadFollowUpId,
        LeadId,
        ContactType,
        Notes,
        ContactedAt,
        NextFollowUpAt,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadFollowUps
    WHERE LeadFollowUpId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadFollowUps_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadFollowUps_Insert;
GO
CREATE PROCEDURE dbo.usp_LeadFollowUps_Insert
    @LeadId int,
    @ContactType nvarchar(20) = N'Call',
    @Notes nvarchar(1000),
    @ContactedAt datetime2(7),
    @NextFollowUpAt datetime2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.LeadFollowUps
    (
        LeadId,
        ContactType,
        Notes,
        ContactedAt,
        NextFollowUpAt
    )
    VALUES
    (
        @LeadId,
        @ContactType,
        @Notes,
        @ContactedAt,
        @NextFollowUpAt
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        LeadFollowUpId,
        LeadId,
        ContactType,
        Notes,
        ContactedAt,
        NextFollowUpAt,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadFollowUps
    WHERE LeadFollowUpId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadFollowUps_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadFollowUps_Update;
GO
CREATE PROCEDURE dbo.usp_LeadFollowUps_Update
    @Id int,
    @LeadId int,
    @ContactType nvarchar(20) = N'Call',
    @Notes nvarchar(1000),
    @ContactedAt datetime2(7),
    @NextFollowUpAt datetime2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.LeadFollowUps
    SET
        LeadId = @LeadId,
        ContactType = @ContactType,
        Notes = @Notes,
        ContactedAt = @ContactedAt,
        NextFollowUpAt = @NextFollowUpAt,
        ModifiedDate = SYSUTCDATETIME()
    WHERE LeadFollowUpId = @Id;

    SELECT
        LeadFollowUpId,
        LeadId,
        ContactType,
        Notes,
        ContactedAt,
        NextFollowUpAt,
        CreatedDate,
        ModifiedDate
    FROM dbo.LeadFollowUps
    WHERE LeadFollowUpId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_LeadFollowUps_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_LeadFollowUps_Delete;
GO
CREATE PROCEDURE dbo.usp_LeadFollowUps_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.LeadFollowUps WHERE LeadFollowUpId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* SalesOpportunities */
IF OBJECT_ID(N'dbo.SalesOpportunities', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesOpportunities
    (
        SalesOpportunityId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOpportunities PRIMARY KEY,
        SalesCode NVARCHAR(20) NULL,
        LeadId int NULL,
        CustomerId int NOT NULL,
        SalespersonEmployeeId int NOT NULL,
        TradeLicenceNo nvarchar(60) NULL,
        CustomerRequirements nvarchar(2000) NULL,
        EmployeeCount int NULL,
        ExpectedCompletionDate date NULL,
        EstimatedValue decimal(12,2) NULL,
        Priority nvarchar(10) NOT NULL CONSTRAINT DF_SalesOpportunities_Priority DEFAULT (N'Normal'),
        SalesStatus nvarchar(40) NOT NULL CONSTRAINT DF_SalesOpportunities_SalesStatus DEFAULT (N'New'),
        QuotationStatus nvarchar(60) NULL,
        Remarks nvarchar(1000) NULL,
        EnquiryDate datetime2(7) NOT NULL CONSTRAINT DF_SalesOpportunities_EnquiryDate DEFAULT (SYSUTCDATETIME()),
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_SalesOpportunities_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_SalesOpportunities_LeadId FOREIGN KEY (LeadId) REFERENCES dbo.Leads (LeadId),
        CONSTRAINT FK_SalesOpportunities_CustomerId FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId),
        CONSTRAINT FK_SalesOpportunities_SalespersonEmployeeId FOREIGN KEY (SalespersonEmployeeId) REFERENCES dbo.Employees (EmployeeId)
    );
    CREATE UNIQUE INDEX UX_SalesOpportunities_SalesCode ON dbo.SalesOpportunities (SalesCode) WHERE SalesCode IS NOT NULL;
    CREATE INDEX IX_SalesOpportunities_Lead ON dbo.SalesOpportunities (LeadId);
    RAISERROR(N'CREATED dbo.SalesOpportunities', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunities_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunities_GetPaged;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunities_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.SalesOpportunities;

    SELECT
        SalesOpportunityId,
        SalesCode,
        LeadId,
        CustomerId,
        SalespersonEmployeeId,
        TradeLicenceNo,
        CustomerRequirements,
        EmployeeCount,
        ExpectedCompletionDate,
        EstimatedValue,
        Priority,
        SalesStatus,
        QuotationStatus,
        Remarks,
        EnquiryDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunities
    ORDER BY SalesOpportunityId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunities_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunities_GetById;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunities_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        SalesOpportunityId,
        SalesCode,
        LeadId,
        CustomerId,
        SalespersonEmployeeId,
        TradeLicenceNo,
        CustomerRequirements,
        EmployeeCount,
        ExpectedCompletionDate,
        EstimatedValue,
        Priority,
        SalesStatus,
        QuotationStatus,
        Remarks,
        EnquiryDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunities
    WHERE SalesOpportunityId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunities_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunities_Insert;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunities_Insert
    @LeadId int = NULL,
    @CustomerId int,
    @SalespersonEmployeeId int,
    @TradeLicenceNo nvarchar(60) = NULL,
    @CustomerRequirements nvarchar(2000) = NULL,
    @EmployeeCount int = NULL,
    @ExpectedCompletionDate date = NULL,
    @EstimatedValue decimal(12,2) = NULL,
    @Priority nvarchar(10) = N'Normal',
    @SalesStatus nvarchar(40) = N'New',
    @QuotationStatus nvarchar(60) = NULL,
    @Remarks nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.SalesOpportunities
    (
        LeadId,
        CustomerId,
        SalespersonEmployeeId,
        TradeLicenceNo,
        CustomerRequirements,
        EmployeeCount,
        ExpectedCompletionDate,
        EstimatedValue,
        Priority,
        SalesStatus,
        QuotationStatus,
        Remarks
    )
    VALUES
    (
        @LeadId,
        @CustomerId,
        @SalespersonEmployeeId,
        @TradeLicenceNo,
        @CustomerRequirements,
        @EmployeeCount,
        @ExpectedCompletionDate,
        @EstimatedValue,
        @Priority,
        @SalesStatus,
        @QuotationStatus,
        @Remarks
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    UPDATE dbo.SalesOpportunities SET SalesCode = N'SL-' + RIGHT(N'0000' + CAST(SalesOpportunityId AS nvarchar(10)), 4) WHERE SalesOpportunityId = @NewId;

    SELECT
        SalesOpportunityId,
        SalesCode,
        LeadId,
        CustomerId,
        SalespersonEmployeeId,
        TradeLicenceNo,
        CustomerRequirements,
        EmployeeCount,
        ExpectedCompletionDate,
        EstimatedValue,
        Priority,
        SalesStatus,
        QuotationStatus,
        Remarks,
        EnquiryDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunities
    WHERE SalesOpportunityId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunities_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunities_Update;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunities_Update
    @Id int,
    @LeadId int = NULL,
    @CustomerId int,
    @SalespersonEmployeeId int,
    @TradeLicenceNo nvarchar(60) = NULL,
    @CustomerRequirements nvarchar(2000) = NULL,
    @EmployeeCount int = NULL,
    @ExpectedCompletionDate date = NULL,
    @EstimatedValue decimal(12,2) = NULL,
    @Priority nvarchar(10) = N'Normal',
    @SalesStatus nvarchar(40) = N'New',
    @QuotationStatus nvarchar(60) = NULL,
    @Remarks nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.SalesOpportunities
    SET
        LeadId = @LeadId,
        CustomerId = @CustomerId,
        SalespersonEmployeeId = @SalespersonEmployeeId,
        TradeLicenceNo = @TradeLicenceNo,
        CustomerRequirements = @CustomerRequirements,
        EmployeeCount = @EmployeeCount,
        ExpectedCompletionDate = @ExpectedCompletionDate,
        EstimatedValue = @EstimatedValue,
        Priority = @Priority,
        SalesStatus = @SalesStatus,
        QuotationStatus = @QuotationStatus,
        Remarks = @Remarks,
        ModifiedDate = SYSUTCDATETIME()
    WHERE SalesOpportunityId = @Id;

    SELECT
        SalesOpportunityId,
        SalesCode,
        LeadId,
        CustomerId,
        SalespersonEmployeeId,
        TradeLicenceNo,
        CustomerRequirements,
        EmployeeCount,
        ExpectedCompletionDate,
        EstimatedValue,
        Priority,
        SalesStatus,
        QuotationStatus,
        Remarks,
        EnquiryDate,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunities
    WHERE SalesOpportunityId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunities_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunities_Delete;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunities_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.SalesOpportunities WHERE SalesOpportunityId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* SalesOpportunityServices */
IF OBJECT_ID(N'dbo.SalesOpportunityServices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesOpportunityServices
    (
        SalesOpportunityServiceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOpportunityServices PRIMARY KEY,
        SalesOpportunityId int NOT NULL,
        ServiceId int NOT NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_SalesOpportunityServices_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_SalesOpportunityServices_SalesOpportunities FOREIGN KEY (SalesOpportunityId) REFERENCES dbo.SalesOpportunities (SalesOpportunityId) ON DELETE CASCADE,
        CONSTRAINT FK_SalesOpportunityServices_Services FOREIGN KEY (ServiceId) REFERENCES dbo.Services (ServiceId)
    );
    CREATE INDEX IX_SalesOpportunityServices_SalesOpportunityId ON dbo.SalesOpportunityServices (SalesOpportunityId);
    CREATE UNIQUE INDEX UX_SalesOppServices ON dbo.SalesOpportunityServices (SalesOpportunityId, ServiceId);
    RAISERROR(N'CREATED dbo.SalesOpportunityServices', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunityServices_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunityServices_GetPaged;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunityServices_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.SalesOpportunityServices;

    SELECT
        SalesOpportunityServiceId,
        SalesOpportunityId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunityServices
    ORDER BY SalesOpportunityServiceId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunityServices_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunityServices_GetById;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunityServices_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        SalesOpportunityServiceId,
        SalesOpportunityId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunityServices
    WHERE SalesOpportunityServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunityServices_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunityServices_Insert;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunityServices_Insert
    @SalesOpportunityId int,
    @ServiceId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.SalesOpportunityServices
    (
        SalesOpportunityId,
        ServiceId
    )
    VALUES
    (
        @SalesOpportunityId,
        @ServiceId
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        SalesOpportunityServiceId,
        SalesOpportunityId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunityServices
    WHERE SalesOpportunityServiceId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunityServices_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunityServices_Update;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunityServices_Update
    @Id int,
    @SalesOpportunityId int,
    @ServiceId int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.SalesOpportunityServices
    SET
        SalesOpportunityId = @SalesOpportunityId,
        ServiceId = @ServiceId,
        ModifiedDate = SYSUTCDATETIME()
    WHERE SalesOpportunityServiceId = @Id;

    SELECT
        SalesOpportunityServiceId,
        SalesOpportunityId,
        ServiceId,
        CreatedDate,
        ModifiedDate
    FROM dbo.SalesOpportunityServices
    WHERE SalesOpportunityServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_SalesOpportunityServices_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_SalesOpportunityServices_Delete;
GO
CREATE PROCEDURE dbo.usp_SalesOpportunityServices_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.SalesOpportunityServices WHERE SalesOpportunityServiceId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* ---- Lead -> Sales conversion ---- */
IF OBJECT_ID(N'dbo.usp_Leads_ConvertToSales', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Leads_ConvertToSales;
GO
CREATE PROCEDURE dbo.usp_Leads_ConvertToSales
    @LeadId INT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @CustomerId INT, @Employee INT, @Remarks NVARCHAR(1000), @Existing INT;

    SELECT @Existing = MIN(SalesOpportunityId) FROM dbo.SalesOpportunities WHERE LeadId = @LeadId;
    IF @Existing IS NOT NULL
    BEGIN   -- already converted: hand back the same sale, never create a second one
        SELECT SalesOpportunityId, SalesCode FROM dbo.SalesOpportunities WHERE SalesOpportunityId = @Existing;
        RETURN;
    END;

    SELECT @CustomerId = CustomerId, @Employee = AssignedEmployeeId, @Remarks = Remarks FROM dbo.Leads WHERE LeadId = @LeadId;
    IF @Employee IS NULL THROW 50010, 'The lead was not found.', 1;
    IF @CustomerId IS NULL THROW 50012, 'Link the lead to a customer (company) before converting it to a sale.', 1;

    BEGIN TRANSACTION;
        INSERT dbo.SalesOpportunities (LeadId, CustomerId, SalespersonEmployeeId, CustomerRequirements, Priority, SalesStatus)
        VALUES (@LeadId, @CustomerId, @Employee, @Remarks, N'Normal', N'New');
        DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
        UPDATE dbo.SalesOpportunities SET SalesCode = N'SL-' + RIGHT(N'0000' + CAST(@NewId AS NVARCHAR(10)), 4) WHERE SalesOpportunityId = @NewId;
        INSERT dbo.SalesOpportunityServices (SalesOpportunityId, ServiceId)
            SELECT @NewId, ServiceId FROM dbo.LeadServices WHERE LeadId = @LeadId;
        UPDATE dbo.Leads SET LeadStatus = N'Converted to Sales', ModifiedDate = SYSUTCDATETIME() WHERE LeadId = @LeadId;
    COMMIT TRANSACTION;

    SELECT SalesOpportunityId, SalesCode FROM dbo.SalesOpportunities WHERE SalesOpportunityId = @NewId;
END
GO

/* ---- Menu: its own 'Leads & Sales' section ---- */
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
BEGIN
    DECLARE @App NVARCHAR(30), @Parent INT, @Max INT;
    SELECT TOP (1) @App = ApplicationCode FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'customers' ORDER BY MenuItemId;
    SELECT @Max = ISNULL(MAX(DisplayOrder), 0) FROM dbo.MenuItems;
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'leads-sales')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'leads-sales', N'Leads & Sales', N'people_alt', NULL, 1, N'Leads & Sales', NULL, @Max + 1, ISNULL(@App, N'TRACKER'), 0, NULL, SYSUTCDATETIME());
    SELECT @Parent = MenuItemId FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'leads-sales';
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'leads')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'leads', N'Leads', N'people_alt', NULL, 1, N'Leads & Sales', N'/leads', 1, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'lead-follow-ups')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'lead-follow-ups', N'Lead Follow-ups', N'followups', NULL, 1, N'Leads & Sales', N'/lead-follow-ups', 2, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'lead-services')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'lead-services', N'Lead Services', N'list_alt', NULL, 1, N'Leads & Sales', N'/lead-services', 3, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sales')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'sales', N'Sales', N'shopping_cart', NULL, 1, N'Leads & Sales', N'/sales', 4, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sales-services')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'sales-services', N'Sales Services', N'list_alt', NULL, 1, N'Leads & Sales', N'/sales-services', 5, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
BEGIN
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'leads-sales', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'leads-sales');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'leads', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'leads');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'lead-follow-ups', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'lead-follow-ups');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'lead-services', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'lead-services');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'sales', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'sales');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'sales-services', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'customers'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'sales-services');
END
GO
