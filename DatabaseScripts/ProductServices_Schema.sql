/*
  Product Services (Service Catalog ▸ Product Services)
  Target: SuperAdminPanel
  - Creates dbo.ProductServices: master services per Product (dbo.Applications), Product -> Service only.
  - Adds QuotationServiceLines.ProductServiceId (nullable, FK ON DELETE SET NULL) so each quotation
    line records which master service it was taken from. Existing lines stay as free text (NULL).
  - Menu: replaces Service Catalog ▸ Category / Sub-Category / Service with Service Catalog ▸ Product Services.
    The old dbo.ServiceCategories / ServiceSubCategories / Services tables are NOT touched.
  Safe to re-run.
*/
USE [SuperAdminPanel];
GO

/* ---------- 1. dbo.ProductServices ---------- */
IF OBJECT_ID(N'dbo.ProductServices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductServices
    (
        ProductServiceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductServices PRIMARY KEY,
        ApplicationId    INT            NOT NULL,
        ServiceName      NVARCHAR(200)  NOT NULL,
        DefaultQty       INT            NOT NULL CONSTRAINT DF_ProductServices_DefaultQty   DEFAULT (1),
        Amount           DECIMAL(18,2)  NOT NULL,
        VatPercent       DECIMAL(5,2)   NOT NULL CONSTRAINT DF_ProductServices_VatPercent   DEFAULT (0),
        IsActive         BIT            NOT NULL CONSTRAINT DF_ProductServices_IsActive     DEFAULT (1),
        DisplayOrder     INT            NOT NULL CONSTRAINT DF_ProductServices_DisplayOrder DEFAULT (0),
        CreatedDate      DATETIME2(7)   NOT NULL CONSTRAINT DF_ProductServices_CreatedDate  DEFAULT (SYSUTCDATETIME()),
        ModifiedDate     DATETIME2(7)   NULL,
        CONSTRAINT FK_ProductServices_Applications FOREIGN KEY (ApplicationId) REFERENCES dbo.Applications (ApplicationId),
        CONSTRAINT CK_ProductServices_DefaultQty CHECK (DefaultQty >= 1),
        CONSTRAINT CK_ProductServices_Amount     CHECK (Amount >= 0),
        CONSTRAINT CK_ProductServices_VatPercent CHECK (VatPercent BETWEEN 0 AND 100)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ProductServices') AND name = N'IX_ProductServices_ApplicationId_ServiceName')
    CREATE UNIQUE INDEX IX_ProductServices_ApplicationId_ServiceName ON dbo.ProductServices (ApplicationId, ServiceName);
GO

/* ---------- 2. QuotationServiceLines.ProductServiceId ---------- */
IF COL_LENGTH(N'dbo.QuotationServiceLines', N'ProductServiceId') IS NULL
    ALTER TABLE dbo.QuotationServiceLines ADD ProductServiceId INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_QuotationServiceLines_ProductServices')
    ALTER TABLE dbo.QuotationServiceLines
        ADD CONSTRAINT FK_QuotationServiceLines_ProductServices
        FOREIGN KEY (ProductServiceId) REFERENCES dbo.ProductServices (ProductServiceId) ON DELETE SET NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.QuotationServiceLines') AND name = N'IX_QuotationServiceLines_ProductServiceId')
    CREATE INDEX IX_QuotationServiceLines_ProductServiceId ON dbo.QuotationServiceLines (ProductServiceId);
GO

/* ---------- 3. Menu: Service Catalog ▸ Product Services ---------- */
DECLARE @parentId INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = N'SERVICE_CATALOG');

IF @parentId IS NOT NULL
BEGIN
    -- Old Category / Sub-Category screens off the menu.
    UPDATE dbo.Modules SET IsActive = 0, ModifiedDate = SYSUTCDATETIME()
     WHERE ModuleCode IN (N'SVC_CATEGORIES', N'SVC_SUBCATEGORIES') AND IsActive = 1;

    -- Re-point the old "Service" entry to the new screen (keeps any role grants).
    IF EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = N'SVC_SERVICES')
        UPDATE dbo.Modules
           SET ModuleName = N'Product Services', MenuUrl = N'/service-catalog/product-services',
               Description = N'Master services per product, used to pre-fill quotations',
               IsActive = 1, DisplayOrder = 10, ParentModuleId = @parentId, ModifiedDate = SYSUTCDATETIME()
         WHERE ModuleCode = N'SVC_SERVICES';
    ELSE
        INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsCore, IsActive, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
        VALUES (N'SVC_SERVICES', N'Product Services', N'Master services per product, used to pre-fill quotations',
                1, 1, 10, SYSUTCDATETIME(), @parentId, N'/service-catalog/product-services', NULL);
END
ELSE
    PRINT N'SERVICE_CATALOG module not found - menu not changed.';
GO
