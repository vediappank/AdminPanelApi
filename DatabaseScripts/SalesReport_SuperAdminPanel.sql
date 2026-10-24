/* =====================================================================================
   Sales & Collections report - SuperAdminPanel (safe to run again)
   Reports > Sales & Collections (/reports/sales-consolidated): pipeline, billing, payments, outstanding with ageing,
   MRR / ARR - per salesperson, month, source, plan and payment method. See PlatformSalesReportController.

   Menu SALES_CONSOLIDATED_REPORT, placed with the other reports (next to Sales Pipeline Report) and granted to every
   role that has the Sales Pipeline report, the Payments report or Billing > Payments.
   ===================================================================================== */
SET NOCOUNT ON;
GO

DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();
DECLARE @SalesReport INT = (SELECT TOP (1) ModuleId FROM dbo.Modules WHERE MenuUrl = '/reports/sales' ORDER BY ModuleId);
DECLARE @Parent INT = (SELECT ParentModuleId FROM dbo.Modules WHERE ModuleId = @SalesReport);   -- NULL = top level
DECLARE @Order INT = ISNULL((SELECT DisplayOrder FROM dbo.Modules WHERE ModuleId = @SalesReport), 60) - 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'SALES_CONSOLIDATED_REPORT')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('SALES_CONSOLIDATED_REPORT', N'Sales & Collections', N'Consolidated sales, billing, payments and outstanding report', 1, 0, @Order, @Now, @Parent, '/reports/sales-consolidated', 'reports');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/reports/sales-consolidated', IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'SALES_CONSOLIDATED_REPORT';

INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT DISTINCT r.PlatformRoleId, m.ModuleId
FROM dbo.PlatformRoles r
JOIN dbo.Modules m ON m.ModuleCode = 'SALES_CONSOLIDATED_REPORT'
WHERE EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x JOIN dbo.Modules mm ON mm.ModuleId = x.ModuleId
              WHERE x.PlatformRoleId = r.PlatformRoleId
                AND (mm.MenuUrl IN ('/reports/sales', '/reports/payments', '/billing/payments') OR mm.ModuleCode IN ('REPORTS', 'REPORT_SALES', 'REPORT_PAYMENTS', 'BILLING_PAYMENTS')))
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = r.PlatformRoleId AND x.ModuleId = m.ModuleId);
PRINT CONCAT('Sales & Collections granted to ', @@ROWCOUNT, ' role(s).');
GO

SELECT m.ModuleCode, m.ModuleName, m.MenuUrl, p.ModuleName AS MenuGroup
FROM dbo.Modules m LEFT JOIN dbo.Modules p ON p.ModuleId = m.ParentModuleId
WHERE m.ModuleCode = 'SALES_CONSOLIDATED_REPORT';
PRINT 'Sales & Collections installed. Sign out and in again to see Reports > Sales & Collections in the menu.';
GO
