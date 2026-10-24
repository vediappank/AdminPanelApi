/*
  008 - Remove Customer Requirements, Service History, Communication History and Customer Status
  ==============================================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-23_remove_customer_activity_tables.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/008_Remove_Customer_Activity.sql        run automatically on every tenant database (BP_*)

  Drops these tables and ALL their rows (cannot be undone - take a backup first if the data may be needed):
    dbo.CustomerRequirements, dbo.CustomerServiceHistory, dbo.CustomerCommunications, dbo.CustomerStatusHistory

  Deploy the new API first: the old API still reads these tables when a customer is opened.
  Customers, CustomerProfiles and CustomerContacts are not touched.
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

/* One batch: stop before dropping anything if a view, procedure or function still uses one of the tables. */
DECLARE @Users NVARCHAR(MAX) = N'';
SELECT @Users = @Users + N', ' + OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id)
FROM sys.sql_expression_dependencies d
INNER JOIN sys.objects o ON o.object_id = d.referencing_id
WHERE d.referenced_entity_name IN (N'CustomerRequirements', N'CustomerServiceHistory', N'CustomerCommunications', N'CustomerStatusHistory')
  -- constraints, defaults and triggers that belong to these tables are dropped with them
  AND ISNULL(OBJECT_NAME(o.parent_object_id), N'') NOT IN (N'CustomerRequirements', N'CustomerServiceHistory', N'CustomerCommunications', N'CustomerStatusHistory')
  AND o.name NOT IN (N'CustomerRequirements', N'CustomerServiceHistory', N'CustomerCommunications', N'CustomerStatusHistory');
IF LEN(@Users) > 0
BEGIN
    SET @Users = STUFF(@Users, 1, 2, N'');
    RAISERROR(N'Nothing removed: these objects still use the customer activity tables: %s', 16, 1, @Users);
    RETURN;
END

IF OBJECT_ID(N'dbo.CustomerRequirements', N'U') IS NOT NULL DROP TABLE dbo.CustomerRequirements;
IF OBJECT_ID(N'dbo.CustomerServiceHistory', N'U') IS NOT NULL DROP TABLE dbo.CustomerServiceHistory;
IF OBJECT_ID(N'dbo.CustomerCommunications', N'U') IS NOT NULL DROP TABLE dbo.CustomerCommunications;
IF OBJECT_ID(N'dbo.CustomerStatusHistory', N'U') IS NOT NULL DROP TABLE dbo.CustomerStatusHistory;
PRINT N'Customer requirements, service history, communications and status history tables removed.';
GO
