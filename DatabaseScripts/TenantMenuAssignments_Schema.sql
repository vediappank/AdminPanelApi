/*
  Provision tenant ▸ Modules and menus
  Target: SuperAdminPanel
  - Creates dbo.TenantMenuAssignments: which Business Panel menus (dbo.TenantMenuItems)
    each tenant was given. Written by PlatformApi when a tenant is provisioned.
  Safe to re-run.
*/
USE [SuperAdminPanel];
GO

IF OBJECT_ID(N'dbo.TenantMenuAssignments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantMenuAssignments
    (
        TenantMenuAssignmentId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantMenuAssignments PRIMARY KEY,
        TenantId     INT          NOT NULL,
        MenuItemId   INT          NOT NULL,
        CreatedDate  DATETIME2(7) NOT NULL CONSTRAINT DF_TenantMenuAssignments_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_TenantMenuAssignments_Tenants   FOREIGN KEY (TenantId)   REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE,
        CONSTRAINT FK_TenantMenuAssignments_MenuItems FOREIGN KEY (MenuItemId) REFERENCES dbo.TenantMenuItems (MenuItemId) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TenantMenuAssignments') AND name = N'IX_TenantMenuAssignments_Tenant_Menu')
    CREATE UNIQUE INDEX IX_TenantMenuAssignments_Tenant_Menu ON dbo.TenantMenuAssignments (TenantId, MenuItemId);
GO

/*
  Check one tenant's menus:
SELECT t.TenantCode, m.MenuItemId, m.DisplayName, m.ParentMenuItemId, m.IsSystemMenu
FROM dbo.TenantMenuAssignments a
JOIN dbo.Tenants t ON t.TenantId = a.TenantId
JOIN dbo.TenantMenuItems m ON m.MenuItemId = a.MenuItemId
WHERE t.TenantCode = N'INFOSYS'
ORDER BY m.DisplayOrder;
*/
