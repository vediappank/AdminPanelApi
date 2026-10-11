/*
  TenantMenuItems schema and BusinessPanel menu import
  Target: SuperAdminPanel.dbo.TenantMenuItems
  Source: BusinessPanel.dbo.MenuItems
  Safe to re-run: existing MenuKey values are not duplicated.
*/
USE [SuperAdminPanel];
GO

IF OBJECT_ID(N'dbo.TenantMenuItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantMenuItems
    (
        MenuItemId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantMenuItems PRIMARY KEY,
        MenuKey NVARCHAR(100) NOT NULL,
        DisplayName NVARCHAR(150) NOT NULL,
        Icon NVARCHAR(100) NULL,
        RoleRestriction NVARCHAR(100) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_TenantMenuItems_IsActive DEFAULT (1),
        MenuGroup NVARCHAR(100) NULL,
        Route NVARCHAR(250) NULL,
        DisplayOrder INT NOT NULL CONSTRAINT DF_TenantMenuItems_DisplayOrder DEFAULT (0),
        ApplicationCode NVARCHAR(30) NOT NULL CONSTRAINT DF_TenantMenuItems_ApplicationCode DEFAULT ('TRACKER'),
        IsSystemMenu BIT NOT NULL CONSTRAINT DF_TenantMenuItems_IsSystemMenu DEFAULT (0),
        ParentMenuItemId INT NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_TenantMenuItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        ModuleId INT NULL
    );
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.TenantMenuItems')
      AND name = N'UX_TenantMenuItems_MenuKey'
)
    CREATE UNIQUE INDEX UX_TenantMenuItems_MenuKey ON dbo.TenantMenuItems(MenuKey);
GO

/* Import source records only when MenuKey does not already exist.
   Parent links are mapped by MenuKey, never by source identity values. */
IF DB_ID(N'BusinessPanel') IS NOT NULL
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO dbo.TenantMenuItems
        (
            MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup,
            Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId,
            CreatedDate, ModifiedDate, ModuleId
        )
        SELECT
            s.MenuKey, s.DisplayName, s.Icon, s.RoleRestriction, s.IsActive, s.MenuGroup,
            s.Route, s.DisplayOrder, s.ApplicationCode, s.IsSystemMenu, NULL,
            s.CreatedDate, s.ModifiedDate, s.ModuleId
        FROM BusinessPanel.dbo.MenuItems s
        WHERE NOT EXISTS
        (
            SELECT 1 FROM dbo.TenantMenuItems t WHERE t.MenuKey = s.MenuKey
        );

        UPDATE target
        SET target.ParentMenuItemId = parentTarget.MenuItemId
        FROM dbo.TenantMenuItems target
        INNER JOIN BusinessPanel.dbo.MenuItems sourceChild
            ON sourceChild.MenuKey = target.MenuKey
        INNER JOIN BusinessPanel.dbo.MenuItems sourceParent
            ON sourceParent.MenuItemId = sourceChild.ParentMenuItemId
        INNER JOIN dbo.TenantMenuItems parentTarget
            ON parentTarget.MenuKey = sourceParent.MenuKey
        WHERE target.ParentMenuItemId IS NULL
          AND sourceChild.ParentMenuItemId IS NOT NULL;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END
GO

/* Parent FK is added only when it does not already exist. */
IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_TenantMenuItems_ParentMenuItem'
      AND parent_object_id = OBJECT_ID(N'dbo.TenantMenuItems')
)
BEGIN
    ALTER TABLE dbo.TenantMenuItems WITH CHECK
      ADD CONSTRAINT FK_TenantMenuItems_ParentMenuItem
      FOREIGN KEY (ParentMenuItemId) REFERENCES dbo.TenantMenuItems(MenuItemId);
END
GO

SELECT MenuItemId, MenuKey, DisplayName, ParentMenuItemId, Route,
       DisplayOrder, ApplicationCode, IsActive
FROM dbo.TenantMenuItems
ORDER BY ISNULL(ParentMenuItemId, MenuItemId), DisplayOrder, MenuItemId;
GO
