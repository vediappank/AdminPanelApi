/*
  006 - Customers: Client ID format, "Company" customer type, contact insurance / notes
  ====================================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-23_customer_client_id_and_contacts.sql   run by hand on the shared BusinessPanel database
                                                                               (after customer-management-upgrade.sql)
    AdminPanelApi/TenantDatabase/Schema/006_Customer_ClientId_Contacts.sql     run automatically on every tenant database (BP_*)

  * Client ID (Customers.CustomerCode) for NEW customers = ddMMyyyy + running number of that day,
    e.g. 2110202601, 2110202602 (UAE date). Existing BP-CUST-xxx codes are not changed.
    A Client ID given by the caller (Excel import) is kept when it is not used yet.
  * Customer type "Company" (the customer sheets use it).
  * CustomerContacts: UnemploymentInsuranceNo, UnemploymentInsuranceExpiry, Notes - per person, as in the sheets.
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE LOWER(LTRIM(RTRIM(CustomerTypeName))) = N'company')
    INSERT dbo.CustomerTypes (CustomerTypeName, IsActive) VALUES (N'Company', 1);
GO

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'UnemploymentInsuranceNo') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD UnemploymentInsuranceNo NVARCHAR(100) NULL;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'UnemploymentInsuranceExpiry') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD UnemploymentInsuranceExpiry DATE NULL;
GO
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerContacts', N'Notes') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD Notes NVARCHAR(1000) NULL;
GO

/* Next Client ID for today: ddMMyyyy + 2-digit running number (3 digits after 99). */
CREATE OR ALTER FUNCTION dbo.fn_Customers_NextClientId()
RETURNS NVARCHAR(50)
AS
BEGIN
    DECLARE @Prefix NVARCHAR(8) = FORMAT(DATEADD(HOUR, 4, SYSUTCDATETIME()), 'ddMMyyyy');   -- UAE date
    DECLARE @Last INT = ISNULL((
        SELECT MAX(TRY_CONVERT(INT, SUBSTRING(CustomerCode, 9, 10)))
        FROM dbo.Customers
        WHERE CustomerCode LIKE @Prefix + N'[0-9][0-9]%' AND LEN(CustomerCode) BETWEEN 10 AND 12
          AND TRY_CONVERT(BIGINT, CustomerCode) IS NOT NULL), 0);
    RETURN @Prefix + CASE WHEN @Last + 1 < 100 THEN RIGHT(N'0' + CONVERT(NVARCHAR(10), @Last + 1), 2) ELSE CONVERT(NVARCHAR(10), @Last + 1) END;
END;
GO

/* Preview shown on the "New Customer" form - the real number is taken when the customer is saved. */
CREATE OR ALTER PROCEDURE dbo.usp_Customers_GetNextCode
AS
BEGIN
    SET NOCOUNT ON;
    SELECT dbo.fn_Customers_NextClientId() AS CustomerCode;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Customers_Insert
    @CustomerCode NVARCHAR(50) = NULL, @CustomerTypeId INT, @CustomerName NVARCHAR(150),
    @CustomerPhone NVARCHAR(20) = NULL, @CustomerEmail NVARCHAR(150) = NULL, @Address NVARCHAR(255) = NULL,
    @IsActive BIT, @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId=@CustomerTypeId AND IsActive=1)
        THROW 50002, 'Invalid or inactive Customer Type.', 1;

    SET @CustomerCode = NULLIF(LTRIM(RTRIM(@CustomerCode)), N'');
    BEGIN TRANSACTION;
        -- Serialise code allocation so two saves at the same moment cannot get the same Client ID.
        DECLARE @lock INT;
        EXEC @lock = sys.sp_getapplock @Resource = N'CustomerClientId', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
        IF @lock < 0 BEGIN ROLLBACK; THROW 50004, 'Could not allocate a Client ID - please try again.', 1; END;

        IF @CustomerCode IS NOT NULL
        BEGIN
            -- Excel import keeps the Client ID from the sheet.
            IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerCode = @CustomerCode)
            BEGIN ROLLBACK; THROW 50003, 'This Client ID already exists.', 1; END;
        END
        ELSE
            SET @CustomerCode = dbo.fn_Customers_NextClientId();

        INSERT dbo.Customers(CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate)
        VALUES(@CustomerCode,@CustomerTypeId,@CustomerName,@CustomerPhone,@CustomerEmail,@Address,@IsActive,@CreatedBy,SYSUTCDATETIME());
        DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    COMMIT TRANSACTION;

    SELECT CustomerId,CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate
    FROM dbo.Customers WHERE CustomerId=@NewId;
END;
GO
