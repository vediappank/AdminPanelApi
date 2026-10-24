/* =====================================================================================
   Subscriptions - SuperAdminPanel (safe to run again)
   Billing ▸ Subscriptions: plans with a monthly and a yearly price, and each tenant's
   subscription terms. Starting / renewing a term updates the tenant's License (dates,
   user / device limits, amount billed), so Billing ▸ Payments and the Business Panel
   login check keep working unchanged. See PlatformSubscriptionsController.

   1. Table  dbo.SubscriptionPlans
   2. Table  dbo.TenantSubscriptions   (at most one Active term per tenant)
   3. Menu   SUBSCRIPTIONS (/billing/subscriptions) - next to Payments, granted to every
             role that has the Payments or Tenants screen
   4. Default plan  SubscriptionPlans.IsDefault (one plan) + 3 sample plans:
             STARTER (default), BUSINESS, ENTERPRISE - edit prices on Billing > Subscriptions.
             Every active tenant without a subscription is put on the default plan by the API
             (a tenant whose license is still valid keeps it free until expiry, then the plan bills).
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. PLANS ------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.SubscriptionPlans', N'U') IS NULL
CREATE TABLE dbo.SubscriptionPlans
(
    SubscriptionPlanId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubscriptionPlans PRIMARY KEY,
    PlanCode            NVARCHAR(50)      NOT NULL,
    PlanName            NVARCHAR(100)     NOT NULL,
    Description         NVARCHAR(500)     NULL,
    MonthlyPrice        DECIMAL(18,2)     NOT NULL CONSTRAINT DF_SubscriptionPlans_Monthly DEFAULT (0),
    YearlyPrice         DECIMAL(18,2)     NOT NULL CONSTRAINT DF_SubscriptionPlans_Yearly  DEFAULT (0),
    Currency            NVARCHAR(3)       NOT NULL CONSTRAINT DF_SubscriptionPlans_Currency DEFAULT (N'AED'),
    UserLimit           INT               NOT NULL CONSTRAINT DF_SubscriptionPlans_Users   DEFAULT (0),
    MaxDevices          INT               NOT NULL CONSTRAINT DF_SubscriptionPlans_Devices DEFAULT (0),
    IsActive            BIT               NOT NULL CONSTRAINT DF_SubscriptionPlans_Active  DEFAULT (1),
    DisplayOrder        INT               NOT NULL CONSTRAINT DF_SubscriptionPlans_Order   DEFAULT (0),
    CreatedDate         DATETIME2         NOT NULL CONSTRAINT DF_SubscriptionPlans_Created DEFAULT (SYSUTCDATETIME()),
    ModifiedDate        DATETIME2         NULL,
    CONSTRAINT UX_SubscriptionPlans_PlanCode UNIQUE (PlanCode),
    CONSTRAINT CK_SubscriptionPlans_Prices CHECK (MonthlyPrice >= 0 AND YearlyPrice >= 0)
);
GO

/* 2. TENANT SUBSCRIPTIONS -------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.TenantSubscriptions', N'U') IS NULL
CREATE TABLE dbo.TenantSubscriptions
(
    TenantSubscriptionId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantSubscriptions PRIMARY KEY,
    TenantId             INT               NOT NULL CONSTRAINT FK_TenantSubscriptions_Tenants REFERENCES dbo.Tenants (TenantId),
    SubscriptionPlanId   INT               NOT NULL CONSTRAINT FK_TenantSubscriptions_Plans REFERENCES dbo.SubscriptionPlans (SubscriptionPlanId),
    LicenseId            INT               NULL     CONSTRAINT FK_TenantSubscriptions_Licenses REFERENCES dbo.Licenses (LicenseId),
    BillingCycle         NVARCHAR(10)      NOT NULL CONSTRAINT CK_TenantSubscriptions_Cycle CHECK (BillingCycle IN (N'Monthly', N'Yearly')),
    Price                DECIMAL(18,2)     NOT NULL,
    Currency             NVARCHAR(3)       NOT NULL CONSTRAINT DF_TenantSubscriptions_Currency DEFAULT (N'AED'),
    StartDate            DATE              NOT NULL,
    EndDate              DATE              NOT NULL,
    Status               NVARCHAR(20)      NOT NULL CONSTRAINT DF_TenantSubscriptions_Status DEFAULT (N'Active')
                                           CONSTRAINT CK_TenantSubscriptions_Status CHECK (Status IN (N'Active', N'Renewed', N'Superseded', N'Cancelled', N'Expired')),
    AutoRenew            BIT               NOT NULL CONSTRAINT DF_TenantSubscriptions_AutoRenew DEFAULT (0),
    RenewedFromId        INT               NULL     CONSTRAINT FK_TenantSubscriptions_RenewedFrom REFERENCES dbo.TenantSubscriptions (TenantSubscriptionId),
    Notes                NVARCHAR(500)     NULL,
    CreatedDate          DATETIME2         NOT NULL CONSTRAINT DF_TenantSubscriptions_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy            NVARCHAR(200)     NULL,
    ModifiedDate         DATETIME2         NULL,
    CancelledDate        DATETIME2         NULL,
    CONSTRAINT CK_TenantSubscriptions_Dates CHECK (EndDate >= StartDate)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TenantSubscriptions_ActivePerTenant' AND object_id = OBJECT_ID(N'dbo.TenantSubscriptions'))
    CREATE UNIQUE INDEX UX_TenantSubscriptions_ActivePerTenant ON dbo.TenantSubscriptions (TenantId) WHERE Status = N'Active';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TenantSubscriptions_Status_EndDate' AND object_id = OBJECT_ID(N'dbo.TenantSubscriptions'))
    CREATE INDEX IX_TenantSubscriptions_Status_EndDate ON dbo.TenantSubscriptions (Status, EndDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TenantSubscriptions_LicenseId' AND object_id = OBJECT_ID(N'dbo.TenantSubscriptions'))
    CREATE INDEX IX_TenantSubscriptions_LicenseId ON dbo.TenantSubscriptions (LicenseId);
GO

/* 3. MENU ------------------------------------------------------------------------------ */
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @Payments INT = (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE MenuUrl = '/billing/payments' ORDER BY ModuleId);
DECLARE @Parent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @Payments);   -- NULL = top level
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleId = @Payments), 50) + 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'SUBSCRIPTIONS')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('SUBSCRIPTIONS', N'Subscriptions', N'Subscription plans (monthly / yearly) and tenant subscriptions', 1, 0, @Order, @Now, @Parent, '/billing/subscriptions', 'billing');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/billing/subscriptions', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'SUBSCRIPTIONS';

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT r.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoles r
JOIN dbo.Modules m ON m.ModuleCode = 'SUBSCRIPTIONS'
WHERE (@Payments IS NULL
       OR EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = @Payments)
       OR EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x JOIN dbo.Modules mm ON mm.ModuleId = x.ModuleId
                  WHERE x.PlatformRoleId = r.PlatformRoleId AND mm.ModuleCode IN ('TENANTS_LIST', 'BILLING')))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Subscriptions granted to ', @@ROWCOUNT, ' role(s).');
GO

/* 5. PLAN MENUS - the Business Panel menus each plan includes (none = menus chosen by hand per tenant) */
IF OBJECT_ID(N'dbo.SubscriptionPlanMenus', N'U') IS NULL
CREATE TABLE dbo.SubscriptionPlanMenus
(
    SubscriptionPlanId INT NOT NULL CONSTRAINT FK_SubscriptionPlanMenus_Plans REFERENCES dbo.SubscriptionPlans (SubscriptionPlanId) ON DELETE CASCADE,
    MenuItemId         INT NOT NULL CONSTRAINT FK_SubscriptionPlanMenus_Menus REFERENCES dbo.TenantMenuItems (MenuItemId) ON DELETE CASCADE,
    CONSTRAINT PK_SubscriptionPlanMenus PRIMARY KEY (SubscriptionPlanId, MenuItemId)
);
GO

/* 4. DEFAULT PLAN + SAMPLE PLANS ------------------------------------------------------- */
IF COL_LENGTH(N'dbo.SubscriptionPlans', N'IsDefault') IS NULL
    ALTER TABLE dbo.SubscriptionPlans ADD IsDefault BIT NOT NULL CONSTRAINT DF_SubscriptionPlans_Default DEFAULT (0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubscriptionPlans_Default' AND object_id = OBJECT_ID(N'dbo.SubscriptionPlans'))
    CREATE UNIQUE INDEX UX_SubscriptionPlans_Default ON dbo.SubscriptionPlans (IsDefault) WHERE IsDefault = 1;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SubscriptionPlans WHERE PlanCode = N'STARTER')
    INSERT dbo.SubscriptionPlans (PlanCode, PlanName, Description, MonthlyPrice, YearlyPrice, Currency, UserLimit, MaxDevices, IsActive, DisplayOrder)
    VALUES (N'STARTER', N'Starter', N'Small teams getting started - CRM, quotations, invoices and payments.', 199, 1990, N'AED', 5, 5, 1, 1);
IF NOT EXISTS (SELECT 1 FROM dbo.SubscriptionPlans WHERE PlanCode = N'BUSINESS')
    INSERT dbo.SubscriptionPlans (PlanCode, PlanName, Description, MonthlyPrice, YearlyPrice, Currency, UserLimit, MaxDevices, IsActive, DisplayOrder)
    VALUES (N'BUSINESS', N'Business', N'Growing companies - all modules, renewals tracker and reports.', 499, 4990, N'AED', 25, 25, 1, 2);
IF NOT EXISTS (SELECT 1 FROM dbo.SubscriptionPlans WHERE PlanCode = N'ENTERPRISE')
    INSERT dbo.SubscriptionPlans (PlanCode, PlanName, Description, MonthlyPrice, YearlyPrice, Currency, UserLimit, MaxDevices, IsActive, DisplayOrder)
    VALUES (N'ENTERPRISE', N'Enterprise', N'Large teams - all modules, priority support, 100 users.', 999, 9990, N'AED', 100, 100, 1, 3);

IF NOT EXISTS (SELECT 1 FROM dbo.SubscriptionPlans WHERE IsDefault = 1)
    UPDATE dbo.SubscriptionPlans SET IsDefault = 1, IsActive = 1, ModifiedDate = SYSUTCDATETIME() WHERE PlanCode = N'STARTER';
GO

SELECT PlanCode, PlanName, MonthlyPrice, YearlyPrice, Currency, UserLimit, MaxDevices, IsActive, IsDefault
FROM dbo.SubscriptionPlans ORDER BY DisplayOrder, PlanName;
GO

SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'SUBSCRIPTIONS';
PRINT 'Subscriptions installed. Add plans on Billing > Subscriptions, then start a subscription from Tenants > Control Center.';
GO
