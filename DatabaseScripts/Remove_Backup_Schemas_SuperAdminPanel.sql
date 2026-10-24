/* =====================================================================================
   Remove backup schemas - SuperAdminPanel (safe to run again)
   Drops every schema named bak_... (made by Truncate_Tenant_Data.sql before it deletes data,
   e.g. bak_20261017_1136) together with everything in it: tables, views, procedures,
   functions, synonyms and types. Nothing in dbo or any other schema is touched.

   1. Run once with @Execute = 0 to see what would be removed (nothing is changed).
   2. Set @Execute = 1 and run again to remove it. All in one transaction - an error undoes everything.
   SQL Server 2016 compatible.
   ===================================================================================== */
USE [SuperAdminPanel];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Execute BIT = 1;      -- 0 = only list what would be removed

DECLARE @list TABLE (Seq INT IDENTITY(1,1), SchemaName SYSNAME, ObjectName SYSNAME NULL, Kind NVARCHAR(40), Sql NVARCHAR(MAX));

-- foreign keys first (between backup tables), then the objects, then the schemas
INSERT @list (SchemaName, ObjectName, Kind, Sql)
SELECT s.name, fk.name, N'Foreign key',
       N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
FROM sys.foreign_keys fk
JOIN sys.tables t  ON t.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name LIKE N'bak[_]%';

INSERT @list (SchemaName, ObjectName, Kind, Sql)
SELECT s.name, o.name,
       CASE o.type WHEN 'U' THEN N'Table' WHEN 'V' THEN N'View' WHEN 'P' THEN N'Procedure' WHEN 'SN' THEN N'Synonym' ELSE N'Function' END,
       CASE o.type WHEN 'U' THEN N'DROP TABLE ' WHEN 'V' THEN N'DROP VIEW ' WHEN 'P' THEN N'DROP PROCEDURE ' WHEN 'SN' THEN N'DROP SYNONYM ' ELSE N'DROP FUNCTION ' END
       + QUOTENAME(s.name) + N'.' + QUOTENAME(o.name) + N';'
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name LIKE N'bak[_]%'
  AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'SN', 'U')
ORDER BY CASE o.type WHEN 'V' THEN 1 WHEN 'P' THEN 2 WHEN 'SN' THEN 3 WHEN 'U' THEN 5 ELSE 4 END, o.name;

INSERT @list (SchemaName, ObjectName, Kind, Sql)
SELECT s.name, ty.name, N'Type', N'DROP TYPE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(ty.name) + N';'
FROM sys.types ty JOIN sys.schemas s ON s.schema_id = ty.schema_id
WHERE s.name LIKE N'bak[_]%' AND ty.is_user_defined = 1;

INSERT @list (SchemaName, ObjectName, Kind, Sql)
SELECT s.name, NULL, N'Schema', N'DROP SCHEMA ' + QUOTENAME(s.name) + N';'
FROM sys.schemas s
WHERE s.name LIKE N'bak[_]%'
ORDER BY s.name;

SELECT SchemaName, Kind, COUNT(*) AS Items FROM @list GROUP BY SchemaName, Kind ORDER BY SchemaName, Kind;

IF NOT EXISTS (SELECT 1 FROM @list)
BEGIN
    PRINT 'No bak_ schemas in this database - nothing to remove.';
    RETURN;
END

IF @Execute = 0
BEGIN
    SELECT Seq, Sql FROM @list ORDER BY Seq;
    PRINT 'Preview only - set @Execute = 1 and run again to remove the backup schemas.';
    RETURN;
END

BEGIN TRANSACTION;
DECLARE @seq INT = 1, @max INT = (SELECT MAX(Seq) FROM @list), @sql NVARCHAR(MAX);
WHILE @seq <= @max
BEGIN
    SELECT @sql = Sql FROM @list WHERE Seq = @seq;
    EXEC sys.sp_executesql @sql;
    SET @seq += 1;
END
COMMIT TRANSACTION;

DECLARE @schemas INT = (SELECT COUNT(*) FROM @list WHERE Kind = N'Schema'),
        @tables  INT = (SELECT COUNT(*) FROM @list WHERE Kind = N'Table');
PRINT CONCAT('Removed ', @schemas, ' backup schema(s) and ', @tables, ' table(s).');
SELECT name AS RemainingBakSchemas FROM sys.schemas WHERE name LIKE N'bak[_]%';   -- should be empty
GO
