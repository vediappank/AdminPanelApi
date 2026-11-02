/*
  013 - Quotations: Company / Employee scope, people covered by each line, service pricing unit
  ==============================================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-30_quotation_scope_and_pricing_unit.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/013_Quotation_Scope_Pricing_Unit.sql     run automatically on every tenant database (BP_*)

  * Services.PricingUnit = PerPerson | PerFamily | Fixed (default PerPerson - the quantity rule used so far).
      Quotation quantity = tracker service Count x (number of people covered, for PerPerson; 1 otherwise).
  * Quotations.QuotationScope = Company | Employee (empty for quotations made before this change).
    Quotations.EmployeeContactId = for an Employee quotation: the employee (customer contact) it is for.
  * dbo.QuotationItemBeneficiaries - which tracker members (employee and/or family members) each quotation
      line covers. Deleted with the quotation line (ON DELETE CASCADE).
  The stored procedures are not changed - the API writes and reads these columns itself.
  Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

/* ---- 1. Services.PricingUnit ---- */
IF COL_LENGTH(N'dbo.Services', N'PricingUnit') IS NULL
    ALTER TABLE dbo.Services ADD PricingUnit NVARCHAR(20) NOT NULL
        CONSTRAINT DF_Services_PricingUnit DEFAULT (N'PerPerson') WITH VALUES;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Services_PricingUnit')
    ALTER TABLE dbo.Services WITH CHECK ADD CONSTRAINT CK_Services_PricingUnit
        CHECK (PricingUnit IN (N'PerPerson', N'PerFamily', N'Fixed'));
GO

/* ---- 2. Quotations.QuotationScope / EmployeeContactId ---- */
IF COL_LENGTH(N'dbo.Quotations', N'QuotationScope') IS NULL
    ALTER TABLE dbo.Quotations ADD QuotationScope NVARCHAR(20) NULL;
GO
IF COL_LENGTH(N'dbo.Quotations', N'EmployeeContactId') IS NULL
    ALTER TABLE dbo.Quotations ADD EmployeeContactId INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Quotations_QuotationScope')
    ALTER TABLE dbo.Quotations WITH CHECK ADD CONSTRAINT CK_Quotations_QuotationScope
        CHECK (QuotationScope IS NULL OR QuotationScope IN (N'Company', N'Employee'));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Quotations_EmployeeContactId' AND object_id = OBJECT_ID(N'dbo.Quotations'))
    CREATE INDEX IX_Quotations_EmployeeContactId ON dbo.Quotations (EmployeeContactId) WHERE EmployeeContactId IS NOT NULL;
GO

/* ---- 3. People covered by each quotation line ---- */
IF OBJECT_ID(N'dbo.QuotationItemBeneficiaries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuotationItemBeneficiaries
    (
        QuotationItemBeneficiaryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QuotationItemBeneficiaries PRIMARY KEY,
        QuotationItemId            INT NOT NULL,
        TrackerFamilyMemberId      INT NOT NULL,
        CreatedBy                  INT NULL,
        CreatedDate                DATETIME2(7) NOT NULL CONSTRAINT DF_QuotationItemBeneficiaries_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_QuotationItemBeneficiaries_QuotationItems FOREIGN KEY (QuotationItemId)
            REFERENCES dbo.QuotationItems (QuotationItemId) ON DELETE CASCADE,
        CONSTRAINT FK_QuotationItemBeneficiaries_TrackerFamilyMembers FOREIGN KEY (TrackerFamilyMemberId)
            REFERENCES dbo.TrackerFamilyMembers (TrackerFamilyMemberId),
        CONSTRAINT UQ_QuotationItemBeneficiaries UNIQUE (QuotationItemId, TrackerFamilyMemberId)
    );
    CREATE INDEX IX_QuotationItemBeneficiaries_Member ON dbo.QuotationItemBeneficiaries (TrackerFamilyMemberId);
    RAISERROR(N'CREATED dbo.QuotationItemBeneficiaries', 10, 1) WITH NOWAIT;
END
GO
