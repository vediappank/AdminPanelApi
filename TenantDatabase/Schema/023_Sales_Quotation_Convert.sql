/* =====================================================================================
   Sales Quotation (before the tracker) + Convert to Renewal Tracker
   Idempotent. SQL Server 2016+. Run after 2026-11-17_leads_and_sales.sql.

   Business Panel flow (Requirements v1.1, A3-A5; NOT the Admin Panel's product-sales flow):
     Sales marks the sale Confirmed  -> Accounts prepares the quotation (sale: Awaiting Quotation / Accounts)
     -> Sent to Customer / Awaiting Approval -> Approved (sale: Quotation Approved; invoice allowed)
     -> "Convert to Renewal Tracker" creates the job (sale: Job Created).
   Quotation statuses: Draft, Sent to Customer, Awaiting Approval, Approved, Rejected, Revised.
   Only Draft / Revised can be edited; Rejected sends the sale back to Follow-up Required.

   Services are shown and stored grouped by Category / Sub Category (snapshotted on each line).

   Adds
     dbo.SalesQuotations        SQ-0001 (automatic), one per quote on a sale
     dbo.SalesQuotationItems    lines, each with Category + Sub Category + qty/amount/VAT %
     dbo.SalesOpportunities.TrackerId / ConvertedToTrackerDate
     usp_SalesQuotations_GetAll / GetBySale / Delete / Generate / SaveItems / SetStatus / ConvertToTracker
     Menu 'Sale Quotations' (key sale-quotations) under Leads & Sales

   Conversion creates, in one transaction:
     Trackers row (customer, assigned employee) -> one TrackerCategorySubCategorySpecs per
     category/sub-category -> one TrackerServiceSpecs per line -> a regular Quotations row
     (+ QuotationItems) linked to the tracker, so the existing invoice / payment / renewal
     screens keep working unchanged.

   Error codes (mapped to HTTP 400 by SalesQuotationsController):
     50020 quotation not found     50021 only an Accepted quotation converts
     50022 quotation has no lines  50023 quotation is locked (already converted / accepted)
     50024 invalid status          50025 sale has no services to quote
     50026 line references an unknown service
     50027 the sale already has an open quotation
     50028 the sale is not Confirmed yet (Accounts quotes only confirmed sales)
   ===================================================================================== */
SET NOCOUNT ON;
GO

/* ---------- Tables ---------- */
IF OBJECT_ID(N'dbo.SalesQuotations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesQuotations
    (
        SalesQuotationId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesQuotations PRIMARY KEY,
        QuoteNo            NVARCHAR(20)  NULL,
        SalesOpportunityId INT           NOT NULL,
        QuotationStatus    NVARCHAR(20)  NOT NULL CONSTRAINT DF_SalesQuotations_Status DEFAULT (N'Draft'),
        QuoteDate          DATE          NOT NULL CONSTRAINT DF_SalesQuotations_QuoteDate DEFAULT (CONVERT(date, SYSUTCDATETIME())),
        ValidUntil         DATE          NULL,
        Notes              NVARCHAR(1000) NULL,
        TrackerId          INT           NULL,
        QuotationId        INT           NULL,
        CreatedBy          INT           NULL,
        CreatedDate        DATETIME2(7)  NOT NULL CONSTRAINT DF_SalesQuotations_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedDate       DATETIME2(7)  NULL,
        CONSTRAINT FK_SalesQuotations_Sale      FOREIGN KEY (SalesOpportunityId) REFERENCES dbo.SalesOpportunities (SalesOpportunityId) ON DELETE CASCADE,
        CONSTRAINT FK_SalesQuotations_Tracker   FOREIGN KEY (TrackerId)   REFERENCES dbo.Trackers (TrackerId),
        CONSTRAINT FK_SalesQuotations_Quotation FOREIGN KEY (QuotationId) REFERENCES dbo.Quotations (QuotationId),
        CONSTRAINT CK_SalesQuotations_Status CHECK (QuotationStatus IN (N'Draft', N'Sent to Customer', N'Awaiting Approval', N'Approved', N'Rejected', N'Revised'))
    );
    CREATE UNIQUE INDEX UX_SalesQuotations_QuoteNo ON dbo.SalesQuotations (QuoteNo) WHERE QuoteNo IS NOT NULL;
    CREATE INDEX IX_SalesQuotations_Sale ON dbo.SalesQuotations (SalesOpportunityId);
    RAISERROR(N'CREATED dbo.SalesQuotations', 10, 1) WITH NOWAIT;
END
GO

/* Move an earlier install (Draft/Sent/Accepted/Rejected) to the Business Panel quotation statuses. */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SalesQuotations_Status' AND parent_object_id = OBJECT_ID(N'dbo.SalesQuotations') AND definition LIKE N'%Accepted%')
BEGIN
    ALTER TABLE dbo.SalesQuotations DROP CONSTRAINT CK_SalesQuotations_Status;
    UPDATE dbo.SalesQuotations SET QuotationStatus = CASE QuotationStatus WHEN N'Sent' THEN N'Sent to Customer' WHEN N'Accepted' THEN N'Approved' ELSE QuotationStatus END;
    UPDATE dbo.SalesOpportunities SET QuotationStatus = CASE QuotationStatus WHEN N'Sent' THEN N'Sent to Customer' WHEN N'Accepted' THEN N'Approved' ELSE QuotationStatus END
    WHERE QuotationStatus IN (N'Sent', N'Accepted');
    ALTER TABLE dbo.SalesQuotations ADD CONSTRAINT CK_SalesQuotations_Status CHECK (QuotationStatus IN (N'Draft', N'Sent to Customer', N'Awaiting Approval', N'Approved', N'Rejected', N'Revised'));
    RAISERROR(N'UPDATED CK_SalesQuotations_Status', 10, 1) WITH NOWAIT;
END
GO

IF OBJECT_ID(N'dbo.SalesQuotationItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesQuotationItems
    (
        SalesQuotationItemId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesQuotationItems PRIMARY KEY,
        SalesQuotationId     INT           NOT NULL,
        ServiceId            INT           NOT NULL,
        CategoryId           INT           NOT NULL,
        CategoryName         NVARCHAR(100) NOT NULL,
        SubCategoryId        INT           NOT NULL,
        SubCategoryName      NVARCHAR(100) NOT NULL,
        ServiceName          NVARCHAR(150) NOT NULL,
        Quantity             INT           NOT NULL CONSTRAINT DF_SalesQuotationItems_Qty DEFAULT (1),
        Amount               DECIMAL(18,2) NOT NULL CONSTRAINT DF_SalesQuotationItems_Amount DEFAULT (0),
        VatPercent           DECIMAL(5,2)  NOT NULL CONSTRAINT DF_SalesQuotationItems_Vat DEFAULT (0),
        CreatedDate          DATETIME2(7)  NOT NULL CONSTRAINT DF_SalesQuotationItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_SalesQuotationItems_Quotation FOREIGN KEY (SalesQuotationId) REFERENCES dbo.SalesQuotations (SalesQuotationId) ON DELETE CASCADE,
        CONSTRAINT FK_SalesQuotationItems_Service   FOREIGN KEY (ServiceId) REFERENCES dbo.Services (ServiceId),
        CONSTRAINT CK_SalesQuotationItems_Qty CHECK (Quantity >= 1 AND Amount >= 0 AND VatPercent >= 0)
    );
    CREATE INDEX IX_SalesQuotationItems_Quotation ON dbo.SalesQuotationItems (SalesQuotationId);
    RAISERROR(N'CREATED dbo.SalesQuotationItems', 10, 1) WITH NOWAIT;
END
GO

IF COL_LENGTH(N'dbo.SalesOpportunities', N'TrackerId') IS NULL
BEGIN
    ALTER TABLE dbo.SalesOpportunities ADD TrackerId INT NULL, ConvertedToTrackerDate DATETIME2(7) NULL;
    RAISERROR(N'ADDED dbo.SalesOpportunities.TrackerId', 10, 1) WITH NOWAIT;
END
GO
IF COL_LENGTH(N'dbo.SalesOpportunities', N'TrackerId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SalesOpportunities_Tracker')
    ALTER TABLE dbo.SalesOpportunities ADD CONSTRAINT FK_SalesOpportunities_Tracker FOREIGN KEY (TrackerId) REFERENCES dbo.Trackers (TrackerId);
GO

/* ---------- usp_SalesQuotations_GetBySale : result 1 = quotations (with totals), result 2 = lines ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_GetBySale
    @SalesOpportunityId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT q.SalesQuotationId, q.QuoteNo, q.SalesOpportunityId, q.QuotationStatus, q.QuoteDate, q.ValidUntil, q.Notes,
           q.TrackerId, q.QuotationId, q.CreatedDate,
           ISNULL(t.Subtotal, 0)   AS Subtotal,
           ISNULL(t.VatAmount, 0)  AS VatAmount,
           ISNULL(t.Subtotal, 0) + ISNULL(t.VatAmount, 0) AS GrandTotal
    FROM dbo.SalesQuotations q
    OUTER APPLY (SELECT SUM(i.Quantity * i.Amount) AS Subtotal,
                        SUM(ROUND(i.Quantity * i.Amount * i.VatPercent / 100.0, 2)) AS VatAmount
                 FROM dbo.SalesQuotationItems i WHERE i.SalesQuotationId = q.SalesQuotationId) t
    WHERE q.SalesOpportunityId = @SalesOpportunityId
    ORDER BY q.SalesQuotationId DESC;

    SELECT i.SalesQuotationItemId, i.SalesQuotationId, i.ServiceId,
           i.CategoryId, i.CategoryName, i.SubCategoryId, i.SubCategoryName, i.ServiceName,
           i.Quantity, i.Amount, i.VatPercent,
           ROUND(i.Quantity * i.Amount * i.VatPercent / 100.0, 2) AS VatAmount,
           i.Quantity * i.Amount + ROUND(i.Quantity * i.Amount * i.VatPercent / 100.0, 2) AS LineTotal
    FROM dbo.SalesQuotationItems i
    INNER JOIN dbo.SalesQuotations q ON q.SalesQuotationId = i.SalesQuotationId
    WHERE q.SalesOpportunityId = @SalesOpportunityId
    ORDER BY i.SalesQuotationId DESC, i.CategoryName, i.SubCategoryName, i.ServiceName, i.SalesQuotationItemId;
END
GO

/* ---------- usp_SalesQuotations_Generate : one open quotation per sale ----------
   @ItemsJson = the lines priced on the New quotation form: [{"serviceId":1,"quantity":2,"amount":100,"vatPercent":5}, ...]
   Without it the lines default to the sale's services at their master price. */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_Generate
    @SalesOpportunityId INT,
    @CreatedBy INT = NULL,
    @ItemsJson NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @SaleStatus NVARCHAR(40) = (SELECT SalesStatus FROM dbo.SalesOpportunities WHERE SalesOpportunityId = @SalesOpportunityId);
    IF @SaleStatus IS NULL THROW 50020, 'The sale was not found.', 1;

    DECLARE @Existing INT = (SELECT TOP (1) SalesQuotationId FROM dbo.SalesQuotations
                             WHERE SalesOpportunityId = @SalesOpportunityId AND QuotationStatus <> N'Rejected'
                             ORDER BY SalesQuotationId DESC);
    IF @Existing IS NOT NULL
    BEGIN
        -- A priced form must not silently vanish: tell the user the sale is already quoted.
        IF @ItemsJson IS NOT NULL
            THROW 50027, 'This sale already has an open quotation. Open it from the list, or reject it first.', 1;
        SELECT @Existing AS SalesQuotationId;
        RETURN;
    END

    -- Accounts quotes a sale only once Sales has confirmed it (the customer agreed to proceed).
    IF @SaleStatus NOT IN (N'Confirmed', N'Awaiting Quotation / Accounts')
        THROW 50028, 'Mark the sale Confirmed first. Accounts prepares the quotation after the customer agrees to proceed.', 1;

    DECLARE @In TABLE (ServiceId INT NOT NULL, Quantity INT NOT NULL, Amount DECIMAL(18,2) NOT NULL, VatPercent DECIMAL(5,2) NOT NULL);
    IF @ItemsJson IS NOT NULL
    BEGIN
        INSERT @In (ServiceId, Quantity, Amount, VatPercent)
        SELECT j.serviceId, CASE WHEN ISNULL(j.quantity, 1) < 1 THEN 1 ELSE ISNULL(j.quantity, 1) END,
               CASE WHEN ISNULL(j.amount, 0) < 0 THEN 0 ELSE ISNULL(j.amount, 0) END,
               CASE WHEN ISNULL(j.vatPercent, 0) < 0 THEN 0 ELSE ISNULL(j.vatPercent, 0) END
        FROM OPENJSON(@ItemsJson) WITH (serviceId INT '$.serviceId', quantity INT '$.quantity', amount DECIMAL(18,2) '$.amount', vatPercent DECIMAL(5,2) '$.vatPercent') j
        WHERE j.serviceId IS NOT NULL;
        IF NOT EXISTS (SELECT 1 FROM @In) THROW 50022, 'A quotation needs at least one service line.', 1;
        IF EXISTS (SELECT 1 FROM @In i WHERE NOT EXISTS (SELECT 1 FROM dbo.Services s WHERE s.ServiceId = i.ServiceId))
            THROW 50026, 'One of the services no longer exists.', 1;
    END
    ELSE
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.SalesOpportunityServices WHERE SalesOpportunityId = @SalesOpportunityId)
            THROW 50025, 'Add at least one service to the sale before preparing a quotation.', 1;
        INSERT @In (ServiceId, Quantity, Amount, VatPercent)
        SELECT s.ServiceId, 1, s.Amount, s.VAT
        FROM dbo.SalesOpportunityServices sos INNER JOIN dbo.Services s ON s.ServiceId = sos.ServiceId
        WHERE sos.SalesOpportunityId = @SalesOpportunityId;
    END

    BEGIN TRAN;
        EXEC sp_getapplock @Resource = N'SalesQuotationNo', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;

        DECLARE @Next INT = ISNULL((SELECT MAX(TRY_CONVERT(INT, SUBSTRING(QuoteNo, 4, 10))) FROM dbo.SalesQuotations WHERE QuoteNo LIKE N'SQ-%'), 0) + 1;
        DECLARE @QuoteNo NVARCHAR(20) = N'SQ-' + RIGHT(N'0000' + CONVERT(NVARCHAR(10), @Next), CASE WHEN LEN(CONVERT(NVARCHAR(10), @Next)) > 4 THEN LEN(CONVERT(NVARCHAR(10), @Next)) ELSE 4 END);

        INSERT dbo.SalesQuotations (QuoteNo, SalesOpportunityId, CreatedBy) VALUES (@QuoteNo, @SalesOpportunityId, @CreatedBy);
        DECLARE @Id INT = CONVERT(INT, SCOPE_IDENTITY());

        INSERT dbo.SalesQuotationItems (SalesQuotationId, ServiceId, CategoryId, CategoryName, SubCategoryId, SubCategoryName, ServiceName, Quantity, Amount, VatPercent)
        SELECT @Id, s.ServiceId, c.CategoryId, c.CategoryName, sc.SubCategoryId, sc.SubCategoryName, s.ServiceName, i.Quantity, i.Amount, i.VatPercent
        FROM @In i
        INNER JOIN dbo.Services s ON s.ServiceId = i.ServiceId
        INNER JOIN dbo.ServiceCategories c ON c.CategoryId = s.CategoryId
        INNER JOIN dbo.ServiceSubCategories sc ON sc.SubCategoryId = s.SubCategoryId;

        UPDATE dbo.SalesOpportunities
        SET QuotationStatus = N'Draft', SalesStatus = N'Awaiting Quotation / Accounts', ModifiedDate = SYSUTCDATETIME()
        WHERE SalesOpportunityId = @SalesOpportunityId;
    COMMIT;

    SELECT @Id AS SalesQuotationId;
END
GO

/* ---------- usp_SalesQuotations_SaveItems : replace the lines of a Draft/Sent quotation ----------
   @ItemsJson = [{"serviceId":1,"quantity":2,"amount":100.00,"vatPercent":5}, ...]
   Category / Sub Category / service name are always taken from the Service master. */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_SaveItems
    @SalesQuotationId INT,
    @ItemsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Status NVARCHAR(20), @Tracker INT;
    SELECT @Status = QuotationStatus, @Tracker = TrackerId FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
    IF @Status IS NULL THROW 50020, 'The quotation was not found.', 1;
    IF @Tracker IS NOT NULL OR @Status NOT IN (N'Draft', N'Revised')
        THROW 50023, 'Only a Draft or Revised quotation can be edited. Use Revise first.', 1;

    DECLARE @In TABLE (ServiceId INT NOT NULL, Quantity INT NOT NULL, Amount DECIMAL(18,2) NOT NULL, VatPercent DECIMAL(5,2) NOT NULL);
    INSERT @In (ServiceId, Quantity, Amount, VatPercent)
    SELECT j.serviceId, CASE WHEN ISNULL(j.quantity, 1) < 1 THEN 1 ELSE ISNULL(j.quantity, 1) END,
           CASE WHEN ISNULL(j.amount, 0) < 0 THEN 0 ELSE ISNULL(j.amount, 0) END,
           CASE WHEN ISNULL(j.vatPercent, 0) < 0 THEN 0 ELSE ISNULL(j.vatPercent, 0) END
    FROM OPENJSON(@ItemsJson) WITH (serviceId INT '$.serviceId', quantity INT '$.quantity', amount DECIMAL(18,2) '$.amount', vatPercent DECIMAL(5,2) '$.vatPercent') j
    WHERE j.serviceId IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM @In) THROW 50022, 'A quotation needs at least one service line.', 1;
    IF EXISTS (SELECT 1 FROM @In i WHERE NOT EXISTS (SELECT 1 FROM dbo.Services s WHERE s.ServiceId = i.ServiceId))
        THROW 50026, 'One of the services no longer exists.', 1;

    BEGIN TRAN;
        DELETE FROM dbo.SalesQuotationItems WHERE SalesQuotationId = @SalesQuotationId;
        INSERT dbo.SalesQuotationItems (SalesQuotationId, ServiceId, CategoryId, CategoryName, SubCategoryId, SubCategoryName, ServiceName, Quantity, Amount, VatPercent)
        SELECT @SalesQuotationId, s.ServiceId, c.CategoryId, c.CategoryName, sc.SubCategoryId, sc.SubCategoryName, s.ServiceName, i.Quantity, i.Amount, i.VatPercent
        FROM @In i
        INNER JOIN dbo.Services s ON s.ServiceId = i.ServiceId
        INNER JOIN dbo.ServiceCategories c ON c.CategoryId = s.CategoryId
        INNER JOIN dbo.ServiceSubCategories sc ON sc.SubCategoryId = s.SubCategoryId;
        UPDATE dbo.SalesQuotations SET ModifiedDate = SYSUTCDATETIME() WHERE SalesQuotationId = @SalesQuotationId;
    COMMIT;
END
GO

/* ---------- usp_SalesQuotations_SetStatus : keeps the sale's own status in step ----------
   Draft / Revised            -> sale: Awaiting Quotation / Accounts
   Sent to Customer / Awaiting Approval -> sale: Awaiting Customer
   Approved                   -> sale: Quotation Approved (invoice allowed)
   Rejected                   -> sale: Follow-up Required (Sales follows up, re-confirms, Accounts re-quotes)
   A Lost / Cancelled / Job Created sale is never moved. */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_SetStatus
    @SalesQuotationId INT,
    @Status NVARCHAR(20),
    @Notes NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @Status = LTRIM(RTRIM(@Status));
    IF @Status NOT IN (N'Draft', N'Sent to Customer', N'Awaiting Approval', N'Approved', N'Rejected', N'Revised')
        THROW 50024, 'Status must be Draft, Sent to Customer, Awaiting Approval, Approved, Rejected or Revised.', 1;

    DECLARE @Sale INT, @Tracker INT;
    SELECT @Sale = SalesOpportunityId, @Tracker = TrackerId FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
    IF @Sale IS NULL THROW 50020, 'The quotation was not found.', 1;
    IF @Tracker IS NOT NULL AND @Status <> N'Approved'
        THROW 50023, 'This quotation is already converted to a renewal tracker and stays Approved.', 1;
    IF @Status IN (N'Sent to Customer', N'Awaiting Approval', N'Approved')
       AND NOT EXISTS (SELECT 1 FROM dbo.SalesQuotationItems WHERE SalesQuotationId = @SalesQuotationId)
        THROW 50022, 'A quotation without service lines cannot be sent or approved.', 1;

    BEGIN TRAN;
        UPDATE dbo.SalesQuotations
        SET QuotationStatus = @Status, Notes = ISNULL(@Notes, Notes), ModifiedDate = SYSUTCDATETIME()
        WHERE SalesQuotationId = @SalesQuotationId;

        UPDATE dbo.SalesOpportunities
        SET QuotationStatus = @Status,
            SalesStatus = CASE WHEN SalesStatus IN (N'Lost', N'Cancelled', N'Job Created') THEN SalesStatus
                               WHEN @Status IN (N'Draft', N'Revised') THEN N'Awaiting Quotation / Accounts'
                               WHEN @Status IN (N'Sent to Customer', N'Awaiting Approval') THEN N'Awaiting Customer'
                               WHEN @Status = N'Approved' THEN N'Quotation Approved'
                               WHEN @Status = N'Rejected' THEN N'Follow-up Required'
                               ELSE SalesStatus END,
            ModifiedDate = SYSUTCDATETIME()
        WHERE SalesOpportunityId = @Sale;
    COMMIT;
END
GO

/* ---------- usp_SalesQuotations_ConvertToTracker ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_ConvertToTracker
    @SalesQuotationId INT,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Sale INT, @Status NVARCHAR(20), @QTracker INT;
    SELECT @Sale = SalesOpportunityId, @Status = QuotationStatus, @QTracker = TrackerId
    FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
    IF @Sale IS NULL THROW 50020, 'The quotation was not found.', 1;

    -- Idempotent: one tracker per sale. Pressing the button again returns the same tracker.
    DECLARE @SaleTracker INT = (SELECT TrackerId FROM dbo.SalesOpportunities WHERE SalesOpportunityId = @Sale);
    IF COALESCE(@QTracker, @SaleTracker) IS NOT NULL
    BEGIN
        SELECT COALESCE(@QTracker, @SaleTracker) AS TrackerId, CAST(1 AS BIT) AS AlreadyConverted;
        RETURN;
    END

    IF @Status <> N'Approved' THROW 50021, 'Only an Approved quotation can be converted to a renewal tracker (the job).', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SalesQuotationItems WHERE SalesQuotationId = @SalesQuotationId)
        THROW 50022, 'The quotation has no service lines.', 1;

    DECLARE @CustomerId INT, @CustomerCode NVARCHAR(50), @CustomerName NVARCHAR(150), @Phone NVARCHAR(20), @Email NVARCHAR(150),
            @Address NVARCHAR(255), @EmpId INT, @EmpName NVARCHAR(150), @Remarks NVARCHAR(1000), @QuoteNo NVARCHAR(20);

    SELECT @CustomerId = c.CustomerId, @CustomerCode = c.CustomerCode, @CustomerName = c.CustomerName,
           @Phone = c.CustomerPhone, @Email = c.CustomerEmail, @Address = c.Address,
           @EmpId = COALESCE(c.AssignedEmployeeId, s.SalespersonEmployeeId)
    FROM dbo.SalesOpportunities s
    INNER JOIN dbo.Customers c ON c.CustomerId = s.CustomerId
    WHERE s.SalesOpportunityId = @Sale;
    SELECT @EmpName = FullName FROM dbo.Employees WHERE EmployeeId = @EmpId;
    SELECT @QuoteNo = QuoteNo FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
    SET @Remarks = LEFT(N'From sales quotation ' + ISNULL(@QuoteNo, N'') , 1000);

    DECLARE @TrackerStatus NVARCHAR(30) = ISNULL((SELECT TOP (1) StatusCode FROM dbo.TrackerStatuses WHERE IsActive = 1 ORDER BY IsDefault DESC, DisplayOrder, TrackerStatusId), N'NEW');
    DECLARE @QStatus NVARCHAR(30) = ISNULL(
        (SELECT TOP (1) StatusName FROM dbo.QuotationStatuses
         WHERE IsActive = 1 AND (UPPER(StatusCode) IN (N'APPROVED', N'ACCEPTED') OR StatusName IN (N'Approved', N'Accepted')) ORDER BY DisplayOrder, QuotationStatusId),
        (SELECT TOP (1) StatusName FROM dbo.QuotationStatuses WHERE IsActive = 1 ORDER BY IsDefault DESC, DisplayOrder, QuotationStatusId));
    IF @QStatus IS NULL THROW 50004, 'No active quotation status is configured.', 1;

    DECLARE @TrackerId INT, @QuotationId INT;
    DECLARE @Specs TABLE (SpecId INT NOT NULL, CategoryId INT NOT NULL, SubCategoryId INT NOT NULL);
    DECLARE @Lines TABLE (ItemId INT NOT NULL, SpecId INT NOT NULL, ServiceSpecId INT NOT NULL);

    BEGIN TRAN;
        INSERT dbo.Trackers (CustomerId, CustomerCode, CustomerName, Mobile, Email, AssignedToEmployeeId, AssignedToEmployeeName, Status, FollowUpFrequencyDays, CreatedBy, CreatedDate)
        VALUES (@CustomerId, @CustomerCode, @CustomerName, @Phone, @Email, @EmpId, @EmpName, @TrackerStatus, 5, @CreatedBy, SYSUTCDATETIME());
        SET @TrackerId = CONVERT(INT, SCOPE_IDENTITY());

        -- One Category / Sub Category block per distinct pair on the quotation.
        INSERT dbo.TrackerCategorySubCategorySpecs (TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName, Status, Notes, CreatedBy, CreatedDate)
        OUTPUT inserted.TrackerCategorySubCategorySpecId, inserted.CategoryId, inserted.SubCategoryId INTO @Specs (SpecId, CategoryId, SubCategoryId)
        SELECT DISTINCT @TrackerId, i.CategoryId, i.CategoryName, i.SubCategoryId, i.SubCategoryName, N'Open', @Remarks, @CreatedBy, SYSUTCDATETIME()
        FROM dbo.SalesQuotationItems i WHERE i.SalesQuotationId = @SalesQuotationId;

        -- One service line per quotation line (Count = quantity, VAT stays a percentage here).
        DECLARE @ItemId INT, @SpecId INT, @CatId INT, @CatName NVARCHAR(100), @SubId INT, @SubName NVARCHAR(100),
                @SvcId INT, @SvcName NVARCHAR(150), @Qty INT, @Amt DECIMAL(18,2), @Vat DECIMAL(5,2), @NewSvcSpec INT;
        DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
            SELECT i.SalesQuotationItemId, sp.SpecId, i.CategoryId, i.CategoryName, i.SubCategoryId, i.SubCategoryName, i.ServiceId, i.ServiceName, i.Quantity, i.Amount, i.VatPercent
            FROM dbo.SalesQuotationItems i
            INNER JOIN @Specs sp ON sp.CategoryId = i.CategoryId AND sp.SubCategoryId = i.SubCategoryId
            WHERE i.SalesQuotationId = @SalesQuotationId
            ORDER BY i.CategoryName, i.SubCategoryName, i.ServiceName, i.SalesQuotationItemId;
        OPEN cur;
        FETCH NEXT FROM cur INTO @ItemId, @SpecId, @CatId, @CatName, @SubId, @SubName, @SvcId, @SvcName, @Qty, @Amt, @Vat;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT dbo.TrackerServiceSpecs (TrackerCategorySubCategorySpecId, TrackerId, CategoryId, CategoryName, SubCategoryId, SubCategoryName,
                                            ServiceId, ServiceName, Count, Amount, VAT, Status, CreatedBy, CreatedDate)
            VALUES (@SpecId, @TrackerId, @CatId, @CatName, @SubId, @SubName, @SvcId, @SvcName, @Qty, @Amt, @Vat, N'Open', @CreatedBy, SYSUTCDATETIME());
            SET @NewSvcSpec = CONVERT(INT, SCOPE_IDENTITY());
            INSERT @Lines (ItemId, SpecId, ServiceSpecId) VALUES (@ItemId, @SpecId, @NewSvcSpec);
            FETCH NEXT FROM cur INTO @ItemId, @SpecId, @CatId, @CatName, @SubId, @SubName, @SvcId, @SvcName, @Qty, @Amt, @Vat;
        END
        CLOSE cur; DEALLOCATE cur;

        -- The regular tracker quotation, so invoices, payments and renewals carry on from here.
        INSERT dbo.Quotations (TrackerId, CustomerCode, QuotationStatus, SenderInfo, Notes, CreatedBy, CreatedDate, QuotationMode, CustomerAddress)
        VALUES (@TrackerId, @CustomerCode, @QStatus, NULL, @Remarks, @CreatedBy, SYSUTCDATETIME(), N'ALL', @Address);
        SET @QuotationId = CONVERT(INT, SCOPE_IDENTITY());

        -- QuotationItems.VAT is a per-unit currency amount; TaxableValue = Quantity x (Amount + VAT).
        INSERT dbo.QuotationItems (QuotationId, TrackerCategorySubCategorySpecId, TrackerServiceSpecId, CreatedDate, ServiceName, Quantity, Amount, VAT, TaxableValue)
        SELECT @QuotationId, l.SpecId, l.ServiceSpecId, SYSUTCDATETIME(), i.ServiceName, i.Quantity, i.Amount,
               ROUND(i.Amount * i.VatPercent / 100.0, 2),
               i.Quantity * (i.Amount + ROUND(i.Amount * i.VatPercent / 100.0, 2))
        FROM @Lines l
        INNER JOIN dbo.SalesQuotationItems i ON i.SalesQuotationItemId = l.ItemId;

        UPDATE ts SET QuotationId = @QuotationId
        FROM dbo.TrackerServiceSpecs ts INNER JOIN @Lines l ON l.ServiceSpecId = ts.TrackerServiceSpecId;

        UPDATE dbo.SalesQuotations SET TrackerId = @TrackerId, QuotationId = @QuotationId, ModifiedDate = SYSUTCDATETIME()
        WHERE SalesQuotationId = @SalesQuotationId;
        UPDATE dbo.SalesOpportunities SET TrackerId = @TrackerId, ConvertedToTrackerDate = SYSUTCDATETIME(), SalesStatus = N'Job Created', ModifiedDate = SYSUTCDATETIME()
        WHERE SalesOpportunityId = @Sale;
    COMMIT;

    SELECT @TrackerId AS TrackerId, CAST(0 AS BIT) AS AlreadyConverted;
END
GO

/* ---------- usp_SalesQuotations_GetAll : the quotations list screen ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_GetAll
AS
BEGIN
    SET NOCOUNT ON;
    SELECT q.SalesQuotationId, q.QuoteNo, q.SalesOpportunityId, s.SalesCode, c.CustomerName,
           q.QuotationStatus, q.QuoteDate, q.TrackerId, q.CreatedDate,
           ISNULL(t.Subtotal, 0) AS Subtotal, ISNULL(t.VatAmount, 0) AS VatAmount,
           ISNULL(t.Subtotal, 0) + ISNULL(t.VatAmount, 0) AS GrandTotal,
           ISNULL(STUFF((SELECT DISTINCT N'|' + i.CategoryName FROM dbo.SalesQuotationItems i
                         WHERE i.SalesQuotationId = q.SalesQuotationId
                         FOR XML PATH(N''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N''), N'') AS Categories
    FROM dbo.SalesQuotations q
    INNER JOIN dbo.SalesOpportunities s ON s.SalesOpportunityId = q.SalesOpportunityId
    INNER JOIN dbo.Customers c ON c.CustomerId = s.CustomerId
    OUTER APPLY (SELECT SUM(i.Quantity * i.Amount) AS Subtotal,
                        SUM(ROUND(i.Quantity * i.Amount * i.VatPercent / 100.0, 2)) AS VatAmount
                 FROM dbo.SalesQuotationItems i WHERE i.SalesQuotationId = q.SalesQuotationId) t
    ORDER BY q.SalesQuotationId DESC;
END
GO

/* ---------- usp_SalesQuotations_Delete : not allowed once converted to a tracker ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SalesQuotations_Delete
    @SalesQuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Sale INT, @Tracker INT;
    SELECT @Sale = SalesOpportunityId, @Tracker = TrackerId FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
    IF @Sale IS NULL THROW 50020, 'The quotation was not found.', 1;
    IF @Tracker IS NOT NULL THROW 50023, 'This quotation is already converted to a renewal tracker and cannot be deleted.', 1;
    BEGIN TRAN;
        DELETE FROM dbo.SalesQuotations WHERE SalesQuotationId = @SalesQuotationId;
        UPDATE dbo.SalesOpportunities
        SET QuotationStatus = (SELECT TOP (1) QuotationStatus FROM dbo.SalesQuotations WHERE SalesOpportunityId = @Sale ORDER BY SalesQuotationId DESC),
            SalesStatus = CASE WHEN SalesStatus = N'Awaiting Quotation / Accounts'
                                AND NOT EXISTS (SELECT 1 FROM dbo.SalesQuotations WHERE SalesOpportunityId = @Sale AND QuotationStatus <> N'Rejected')
                               THEN N'Confirmed' ELSE SalesStatus END,
            ModifiedDate = SYSUTCDATETIME()
        WHERE SalesOpportunityId = @Sale;
    COMMIT;
END
GO

/* ---------- Menu: "Sale Quotations" under Leads & Sales, permissions copied from the Sales menu ---------- */
IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NOT NULL
BEGIN
    DECLARE @App NVARCHAR(30), @Parent INT;
    SELECT TOP (1) @App = ApplicationCode FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sales' ORDER BY MenuItemId;
    SELECT @Parent = MenuItemId FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'leads-sales';
    IF @Parent IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE LOWER(MenuKey) = N'sale-quotations')
        INSERT dbo.MenuItems (MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
        VALUES (N'sale-quotations', N'Sale Quotations', N'receipt_long', NULL, 1, N'Leads & Sales', N'/sale-quotations', 5, ISNULL(@App, N'TRACKER'), 0, @Parent, SYSUTCDATETIME());
END
GO
IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NOT NULL
    INSERT dbo.RolePermissions (RoleId, MenuKey, CanView, CanAdd, CanEdit, CanDelete, CanImport, CanExport)
    SELECT rp.RoleId, N'sale-quotations', rp.CanView, rp.CanAdd, rp.CanEdit, rp.CanDelete, 0, rp.CanExport
    FROM dbo.RolePermissions rp
    WHERE LOWER(rp.MenuKey) = N'sales'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = rp.RoleId AND LOWER(x.MenuKey) = N'sale-quotations');
GO

RAISERROR(N'Sales quotation + Convert to Renewal Tracker installed.', 10, 1) WITH NOWAIT;
GO
