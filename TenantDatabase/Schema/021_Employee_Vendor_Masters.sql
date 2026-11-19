/*
  021 - Employee and Vendor masters (Bliss CRM v1.1, requirements A1.3 and A1.4)
  =============================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-11-17_employee_vendor_masters.sql      run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/021_Employee_Vendor_Masters.sql   run automatically on every tenant database (BP_*)

  * dbo.Employees gets: Emirates ID, passport number, biometric device ID and linked CRM user (each unique) plus EmployeeCode (EMP-0001, filled automatically), JobTitle, Department, EmploymentStatus
    (Active, On Probation, On Leave, Resigned, Terminated), DateOfBirth, Nationality, Address, emergency contact.
    Existing employees keep all their data and get an EmployeeCode. IsActive still decides who can be assigned work.
  * dbo.EmployeeSalaries (own screen and rights - salary is not on the Employee screen), dbo.EmployeeDocuments.
  * dbo.Vendors (VEN-0001, filled automatically; licence, tax number, mobile and e-mail must each be unique),
    dbo.VendorDocuments, dbo.VendorPaymentDetails (own screen and rights).
  * The usp_Employees_* procedures are replaced to carry the new columns; the five new tables get usp_<Table>_GetPaged / GetById / Insert / Update / Delete.
  * dbo.Customers gets AssignedEmployeeId (the employee looking after the customer); Employees list shows each employee's customer count.
  Nothing existing is removed. Safe to run more than once. SQL Server 2016 compatible.
*/
SET NOCOUNT ON;
GO

/* ---- Employees: new columns ---- */
IF COL_LENGTH(N'dbo.Employees', N'JobTitle') IS NULL ALTER TABLE dbo.Employees ADD JobTitle nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'Department') IS NULL ALTER TABLE dbo.Employees ADD Department nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'EmploymentStatus') IS NULL ALTER TABLE dbo.Employees ADD EmploymentStatus nvarchar(30) NOT NULL CONSTRAINT DF_Employees_EmploymentStatus DEFAULT (N'Active');
GO
IF COL_LENGTH(N'dbo.Employees', N'DateOfBirth') IS NULL ALTER TABLE dbo.Employees ADD DateOfBirth date NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'Nationality') IS NULL ALTER TABLE dbo.Employees ADD Nationality nvarchar(80) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'Address') IS NULL ALTER TABLE dbo.Employees ADD Address nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'EmergencyContactName') IS NULL ALTER TABLE dbo.Employees ADD EmergencyContactName nvarchar(150) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'EmergencyContactPhone') IS NULL ALTER TABLE dbo.Employees ADD EmergencyContactPhone nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'EmiratesIdNo') IS NULL ALTER TABLE dbo.Employees ADD EmiratesIdNo nvarchar(30) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'PassportNo') IS NULL ALTER TABLE dbo.Employees ADD PassportNo nvarchar(30) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'BiometricDeviceId') IS NULL ALTER TABLE dbo.Employees ADD BiometricDeviceId nvarchar(40) NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'UserId') IS NULL ALTER TABLE dbo.Employees ADD UserId int NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Employees_Users')
    ALTER TABLE dbo.Employees ADD CONSTRAINT FK_Employees_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employees_EmiratesIdNo' AND object_id = OBJECT_ID(N'dbo.Employees'))
    CREATE UNIQUE INDEX UX_Employees_EmiratesIdNo ON dbo.Employees (EmiratesIdNo) WHERE EmiratesIdNo IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employees_PassportNo' AND object_id = OBJECT_ID(N'dbo.Employees'))
    CREATE UNIQUE INDEX UX_Employees_PassportNo ON dbo.Employees (PassportNo) WHERE PassportNo IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employees_BiometricDeviceId' AND object_id = OBJECT_ID(N'dbo.Employees'))
    CREATE UNIQUE INDEX UX_Employees_BiometricDeviceId ON dbo.Employees (BiometricDeviceId) WHERE BiometricDeviceId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employees_UserId' AND object_id = OBJECT_ID(N'dbo.Employees'))
    CREATE UNIQUE INDEX UX_Employees_UserId ON dbo.Employees (UserId) WHERE UserId IS NOT NULL;
GO
IF COL_LENGTH(N'dbo.Employees', N'EmployeeCode') IS NULL ALTER TABLE dbo.Employees ADD EmployeeCode NVARCHAR(20) NULL;
GO
/* ---- Customers: assigned employee (account manager) ---- */
IF COL_LENGTH(N'dbo.Customers', N'AssignedEmployeeId') IS NULL
BEGIN
    ALTER TABLE dbo.Customers ADD AssignedEmployeeId INT NULL;
    ALTER TABLE dbo.Customers ADD CONSTRAINT FK_Customers_AssignedEmployee FOREIGN KEY (AssignedEmployeeId) REFERENCES dbo.Employees (EmployeeId);
    CREATE INDEX IX_Customers_AssignedEmployeeId ON dbo.Customers (AssignedEmployeeId);
END
GO
UPDATE dbo.Employees SET EmployeeCode = N'EMP-' + RIGHT(N'0000' + CAST(EmployeeId AS nvarchar(10)), 4) WHERE EmployeeCode IS NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employees_EmployeeCode' AND object_id = OBJECT_ID(N'dbo.Employees'))
    CREATE UNIQUE INDEX UX_Employees_EmployeeCode ON dbo.Employees (EmployeeCode) WHERE EmployeeCode IS NOT NULL;
GO

/* ---- Employees: procedures ---- */
IF OBJECT_ID(N'dbo.usp_Employees_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Employees_GetPaged;
GO
CREATE PROCEDURE dbo.usp_Employees_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.Employees;

    SELECT
        EmployeeId,
        EmployeeCode,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        JobTitle,
        Department,
        EmploymentStatus,
        DateOfBirth,
        Nationality,
        Address,
        EmergencyContactName,
        EmergencyContactPhone,
        EmiratesIdNo,
        PassportNo,
        BiometricDeviceId,
        UserId,
        CreatedDate,
        ModifiedDate,
        (SELECT COUNT(1) FROM dbo.Customers c WHERE c.AssignedEmployeeId = dbo.Employees.EmployeeId) AS CustomerCount
    FROM dbo.Employees
    ORDER BY EmployeeId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_Employees_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Employees_GetById;
GO
CREATE PROCEDURE dbo.usp_Employees_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        EmployeeId,
        EmployeeCode,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        JobTitle,
        Department,
        EmploymentStatus,
        DateOfBirth,
        Nationality,
        Address,
        EmergencyContactName,
        EmergencyContactPhone,
        EmiratesIdNo,
        PassportNo,
        BiometricDeviceId,
        UserId,
        CreatedDate,
        ModifiedDate,
        (SELECT COUNT(1) FROM dbo.Customers c WHERE c.AssignedEmployeeId = dbo.Employees.EmployeeId) AS CustomerCount
    FROM dbo.Employees
    WHERE EmployeeId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_Employees_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Employees_Insert;
GO
CREATE PROCEDURE dbo.usp_Employees_Insert
    @FullName nvarchar(150),
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @RoleId int = NULL,
    @JoinDate date = NULL,
    @IsActive bit = 1,
    @JobTitle nvarchar(100) = NULL,
    @Department nvarchar(100) = NULL,
    @EmploymentStatus nvarchar(30) = N'Active',
    @DateOfBirth date = NULL,
    @Nationality nvarchar(80) = NULL,
    @Address nvarchar(500) = NULL,
    @EmergencyContactName nvarchar(150) = NULL,
    @EmergencyContactPhone nvarchar(20) = NULL,
    @EmiratesIdNo nvarchar(30) = NULL,
    @PassportNo nvarchar(30) = NULL,
    @BiometricDeviceId nvarchar(40) = NULL,
    @UserId int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Employees
    (
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        JobTitle,
        Department,
        EmploymentStatus,
        DateOfBirth,
        Nationality,
        Address,
        EmergencyContactName,
        EmergencyContactPhone,
        EmiratesIdNo,
        PassportNo,
        BiometricDeviceId,
        UserId
    )
    VALUES
    (
        @FullName,
        @Mobile,
        @Email,
        @RoleId,
        @JoinDate,
        @IsActive,
        @JobTitle,
        @Department,
        @EmploymentStatus,
        @DateOfBirth,
        @Nationality,
        @Address,
        @EmergencyContactName,
        @EmergencyContactPhone,
        @EmiratesIdNo,
        @PassportNo,
        @BiometricDeviceId,
        @UserId
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    UPDATE dbo.Employees SET EmployeeCode = N'EMP-' + RIGHT(N'0000' + CAST(EmployeeId AS nvarchar(10)), 4) WHERE EmployeeId = @NewId;

    SELECT
        EmployeeId,
        EmployeeCode,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        JobTitle,
        Department,
        EmploymentStatus,
        DateOfBirth,
        Nationality,
        Address,
        EmergencyContactName,
        EmergencyContactPhone,
        EmiratesIdNo,
        PassportNo,
        BiometricDeviceId,
        UserId,
        CreatedDate,
        ModifiedDate,
        (SELECT COUNT(1) FROM dbo.Customers c WHERE c.AssignedEmployeeId = dbo.Employees.EmployeeId) AS CustomerCount
    FROM dbo.Employees
    WHERE EmployeeId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_Employees_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Employees_Update;
GO
CREATE PROCEDURE dbo.usp_Employees_Update
    @Id int,
    @FullName nvarchar(150),
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @RoleId int = NULL,
    @JoinDate date = NULL,
    @IsActive bit = 1,
    @JobTitle nvarchar(100) = NULL,
    @Department nvarchar(100) = NULL,
    @EmploymentStatus nvarchar(30) = N'Active',
    @DateOfBirth date = NULL,
    @Nationality nvarchar(80) = NULL,
    @Address nvarchar(500) = NULL,
    @EmergencyContactName nvarchar(150) = NULL,
    @EmergencyContactPhone nvarchar(20) = NULL,
    @EmiratesIdNo nvarchar(30) = NULL,
    @PassportNo nvarchar(30) = NULL,
    @BiometricDeviceId nvarchar(40) = NULL,
    @UserId int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Employees
    SET
        FullName = @FullName,
        Mobile = @Mobile,
        Email = @Email,
        RoleId = @RoleId,
        JoinDate = @JoinDate,
        IsActive = @IsActive,
        JobTitle = @JobTitle,
        Department = @Department,
        EmploymentStatus = @EmploymentStatus,
        DateOfBirth = @DateOfBirth,
        Nationality = @Nationality,
        Address = @Address,
        EmergencyContactName = @EmergencyContactName,
        EmergencyContactPhone = @EmergencyContactPhone,
        EmiratesIdNo = @EmiratesIdNo,
        PassportNo = @PassportNo,
        BiometricDeviceId = @BiometricDeviceId,
        UserId = @UserId,
        ModifiedDate = SYSUTCDATETIME()
    WHERE EmployeeId = @Id;

    SELECT
        EmployeeId,
        EmployeeCode,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        JobTitle,
        Department,
        EmploymentStatus,
        DateOfBirth,
        Nationality,
        Address,
        EmergencyContactName,
        EmergencyContactPhone,
        EmiratesIdNo,
        PassportNo,
        BiometricDeviceId,
        UserId,
        CreatedDate,
        ModifiedDate,
        (SELECT COUNT(1) FROM dbo.Customers c WHERE c.AssignedEmployeeId = dbo.Employees.EmployeeId) AS CustomerCount
    FROM dbo.Employees
    WHERE EmployeeId = @Id;
END
GO
/* ---- Customers: procedures carry AssignedEmployeeId (Insert keeps the Client ID logic from 2026-10-23) ---- */
CREATE OR ALTER PROCEDURE dbo.usp_Customers_GetById @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, CustomerCode, CustomerTypeId, CustomerName, CustomerPhone, CustomerEmail, Address, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate, AssignedEmployeeId
    FROM dbo.Customers WHERE CustomerId = @Id;
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Customers_GetPaged
    @PageNumber INT = 1, @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1) FROM dbo.Customers;
    SELECT @TotalCount AS TotalCount;
    SELECT CustomerId, CustomerCode, CustomerTypeId, CustomerName, CustomerPhone, CustomerEmail, Address, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate, AssignedEmployeeId
    FROM dbo.Customers
    ORDER BY CustomerId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Customers_Insert
    @CustomerCode NVARCHAR(50) = NULL, @CustomerTypeId INT, @CustomerName NVARCHAR(150),
    @CustomerPhone NVARCHAR(20) = NULL, @CustomerEmail NVARCHAR(150) = NULL, @Address NVARCHAR(255) = NULL,
    @IsActive BIT, @CreatedBy INT = NULL, @AssignedEmployeeId INT = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId=@CustomerTypeId AND IsActive=1)
        THROW 50002, 'Invalid or inactive Customer Type.', 1;
    IF @AssignedEmployeeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE EmployeeId=@AssignedEmployeeId AND IsActive=1)
        THROW 50005, 'The assigned employee does not exist or is not active.', 1;

    SET @CustomerCode = NULLIF(LTRIM(RTRIM(@CustomerCode)), N'');
    BEGIN TRANSACTION;
        DECLARE @lock INT;
        EXEC @lock = sys.sp_getapplock @Resource = N'CustomerClientId', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
        IF @lock < 0 BEGIN ROLLBACK; THROW 50004, 'Could not allocate a Client ID - please try again.', 1; END;

        IF @CustomerCode IS NOT NULL
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerCode = @CustomerCode)
            BEGIN ROLLBACK; THROW 50003, 'This Client ID already exists.', 1; END;
        END
        ELSE
            SET @CustomerCode = dbo.fn_Customers_NextClientId();

        INSERT dbo.Customers(CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,AssignedEmployeeId)
        VALUES(@CustomerCode,@CustomerTypeId,@CustomerName,@CustomerPhone,@CustomerEmail,@Address,@IsActive,@CreatedBy,SYSUTCDATETIME(),@AssignedEmployeeId);
        DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    COMMIT TRANSACTION;

    SELECT CustomerId,CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate,AssignedEmployeeId
    FROM dbo.Customers WHERE CustomerId=@NewId;
END;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Customers_Update
    @Id INT, @CustomerCode NVARCHAR(50), @CustomerTypeId INT, @CustomerName NVARCHAR(150),
    @CustomerPhone NVARCHAR(20)=NULL, @CustomerEmail NVARCHAR(150)=NULL, @Address NVARCHAR(255)=NULL,
    @IsActive BIT, @ModifiedBy INT=NULL, @AssignedEmployeeId INT = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId=@Id) RETURN;
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId=@CustomerTypeId AND IsActive=1)
        THROW 50002, 'Invalid or inactive Customer Type.', 1;
    -- A customer may keep an employee who has since been deactivated; a different employee must be active.
    IF @AssignedEmployeeId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId=@Id AND AssignedEmployeeId=@AssignedEmployeeId)
       AND NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE EmployeeId=@AssignedEmployeeId AND IsActive=1)
        THROW 50005, 'The assigned employee does not exist or is not active.', 1;
    UPDATE dbo.Customers SET CustomerTypeId=@CustomerTypeId, CustomerName=@CustomerName, CustomerPhone=@CustomerPhone,
        CustomerEmail=@CustomerEmail, Address=@Address, IsActive=@IsActive, AssignedEmployeeId=@AssignedEmployeeId,
        ModifiedBy=@ModifiedBy, ModifiedDate=SYSUTCDATETIME()
    WHERE CustomerId=@Id;
    SELECT CustomerId,CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate,AssignedEmployeeId
    FROM dbo.Customers WHERE CustomerId=@Id;
END;
GO
/* ---- Customers of one employee ---- */
CREATE OR ALTER PROCEDURE dbo.usp_Employees_GetCustomers @EmployeeId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, CustomerCode, CustomerName, CustomerPhone, IsActive
    FROM dbo.Customers WHERE AssignedEmployeeId = @EmployeeId
    ORDER BY CustomerName;
END;
GO

/* ---- New tables and their procedures ---- */
/* EmployeeSalaries */
IF OBJECT_ID(N'dbo.EmployeeSalaries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeSalaries
    (
        EmployeeSalaryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeSalaries PRIMARY KEY,
        EmployeeId int NOT NULL,
        BasicSalary decimal(12,2) NOT NULL,
        Allowances decimal(12,2) NULL,
        EffectiveFrom date NOT NULL,
        Notes nvarchar(500) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_EmployeeSalaries_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_EmployeeSalaries_Employees FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees (EmployeeId) ON DELETE CASCADE
    );
    CREATE INDEX IX_EmployeeSalaries_EmployeeId ON dbo.EmployeeSalaries (EmployeeId);
    RAISERROR(N'CREATED dbo.EmployeeSalaries', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeSalaries_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeSalaries_GetPaged;
GO
CREATE PROCEDURE dbo.usp_EmployeeSalaries_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.EmployeeSalaries;

    SELECT
        EmployeeSalaryId,
        EmployeeId,
        BasicSalary,
        Allowances,
        EffectiveFrom,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeSalaries
    ORDER BY EmployeeSalaryId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeSalaries_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeSalaries_GetById;
GO
CREATE PROCEDURE dbo.usp_EmployeeSalaries_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        EmployeeSalaryId,
        EmployeeId,
        BasicSalary,
        Allowances,
        EffectiveFrom,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeSalaries
    WHERE EmployeeSalaryId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeSalaries_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeSalaries_Insert;
GO
CREATE PROCEDURE dbo.usp_EmployeeSalaries_Insert
    @EmployeeId int,
    @BasicSalary decimal(12,2),
    @Allowances decimal(12,2) = NULL,
    @EffectiveFrom date,
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.EmployeeSalaries
    (
        EmployeeId,
        BasicSalary,
        Allowances,
        EffectiveFrom,
        Notes
    )
    VALUES
    (
        @EmployeeId,
        @BasicSalary,
        @Allowances,
        @EffectiveFrom,
        @Notes
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        EmployeeSalaryId,
        EmployeeId,
        BasicSalary,
        Allowances,
        EffectiveFrom,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeSalaries
    WHERE EmployeeSalaryId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeSalaries_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeSalaries_Update;
GO
CREATE PROCEDURE dbo.usp_EmployeeSalaries_Update
    @Id int,
    @EmployeeId int,
    @BasicSalary decimal(12,2),
    @Allowances decimal(12,2) = NULL,
    @EffectiveFrom date,
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.EmployeeSalaries
    SET
        EmployeeId = @EmployeeId,
        BasicSalary = @BasicSalary,
        Allowances = @Allowances,
        EffectiveFrom = @EffectiveFrom,
        Notes = @Notes,
        ModifiedDate = SYSUTCDATETIME()
    WHERE EmployeeSalaryId = @Id;

    SELECT
        EmployeeSalaryId,
        EmployeeId,
        BasicSalary,
        Allowances,
        EffectiveFrom,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeSalaries
    WHERE EmployeeSalaryId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeSalaries_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeSalaries_Delete;
GO
CREATE PROCEDURE dbo.usp_EmployeeSalaries_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.EmployeeSalaries WHERE EmployeeSalaryId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* EmployeeDocuments */
IF OBJECT_ID(N'dbo.EmployeeDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeDocuments
    (
        EmployeeDocumentId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeDocuments PRIMARY KEY,
        EmployeeId int NOT NULL,
        DocumentType nvarchar(60) NOT NULL,
        DocumentNumber nvarchar(80) NULL,
        IssueDate date NULL,
        ExpiryDate date NULL,
        DocumentLocation nvarchar(500) NULL,
        Status nvarchar(30) NOT NULL CONSTRAINT DF_EmployeeDocuments_Status DEFAULT (N'Active'),
        Notes nvarchar(500) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_EmployeeDocuments_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_EmployeeDocuments_Employees FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees (EmployeeId) ON DELETE CASCADE
    );
    CREATE INDEX IX_EmployeeDocuments_EmployeeId ON dbo.EmployeeDocuments (EmployeeId);
    RAISERROR(N'CREATED dbo.EmployeeDocuments', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeDocuments_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeDocuments_GetPaged;
GO
CREATE PROCEDURE dbo.usp_EmployeeDocuments_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.EmployeeDocuments;

    SELECT
        EmployeeDocumentId,
        EmployeeId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeDocuments
    ORDER BY EmployeeDocumentId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeDocuments_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeDocuments_GetById;
GO
CREATE PROCEDURE dbo.usp_EmployeeDocuments_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        EmployeeDocumentId,
        EmployeeId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeDocuments
    WHERE EmployeeDocumentId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeDocuments_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeDocuments_Insert;
GO
CREATE PROCEDURE dbo.usp_EmployeeDocuments_Insert
    @EmployeeId int,
    @DocumentType nvarchar(60),
    @DocumentNumber nvarchar(80) = NULL,
    @IssueDate date = NULL,
    @ExpiryDate date = NULL,
    @DocumentLocation nvarchar(500) = NULL,
    @Status nvarchar(30) = N'Active',
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.EmployeeDocuments
    (
        EmployeeId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes
    )
    VALUES
    (
        @EmployeeId,
        @DocumentType,
        @DocumentNumber,
        @IssueDate,
        @ExpiryDate,
        @DocumentLocation,
        @Status,
        @Notes
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        EmployeeDocumentId,
        EmployeeId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeDocuments
    WHERE EmployeeDocumentId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeDocuments_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeDocuments_Update;
GO
CREATE PROCEDURE dbo.usp_EmployeeDocuments_Update
    @Id int,
    @EmployeeId int,
    @DocumentType nvarchar(60),
    @DocumentNumber nvarchar(80) = NULL,
    @IssueDate date = NULL,
    @ExpiryDate date = NULL,
    @DocumentLocation nvarchar(500) = NULL,
    @Status nvarchar(30) = N'Active',
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.EmployeeDocuments
    SET
        EmployeeId = @EmployeeId,
        DocumentType = @DocumentType,
        DocumentNumber = @DocumentNumber,
        IssueDate = @IssueDate,
        ExpiryDate = @ExpiryDate,
        DocumentLocation = @DocumentLocation,
        Status = @Status,
        Notes = @Notes,
        ModifiedDate = SYSUTCDATETIME()
    WHERE EmployeeDocumentId = @Id;

    SELECT
        EmployeeDocumentId,
        EmployeeId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.EmployeeDocuments
    WHERE EmployeeDocumentId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_EmployeeDocuments_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_EmployeeDocuments_Delete;
GO
CREATE PROCEDURE dbo.usp_EmployeeDocuments_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.EmployeeDocuments WHERE EmployeeDocumentId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* Vendors */
IF OBJECT_ID(N'dbo.Vendors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Vendors
    (
        VendorId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Vendors PRIMARY KEY,
        VendorCode NVARCHAR(20) NULL,
        VendorName nvarchar(200) NOT NULL,
        VendorType nvarchar(20) NOT NULL CONSTRAINT DF_Vendors_VendorType DEFAULT (N'Company'),
        ContactPerson nvarchar(150) NULL,
        Mobile nvarchar(20) NULL,
        Email nvarchar(150) NULL,
        Address nvarchar(500) NULL,
        TradeLicenceNo nvarchar(60) NULL,
        LicenceExpiryDate date NULL,
        TaxRegNo nvarchar(60) NULL,
        ServicesProvided nvarchar(1000) NULL,
        AgreedCharges nvarchar(1000) NULL,
        PaymentTerms nvarchar(300) NULL,
        Notes nvarchar(500) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Vendors_IsActive DEFAULT (1),
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_Vendors_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL
    );
    CREATE UNIQUE INDEX UX_Vendors_VendorCode ON dbo.Vendors (VendorCode) WHERE VendorCode IS NOT NULL;
    CREATE UNIQUE INDEX UX_Vendors_TradeLicenceNo ON dbo.Vendors (TradeLicenceNo) WHERE TradeLicenceNo IS NOT NULL;
    CREATE UNIQUE INDEX UX_Vendors_TaxRegNo ON dbo.Vendors (TaxRegNo) WHERE TaxRegNo IS NOT NULL;
    CREATE UNIQUE INDEX UX_Vendors_Mobile ON dbo.Vendors (Mobile) WHERE Mobile IS NOT NULL;
    CREATE UNIQUE INDEX UX_Vendors_Email ON dbo.Vendors (Email) WHERE Email IS NOT NULL;
    RAISERROR(N'CREATED dbo.Vendors', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_Vendors_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Vendors_GetPaged;
GO
CREATE PROCEDURE dbo.usp_Vendors_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.Vendors;

    SELECT
        VendorId,
        VendorCode,
        VendorName,
        VendorType,
        ContactPerson,
        Mobile,
        Email,
        Address,
        TradeLicenceNo,
        LicenceExpiryDate,
        TaxRegNo,
        ServicesProvided,
        AgreedCharges,
        PaymentTerms,
        Notes,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Vendors
    ORDER BY VendorId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_Vendors_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Vendors_GetById;
GO
CREATE PROCEDURE dbo.usp_Vendors_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        VendorId,
        VendorCode,
        VendorName,
        VendorType,
        ContactPerson,
        Mobile,
        Email,
        Address,
        TradeLicenceNo,
        LicenceExpiryDate,
        TaxRegNo,
        ServicesProvided,
        AgreedCharges,
        PaymentTerms,
        Notes,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Vendors
    WHERE VendorId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_Vendors_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Vendors_Insert;
GO
CREATE PROCEDURE dbo.usp_Vendors_Insert
    @VendorName nvarchar(200),
    @VendorType nvarchar(20) = N'Company',
    @ContactPerson nvarchar(150) = NULL,
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @Address nvarchar(500) = NULL,
    @TradeLicenceNo nvarchar(60) = NULL,
    @LicenceExpiryDate date = NULL,
    @TaxRegNo nvarchar(60) = NULL,
    @ServicesProvided nvarchar(1000) = NULL,
    @AgreedCharges nvarchar(1000) = NULL,
    @PaymentTerms nvarchar(300) = NULL,
    @Notes nvarchar(500) = NULL,
    @IsActive bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Vendors
    (
        VendorName,
        VendorType,
        ContactPerson,
        Mobile,
        Email,
        Address,
        TradeLicenceNo,
        LicenceExpiryDate,
        TaxRegNo,
        ServicesProvided,
        AgreedCharges,
        PaymentTerms,
        Notes,
        IsActive
    )
    VALUES
    (
        @VendorName,
        @VendorType,
        @ContactPerson,
        @Mobile,
        @Email,
        @Address,
        @TradeLicenceNo,
        @LicenceExpiryDate,
        @TaxRegNo,
        @ServicesProvided,
        @AgreedCharges,
        @PaymentTerms,
        @Notes,
        @IsActive
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    UPDATE dbo.Vendors SET VendorCode = N'VEN-' + RIGHT(N'0000' + CAST(VendorId AS nvarchar(10)), 4) WHERE VendorId = @NewId;

    SELECT
        VendorId,
        VendorCode,
        VendorName,
        VendorType,
        ContactPerson,
        Mobile,
        Email,
        Address,
        TradeLicenceNo,
        LicenceExpiryDate,
        TaxRegNo,
        ServicesProvided,
        AgreedCharges,
        PaymentTerms,
        Notes,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Vendors
    WHERE VendorId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_Vendors_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Vendors_Update;
GO
CREATE PROCEDURE dbo.usp_Vendors_Update
    @Id int,
    @VendorName nvarchar(200),
    @VendorType nvarchar(20) = N'Company',
    @ContactPerson nvarchar(150) = NULL,
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @Address nvarchar(500) = NULL,
    @TradeLicenceNo nvarchar(60) = NULL,
    @LicenceExpiryDate date = NULL,
    @TaxRegNo nvarchar(60) = NULL,
    @ServicesProvided nvarchar(1000) = NULL,
    @AgreedCharges nvarchar(1000) = NULL,
    @PaymentTerms nvarchar(300) = NULL,
    @Notes nvarchar(500) = NULL,
    @IsActive bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Vendors
    SET
        VendorName = @VendorName,
        VendorType = @VendorType,
        ContactPerson = @ContactPerson,
        Mobile = @Mobile,
        Email = @Email,
        Address = @Address,
        TradeLicenceNo = @TradeLicenceNo,
        LicenceExpiryDate = @LicenceExpiryDate,
        TaxRegNo = @TaxRegNo,
        ServicesProvided = @ServicesProvided,
        AgreedCharges = @AgreedCharges,
        PaymentTerms = @PaymentTerms,
        Notes = @Notes,
        IsActive = @IsActive,
        ModifiedDate = SYSUTCDATETIME()
    WHERE VendorId = @Id;

    SELECT
        VendorId,
        VendorCode,
        VendorName,
        VendorType,
        ContactPerson,
        Mobile,
        Email,
        Address,
        TradeLicenceNo,
        LicenceExpiryDate,
        TaxRegNo,
        ServicesProvided,
        AgreedCharges,
        PaymentTerms,
        Notes,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Vendors
    WHERE VendorId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_Vendors_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_Vendors_Delete;
GO
CREATE PROCEDURE dbo.usp_Vendors_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.Vendors WHERE VendorId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* VendorDocuments */
IF OBJECT_ID(N'dbo.VendorDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VendorDocuments
    (
        VendorDocumentId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VendorDocuments PRIMARY KEY,
        VendorId int NOT NULL,
        DocumentType nvarchar(60) NOT NULL,
        DocumentNumber nvarchar(80) NULL,
        IssueDate date NULL,
        ExpiryDate date NULL,
        DocumentLocation nvarchar(500) NULL,
        Status nvarchar(30) NOT NULL CONSTRAINT DF_VendorDocuments_Status DEFAULT (N'Active'),
        Notes nvarchar(500) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_VendorDocuments_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_VendorDocuments_Vendors FOREIGN KEY (VendorId) REFERENCES dbo.Vendors (VendorId) ON DELETE CASCADE
    );
    CREATE INDEX IX_VendorDocuments_VendorId ON dbo.VendorDocuments (VendorId);
    RAISERROR(N'CREATED dbo.VendorDocuments', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorDocuments_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorDocuments_GetPaged;
GO
CREATE PROCEDURE dbo.usp_VendorDocuments_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.VendorDocuments;

    SELECT
        VendorDocumentId,
        VendorId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorDocuments
    ORDER BY VendorDocumentId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorDocuments_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorDocuments_GetById;
GO
CREATE PROCEDURE dbo.usp_VendorDocuments_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        VendorDocumentId,
        VendorId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorDocuments
    WHERE VendorDocumentId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorDocuments_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorDocuments_Insert;
GO
CREATE PROCEDURE dbo.usp_VendorDocuments_Insert
    @VendorId int,
    @DocumentType nvarchar(60),
    @DocumentNumber nvarchar(80) = NULL,
    @IssueDate date = NULL,
    @ExpiryDate date = NULL,
    @DocumentLocation nvarchar(500) = NULL,
    @Status nvarchar(30) = N'Active',
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.VendorDocuments
    (
        VendorId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes
    )
    VALUES
    (
        @VendorId,
        @DocumentType,
        @DocumentNumber,
        @IssueDate,
        @ExpiryDate,
        @DocumentLocation,
        @Status,
        @Notes
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        VendorDocumentId,
        VendorId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorDocuments
    WHERE VendorDocumentId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorDocuments_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorDocuments_Update;
GO
CREATE PROCEDURE dbo.usp_VendorDocuments_Update
    @Id int,
    @VendorId int,
    @DocumentType nvarchar(60),
    @DocumentNumber nvarchar(80) = NULL,
    @IssueDate date = NULL,
    @ExpiryDate date = NULL,
    @DocumentLocation nvarchar(500) = NULL,
    @Status nvarchar(30) = N'Active',
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.VendorDocuments
    SET
        VendorId = @VendorId,
        DocumentType = @DocumentType,
        DocumentNumber = @DocumentNumber,
        IssueDate = @IssueDate,
        ExpiryDate = @ExpiryDate,
        DocumentLocation = @DocumentLocation,
        Status = @Status,
        Notes = @Notes,
        ModifiedDate = SYSUTCDATETIME()
    WHERE VendorDocumentId = @Id;

    SELECT
        VendorDocumentId,
        VendorId,
        DocumentType,
        DocumentNumber,
        IssueDate,
        ExpiryDate,
        DocumentLocation,
        Status,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorDocuments
    WHERE VendorDocumentId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorDocuments_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorDocuments_Delete;
GO
CREATE PROCEDURE dbo.usp_VendorDocuments_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.VendorDocuments WHERE VendorDocumentId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* VendorPaymentDetails */
IF OBJECT_ID(N'dbo.VendorPaymentDetails', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VendorPaymentDetails
    (
        VendorPaymentDetailId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VendorPaymentDetails PRIMARY KEY,
        VendorId int NOT NULL,
        AccountName nvarchar(150) NOT NULL,
        BankName nvarchar(150) NULL,
        AccountNumber nvarchar(60) NULL,
        Branch nvarchar(150) NULL,
        SwiftCode nvarchar(20) NULL,
        PreferredMethod nvarchar(30) NOT NULL CONSTRAINT DF_VendorPaymentDetails_PreferredMethod DEFAULT (N'Bank Transfer'),
        IsPrimary bit NOT NULL CONSTRAINT DF_VendorPaymentDetails_IsPrimary DEFAULT (0),
        Notes nvarchar(500) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_VendorPaymentDetails_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_VendorPaymentDetails_Vendors FOREIGN KEY (VendorId) REFERENCES dbo.Vendors (VendorId) ON DELETE CASCADE
    );
    CREATE INDEX IX_VendorPaymentDetails_VendorId ON dbo.VendorPaymentDetails (VendorId);
    RAISERROR(N'CREATED dbo.VendorPaymentDetails', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorPaymentDetails_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorPaymentDetails_GetPaged;
GO
CREATE PROCEDURE dbo.usp_VendorPaymentDetails_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.VendorPaymentDetails;

    SELECT
        VendorPaymentDetailId,
        VendorId,
        AccountName,
        BankName,
        AccountNumber,
        Branch,
        SwiftCode,
        PreferredMethod,
        IsPrimary,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorPaymentDetails
    ORDER BY VendorPaymentDetailId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorPaymentDetails_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorPaymentDetails_GetById;
GO
CREATE PROCEDURE dbo.usp_VendorPaymentDetails_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        VendorPaymentDetailId,
        VendorId,
        AccountName,
        BankName,
        AccountNumber,
        Branch,
        SwiftCode,
        PreferredMethod,
        IsPrimary,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorPaymentDetails
    WHERE VendorPaymentDetailId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorPaymentDetails_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorPaymentDetails_Insert;
GO
CREATE PROCEDURE dbo.usp_VendorPaymentDetails_Insert
    @VendorId int,
    @AccountName nvarchar(150),
    @BankName nvarchar(150) = NULL,
    @AccountNumber nvarchar(60) = NULL,
    @Branch nvarchar(150) = NULL,
    @SwiftCode nvarchar(20) = NULL,
    @PreferredMethod nvarchar(30) = N'Bank Transfer',
    @IsPrimary bit = 0,
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.VendorPaymentDetails
    (
        VendorId,
        AccountName,
        BankName,
        AccountNumber,
        Branch,
        SwiftCode,
        PreferredMethod,
        IsPrimary,
        Notes
    )
    VALUES
    (
        @VendorId,
        @AccountName,
        @BankName,
        @AccountNumber,
        @Branch,
        @SwiftCode,
        @PreferredMethod,
        @IsPrimary,
        @Notes
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        VendorPaymentDetailId,
        VendorId,
        AccountName,
        BankName,
        AccountNumber,
        Branch,
        SwiftCode,
        PreferredMethod,
        IsPrimary,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorPaymentDetails
    WHERE VendorPaymentDetailId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorPaymentDetails_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorPaymentDetails_Update;
GO
CREATE PROCEDURE dbo.usp_VendorPaymentDetails_Update
    @Id int,
    @VendorId int,
    @AccountName nvarchar(150),
    @BankName nvarchar(150) = NULL,
    @AccountNumber nvarchar(60) = NULL,
    @Branch nvarchar(150) = NULL,
    @SwiftCode nvarchar(20) = NULL,
    @PreferredMethod nvarchar(30) = N'Bank Transfer',
    @IsPrimary bit = 0,
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.VendorPaymentDetails
    SET
        VendorId = @VendorId,
        AccountName = @AccountName,
        BankName = @BankName,
        AccountNumber = @AccountNumber,
        Branch = @Branch,
        SwiftCode = @SwiftCode,
        PreferredMethod = @PreferredMethod,
        IsPrimary = @IsPrimary,
        Notes = @Notes,
        ModifiedDate = SYSUTCDATETIME()
    WHERE VendorPaymentDetailId = @Id;

    SELECT
        VendorPaymentDetailId,
        VendorId,
        AccountName,
        BankName,
        AccountNumber,
        Branch,
        SwiftCode,
        PreferredMethod,
        IsPrimary,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorPaymentDetails
    WHERE VendorPaymentDetailId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorPaymentDetails_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorPaymentDetails_Delete;
GO
CREATE PROCEDURE dbo.usp_VendorPaymentDetails_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.VendorPaymentDetails WHERE VendorPaymentDetailId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* VendorServices */
IF OBJECT_ID(N'dbo.VendorServices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VendorServices
    (
        VendorServiceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VendorServices PRIMARY KEY,
        VendorId int NOT NULL,
        ServiceId int NOT NULL,
        AgreedCharge decimal(12,2) NOT NULL,
        Notes nvarchar(500) NULL,
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_VendorServices_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_VendorServices_Vendors FOREIGN KEY (VendorId) REFERENCES dbo.Vendors (VendorId) ON DELETE CASCADE,
        CONSTRAINT FK_VendorServices_Services FOREIGN KEY (ServiceId) REFERENCES dbo.Services (ServiceId)
    );
    CREATE INDEX IX_VendorServices_VendorId ON dbo.VendorServices (VendorId);
    CREATE UNIQUE INDEX UX_VendorServices_VendorService ON dbo.VendorServices (VendorId, ServiceId);
    RAISERROR(N'CREATED dbo.VendorServices', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorServices_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorServices_GetPaged;
GO
CREATE PROCEDURE dbo.usp_VendorServices_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.VendorServices;

    SELECT
        VendorServiceId,
        VendorId,
        ServiceId,
        AgreedCharge,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorServices
    ORDER BY VendorServiceId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorServices_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorServices_GetById;
GO
CREATE PROCEDURE dbo.usp_VendorServices_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        VendorServiceId,
        VendorId,
        ServiceId,
        AgreedCharge,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorServices
    WHERE VendorServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorServices_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorServices_Insert;
GO
CREATE PROCEDURE dbo.usp_VendorServices_Insert
    @VendorId int,
    @ServiceId int,
    @AgreedCharge decimal(12,2),
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.VendorServices
    (
        VendorId,
        ServiceId,
        AgreedCharge,
        Notes
    )
    VALUES
    (
        @VendorId,
        @ServiceId,
        @AgreedCharge,
        @Notes
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        VendorServiceId,
        VendorId,
        ServiceId,
        AgreedCharge,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorServices
    WHERE VendorServiceId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorServices_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorServices_Update;
GO
CREATE PROCEDURE dbo.usp_VendorServices_Update
    @Id int,
    @VendorId int,
    @ServiceId int,
    @AgreedCharge decimal(12,2),
    @Notes nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.VendorServices
    SET
        VendorId = @VendorId,
        ServiceId = @ServiceId,
        AgreedCharge = @AgreedCharge,
        Notes = @Notes,
        ModifiedDate = SYSUTCDATETIME()
    WHERE VendorServiceId = @Id;

    SELECT
        VendorServiceId,
        VendorId,
        ServiceId,
        AgreedCharge,
        Notes,
        CreatedDate,
        ModifiedDate
    FROM dbo.VendorServices
    WHERE VendorServiceId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_VendorServices_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_VendorServices_Delete;
GO
CREATE PROCEDURE dbo.usp_VendorServices_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.VendorServices WHERE VendorServiceId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* ServiceChecklistItems */
IF OBJECT_ID(N'dbo.ServiceChecklistItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ServiceChecklistItems
    (
        ServiceChecklistItemId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ServiceChecklistItems PRIMARY KEY,
        ServiceId int NOT NULL,
        DocumentName nvarchar(150) NOT NULL,
        IsRequired bit NOT NULL CONSTRAINT DF_ServiceChecklistItems_IsRequired DEFAULT (1),
        ExpiryRequired bit NOT NULL CONSTRAINT DF_ServiceChecklistItems_ExpiryRequired DEFAULT (0),
        DisplayOrder int NOT NULL CONSTRAINT DF_ServiceChecklistItems_DisplayOrder DEFAULT (0),
        IsActive bit NOT NULL CONSTRAINT DF_ServiceChecklistItems_IsActive DEFAULT (1),
        CreatedDate DATETIME2(7) NOT NULL CONSTRAINT DF_ServiceChecklistItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate DATETIME2(7) NULL,
        CONSTRAINT FK_ServiceChecklistItems_Services FOREIGN KEY (ServiceId) REFERENCES dbo.Services (ServiceId) ON DELETE CASCADE
    );
    CREATE INDEX IX_ServiceChecklistItems_ServiceId ON dbo.ServiceChecklistItems (ServiceId);
    CREATE UNIQUE INDEX UX_ServiceChecklistItems_Doc ON dbo.ServiceChecklistItems (ServiceId, DocumentName);
    RAISERROR(N'CREATED dbo.ServiceChecklistItems', 10, 1) WITH NOWAIT;
END
GO
IF OBJECT_ID(N'dbo.usp_ServiceChecklistItems_GetPaged', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_ServiceChecklistItems_GetPaged;
GO
CREATE PROCEDURE dbo.usp_ServiceChecklistItems_GetPaged
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount FROM dbo.ServiceChecklistItems;

    SELECT
        ServiceChecklistItemId,
        ServiceId,
        DocumentName,
        IsRequired,
        ExpiryRequired,
        DisplayOrder,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.ServiceChecklistItems
    ORDER BY ServiceChecklistItemId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
IF OBJECT_ID(N'dbo.usp_ServiceChecklistItems_GetById', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_ServiceChecklistItems_GetById;
GO
CREATE PROCEDURE dbo.usp_ServiceChecklistItems_GetById
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        ServiceChecklistItemId,
        ServiceId,
        DocumentName,
        IsRequired,
        ExpiryRequired,
        DisplayOrder,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.ServiceChecklistItems
    WHERE ServiceChecklistItemId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_ServiceChecklistItems_Insert', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_ServiceChecklistItems_Insert;
GO
CREATE PROCEDURE dbo.usp_ServiceChecklistItems_Insert
    @ServiceId int,
    @DocumentName nvarchar(150),
    @IsRequired bit = 1,
    @ExpiryRequired bit = 0,
    @DisplayOrder int = 0,
    @IsActive bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ServiceChecklistItems
    (
        ServiceId,
        DocumentName,
        IsRequired,
        ExpiryRequired,
        DisplayOrder,
        IsActive
    )
    VALUES
    (
        @ServiceId,
        @DocumentName,
        @IsRequired,
        @ExpiryRequired,
        @DisplayOrder,
        @IsActive
    );

    DECLARE @NewId int = CONVERT(int, SCOPE_IDENTITY());
    SELECT
        ServiceChecklistItemId,
        ServiceId,
        DocumentName,
        IsRequired,
        ExpiryRequired,
        DisplayOrder,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.ServiceChecklistItems
    WHERE ServiceChecklistItemId = @NewId;
END
GO
IF OBJECT_ID(N'dbo.usp_ServiceChecklistItems_Update', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_ServiceChecklistItems_Update;
GO
CREATE PROCEDURE dbo.usp_ServiceChecklistItems_Update
    @Id int,
    @ServiceId int,
    @DocumentName nvarchar(150),
    @IsRequired bit = 1,
    @ExpiryRequired bit = 0,
    @DisplayOrder int = 0,
    @IsActive bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.ServiceChecklistItems
    SET
        ServiceId = @ServiceId,
        DocumentName = @DocumentName,
        IsRequired = @IsRequired,
        ExpiryRequired = @ExpiryRequired,
        DisplayOrder = @DisplayOrder,
        IsActive = @IsActive,
        ModifiedDate = SYSUTCDATETIME()
    WHERE ServiceChecklistItemId = @Id;

    SELECT
        ServiceChecklistItemId,
        ServiceId,
        DocumentName,
        IsRequired,
        ExpiryRequired,
        DisplayOrder,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.ServiceChecklistItems
    WHERE ServiceChecklistItemId = @Id;
END
GO
IF OBJECT_ID(N'dbo.usp_ServiceChecklistItems_Delete', N'P') IS NOT NULL DROP PROCEDURE dbo.usp_ServiceChecklistItems_Delete;
GO
CREATE PROCEDURE dbo.usp_ServiceChecklistItems_Delete
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.ServiceChecklistItems WHERE ServiceChecklistItemId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/* ---- Menu items and role permissions ----
   Employee Documents and Employee Salaries sit beside Employees. Salaries start with the Payments rights (finance roles only).
   Vendors is its OWN top-level menu section, separate from Masters and Customers: 'Vendors' (parent), then Vendor List,
   Vendor Documents and Vendor Payment Details (finance roles only). Adjust any role under Roles & access. */
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
BEGIN
    DECLARE @Parent INT, @App NVARCHAR(30), @Group NVARCHAR(100), @Order INT, @VendorParent INT, @MaxOrder INT;
    SELECT TOP (1) @Parent = ParentMenuItemId, @App = ApplicationCode, @Group = MenuGroup, @Order = DisplayOrder
    FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'employees' ORDER BY MenuItemId;
    SELECT @MaxOrder = ISNULL(MAX(DisplayOrder), 0) FROM dbo.MenuItems;

    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendor-management')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'vendor-management', N'Vendors', N'briefcase', NULL, 1, N'Vendors', NULL, @MaxOrder + 1, ISNULL(@App, N'TRACKER'), 0, NULL, SYSUTCDATETIME());
    SELECT @VendorParent = MenuItemId FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendor-management';
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'employee-documents')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'employee-documents', N'Employee Documents', N'folder', NULL, 1, @Group, N'/employee-documents', ISNULL(@Order, 0) + 1, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'employee-salaries')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'employee-salaries', N'Employee Salaries', N'coin', NULL, 1, @Group, N'/employee-salaries', ISNULL(@Order, 0) + 2, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendors')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'vendors', N'Vendor List', N'briefcase', NULL, 1, N'Vendors', N'/vendors', 1, ISNULL(@App, N'TRACKER'), 0, @VendorParent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendor-documents')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'vendor-documents', N'Vendor Documents', N'description', NULL, 1, N'Vendors', N'/vendor-documents', 2, ISNULL(@App, N'TRACKER'), 0, @VendorParent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendor-payment-details')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'vendor-payment-details', N'Vendor Payment Details', N'credit_card', NULL, 1, N'Vendors', N'/vendor-payment-details', 3, ISNULL(@App, N'TRACKER'), 0, @VendorParent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'vendor-services')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'vendor-services', N'Vendor Services & Charges', N'list_alt', NULL, 1, N'Vendors', N'/vendor-services', 4, ISNULL(@App, N'TRACKER'), 0, @VendorParent, SYSUTCDATETIME());
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'service-checklist')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'service-checklist', N'Service Document Checklist', N'checklist', NULL, 1, @Group, N'/service-checklist', ISNULL(@Order, 0) + 3, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
BEGIN
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'employee-documents', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'employees'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'employee-documents');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'employee-salaries', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'payments'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'employee-salaries');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'vendor-management', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'employees'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'vendor-management');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'vendors', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'employees'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'vendors');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'vendor-documents', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'employees'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'vendor-documents');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'vendor-payment-details', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'payments'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'vendor-payment-details');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'vendor-services', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'employees'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'vendor-services');
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'service-checklist', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'services'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'service-checklist');
END
GO
