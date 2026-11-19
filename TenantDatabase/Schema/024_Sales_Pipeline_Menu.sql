/*
  Sales Pipeline menu (Business Panel) - run AFTER 2026-11-17_leads_and_sales.sql and 2026-11-17_sales_quotation_convert.sql
  Same script in two places:
    BusinessPanelApi/database/2026-11-17_sales_pipeline_menu.sql
    AdminPanelApi/TenantDatabase/Schema/024_Sales_Pipeline_Menu.sql    (run automatically on every tenant database)
  * Adds "Sales Pipeline" (/sales-pipeline) as the first item of the Leads & Sales section, with the same role permissions as Leads.
  * Hides Lead Follow-ups, Lead Services and Sales Services from the menu - they now live inside the pipeline rows.
    Nothing is deleted: Leads, Sales and Sale Quotations stay as list screens, and the hidden screens can be switched back on
    from Menu Configuration (or with:  UPDATE dbo.MenuItems SET IsActive = 1 WHERE MenuKey IN ('lead-follow-ups','lead-services','sales-services') ).
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
BEGIN
    DECLARE @App NVARCHAR(30), @Parent INT;
    SELECT TOP (1) @App = ApplicationCode FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sales' ORDER BY MenuItemId;
    SELECT @Parent = MenuItemId FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'leads-sales';
    IF @Parent IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sales-pipeline')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'sales-pipeline', N'Sales Pipeline', N'shopping_cart', NULL, 1, N'Leads & Sales', N'/sales-pipeline', 0, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());

    UPDATE dbo.MenuItems SET IsActive = 0 WHERE LOWER(MenuKey) IN (N'lead-follow-ups', N'lead-services', N'sales-services') AND IsActive = 1;
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'sales-pipeline', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'leads'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'sales-pipeline');
GO
RAISERROR(N'Sales Pipeline menu installed.', 10, 1) WITH NOWAIT;
GO
