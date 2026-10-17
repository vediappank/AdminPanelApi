/*
  002 - number sequences (missing from 001_BusinessPanel_Schema.sql: the schema export left out SEQUENCE objects)

  Used by the stored procedures:
    dbo.CustomerCodeSequence   customer codes  BP-CUST-001 ...   (usp_Customers_Insert)
    dbo.InvoiceNumberSequence  invoice numbers                    (invoice create procedure)
    dbo.PaymentNumberSequence  payment numbers                    (payment create procedure)

  Each starts after the highest number already used in this database, so it is safe on a tenant
  database that already has customers / invoices / payments. Re-runnable (skips what exists).
  Runs inside each tenant database automatically (Admin Panel tenant migration); no USE / database names.
*/
SET NOCOUNT ON;

DECLARE @start BIGINT, @sql NVARCHAR(400);

IF OBJECT_ID(N'dbo.CustomerCodeSequence', N'SO') IS NULL
BEGIN
    SET @start = 1;
    IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
        SELECT @start = ISNULL(MAX(TRY_CONVERT(BIGINT, SUBSTRING(CustomerCode, 9, 30))), 0) + 1
        FROM dbo.Customers WHERE CustomerCode LIKE N'BP-CUST-[0-9]%';
    SET @sql = N'CREATE SEQUENCE dbo.CustomerCodeSequence AS BIGINT START WITH ' + CONVERT(NVARCHAR(20), @start) + N' INCREMENT BY 1 MINVALUE 1 NO CYCLE;';
    EXEC (@sql);
    PRINT N'Created dbo.CustomerCodeSequence, starting at ' + CONVERT(NVARCHAR(20), @start);
END;

IF OBJECT_ID(N'dbo.InvoiceNumberSequence', N'SO') IS NULL
BEGIN
    SET @start = 1;
    IF OBJECT_ID(N'dbo.Invoices', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Invoices', N'InvoiceId') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @s = ISNULL(MAX(InvoiceId), 0) + 1 FROM dbo.Invoices;', N'@s BIGINT OUTPUT', @s = @start OUTPUT;
    SET @sql = N'CREATE SEQUENCE dbo.InvoiceNumberSequence AS INT START WITH ' + CONVERT(NVARCHAR(20), @start) + N' INCREMENT BY 1 MINVALUE 1 NO CYCLE;';
    EXEC (@sql);
    PRINT N'Created dbo.InvoiceNumberSequence, starting at ' + CONVERT(NVARCHAR(20), @start);
END;

IF OBJECT_ID(N'dbo.PaymentNumberSequence', N'SO') IS NULL
BEGIN
    SET @start = 1;
    IF OBJECT_ID(N'dbo.Payments', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Payments', N'PaymentId') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @s = ISNULL(MAX(PaymentId), 0) + 1 FROM dbo.Payments;', N'@s BIGINT OUTPUT', @s = @start OUTPUT;
    SET @sql = N'CREATE SEQUENCE dbo.PaymentNumberSequence AS INT START WITH ' + CONVERT(NVARCHAR(20), @start) + N' INCREMENT BY 1 MINVALUE 1 NO CYCLE;';
    EXEC (@sql);
    PRINT N'Created dbo.PaymentNumberSequence, starting at ' + CONVERT(NVARCHAR(20), @start);
END;
