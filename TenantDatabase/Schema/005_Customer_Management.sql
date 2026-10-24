/*
  005 - Customer Management (Customers screen: profile, contacts, requirements, service history,
        communications, status history)
  Same script in two places:
    BusinessPanelApi/database/customer-management-upgrade.sql          run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/005_Customer_Management.sql    run automatically on every tenant database (BP_*)
  Safe to run more than once (every object is created only when missing).
*/
/* BusinessPanel Customer Management extension - SQL Server 2016 compatible.
   Run against each tenant/company database after the core BusinessPanel schema is installed.
   Does not alter existing Customers, Trackers, Quotations, Payments or FollowUps tables. */
SET XACT_ABORT ON;
-- (no BEGIN/COMMIT here: the tenant migration worker already runs each script in one transaction)
IF OBJECT_ID('dbo.CustomerProfiles','U') IS NULL
CREATE TABLE dbo.CustomerProfiles(
 CustomerId int NOT NULL CONSTRAINT PK_CustomerProfiles PRIMARY KEY,
 Nationality nvarchar(100) NULL, SourceOfCustomer nvarchar(100) NULL, FirstContactDate date NULL,
 TradeLicenceNo nvarchar(100) NULL, LicenceIssueDate date NULL, LicenceExpiryDate date NULL,
 EstablishmentCardExpiry date NULL, UnemploymentInsuranceNo nvarchar(100) NULL,
 UnemploymentInsuranceExpiry date NULL, Notes nvarchar(max) NULL,
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerProfiles_CreatedDate DEFAULT SYSUTCDATETIME(),
 ModifiedDate datetime2(7) NULL,
 CONSTRAINT FK_CustomerProfiles_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE);
IF OBJECT_ID('dbo.CustomerContacts','U') IS NULL
CREATE TABLE dbo.CustomerContacts(
 ContactId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerContacts PRIMARY KEY,
 CustomerId int NOT NULL, ContactName nvarchar(200) NOT NULL, PositionTitle nvarchar(100) NULL, Nationality nvarchar(100) NULL,
 Email nvarchar(254) NULL, Mobile nvarchar(30) NULL, PassportNumber nvarchar(80) NULL, PassportExpiry date NULL,
 VisaExpiry date NULL, LabourCardNumber nvarchar(80) NULL, LabourCardExpiry date NULL, ResidencyExpiry date NULL,
 HealthInsuranceIssued bit NOT NULL CONSTRAINT DF_CustomerContacts_InsuranceIssued DEFAULT(0), HealthInsuranceExpiry date NULL,
 DateOfBirth date NULL, IsActive bit NOT NULL CONSTRAINT DF_CustomerContacts_IsActive DEFAULT(1),
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerContacts_CreatedDate DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_CustomerContacts_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE);
IF OBJECT_ID('dbo.CustomerRequirements','U') IS NULL
CREATE TABLE dbo.CustomerRequirements(
 RequirementId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerRequirements PRIMARY KEY, CustomerId int NOT NULL,
 NeedDescription nvarchar(500) NULL, ServiceName nvarchar(200) NULL, ApplicationCount int NULL,
 SpecialRequirements nvarchar(max) NULL, ExpectedCompletion date NULL, CustomerBudget decimal(18,2) NULL, Instructions nvarchar(max) NULL,
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerRequirements_CreatedDate DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_CustomerRequirements_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE,
 CONSTRAINT CK_CustomerRequirements_ApplicationCount CHECK(ApplicationCount IS NULL OR ApplicationCount>=0),
 CONSTRAINT CK_CustomerRequirements_Budget CHECK(CustomerBudget IS NULL OR CustomerBudget>=0));
IF OBJECT_ID('dbo.CustomerServiceHistory','U') IS NULL
CREATE TABLE dbo.CustomerServiceHistory(
 ServiceHistoryId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerServiceHistory PRIMARY KEY, CustomerId int NOT NULL,
 ServiceName nvarchar(200) NULL, RequestedDate date NULL, StaffResponsible nvarchar(150) NULL,
 QuotationAmount decimal(18,2) NULL, PaymentAmount decimal(18,2) NULL, SubmissionDate date NULL, Status nvarchar(100) NULL,
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerServiceHistory_CreatedDate DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_CustomerServiceHistory_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE,
 CONSTRAINT CK_CustomerServiceHistory_Quotation CHECK(QuotationAmount IS NULL OR QuotationAmount>=0),
 CONSTRAINT CK_CustomerServiceHistory_Payment CHECK(PaymentAmount IS NULL OR PaymentAmount>=0));
IF OBJECT_ID('dbo.CustomerCommunications','U') IS NULL
CREATE TABLE dbo.CustomerCommunications(
 CommunicationId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerCommunications PRIMARY KEY, CustomerId int NOT NULL,
 ContactDate datetime2(7) NULL, ContactMethod nvarchar(50) NULL, Purpose nvarchar(250) NULL,
 CustomerMessage nvarchar(max) NULL, OurResponse nvarchar(max) NULL, ActionRequired nvarchar(max) NULL,
 NextFollowUp date NULL, StaffName nvarchar(150) NULL,
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerCommunications_CreatedDate DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_CustomerCommunications_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE);
IF OBJECT_ID('dbo.CustomerStatusHistory','U') IS NULL
CREATE TABLE dbo.CustomerStatusHistory(
 StatusHistoryId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerStatusHistory PRIMARY KEY, CustomerId int NOT NULL,
 Status nvarchar(100) NOT NULL, StatusDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerStatusHistory_StatusDate DEFAULT SYSUTCDATETIME(), Notes nvarchar(1000) NULL,
 CreatedDate datetime2(7) NOT NULL CONSTRAINT DF_CustomerStatusHistory_CreatedDate DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_CustomerStatusHistory_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId) ON DELETE CASCADE);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerContacts_CustomerId' AND object_id=OBJECT_ID('dbo.CustomerContacts')) CREATE INDEX IX_CustomerContacts_CustomerId ON dbo.CustomerContacts(CustomerId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerRequirements_CustomerId' AND object_id=OBJECT_ID('dbo.CustomerRequirements')) CREATE INDEX IX_CustomerRequirements_CustomerId ON dbo.CustomerRequirements(CustomerId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerServiceHistory_CustomerId' AND object_id=OBJECT_ID('dbo.CustomerServiceHistory')) CREATE INDEX IX_CustomerServiceHistory_CustomerId ON dbo.CustomerServiceHistory(CustomerId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerCommunications_CustomerId_NextFollowUp' AND object_id=OBJECT_ID('dbo.CustomerCommunications')) CREATE INDEX IX_CustomerCommunications_CustomerId_NextFollowUp ON dbo.CustomerCommunications(CustomerId,NextFollowUp);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerStatusHistory_CustomerId_StatusDate' AND object_id=OBJECT_ID('dbo.CustomerStatusHistory')) CREATE INDEX IX_CustomerStatusHistory_CustomerId_StatusDate ON dbo.CustomerStatusHistory(CustomerId,StatusDate DESC);
GO
