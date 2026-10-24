/* Business Panel schema for each tenant database (BP_<CODE>).
   Source: BusinessPanelApi\database\BusinessPanel_schema_2026-10-09.sql, with the USE [BusinessPanel] line removed
   so it runs inside the tenant database. Runs once per tenant database - for later changes add 002_..., 003_... files.
   SQL Server 2016 compatible: OPTIMIZE_FOR_SEQUENTIAL_KEY (2019+) removed, STRING_AGG (2017+) replaced by FOR XML PATH. */
/****** Object:  UserDefinedFunction [dbo].[fn_BP_Money]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 1. Small helpers ---------- */
CREATE   FUNCTION [dbo].[fn_BP_Money] (@v DECIMAL(18,2))
RETURNS NVARCHAR(40)
AS
BEGIN
    RETURN CONCAT(N'AED ', FORMAT(ISNULL(@v, 0), N'N0', N'en-US'));
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_DocumentStatusNormalize]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Create / Update: status is Success or Pending (blank = Pending; the old Active / Inactive are accepted and mapped). */
CREATE   FUNCTION [dbo].[fn_DocumentStatusNormalize] (@Status NVARCHAR(30))
RETURNS NVARCHAR(30)
AS
BEGIN
    DECLARE @s NVARCHAR(30) = UPPER(LTRIM(RTRIM(ISNULL(@Status, N''))));
    RETURN CASE
        WHEN @s = N''                       THEN N'New'
        WHEN @s = N'NEW'                    THEN N'New'
        WHEN @s IN (N'SUCCESS', N'ACTIVE')  THEN N'Success'
        WHEN @s IN (N'PENDING', N'INACTIVE') THEN N'Pending'
        ELSE NULL END;
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_Notification_Render]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 2. Message text: {{placeholders}} are replaced when the reminder is created ---------- */
CREATE   FUNCTION [dbo].[fn_Notification_Render]
(
    @Template NVARCHAR(2000), @Customer NVARCHAR(150), @Category NVARCHAR(100), @SubCategory NVARCHAR(100),
    @Person NVARCHAR(200), @DueDate DATE, @DaysLeft INT, @Employee NVARCHAR(150), @Recipient NVARCHAR(150)
)
RETURNS NVARCHAR(2000)
AS
BEGIN
    DECLARE @DaysText NVARCHAR(60) =
        CASE WHEN @DaysLeft < 0 THEN CONCAT(N'overdue by ', -@DaysLeft, CASE WHEN @DaysLeft = -1 THEN N' day' ELSE N' days' END)
             WHEN @DaysLeft = 0 THEN N'due today'
             ELSE CONCAT(@DaysLeft, CASE WHEN @DaysLeft = 1 THEN N' day' ELSE N' days' END, N' left') END;
    RETURN LEFT(
        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(@Template, N''),
            N'{{Customer}}',    ISNULL(@Customer, N'')),
            N'{{Category}}',    ISNULL(@Category, N'')),
            N'{{SubCategory}}', ISNULL(@SubCategory, N'')),
            N'{{Person}}',      ISNULL(NULLIF(@Person, N''), ISNULL(@Customer, N''))),
            N'{{DueDate}}',     CONVERT(NVARCHAR(11), @DueDate, 106)),
            N'{{DaysLeft}}',    @DaysText),
            N'{{Employee}}',    ISNULL(NULLIF(@Employee, N''), N'our team')),
            N'{{Recipient}}',   ISNULL(NULLIF(@Recipient, N''), N'Sir/Madam')),
            N'{{Today}}',       CONVERT(NVARCHAR(11), SYSDATETIME(), 106)),
        2000);
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_Quotation_GrandTotal]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 3. Helpers ---------- */
CREATE   FUNCTION [dbo].[fn_Quotation_GrandTotal] (@QuotationId INT)
RETURNS DECIMAL(18,2)
AS
BEGIN
    RETURN (SELECT ISNULL(SUM((ISNULL(Amount,0)+ISNULL(VAT,0)) * ISNULL(Quantity,1)), 0)
            FROM dbo.QuotationItems WHERE QuotationId = @QuotationId);
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_QuotationStatusKey]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- Helper: normalise a quotation status NAME or CODE to a fixed key ---------- */
CREATE   FUNCTION [dbo].[fn_QuotationStatusKey] (@Status NVARCHAR(50))
RETURNS VARCHAR(20)
AS
BEGIN
    DECLARE @s NVARCHAR(50) = UPPER(LTRIM(RTRIM(ISNULL(@Status, N''))));
    RETURN CASE
        WHEN @s = N'DRAFT'        THEN 'DRAFT'
        WHEN @s = N'SENT'         THEN 'SENT'
        WHEN @s LIKE N'NEGOTIAT%' THEN 'NEGOTIATE'
        WHEN @s LIKE N'ACCEPT%'   THEN 'ACCEPTED'
        WHEN @s LIKE N'REJECT%'   THEN 'REJECTED'
        ELSE '' END;
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_Tracker_CompletionBlocker]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 1. The completion rule (one function; the Completed guard trigger already calls it) ---------- */
CREATE   FUNCTION [dbo].[fn_Tracker_CompletionBlocker] (@TrackerId INT)
RETURNS NVARCHAR(400)
AS
BEGIN
    DECLARE @Open INT = (SELECT COUNT(1) FROM dbo.Quotations
                         WHERE TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(QuotationStatus) IN ('DRAFT', 'SENT', 'NEGOTIATE'));
    DECLARE @Accepted INT = (SELECT COUNT(1) FROM dbo.Quotations
                             WHERE TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(QuotationStatus) = 'ACCEPTED');
    DECLARE @NoInvoice INT = (SELECT COUNT(1) FROM dbo.Quotations q
                              WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED'
                                AND NOT EXISTS (SELECT 1 FROM dbo.Invoices v WHERE v.QuotationId = q.QuotationId));
    DECLARE @Docs INT    = (SELECT COUNT(1) FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId);
    DECLARE @Pending INT = (SELECT COUNT(1) FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId AND UPPER(LTRIM(RTRIM(Status))) = N'PENDING');
    DECLARE @New INT     = (SELECT COUNT(1) FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId AND UPPER(LTRIM(RTRIM(Status))) = N'NEW');
    DECLARE @Grand DECIMAL(18,2) = 0, @Paid DECIMAL(18,2) = 0;
    SELECT @Grand = ISNULL(SUM(dbo.fn_Quotation_GrandTotal(q.QuotationId)), 0)
    FROM dbo.Quotations q
    WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED';
    SELECT @Paid = ISNULL(SUM(p.PaymentAmount), 0)
    FROM dbo.Payments p
    INNER JOIN dbo.Quotations q ON q.QuotationId = p.QuotationId
    WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED';

    DECLARE @Msg NVARCHAR(400) = N'';

    -- Quotations tab
    IF @Open > 0
        SET @Msg = CONCAT(@Msg, N' ', @Open, CASE WHEN @Open = 1 THEN N' quotation is' ELSE N' quotations are' END,
                          N' still open (Draft / Sent / Negotiation) - accept, reject or delete ', CASE WHEN @Open = 1 THEN N'it.' ELSE N'them.' END);
    IF @Accepted = 0
        SET @Msg = CONCAT(@Msg, N' No quotation has been accepted.');
    ELSE
    BEGIN
        IF @NoInvoice > 0
            SET @Msg = CONCAT(@Msg, N' ', @NoInvoice, CASE WHEN @NoInvoice = 1 THEN N' accepted quotation has' ELSE N' accepted quotations have' END, N' no invoice yet.');
        -- Payments tab
        IF @Paid < @Grand
            SET @Msg = CONCAT(@Msg, N' Payment is not complete (balance pending ', FORMAT(@Grand - @Paid, N'N2', N'en-US'), N').');
    END;

    -- Documents tab
    IF @Docs = 0
        SET @Msg = CONCAT(@Msg, N' At least one document is required.');
    ELSE
    BEGIN
        IF @Pending > 0
            SET @Msg = CONCAT(@Msg, N' ', @Pending, CASE WHEN @Pending = 1 THEN N' document is' ELSE N' documents are' END, N' still Pending.');
        IF @New > 0
            SET @Msg = CONCAT(@Msg, N' ', @New, CASE WHEN @New = 1 THEN N' document is' ELSE N' documents are' END, N' still New - mark ', CASE WHEN @New = 1 THEN N'it' ELSE N'them' END, N' Success.');
    END;

    IF @Msg = N'' RETURN NULL;
    RETURN LEFT(CONCAT(N'Cannot complete this tracker -', @Msg), 400);
END;
GO
/****** Object:  UserDefinedFunction [dbo].[fn_Tracker_IsPaidInFull]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 4b. Paid in full?  (an accepted quotation exists and its payments reach its total) ---------- */
CREATE   FUNCTION [dbo].[fn_Tracker_IsPaidInFull] (@TrackerId INT)
RETURNS BIT
AS
BEGIN
    DECLARE @Accepted INT = 0, @Grand DECIMAL(18,2) = 0, @Paid DECIMAL(18,2) = 0;
    SELECT @Accepted = COUNT(1), @Grand = ISNULL(SUM(dbo.fn_Quotation_GrandTotal(q.QuotationId)), 0)
    FROM dbo.Quotations q
    WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED';
    SELECT @Paid = ISNULL(SUM(p.PaymentAmount), 0)
    FROM dbo.Payments p
    INNER JOIN dbo.Quotations q ON q.QuotationId = p.QuotationId
    WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED';
    DECLARE @Result BIT = 0;
    IF @Accepted > 0 AND @Grand > 0 AND @Paid >= @Grand SET @Result = 1;
    RETURN @Result;
END;
GO
/****** Object:  Table [dbo].[Trackers]    Script Date: 10/9/2026 3:24:16 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Trackers](
	[TrackerId] [int] IDENTITY(1,1) NOT NULL,
	[CustomerId] [int] NOT NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[CustomerName] [nvarchar](150) NOT NULL,
	[Mobile] [nvarchar](20) NULL,
	[Email] [nvarchar](150) NULL,
	[AssignedToEmployeeId] [int] NULL,
	[AssignedToEmployeeName] [nvarchar](150) NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[FollowUpFrequencyDays] [int] NOT NULL,
	[NextFollowUpDate] [date] NULL,
	[FollowUpFrequencyId] [int] NULL,
 CONSTRAINT [PK_Trackers] PRIMARY KEY CLUSTERED 
(
	[TrackerId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerStatuses]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerStatuses](
	[TrackerStatusId] [int] IDENTITY(1,1) NOT NULL,
	[StatusCode] [nvarchar](30) NOT NULL,
	[StatusName] [nvarchar](50) NOT NULL,
	[DisplayOrder] [int] NOT NULL,
	[IsDefault] [bit] NOT NULL,
	[IsTerminal] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_TrackerStatuses] PRIMARY KEY CLUSTERED 
(
	[TrackerStatusId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_TrackerStatuses_StatusCode] UNIQUE NONCLUSTERED 
(
	[StatusCode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Tracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 2. Views that give every report the same, correct building blocks ---------- */
CREATE   VIEW [dbo].[vw_BP_Tracker]
AS
SELECT t.TrackerId,
       t.CustomerId,
       t.CustomerCode,
       t.CustomerName,
       t.AssignedToEmployeeId AS EmployeeId,
       ISNULL(NULLIF(LTRIM(RTRIM(t.AssignedToEmployeeName)), N''), N'Unassigned') AS EmployeeName,
       UPPER(LTRIM(RTRIM(t.Status))) AS StatusCode,
       ISNULL(ts.StatusName, t.Status) AS StatusName,
       CASE WHEN UPPER(LTRIM(RTRIM(t.Status))) IN (N'COMPLETED', N'REJECTED') THEN 0 ELSE 1 END AS IsOpen,
       t.NextFollowUpDate,
       CONVERT(DATE, t.CreatedDate) AS CreatedDate
FROM dbo.Trackers t
LEFT JOIN dbo.TrackerStatuses ts ON ts.StatusCode = UPPER(LTRIM(RTRIM(t.Status)));
GO
/****** Object:  Table [dbo].[TrackerCategorySubCategorySpecs]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerCategorySubCategorySpecs](
	[TrackerCategorySubCategorySpecId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[CategoryId] [int] NOT NULL,
	[CategoryName] [nvarchar](100) NOT NULL,
	[SubCategoryId] [int] NOT NULL,
	[SubCategoryName] [nvarchar](100) NOT NULL,
	[IssueDate] [date] NULL,
	[DueDate] [date] NULL,
	[Status] [nvarchar](30) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[PersonName] [nvarchar](200) NULL,
 CONSTRAINT [PK_TrackerCategorySubCategorySpecs] PRIMARY KEY CLUSTERED 
(
	[TrackerCategorySubCategorySpecId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Spec]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   VIEW [dbo].[vw_BP_Spec]
AS
SELECT s.TrackerCategorySubCategorySpecId AS SpecId,
       s.TrackerId,
       s.CategoryId,
       s.CategoryName,
       s.SubCategoryId,
       s.SubCategoryName,
       s.PersonName,
       s.IssueDate,
       s.DueDate,
       tr.CustomerId,
       tr.CustomerName,
       tr.EmployeeId,
       tr.EmployeeName,
       tr.StatusCode,
       tr.StatusName,
       tr.IsOpen
FROM dbo.TrackerCategorySubCategorySpecs s
INNER JOIN dbo.vw_BP_Tracker tr ON tr.TrackerId = s.TrackerId;
GO
/****** Object:  Table [dbo].[Quotations]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Quotations](
	[QuotationId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[QuotationStatus] [nvarchar](30) NOT NULL,
	[SenderInfo] [nvarchar](200) NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[QuotationMode] [nvarchar](20) NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NULL,
	[CustomerAddress] [nvarchar](255) NULL,
	[TrackerCategorySubCategorySpecPersonId] [int] NULL,
 CONSTRAINT [PK_Quotations] PRIMARY KEY CLUSTERED 
(
	[QuotationId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[QuotationItems]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[QuotationItems](
	[QuotationItemId] [int] IDENTITY(1,1) NOT NULL,
	[QuotationId] [int] NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NOT NULL,
	[TrackerServiceSpecId] [int] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ServiceName] [nvarchar](150) NOT NULL,
	[Quantity] [int] NOT NULL,
	[Amount] [decimal](18, 2) NOT NULL,
	[VAT] [decimal](18, 2) NOT NULL,
	[TaxableValue] [decimal](18, 2) NOT NULL,
 CONSTRAINT [PK_QuotationItems] PRIMARY KEY CLUSTERED 
(
	[QuotationItemId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_QuotationItems_Quotation_ServiceSpec] UNIQUE NONCLUSTERED 
(
	[QuotationId] ASC,
	[TrackerServiceSpecId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerCategorySubCategorySpecPersons]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerCategorySubCategorySpecPersons](
	[TrackerCategorySubCategorySpecPersonId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NOT NULL,
	[TrackerId] [int] NOT NULL,
	[PersonName] [nvarchar](200) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_TrackerCategorySubCategorySpecPersons] PRIMARY KEY CLUSTERED 
(
	[TrackerCategorySubCategorySpecPersonId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Quotation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   VIEW [dbo].[vw_BP_Quotation]
AS
SELECT q.QuotationId,
       q.TrackerId,
       tr.CustomerId,
       tr.CustomerName,
       tr.EmployeeId,
       tr.EmployeeName,
       q.QuotationMode,
       q.TrackerCategorySubCategorySpecId AS SpecId,
       pe.PersonName,
       CASE
           WHEN UPPER(LTRIM(RTRIM(q.QuotationStatus))) = N'DRAFT'        THEN 'DRAFT'
           WHEN UPPER(LTRIM(RTRIM(q.QuotationStatus))) = N'SENT'         THEN 'SENT'
           WHEN UPPER(LTRIM(RTRIM(q.QuotationStatus))) LIKE N'NEGOTIAT%' THEN 'NEGOTIATE'
           WHEN UPPER(LTRIM(RTRIM(q.QuotationStatus))) LIKE N'ACCEPT%'   THEN 'ACCEPTED'
           WHEN UPPER(LTRIM(RTRIM(q.QuotationStatus))) LIKE N'REJECT%'   THEN 'REJECTED'
           ELSE 'DRAFT' END AS StatusKey,
       CONVERT(DATE, q.CreatedDate) AS CreatedDate,
       CONVERT(DATE, COALESCE(q.ModifiedDate, q.CreatedDate)) AS LastTouched,
       ISNULL(it.Net, 0) AS NetAmount,
       ISNULL(it.Vat, 0) AS VatAmount,
       ISNULL(it.Net, 0) + ISNULL(it.Vat, 0) AS Grand,
       it.CategoryMin,
       it.CategoryMax,
       it.SubMin,
       it.SubMax
FROM dbo.Quotations q
INNER JOIN dbo.vw_BP_Tracker tr ON tr.TrackerId = q.TrackerId
LEFT JOIN dbo.TrackerCategorySubCategorySpecPersons pe ON pe.TrackerCategorySubCategorySpecPersonId = q.TrackerCategorySubCategorySpecPersonId
OUTER APPLY (SELECT SUM(qi.Amount * qi.Quantity) AS Net,
                    SUM(qi.VAT * qi.Quantity) AS Vat,
                    MIN(sp.CategoryName) AS CategoryMin,
                    MAX(sp.CategoryName) AS CategoryMax,
                    MIN(sp.SubCategoryName) AS SubMin,
                    MAX(sp.SubCategoryName) AS SubMax
             FROM dbo.QuotationItems qi
             LEFT JOIN dbo.TrackerCategorySubCategorySpecs sp ON sp.TrackerCategorySubCategorySpecId = qi.TrackerCategorySubCategorySpecId
             WHERE qi.QuotationId = q.QuotationId) it;
GO
/****** Object:  Table [dbo].[Invoices]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Invoices](
	[InvoiceId] [int] IDENTITY(1,1) NOT NULL,
	[InvoiceNumber] [nvarchar](30) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[QuotationId] [int] NOT NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[InvoiceDate] [date] NOT NULL,
	[DueDate] [date] NULL,
	[NetAmount] [decimal](18, 2) NOT NULL,
	[VatAmount] [decimal](18, 2) NOT NULL,
	[GrandTotal] [decimal](18, 2) NOT NULL,
	[InvoiceStatus] [nvarchar](20) NOT NULL,
	[SentDate] [datetime2](7) NULL,
	[SentVia] [nvarchar](20) NULL,
	[SentCount] [int] NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Invoices] PRIMARY KEY CLUSTERED 
(
	[InvoiceId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Invoices_InvoiceNumber] UNIQUE NONCLUSTERED 
(
	[InvoiceNumber] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Invoices_Quotation] UNIQUE NONCLUSTERED 
(
	[QuotationId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Payments]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Payments](
	[PaymentId] [int] IDENTITY(1,1) NOT NULL,
	[PaymentNumber] [nvarchar](50) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[QuotationId] [int] NOT NULL,
	[CustomerId] [int] NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[PaymentDate] [date] NOT NULL,
	[AmountDue] [decimal](18, 2) NOT NULL,
	[PaymentAmount] [decimal](18, 2) NOT NULL,
	[PaymentMethodId] [int] NOT NULL,
	[TransactionReference] [nvarchar](150) NULL,
	[PaymentStatus] [nvarchar](20) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Payments] PRIMARY KEY CLUSTERED 
(
	[PaymentId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Payments_PaymentNumber] UNIQUE NONCLUSTERED 
(
	[PaymentNumber] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Invoice]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   VIEW [dbo].[vw_BP_Invoice]
AS
SELECT i.InvoiceId,
       i.InvoiceNumber,
       i.TrackerId,
       i.QuotationId,
       tr.CustomerId,
       tr.CustomerName,
       tr.EmployeeId,
       tr.EmployeeName,
       i.InvoiceDate,
       i.DueDate,
       i.NetAmount,
       i.VatAmount,
       i.GrandTotal,
       ISNULL(pay.Paid, 0) AS Paid,
       i.GrandTotal - ISNULL(pay.Paid, 0) AS Balance
FROM dbo.Invoices i
INNER JOIN dbo.vw_BP_Tracker tr ON tr.TrackerId = i.TrackerId
OUTER APPLY (SELECT SUM(p.PaymentAmount) AS Paid FROM dbo.Payments p WHERE p.QuotationId = i.QuotationId) pay;
GO
/****** Object:  Table [dbo].[PaymentMethods]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[PaymentMethods](
	[PaymentMethodId] [int] IDENTITY(1,1) NOT NULL,
	[MethodName] [nvarchar](100) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
 CONSTRAINT [PK_PaymentMethods] PRIMARY KEY CLUSTERED 
(
	[PaymentMethodId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_PaymentMethods_MethodName] UNIQUE NONCLUSTERED 
(
	[MethodName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Payment]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   VIEW [dbo].[vw_BP_Payment]
AS
SELECT p.PaymentId,
       p.PaymentNumber,
       p.PaymentDate,
       p.TrackerId,
       p.QuotationId,
       tr.CustomerId,
       tr.CustomerName,
       tr.EmployeeId,
       tr.EmployeeName,
       p.AmountDue,
       p.PaymentAmount,
       ISNULL(pm.MethodName, N'-') AS MethodName,
       p.PaymentStatus,
       p.CreatedDate
FROM dbo.Payments p
INNER JOIN dbo.vw_BP_Tracker tr ON tr.TrackerId = p.TrackerId
LEFT JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId = p.PaymentMethodId;
GO
/****** Object:  Table [dbo].[TrackerQuotationDocuments]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerQuotationDocuments](
	[DocumentId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[QuotationId] [int] NULL,
	[DocumentName] [nvarchar](256) NOT NULL,
	[ReferenceNo] [nvarchar](100) NULL,
	[Description] [nvarchar](1000) NULL,
	[DocumentDate] [date] NULL,
	[ExpiryDate] [date] NULL,
	[AttachmentOriginalName] [nvarchar](260) NULL,
	[AttachmentStoredName] [nvarchar](260) NULL,
	[AttachmentRelativePath] [nvarchar](500) NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedOn] [datetime2](0) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedOn] [datetime2](0) NULL,
 CONSTRAINT [PK_TrackerQuotationDocuments] PRIMARY KEY CLUSTERED 
(
	[DocumentId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[vw_BP_Document]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   VIEW [dbo].[vw_BP_Document]
AS
SELECT d.DocumentId,
       d.TrackerId,
       d.QuotationId,
       tr.CustomerId,
       tr.CustomerName,
       tr.EmployeeId,
       tr.EmployeeName,
       d.DocumentName,
       d.ReferenceNo,
       d.DocumentDate,
       d.ExpiryDate,
       CASE UPPER(LTRIM(RTRIM(d.Status))) WHEN N'SUCCESS' THEN N'Success' WHEN N'PENDING' THEN N'Pending' ELSE N'New' END AS StatusName,
       ISNULL(d.ModifiedOn, d.CreatedOn) AS LastTouched
FROM dbo.TrackerQuotationDocuments d
INNER JOIN dbo.vw_BP_Tracker tr ON tr.TrackerId = d.TrackerId;
GO
/****** Object:  Table [dbo].[ApplicationSettings]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[ApplicationSettings](
	[ApplicationSettingId] [int] IDENTITY(1,1) NOT NULL,
	[SettingKey] [nvarchar](100) NOT NULL,
	[SettingValue] [nvarchar](max) NULL,
	[IsActive] [bit] NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_ApplicationSettings] PRIMARY KEY CLUSTERED 
(
	[ApplicationSettingId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_ApplicationSettings_SettingKey] UNIQUE NONCLUSTERED 
(
	[SettingKey] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[AuditLogs]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[AuditLogs](
	[AuditLogId] [bigint] IDENTITY(1,1) NOT NULL,
	[TableName] [nvarchar](100) NOT NULL,
	[RecordId] [int] NOT NULL,
	[Action] [nvarchar](10) NOT NULL,
	[ColumnName] [nvarchar](100) NULL,
	[OldValue] [nvarchar](max) NULL,
	[NewValue] [nvarchar](max) NULL,
	[ChangedByUserId] [int] NULL,
	[ChangedDate] [datetime2](7) NOT NULL,
 CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED 
(
	[AuditLogId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Customers]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Customers](
	[CustomerId] [int] IDENTITY(1,1) NOT NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[CustomerName] [nvarchar](150) NOT NULL,
	[CustomerPhone] [nvarchar](20) NULL,
	[CustomerEmail] [nvarchar](150) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[CustomerTypeId] [int] NOT NULL,
	[Address] [nvarchar](255) NULL,
 CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED 
(
	[CustomerId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Customers_CustomerCode] UNIQUE NONCLUSTERED 
(
	[CustomerCode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CustomerTypes]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CustomerTypes](
	[CustomerTypeId] [int] IDENTITY(1,1) NOT NULL,
	[CustomerTypeName] [nvarchar](50) NOT NULL,
	[IsActive] [bit] NOT NULL,
 CONSTRAINT [PK_CustomerTypes] PRIMARY KEY CLUSTERED 
(
	[CustomerTypeId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_CustomerTypes_Name] UNIQUE NONCLUSTERED 
(
	[CustomerTypeName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[DocumentRegistries]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[DocumentRegistries](
	[DocumentId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[DocumentName] [nvarchar](256) NOT NULL,
	[DocumentType] [nvarchar](50) NOT NULL,
	[DocumentNumber] [nvarchar](100) NULL,
	[IssueDate] [datetime2](7) NOT NULL,
	[ExpiryDate] [datetime2](7) NULL,
	[DocumentLocation] [nvarchar](512) NULL,
	[Notes] [nvarchar](1000) NULL,
	[IsVerified] [bit] NOT NULL,
	[Status] [nvarchar](50) NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[DocumentId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[EmailTemplates]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[EmailTemplates](
	[EmailTemplateId] [int] IDENTITY(1,1) NOT NULL,
	[TemplateName] [nvarchar](150) NOT NULL,
	[Subject] [nvarchar](250) NOT NULL,
	[Body] [nvarchar](max) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_EmailTemplates] PRIMARY KEY CLUSTERED 
(
	[EmailTemplateId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Employees]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Employees](
	[EmployeeId] [int] IDENTITY(1,1) NOT NULL,
	[FullName] [nvarchar](150) NOT NULL,
	[Mobile] [nvarchar](20) NULL,
	[Email] [nvarchar](150) NULL,
	[RoleId] [int] NULL,
	[JoinDate] [date] NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Employees] PRIMARY KEY CLUSTERED 
(
	[EmployeeId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[FollowUpFrequencies]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[FollowUpFrequencies](
	[FollowUpFrequencyId] [int] IDENTITY(1,1) NOT NULL,
	[FrequencyCode] [nvarchar](50) NOT NULL,
	[FrequencyName] [nvarchar](100) NOT NULL,
	[FrequencyDays] [int] NULL,
	[IsManual] [bit] NOT NULL,
	[DisplayOrder] [int] NOT NULL,
	[IsDefault] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[AutoSendEmail] [bit] NOT NULL,
	[EmailTemplateId] [int] NULL,
 CONSTRAINT [PK_FollowUpFrequencies] PRIMARY KEY CLUSTERED 
(
	[FollowUpFrequencyId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_FollowUpFrequencies_Code] UNIQUE NONCLUSTERED 
(
	[FrequencyCode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_FollowUpFrequencies_Name] UNIQUE NONCLUSTERED 
(
	[FrequencyName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[FollowUps]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[FollowUps](
	[FollowUpId] [int] IDENTITY(1,1) NOT NULL,
	[CustomerId] [int] NOT NULL,
	[TrackerId] [int] NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NULL,
	[Notes] [nvarchar](1000) NULL,
	[SenderInfo] [nvarchar](200) NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[ActivityType] [nvarchar](20) NOT NULL,
	[CommunicationType] [nvarchar](30) NOT NULL,
	[FollowUpDate] [date] NULL,
	[NextFollowUpDate] [date] NULL,
	[FollowUpFrequencyId] [int] NULL,
	[EmailTemplateId] [int] NULL,
	[EmailSubject] [nvarchar](255) NULL,
	[RecipientEmail] [nvarchar](200) NULL,
	[SentStatus] [nvarchar](20) NULL,
	[AutoGenerated] [bit] NOT NULL,
 CONSTRAINT [PK_FollowUps] PRIMARY KEY CLUSTERED 
(
	[FollowUpId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[MenuItems]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[MenuItems](
	[MenuItemId] [int] IDENTITY(1,1) NOT NULL,
	[MenuKey] [nvarchar](100) NOT NULL,
	[DisplayName] [nvarchar](150) NOT NULL,
	[Icon] [nvarchar](100) NULL,
	[RoleRestriction] [nvarchar](100) NULL,
	[IsActive] [bit] NOT NULL,
	[MenuGroup] [nvarchar](100) NULL,
	[Route] [nvarchar](250) NULL,
	[DisplayOrder] [int] NOT NULL,
	[ApplicationCode] [nvarchar](30) NOT NULL,
	[IsSystemMenu] [bit] NOT NULL,
	[ParentMenuItemId] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[ModuleId] [int] NULL,
 CONSTRAINT [PK_MenuItems] PRIMARY KEY CLUSTERED 
(
	[MenuItemId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_MenuItems_MenuKey] UNIQUE NONCLUSTERED 
(
	[MenuKey] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[NotificationRuleRecipients]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[NotificationRuleRecipients](
	[RecipientId] [int] IDENTITY(1,1) NOT NULL,
	[RuleId] [int] NOT NULL,
	[RecipientName] [nvarchar](150) NOT NULL,
	[Email] [nvarchar](150) NULL,
	[Mobile] [nvarchar](30) NULL,
 CONSTRAINT [PK_NotificationRuleRecipients] PRIMARY KEY CLUSTERED 
(
	[RecipientId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[NotificationRules]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[NotificationRules](
	[RuleId] [int] IDENTITY(1,1) NOT NULL,
	[CategoryId] [int] NOT NULL,
	[SubCategoryId] [int] NULL,
	[DaysBefore] [int] NOT NULL,
	[NotifyEmail] [bit] NOT NULL,
	[NotifyWhatsApp] [bit] NOT NULL,
	[NotifyInApp] [bit] NOT NULL,
	[ToCustomer] [bit] NOT NULL,
	[ToEmployee] [bit] NOT NULL,
	[Subject] [nvarchar](200) NULL,
	[Body] [nvarchar](2000) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_NotificationRules] PRIMARY KEY CLUSTERED 
(
	[RuleId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Notifications]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Notifications](
	[NotificationId] [bigint] IDENTITY(1,1) NOT NULL,
	[RuleId] [int] NULL,
	[TrackerId] [int] NOT NULL,
	[SpecId] [int] NOT NULL,
	[CustomerId] [int] NULL,
	[CustomerName] [nvarchar](150) NULL,
	[CategoryId] [int] NULL,
	[CategoryName] [nvarchar](100) NULL,
	[SubCategoryName] [nvarchar](100) NULL,
	[PersonName] [nvarchar](200) NULL,
	[DueDate] [date] NOT NULL,
	[DaysBefore] [int] NOT NULL,
	[Channel] [nvarchar](10) NOT NULL,
	[RecipientType] [nvarchar](10) NOT NULL,
	[RecipientName] [nvarchar](150) NULL,
	[RecipientAddress] [nvarchar](150) NOT NULL,
	[RecipientEmployeeId] [int] NULL,
	[Subject] [nvarchar](200) NULL,
	[Body] [nvarchar](2000) NULL,
	[Status] [nvarchar](12) NOT NULL,
	[StatusMessage] [nvarchar](400) NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[SentDate] [datetime2](7) NULL,
	[ReadDate] [datetime2](7) NULL,
	[ModifiedBy] [int] NULL,
 CONSTRAINT [PK_Notifications] PRIMARY KEY CLUSTERED 
(
	[NotificationId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[QuotationCommunications]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[QuotationCommunications](
	[CommunicationId] [int] IDENTITY(1,1) NOT NULL,
	[QuotationId] [int] NOT NULL,
	[TrackerId] [int] NOT NULL,
	[CommunicationType] [nvarchar](30) NOT NULL,
	[Recipient] [nvarchar](100) NOT NULL,
	[Cc] [nvarchar](2000) NULL,
	[Subject] [nvarchar](500) NULL,
	[Message] [nvarchar](max) NULL,
	[AttachmentName] [nvarchar](255) NULL,
	[ProviderMessageId] [nvarchar](200) NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[SentDate] [datetime2](7) NULL,
	[DeliveredDate] [datetime2](7) NULL,
	[ReadDate] [datetime2](7) NULL,
	[FailedDate] [datetime2](7) NULL,
	[FailureReason] [nvarchar](2000) NULL,
	[CreatedBy] [int] NULL,
 CONSTRAINT [PK_QuotationCommunications] PRIMARY KEY CLUSTERED 
(
	[CommunicationId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[QuotationEmailHistory]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[QuotationEmailHistory](
	[QuotationEmailHistoryId] [int] IDENTITY(1,1) NOT NULL,
	[QuotationId] [int] NOT NULL,
	[EmailTemplateId] [int] NULL,
	[ToEmail] [nvarchar](1000) NOT NULL,
	[CcEmail] [nvarchar](2000) NULL,
	[Subject] [nvarchar](500) NOT NULL,
	[Body] [nvarchar](max) NOT NULL,
	[AttachmentName] [nvarchar](255) NULL,
	[SendType] [nvarchar](30) NOT NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[SentDate] [datetime2](7) NULL,
	[SentByUserId] [int] NULL,
	[ErrorMessage] [nvarchar](max) NULL,
 CONSTRAINT [PK_QuotationEmailHistory] PRIMARY KEY CLUSTERED 
(
	[QuotationEmailHistoryId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[QuotationStatuses]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[QuotationStatuses](
	[QuotationStatusId] [int] IDENTITY(1,1) NOT NULL,
	[StatusCode] [nvarchar](30) NOT NULL,
	[StatusName] [nvarchar](50) NOT NULL,
	[DisplayOrder] [int] NOT NULL,
	[IsDefault] [bit] NOT NULL,
	[IsTerminal] [bit] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_QuotationStatuses] PRIMARY KEY CLUSTERED 
(
	[QuotationStatusId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_QuotationStatuses_StatusCode] UNIQUE NONCLUSTERED 
(
	[StatusCode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_QuotationStatuses_StatusName] UNIQUE NONCLUSTERED 
(
	[StatusName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[QuotationStatusHistory]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[QuotationStatusHistory](
	[QuotationStatusHistoryId] [int] IDENTITY(1,1) NOT NULL,
	[QuotationId] [int] NOT NULL,
	[TrackerId] [int] NULL,
	[OldStatus] [nvarchar](50) NULL,
	[NewStatus] [nvarchar](50) NOT NULL,
	[ChangedBy] [int] NULL,
	[ChangedDate] [datetime2](7) NOT NULL,
	[Note] [nvarchar](500) NULL,
 CONSTRAINT [PK_QuotationStatusHistory] PRIMARY KEY CLUSTERED 
(
	[QuotationStatusHistoryId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Relationships]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Relationships](
	[RelationshipId] [int] IDENTITY(1,1) NOT NULL,
	[RelationshipName] [nvarchar](100) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Relationships] PRIMARY KEY CLUSTERED 
(
	[RelationshipId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Relationships_RelationshipName] UNIQUE NONCLUSTERED 
(
	[RelationshipName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[RolePermissions]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[RolePermissions](
	[RolePermissionId] [int] IDENTITY(1,1) NOT NULL,
	[RoleId] [int] NOT NULL,
	[MenuKey] [nvarchar](50) NOT NULL,
	[CanView] [bit] NOT NULL,
	[CanAdd] [bit] NOT NULL,
	[CanEdit] [bit] NOT NULL,
	[CanDelete] [bit] NOT NULL,
	[CanImport] [bit] NOT NULL,
	[CanExport] [bit] NOT NULL,
 CONSTRAINT [PK_RolePermissions] PRIMARY KEY CLUSTERED 
(
	[RolePermissionId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Roles]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Roles](
	[RoleId] [int] IDENTITY(1,1) NOT NULL,
	[RoleName] [nvarchar](50) NOT NULL,
	[Description] [nvarchar](255) NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Roles] PRIMARY KEY CLUSTERED 
(
	[RoleId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Roles_RoleName] UNIQUE NONCLUSTERED 
(
	[RoleName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[ServiceCategories]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[ServiceCategories](
	[CategoryId] [int] IDENTITY(1,1) NOT NULL,
	[CategoryName] [nvarchar](100) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_ServiceCategories] PRIMARY KEY CLUSTERED 
(
	[CategoryId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_ServiceCategories_CategoryName] UNIQUE NONCLUSTERED 
(
	[CategoryName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Services]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Services](
	[ServiceId] [int] IDENTITY(1,1) NOT NULL,
	[CategoryId] [int] NOT NULL,
	[SubCategoryId] [int] NOT NULL,
	[ServiceName] [nvarchar](150) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[Amount] [decimal](18, 2) NOT NULL,
	[VAT] [decimal](5, 2) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[Quantity] [int] NOT NULL,
	[Count] [int] NOT NULL,
 CONSTRAINT [PK_Services] PRIMARY KEY CLUSTERED 
(
	[ServiceId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Services_SubCategory_Name] UNIQUE NONCLUSTERED 
(
	[SubCategoryId] ASC,
	[ServiceName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[ServiceSubCategories]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[ServiceSubCategories](
	[SubCategoryId] [int] IDENTITY(1,1) NOT NULL,
	[CategoryId] [int] NOT NULL,
	[SubCategoryName] [nvarchar](100) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_ServiceSubCategories] PRIMARY KEY CLUSTERED 
(
	[SubCategoryId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_ServiceSubCategories_Category_Name] UNIQUE NONCLUSTERED 
(
	[CategoryId] ASC,
	[SubCategoryName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[SupportingDocuments]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[SupportingDocuments](
	[SupportingDocumentId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NOT NULL,
	[CustomerCode] [nvarchar](50) NOT NULL,
	[DocumentName] [nvarchar](255) NOT NULL,
	[Notes] [nvarchar](1000) NULL,
	[Status] [nvarchar](20) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_SupportingDocuments] PRIMARY KEY CLUSTERED 
(
	[SupportingDocumentId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerDocumentHold]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerDocumentHold](
	[TrackerId] [int] NOT NULL,
	[ResumeStatus] [nvarchar](30) NOT NULL,
	[HeldOn] [datetime2](0) NOT NULL,
 CONSTRAINT [PK_TrackerDocumentHold] PRIMARY KEY CLUSTERED 
(
	[TrackerId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerFamilyMembers]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerFamilyMembers](
	[TrackerFamilyMemberId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[RelationshipId] [int] NOT NULL,
	[DateOfBirth] [date] NULL,
	[PassportNo] [nvarchar](100) NULL,
	[VisaType] [nvarchar](100) NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_TrackerFamilyMembers] PRIMARY KEY CLUSTERED 
(
	[TrackerFamilyMemberId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerServiceSpecs]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerServiceSpecs](
	[TrackerServiceSpecId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerCategorySubCategorySpecId] [int] NOT NULL,
	[TrackerId] [int] NOT NULL,
	[CategoryId] [int] NOT NULL,
	[CategoryName] [nvarchar](100) NOT NULL,
	[SubCategoryId] [int] NOT NULL,
	[SubCategoryName] [nvarchar](100) NOT NULL,
	[ServiceId] [int] NULL,
	[ServiceName] [nvarchar](150) NOT NULL,
	[Amount] [decimal](18, 2) NOT NULL,
	[VAT] [decimal](5, 2) NOT NULL,
	[Status] [nvarchar](30) NOT NULL,
	[CreatedBy] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedBy] [int] NULL,
	[ModifiedDate] [datetime2](7) NULL,
	[QuotationId] [int] NULL,
	[Quantity] [int] NOT NULL,
	[Count] [int] NOT NULL,
 CONSTRAINT [PK_TrackerServiceSpecs] PRIMARY KEY CLUSTERED 
(
	[TrackerServiceSpecId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[TrackerStatusHistory]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TrackerStatusHistory](
	[TrackerStatusHistoryId] [int] IDENTITY(1,1) NOT NULL,
	[TrackerId] [int] NOT NULL,
	[OldStatus] [nvarchar](50) NULL,
	[NewStatus] [nvarchar](50) NOT NULL,
	[ChangedBy] [int] NULL,
	[ChangedDate] [datetime2](7) NOT NULL,
	[Note] [nvarchar](500) NULL,
 CONSTRAINT [PK_TrackerStatusHistory] PRIMARY KEY CLUSTERED 
(
	[TrackerStatusHistoryId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Users]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Users](
	[UserId] [int] IDENTITY(1,1) NOT NULL,
	[FirstName] [nvarchar](100) NOT NULL,
	[LastName] [nvarchar](100) NULL,
	[Email] [nvarchar](200) NOT NULL,
	[PasswordHash] [nvarchar](500) NULL,
	[Status] [nvarchar](20) NOT NULL,
	[RoleId] [int] NULL,
	[CreatedDate] [datetime2](7) NOT NULL,
	[ModifiedDate] [datetime2](7) NULL,
 CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED 
(
	[UserId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [UQ_Users_Email] UNIQUE NONCLUSTERED 
(
	[Email] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO
ALTER TABLE [dbo].[ApplicationSettings] ADD  CONSTRAINT [DF_ApplicationSettings_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[AuditLogs] ADD  CONSTRAINT [DF_AuditLogs_ChangedDate]  DEFAULT (sysutcdatetime()) FOR [ChangedDate]
GO
ALTER TABLE [dbo].[Customers] ADD  CONSTRAINT [DF_Customers_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[Customers] ADD  CONSTRAINT [DF_Customers_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[CustomerTypes] ADD  CONSTRAINT [DF_CustomerTypes_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[DocumentRegistries] ADD  DEFAULT ('Legal') FOR [DocumentType]
GO
ALTER TABLE [dbo].[DocumentRegistries] ADD  DEFAULT ((0)) FOR [IsVerified]
GO
ALTER TABLE [dbo].[DocumentRegistries] ADD  DEFAULT ('Active') FOR [Status]
GO
ALTER TABLE [dbo].[DocumentRegistries] ADD  DEFAULT (getutcdate()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[DocumentRegistries] ADD  DEFAULT (getutcdate()) FOR [UpdatedAt]
GO
ALTER TABLE [dbo].[EmailTemplates] ADD  CONSTRAINT [DF_EmailTemplates_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[EmailTemplates] ADD  CONSTRAINT [DF_EmailTemplates_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Employees] ADD  CONSTRAINT [DF_Employees_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[Employees] ADD  CONSTRAINT [DF_Employees_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_IsManual]  DEFAULT ((0)) FOR [IsManual]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_DisplayOrder]  DEFAULT ((0)) FOR [DisplayOrder]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_IsDefault]  DEFAULT ((0)) FOR [IsDefault]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[FollowUpFrequencies] ADD  CONSTRAINT [DF_FollowUpFrequencies_AutoSendEmail]  DEFAULT ((0)) FOR [AutoSendEmail]
GO
ALTER TABLE [dbo].[FollowUps] ADD  CONSTRAINT [DF_FollowUps_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[FollowUps] ADD  CONSTRAINT [DF_FollowUps_ActivityType]  DEFAULT (N'Manual') FOR [ActivityType]
GO
ALTER TABLE [dbo].[FollowUps] ADD  CONSTRAINT [DF_FollowUps_CommunicationType]  DEFAULT (N'Other') FOR [CommunicationType]
GO
ALTER TABLE [dbo].[FollowUps] ADD  CONSTRAINT [DF_FollowUps_AutoGenerated]  DEFAULT ((0)) FOR [AutoGenerated]
GO
ALTER TABLE [dbo].[Invoices] ADD  CONSTRAINT [DF_Invoices_Status]  DEFAULT (N'Created') FOR [InvoiceStatus]
GO
ALTER TABLE [dbo].[Invoices] ADD  CONSTRAINT [DF_Invoices_SentCount]  DEFAULT ((0)) FOR [SentCount]
GO
ALTER TABLE [dbo].[Invoices] ADD  CONSTRAINT [DF_Invoices_Created]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[MenuItems] ADD  CONSTRAINT [DF_MenuItems_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[MenuItems] ADD  CONSTRAINT [DF_MenuItems_DisplayOrder]  DEFAULT ((0)) FOR [DisplayOrder]
GO
ALTER TABLE [dbo].[MenuItems] ADD  CONSTRAINT [DF_MenuItems_ApplicationCode]  DEFAULT (N'TRACKER') FOR [ApplicationCode]
GO
ALTER TABLE [dbo].[MenuItems] ADD  CONSTRAINT [DF_MenuItems_IsSystemMenu]  DEFAULT ((0)) FOR [IsSystemMenu]
GO
ALTER TABLE [dbo].[MenuItems] ADD  CONSTRAINT [DF_MenuItems_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_Email]  DEFAULT ((1)) FOR [NotifyEmail]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_Wa]  DEFAULT ((0)) FOR [NotifyWhatsApp]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_InApp]  DEFAULT ((1)) FOR [NotifyInApp]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_ToCust]  DEFAULT ((1)) FOR [ToCustomer]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_ToEmp]  DEFAULT ((1)) FOR [ToEmployee]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_Active]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[NotificationRules] ADD  CONSTRAINT [DF_NotificationRules_Created]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Notifications] ADD  CONSTRAINT [DF_Notifications_Address]  DEFAULT (N'') FOR [RecipientAddress]
GO
ALTER TABLE [dbo].[Notifications] ADD  CONSTRAINT [DF_Notifications_Created]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[PaymentMethods] ADD  CONSTRAINT [DF_PaymentMethods_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[PaymentMethods] ADD  CONSTRAINT [DF_PaymentMethods_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Payments] ADD  CONSTRAINT [DF_Payments_AmountDue]  DEFAULT ((0)) FOR [AmountDue]
GO
ALTER TABLE [dbo].[Payments] ADD  CONSTRAINT [DF_Payments_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[QuotationCommunications] ADD  CONSTRAINT [DF_QuotationCommunications_Status]  DEFAULT (N'Initiated') FOR [Status]
GO
ALTER TABLE [dbo].[QuotationCommunications] ADD  CONSTRAINT [DF_QuotationCommunications_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[QuotationEmailHistory] ADD  CONSTRAINT [DF_QuotationEmailHistory_SendType]  DEFAULT (N'Manual') FOR [SendType]
GO
ALTER TABLE [dbo].[QuotationEmailHistory] ADD  CONSTRAINT [DF_QuotationEmailHistory_Status]  DEFAULT (N'Pending') FOR [Status]
GO
ALTER TABLE [dbo].[QuotationEmailHistory] ADD  CONSTRAINT [DF_QuotationEmailHistory_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[QuotationItems] ADD  CONSTRAINT [DF_QuotationItems_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[QuotationItems] ADD  CONSTRAINT [DF_QuotationItems_Quantity]  DEFAULT ((1)) FOR [Quantity]
GO
ALTER TABLE [dbo].[QuotationItems] ADD  CONSTRAINT [DF_QuotationItems_Amount]  DEFAULT ((0)) FOR [Amount]
GO
ALTER TABLE [dbo].[QuotationItems] ADD  CONSTRAINT [DF_QuotationItems_VAT]  DEFAULT ((0)) FOR [VAT]
GO
ALTER TABLE [dbo].[QuotationItems] ADD  CONSTRAINT [DF_QuotationItems_TaxableValue]  DEFAULT ((0)) FOR [TaxableValue]
GO
ALTER TABLE [dbo].[Quotations] ADD  CONSTRAINT [DF_Quotations_Status]  DEFAULT ('Draft') FOR [QuotationStatus]
GO
ALTER TABLE [dbo].[Quotations] ADD  CONSTRAINT [DF_Quotations_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Quotations] ADD  CONSTRAINT [DF_Quotations_QuotationMode]  DEFAULT (N'ALL') FOR [QuotationMode]
GO
ALTER TABLE [dbo].[QuotationStatuses] ADD  CONSTRAINT [DF_QuotationStatuses_IsDefault]  DEFAULT ((0)) FOR [IsDefault]
GO
ALTER TABLE [dbo].[QuotationStatuses] ADD  CONSTRAINT [DF_QuotationStatuses_IsTerminal]  DEFAULT ((0)) FOR [IsTerminal]
GO
ALTER TABLE [dbo].[QuotationStatuses] ADD  CONSTRAINT [DF_QuotationStatuses_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[QuotationStatuses] ADD  CONSTRAINT [DF_QuotationStatuses_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[QuotationStatusHistory] ADD  CONSTRAINT [DF_QuotationStatusHistory_ChangedDate]  DEFAULT (sysutcdatetime()) FOR [ChangedDate]
GO
ALTER TABLE [dbo].[Relationships] ADD  CONSTRAINT [DF_Relationships_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[Relationships] ADD  CONSTRAINT [DF_Relationships_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanView]  DEFAULT ((1)) FOR [CanView]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanAdd]  DEFAULT ((0)) FOR [CanAdd]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanEdit]  DEFAULT ((0)) FOR [CanEdit]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanDelete]  DEFAULT ((0)) FOR [CanDelete]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanImport]  DEFAULT ((0)) FOR [CanImport]
GO
ALTER TABLE [dbo].[RolePermissions] ADD  CONSTRAINT [DF_RolePermissions_CanExport]  DEFAULT ((0)) FOR [CanExport]
GO
ALTER TABLE [dbo].[Roles] ADD  CONSTRAINT [DF_Roles_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[Roles] ADD  CONSTRAINT [DF_Roles_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[ServiceCategories] ADD  CONSTRAINT [DF_ServiceCategories_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[ServiceCategories] ADD  CONSTRAINT [DF_ServiceCategories_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Services] ADD  CONSTRAINT [DF_Services_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[Services] ADD  CONSTRAINT [DF_Services_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Services] ADD  CONSTRAINT [DF_Services_Quantity]  DEFAULT ((1)) FOR [Quantity]
GO
ALTER TABLE [dbo].[Services] ADD  CONSTRAINT [DF_Services_Count]  DEFAULT ((1)) FOR [Count]
GO
ALTER TABLE [dbo].[ServiceSubCategories] ADD  CONSTRAINT [DF_ServiceSubCategories_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[ServiceSubCategories] ADD  CONSTRAINT [DF_ServiceSubCategories_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[SupportingDocuments] ADD  CONSTRAINT [DF_SupportingDocuments_Status]  DEFAULT ('Not Collected') FOR [Status]
GO
ALTER TABLE [dbo].[SupportingDocuments] ADD  CONSTRAINT [DF_SupportingDocuments_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecPersons] ADD  CONSTRAINT [DF_TrackerCatSubCatSpecPersons_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs] ADD  CONSTRAINT [DF_TrackerCatSubCatSpecs_Status]  DEFAULT ('Open') FOR [Status]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs] ADD  CONSTRAINT [DF_TrackerCatSubCatSpecs_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerDocumentHold] ADD  CONSTRAINT [DF_TrackerDocumentHold_HeldOn]  DEFAULT (sysutcdatetime()) FOR [HeldOn]
GO
ALTER TABLE [dbo].[TrackerFamilyMembers] ADD  CONSTRAINT [DF_TrackerFamilyMembers_Status]  DEFAULT (N'Active') FOR [Status]
GO
ALTER TABLE [dbo].[TrackerFamilyMembers] ADD  CONSTRAINT [DF_TrackerFamilyMembers_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] ADD  CONSTRAINT [DF_TrackerQuotationDocuments_Status]  DEFAULT (N'New') FOR [Status]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] ADD  CONSTRAINT [DF_TrackerQuotationDocuments_CreatedOn]  DEFAULT (sysutcdatetime()) FOR [CreatedOn]
GO
ALTER TABLE [dbo].[Trackers] ADD  CONSTRAINT [DF_Trackers_Status]  DEFAULT ('Open') FOR [Status]
GO
ALTER TABLE [dbo].[Trackers] ADD  CONSTRAINT [DF_Trackers_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Trackers] ADD  CONSTRAINT [DF_Trackers_FollowUpFrequencyDays]  DEFAULT ((5)) FOR [FollowUpFrequencyDays]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_Amount]  DEFAULT ((0)) FOR [Amount]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_VAT]  DEFAULT ((0)) FOR [VAT]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_Status]  DEFAULT ('Open') FOR [Status]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_Quantity]  DEFAULT ((1)) FOR [Quantity]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] ADD  CONSTRAINT [DF_TrackerServiceSpecs_Count]  DEFAULT ((1)) FOR [Count]
GO
ALTER TABLE [dbo].[TrackerStatuses] ADD  CONSTRAINT [DF_TrackerStatuses_IsDefault]  DEFAULT ((0)) FOR [IsDefault]
GO
ALTER TABLE [dbo].[TrackerStatuses] ADD  CONSTRAINT [DF_TrackerStatuses_IsTerminal]  DEFAULT ((0)) FOR [IsTerminal]
GO
ALTER TABLE [dbo].[TrackerStatuses] ADD  CONSTRAINT [DF_TrackerStatuses_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[TrackerStatuses] ADD  CONSTRAINT [DF_TrackerStatuses_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[TrackerStatusHistory] ADD  CONSTRAINT [DF_TrackerStatusHistory_ChangedDate]  DEFAULT (sysutcdatetime()) FOR [ChangedDate]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_Status]  DEFAULT ('Active') FOR [Status]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_CreatedDate]  DEFAULT (sysutcdatetime()) FOR [CreatedDate]
GO
ALTER TABLE [dbo].[Customers]  WITH CHECK ADD  CONSTRAINT [FK_Customers_CustomerTypes] FOREIGN KEY([CustomerTypeId])
REFERENCES [dbo].[CustomerTypes] ([CustomerTypeId])
GO
ALTER TABLE [dbo].[Customers] CHECK CONSTRAINT [FK_Customers_CustomerTypes]
GO
ALTER TABLE [dbo].[DocumentRegistries]  WITH NOCHECK ADD  CONSTRAINT [FK_DocumentRegistries_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[DocumentRegistries] CHECK CONSTRAINT [FK_DocumentRegistries_Trackers]
GO
ALTER TABLE [dbo].[Employees]  WITH CHECK ADD  CONSTRAINT [FK_Employees_Roles] FOREIGN KEY([RoleId])
REFERENCES [dbo].[Roles] ([RoleId])
GO
ALTER TABLE [dbo].[Employees] CHECK CONSTRAINT [FK_Employees_Roles]
GO
ALTER TABLE [dbo].[FollowUpFrequencies]  WITH CHECK ADD  CONSTRAINT [FK_FollowUpFrequencies_EmailTemplate] FOREIGN KEY([EmailTemplateId])
REFERENCES [dbo].[EmailTemplates] ([EmailTemplateId])
GO
ALTER TABLE [dbo].[FollowUpFrequencies] CHECK CONSTRAINT [FK_FollowUpFrequencies_EmailTemplate]
GO
ALTER TABLE [dbo].[FollowUps]  WITH NOCHECK ADD  CONSTRAINT [FK_FollowUps_CatSubCatSpec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
GO
ALTER TABLE [dbo].[FollowUps] CHECK CONSTRAINT [FK_FollowUps_CatSubCatSpec]
GO
ALTER TABLE [dbo].[FollowUps]  WITH NOCHECK ADD  CONSTRAINT [FK_FollowUps_Customers] FOREIGN KEY([CustomerId])
REFERENCES [dbo].[Customers] ([CustomerId])
GO
ALTER TABLE [dbo].[FollowUps] CHECK CONSTRAINT [FK_FollowUps_Customers]
GO
ALTER TABLE [dbo].[FollowUps]  WITH NOCHECK ADD  CONSTRAINT [FK_FollowUps_EmailTemplate] FOREIGN KEY([EmailTemplateId])
REFERENCES [dbo].[EmailTemplates] ([EmailTemplateId])
GO
ALTER TABLE [dbo].[FollowUps] CHECK CONSTRAINT [FK_FollowUps_EmailTemplate]
GO
ALTER TABLE [dbo].[FollowUps]  WITH NOCHECK ADD  CONSTRAINT [FK_FollowUps_Frequency] FOREIGN KEY([FollowUpFrequencyId])
REFERENCES [dbo].[FollowUpFrequencies] ([FollowUpFrequencyId])
GO
ALTER TABLE [dbo].[FollowUps] CHECK CONSTRAINT [FK_FollowUps_Frequency]
GO
ALTER TABLE [dbo].[FollowUps]  WITH NOCHECK ADD  CONSTRAINT [FK_FollowUps_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[FollowUps] CHECK CONSTRAINT [FK_FollowUps_Trackers]
GO
ALTER TABLE [dbo].[Invoices]  WITH CHECK ADD  CONSTRAINT [FK_Invoices_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[Invoices] CHECK CONSTRAINT [FK_Invoices_Quotation]
GO
ALTER TABLE [dbo].[Invoices]  WITH CHECK ADD  CONSTRAINT [FK_Invoices_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[Invoices] CHECK CONSTRAINT [FK_Invoices_Tracker]
GO
ALTER TABLE [dbo].[MenuItems]  WITH CHECK ADD  CONSTRAINT [FK_MenuItems_Parent] FOREIGN KEY([ParentMenuItemId])
REFERENCES [dbo].[MenuItems] ([MenuItemId])
GO
ALTER TABLE [dbo].[MenuItems] CHECK CONSTRAINT [FK_MenuItems_Parent]
GO
ALTER TABLE [dbo].[MenuItems]  WITH CHECK ADD  CONSTRAINT [FK_MenuItems_ParentMenuItem] FOREIGN KEY([ParentMenuItemId])
REFERENCES [dbo].[MenuItems] ([MenuItemId])
GO
ALTER TABLE [dbo].[MenuItems] CHECK CONSTRAINT [FK_MenuItems_ParentMenuItem]
GO
ALTER TABLE [dbo].[NotificationRuleRecipients]  WITH CHECK ADD  CONSTRAINT [FK_NotificationRuleRecipients_Rule] FOREIGN KEY([RuleId])
REFERENCES [dbo].[NotificationRules] ([RuleId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[NotificationRuleRecipients] CHECK CONSTRAINT [FK_NotificationRuleRecipients_Rule]
GO
ALTER TABLE [dbo].[NotificationRules]  WITH CHECK ADD  CONSTRAINT [FK_NotificationRules_Category] FOREIGN KEY([CategoryId])
REFERENCES [dbo].[ServiceCategories] ([CategoryId])
GO
ALTER TABLE [dbo].[NotificationRules] CHECK CONSTRAINT [FK_NotificationRules_Category]
GO
ALTER TABLE [dbo].[NotificationRules]  WITH CHECK ADD  CONSTRAINT [FK_NotificationRules_SubCategory] FOREIGN KEY([SubCategoryId])
REFERENCES [dbo].[ServiceSubCategories] ([SubCategoryId])
GO
ALTER TABLE [dbo].[NotificationRules] CHECK CONSTRAINT [FK_NotificationRules_SubCategory]
GO
ALTER TABLE [dbo].[Notifications]  WITH CHECK ADD  CONSTRAINT [FK_Notifications_Rule] FOREIGN KEY([RuleId])
REFERENCES [dbo].[NotificationRules] ([RuleId])
ON DELETE SET NULL
GO
ALTER TABLE [dbo].[Notifications] CHECK CONSTRAINT [FK_Notifications_Rule]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [FK_Payments_Customer] FOREIGN KEY([CustomerId])
REFERENCES [dbo].[Customers] ([CustomerId])
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [FK_Payments_Customer]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [FK_Payments_PaymentMethod] FOREIGN KEY([PaymentMethodId])
REFERENCES [dbo].[PaymentMethods] ([PaymentMethodId])
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [FK_Payments_PaymentMethod]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [FK_Payments_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [FK_Payments_Quotation]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [FK_Payments_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [FK_Payments_Tracker]
GO
ALTER TABLE [dbo].[QuotationCommunications]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationCommunications_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[QuotationCommunications] CHECK CONSTRAINT [FK_QuotationCommunications_Quotation]
GO
ALTER TABLE [dbo].[QuotationCommunications]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationCommunications_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[QuotationCommunications] CHECK CONSTRAINT [FK_QuotationCommunications_Tracker]
GO
ALTER TABLE [dbo].[QuotationCommunications]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationCommunications_User] FOREIGN KEY([CreatedBy])
REFERENCES [dbo].[Users] ([UserId])
GO
ALTER TABLE [dbo].[QuotationCommunications] CHECK CONSTRAINT [FK_QuotationCommunications_User]
GO
ALTER TABLE [dbo].[QuotationEmailHistory]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationEmailHistory_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[QuotationEmailHistory] CHECK CONSTRAINT [FK_QuotationEmailHistory_Quotation]
GO
ALTER TABLE [dbo].[QuotationEmailHistory]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationEmailHistory_Template] FOREIGN KEY([EmailTemplateId])
REFERENCES [dbo].[EmailTemplates] ([EmailTemplateId])
GO
ALTER TABLE [dbo].[QuotationEmailHistory] CHECK CONSTRAINT [FK_QuotationEmailHistory_Template]
GO
ALTER TABLE [dbo].[QuotationEmailHistory]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationEmailHistory_User] FOREIGN KEY([SentByUserId])
REFERENCES [dbo].[Users] ([UserId])
GO
ALTER TABLE [dbo].[QuotationEmailHistory] CHECK CONSTRAINT [FK_QuotationEmailHistory_User]
GO
ALTER TABLE [dbo].[QuotationItems]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationItems_CatSubCatSpec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
GO
ALTER TABLE [dbo].[QuotationItems] CHECK CONSTRAINT [FK_QuotationItems_CatSubCatSpec]
GO
ALTER TABLE [dbo].[QuotationItems]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationItems_Quotations] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[QuotationItems] CHECK CONSTRAINT [FK_QuotationItems_Quotations]
GO
ALTER TABLE [dbo].[QuotationItems]  WITH NOCHECK ADD  CONSTRAINT [FK_QuotationItems_ServiceSpec] FOREIGN KEY([TrackerServiceSpecId])
REFERENCES [dbo].[TrackerServiceSpecs] ([TrackerServiceSpecId])
GO
ALTER TABLE [dbo].[QuotationItems] CHECK CONSTRAINT [FK_QuotationItems_ServiceSpec]
GO
ALTER TABLE [dbo].[Quotations]  WITH NOCHECK ADD  CONSTRAINT [FK_Quotations_TrackerCategorySubCategorySpec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
GO
ALTER TABLE [dbo].[Quotations] CHECK CONSTRAINT [FK_Quotations_TrackerCategorySubCategorySpec]
GO
ALTER TABLE [dbo].[Quotations]  WITH NOCHECK ADD  CONSTRAINT [FK_Quotations_TrackerCategorySubCategorySpecPerson] FOREIGN KEY([TrackerCategorySubCategorySpecPersonId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecPersons] ([TrackerCategorySubCategorySpecPersonId])
GO
ALTER TABLE [dbo].[Quotations] CHECK CONSTRAINT [FK_Quotations_TrackerCategorySubCategorySpecPerson]
GO
ALTER TABLE [dbo].[Quotations]  WITH NOCHECK ADD  CONSTRAINT [FK_Quotations_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[Quotations] CHECK CONSTRAINT [FK_Quotations_Trackers]
GO
ALTER TABLE [dbo].[RolePermissions]  WITH CHECK ADD  CONSTRAINT [FK_RolePermissions_Roles] FOREIGN KEY([RoleId])
REFERENCES [dbo].[Roles] ([RoleId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[RolePermissions] CHECK CONSTRAINT [FK_RolePermissions_Roles]
GO
ALTER TABLE [dbo].[Services]  WITH CHECK ADD  CONSTRAINT [FK_Services_Category] FOREIGN KEY([CategoryId])
REFERENCES [dbo].[ServiceCategories] ([CategoryId])
GO
ALTER TABLE [dbo].[Services] CHECK CONSTRAINT [FK_Services_Category]
GO
ALTER TABLE [dbo].[Services]  WITH CHECK ADD  CONSTRAINT [FK_Services_SubCategory] FOREIGN KEY([SubCategoryId])
REFERENCES [dbo].[ServiceSubCategories] ([SubCategoryId])
GO
ALTER TABLE [dbo].[Services] CHECK CONSTRAINT [FK_Services_SubCategory]
GO
ALTER TABLE [dbo].[ServiceSubCategories]  WITH CHECK ADD  CONSTRAINT [FK_ServiceSubCategories_Category] FOREIGN KEY([CategoryId])
REFERENCES [dbo].[ServiceCategories] ([CategoryId])
GO
ALTER TABLE [dbo].[ServiceSubCategories] CHECK CONSTRAINT [FK_ServiceSubCategories_Category]
GO
ALTER TABLE [dbo].[SupportingDocuments]  WITH NOCHECK ADD  CONSTRAINT [FK_SupportingDocuments_CatSubCatSpec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
GO
ALTER TABLE [dbo].[SupportingDocuments] CHECK CONSTRAINT [FK_SupportingDocuments_CatSubCatSpec]
GO
ALTER TABLE [dbo].[SupportingDocuments]  WITH NOCHECK ADD  CONSTRAINT [FK_SupportingDocuments_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[SupportingDocuments] CHECK CONSTRAINT [FK_SupportingDocuments_Trackers]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecPersons]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerCatSubCatSpecPersons_Spec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecPersons] CHECK CONSTRAINT [FK_TrackerCatSubCatSpecPersons_Spec]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecPersons]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerCatSubCatSpecPersons_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecPersons] CHECK CONSTRAINT [FK_TrackerCatSubCatSpecPersons_Tracker]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerCatSubCatSpecs_Category] FOREIGN KEY([CategoryId])
REFERENCES [dbo].[ServiceCategories] ([CategoryId])
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs] CHECK CONSTRAINT [FK_TrackerCatSubCatSpecs_Category]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerCatSubCatSpecs_SubCategory] FOREIGN KEY([SubCategoryId])
REFERENCES [dbo].[ServiceSubCategories] ([SubCategoryId])
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs] CHECK CONSTRAINT [FK_TrackerCatSubCatSpecs_SubCategory]
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerCatSubCatSpecs_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[TrackerCategorySubCategorySpecs] CHECK CONSTRAINT [FK_TrackerCatSubCatSpecs_Trackers]
GO
ALTER TABLE [dbo].[TrackerFamilyMembers]  WITH CHECK ADD  CONSTRAINT [FK_TrackerFamilyMembers_Relationship] FOREIGN KEY([RelationshipId])
REFERENCES [dbo].[Relationships] ([RelationshipId])
GO
ALTER TABLE [dbo].[TrackerFamilyMembers] CHECK CONSTRAINT [FK_TrackerFamilyMembers_Relationship]
GO
ALTER TABLE [dbo].[TrackerFamilyMembers]  WITH CHECK ADD  CONSTRAINT [FK_TrackerFamilyMembers_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[TrackerFamilyMembers] CHECK CONSTRAINT [FK_TrackerFamilyMembers_Tracker]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerQuotationDocuments_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] CHECK CONSTRAINT [FK_TrackerQuotationDocuments_Quotation]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerQuotationDocuments_Tracker] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] CHECK CONSTRAINT [FK_TrackerQuotationDocuments_Tracker]
GO
ALTER TABLE [dbo].[Trackers]  WITH NOCHECK ADD  CONSTRAINT [FK_Trackers_Customers] FOREIGN KEY([CustomerId])
REFERENCES [dbo].[Customers] ([CustomerId])
GO
ALTER TABLE [dbo].[Trackers] CHECK CONSTRAINT [FK_Trackers_Customers]
GO
ALTER TABLE [dbo].[Trackers]  WITH NOCHECK ADD  CONSTRAINT [FK_Trackers_FollowUpFrequencies] FOREIGN KEY([FollowUpFrequencyId])
REFERENCES [dbo].[FollowUpFrequencies] ([FollowUpFrequencyId])
GO
ALTER TABLE [dbo].[Trackers] CHECK CONSTRAINT [FK_Trackers_FollowUpFrequencies]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerServiceSpecs_Category] FOREIGN KEY([CategoryId])
REFERENCES [dbo].[ServiceCategories] ([CategoryId])
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [FK_TrackerServiceSpecs_Category]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerServiceSpecs_CatSubCatSpec] FOREIGN KEY([TrackerCategorySubCategorySpecId])
REFERENCES [dbo].[TrackerCategorySubCategorySpecs] ([TrackerCategorySubCategorySpecId])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [FK_TrackerServiceSpecs_CatSubCatSpec]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerServiceSpecs_Quotation] FOREIGN KEY([QuotationId])
REFERENCES [dbo].[Quotations] ([QuotationId])
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [FK_TrackerServiceSpecs_Quotation]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerServiceSpecs_SubCategory] FOREIGN KEY([SubCategoryId])
REFERENCES [dbo].[ServiceSubCategories] ([SubCategoryId])
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [FK_TrackerServiceSpecs_SubCategory]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [FK_TrackerServiceSpecs_Trackers] FOREIGN KEY([TrackerId])
REFERENCES [dbo].[Trackers] ([TrackerId])
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [FK_TrackerServiceSpecs_Trackers]
GO
ALTER TABLE [dbo].[Users]  WITH CHECK ADD  CONSTRAINT [FK_Users_Roles] FOREIGN KEY([RoleId])
REFERENCES [dbo].[Roles] ([RoleId])
GO
ALTER TABLE [dbo].[Users] CHECK CONSTRAINT [FK_Users_Roles]
GO
ALTER TABLE [dbo].[AuditLogs]  WITH CHECK ADD  CONSTRAINT [CK_AuditLogs_Action] CHECK  (([Action]='DELETE' OR [Action]='UPDATE' OR [Action]='INSERT'))
GO
ALTER TABLE [dbo].[AuditLogs] CHECK CONSTRAINT [CK_AuditLogs_Action]
GO
ALTER TABLE [dbo].[FollowUpFrequencies]  WITH CHECK ADD  CONSTRAINT [CK_FollowUpFrequencies_ManualDays] CHECK  (([IsManual]=(1) AND [FrequencyDays] IS NULL OR [IsManual]=(0) AND [FrequencyDays] IS NOT NULL AND [FrequencyDays]>(0)))
GO
ALTER TABLE [dbo].[FollowUpFrequencies] CHECK CONSTRAINT [CK_FollowUpFrequencies_ManualDays]
GO
ALTER TABLE [dbo].[Invoices]  WITH CHECK ADD  CONSTRAINT [CK_Invoices_Status] CHECK  (([InvoiceStatus]=N'Sent' OR [InvoiceStatus]=N'Created'))
GO
ALTER TABLE [dbo].[Invoices] CHECK CONSTRAINT [CK_Invoices_Status]
GO
ALTER TABLE [dbo].[NotificationRules]  WITH CHECK ADD  CONSTRAINT [CK_NotificationRules_Days] CHECK  (([DaysBefore]>=(-365) AND [DaysBefore]<=(365)))
GO
ALTER TABLE [dbo].[NotificationRules] CHECK CONSTRAINT [CK_NotificationRules_Days]
GO
ALTER TABLE [dbo].[Notifications]  WITH CHECK ADD  CONSTRAINT [CK_Notifications_Channel] CHECK  (([Channel]=N'INAPP' OR [Channel]=N'WHATSAPP' OR [Channel]=N'EMAIL'))
GO
ALTER TABLE [dbo].[Notifications] CHECK CONSTRAINT [CK_Notifications_Channel]
GO
ALTER TABLE [dbo].[Notifications]  WITH CHECK ADD  CONSTRAINT [CK_Notifications_Status] CHECK  (([Status]=N'DISMISSED' OR [Status]=N'SKIPPED' OR [Status]=N'FAILED' OR [Status]=N'SENT' OR [Status]=N'PENDING'))
GO
ALTER TABLE [dbo].[Notifications] CHECK CONSTRAINT [CK_Notifications_Status]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [CK_Payments_Amount] CHECK  (([PaymentAmount]>(0)))
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [CK_Payments_Amount]
GO
ALTER TABLE [dbo].[Payments]  WITH NOCHECK ADD  CONSTRAINT [CK_Payments_Status] CHECK  (([PaymentStatus]=N'Paid' OR [PaymentStatus]=N'Partial'))
GO
ALTER TABLE [dbo].[Payments] CHECK CONSTRAINT [CK_Payments_Status]
GO
ALTER TABLE [dbo].[QuotationCommunications]  WITH NOCHECK ADD  CONSTRAINT [CK_QuotationCommunications_Status] CHECK  (([Status]=N'Failed' OR [Status]=N'Read' OR [Status]=N'Delivered' OR [Status]=N'Sent' OR [Status]=N'Opened' OR [Status]=N'Initiated'))
GO
ALTER TABLE [dbo].[QuotationCommunications] CHECK CONSTRAINT [CK_QuotationCommunications_Status]
GO
ALTER TABLE [dbo].[QuotationCommunications]  WITH NOCHECK ADD  CONSTRAINT [CK_QuotationCommunications_Type] CHECK  (([CommunicationType]=N'WhatsApp' OR [CommunicationType]=N'Email'))
GO
ALTER TABLE [dbo].[QuotationCommunications] CHECK CONSTRAINT [CK_QuotationCommunications_Type]
GO
ALTER TABLE [dbo].[QuotationEmailHistory]  WITH NOCHECK ADD  CONSTRAINT [CK_QuotationEmailHistory_SendType] CHECK  (([SendType]=N'Automatic' OR [SendType]=N'Resend' OR [SendType]=N'Manual'))
GO
ALTER TABLE [dbo].[QuotationEmailHistory] CHECK CONSTRAINT [CK_QuotationEmailHistory_SendType]
GO
ALTER TABLE [dbo].[QuotationEmailHistory]  WITH NOCHECK ADD  CONSTRAINT [CK_QuotationEmailHistory_Status] CHECK  (([Status]=N'Failed' OR [Status]=N'Sent' OR [Status]=N'Pending'))
GO
ALTER TABLE [dbo].[QuotationEmailHistory] CHECK CONSTRAINT [CK_QuotationEmailHistory_Status]
GO
ALTER TABLE [dbo].[QuotationItems]  WITH NOCHECK ADD  CONSTRAINT [CK_QuotationItems_Quantity] CHECK  (([Quantity]>=(1)))
GO
ALTER TABLE [dbo].[QuotationItems] CHECK CONSTRAINT [CK_QuotationItems_Quantity]
GO
ALTER TABLE [dbo].[Quotations]  WITH NOCHECK ADD  CONSTRAINT [CK_Quotations_IndependentScope] CHECK  (([QuotationMode]=N'ALL' AND [TrackerCategorySubCategorySpecId] IS NULL OR [QuotationMode]=N'INDEPENDENT' AND [TrackerCategorySubCategorySpecId] IS NOT NULL))
GO
ALTER TABLE [dbo].[Quotations] CHECK CONSTRAINT [CK_Quotations_IndependentScope]
GO
ALTER TABLE [dbo].[Quotations]  WITH NOCHECK ADD  CONSTRAINT [CK_Quotations_QuotationMode] CHECK  (([QuotationMode]=N'INDEPENDENT' OR [QuotationMode]=N'ALL'))
GO
ALTER TABLE [dbo].[Quotations] CHECK CONSTRAINT [CK_Quotations_QuotationMode]
GO
ALTER TABLE [dbo].[QuotationStatuses]  WITH CHECK ADD  CONSTRAINT [CK_QuotationStatuses_DisplayOrder] CHECK  (([DisplayOrder]>(0)))
GO
ALTER TABLE [dbo].[QuotationStatuses] CHECK CONSTRAINT [CK_QuotationStatuses_DisplayOrder]
GO
ALTER TABLE [dbo].[Services]  WITH NOCHECK ADD  CONSTRAINT [CK_Services_Quantity] CHECK  (([Quantity]>=(1)))
GO
ALTER TABLE [dbo].[Services] CHECK CONSTRAINT [CK_Services_Quantity]
GO
ALTER TABLE [dbo].[SupportingDocuments]  WITH NOCHECK ADD  CONSTRAINT [CK_SupportingDocuments_Status] CHECK  (([Status]='Not Collected' OR [Status]='Collected'))
GO
ALTER TABLE [dbo].[SupportingDocuments] CHECK CONSTRAINT [CK_SupportingDocuments_Status]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments]  WITH NOCHECK ADD  CONSTRAINT [CK_TrackerQuotationDocuments_Expiry] CHECK  (([ExpiryDate] IS NULL OR [DocumentDate] IS NULL OR [ExpiryDate]>=[DocumentDate]))
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] CHECK CONSTRAINT [CK_TrackerQuotationDocuments_Expiry]
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments]  WITH CHECK ADD  CONSTRAINT [CK_TrackerQuotationDocuments_Status] CHECK  (([Status]=N'New' OR [Status]=N'Pending' OR [Status]=N'Success'))
GO
ALTER TABLE [dbo].[TrackerQuotationDocuments] CHECK CONSTRAINT [CK_TrackerQuotationDocuments_Status]
GO
ALTER TABLE [dbo].[TrackerServiceSpecs]  WITH NOCHECK ADD  CONSTRAINT [CK_TrackerServiceSpecs_Quantity] CHECK  (([Quantity]>=(1)))
GO
ALTER TABLE [dbo].[TrackerServiceSpecs] CHECK CONSTRAINT [CK_TrackerServiceSpecs_Quantity]
GO
/****** Object:  StoredProcedure [dbo].[sp_CreateDocument]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- ----------------------------------------------------------------------------
-- 3) sp_CreateDocument - status typo fix only
-- ----------------------------------------------------------------------------
CREATE PROCEDURE [dbo].[sp_CreateDocument]
    @TrackerId INT,
    @DocumentName NVARCHAR(256),
    @DocumentType NVARCHAR(50),
    @DocumentNumber NVARCHAR(100) = NULL,
    @IssueDate DATETIME2,
    @ExpiryDate DATETIME2 = NULL,
    @DocumentLocation NVARCHAR(512) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @IsVerified BIT = 0,
    @DocumentId INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Status NVARCHAR(50) = 'Active';

    -- Calculate status based on expiry date
    IF @ExpiryDate IS NOT NULL
    BEGIN
        IF DATEDIFF(DAY, CAST(GETUTCDATE() AS DATE), CAST(@ExpiryDate AS DATE)) < 0
            SET @Status = 'Expired'
        ELSE IF DATEDIFF(DAY, CAST(GETUTCDATE() AS DATE), CAST(@ExpiryDate AS DATE)) < 90
            SET @Status = 'ExpiringSoon'
        ELSE
            SET @Status = 'Active'
    END;

    INSERT INTO [DocumentRegistries]
        ([TrackerId], [DocumentName], [DocumentType], [DocumentNumber], [IssueDate],
         [ExpiryDate], [DocumentLocation], [Notes], [IsVerified], [Status], [CreatedAt], [UpdatedAt])
    VALUES
        (@TrackerId, @DocumentName, @DocumentType, @DocumentNumber, @IssueDate,
         @ExpiryDate, @DocumentLocation, @Notes, @IsVerified, @Status, GETUTCDATE(), GETUTCDATE());

    SET @DocumentId = SCOPE_IDENTITY();
END;
GO
/****** Object:  StoredProcedure [dbo].[sp_DeleteDocument]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Delete document
CREATE PROCEDURE [dbo].[sp_DeleteDocument]
    @DocumentId INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [DocumentRegistries] WHERE [DocumentId] = @DocumentId;
END;

GO
/****** Object:  StoredProcedure [dbo].[sp_GetAllDocuments]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Get all documents with pagination and filters
CREATE PROCEDURE [dbo].[sp_GetAllDocuments]
    @Page INT = 1,
    @PageSize INT = 25,
    @Search NVARCHAR(256) = NULL,
    @DocumentType NVARCHAR(50) = NULL,
    @Status NVARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Offset INT = (@Page - 1) * @PageSize;

    -- Get total count
    SELECT COUNT(*) as TotalCount
    FROM [DocumentRegistries]
    WHERE (@Search IS NULL OR [DocumentName] LIKE '%' + @Search + '%' OR [DocumentNumber] LIKE '%' + @Search + '%')
      AND (@DocumentType IS NULL OR [DocumentType] = @DocumentType)
      AND (@Status IS NULL OR [Status] = @Status);

    -- Get paginated results
    SELECT
        [DocumentId],
        [TrackerId],
        [DocumentName],
        [DocumentType],
        [DocumentNumber],
        [IssueDate],
        [ExpiryDate],
        [DocumentLocation],
        [Notes],
        [IsVerified],
        [Status],
        [CreatedAt],
        [UpdatedAt]
    FROM [DocumentRegistries]
    WHERE (@Search IS NULL OR [DocumentName] LIKE '%' + @Search + '%' OR [DocumentNumber] LIKE '%' + @Search + '%')
      AND (@DocumentType IS NULL OR [DocumentType] = @DocumentType)
      AND (@Status IS NULL OR [Status] = @Status)
    ORDER BY [CreatedAt] DESC
    OFFSET @Offset ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;

GO
/****** Object:  StoredProcedure [dbo].[sp_GetDocumentById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Get single document
CREATE PROCEDURE [dbo].[sp_GetDocumentById]
    @DocumentId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        [DocumentId],
        [TrackerId],
        [DocumentName],
        [DocumentType],
        [DocumentNumber],
        [IssueDate],
        [ExpiryDate],
        [DocumentLocation],
        [Notes],
        [IsVerified],
        [Status],
        [CreatedAt],
        [UpdatedAt]
    FROM [DocumentRegistries]
    WHERE [DocumentId] = @DocumentId;
END;

GO
/****** Object:  StoredProcedure [dbo].[sp_SearchDocuments]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Search documents
CREATE PROCEDURE [dbo].[sp_SearchDocuments]
    @Search NVARCHAR(256),
    @Page INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Offset INT = (@Page - 1) * @PageSize;

    SELECT COUNT(*) as TotalCount
    FROM [DocumentRegistries]
    WHERE [DocumentName] LIKE '%' + @Search + '%'
       OR [DocumentNumber] LIKE '%' + @Search + '%';

    SELECT
        [DocumentId],
        [TrackerId],
        [DocumentName],
        [DocumentType],
        [DocumentNumber],
        [IssueDate],
        [ExpiryDate],
        [DocumentLocation],
        [Notes],
        [IsVerified],
        [Status],
        [CreatedAt],
        [UpdatedAt]
    FROM [DocumentRegistries]
    WHERE [DocumentName] LIKE '%' + @Search + '%'
       OR [DocumentNumber] LIKE '%' + @Search + '%'
    ORDER BY [CreatedAt] DESC
    OFFSET @Offset ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;

GO
/****** Object:  StoredProcedure [dbo].[sp_UpdateDocument]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- ----------------------------------------------------------------------------
-- 4) sp_UpdateDocument - status typo fix only
-- ----------------------------------------------------------------------------
CREATE PROCEDURE [dbo].[sp_UpdateDocument]
    @DocumentId INT,
    @DocumentName NVARCHAR(256),
    @DocumentType NVARCHAR(50),
    @DocumentNumber NVARCHAR(100) = NULL,
    @IssueDate DATETIME2,
    @ExpiryDate DATETIME2 = NULL,
    @DocumentLocation NVARCHAR(512) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @IsVerified BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Status NVARCHAR(50) = 'Active';

    -- Calculate status based on expiry date
    IF @ExpiryDate IS NOT NULL
    BEGIN
        IF DATEDIFF(DAY, CAST(GETUTCDATE() AS DATE), CAST(@ExpiryDate AS DATE)) < 0
            SET @Status = 'Expired'
        ELSE IF DATEDIFF(DAY, CAST(GETUTCDATE() AS DATE), CAST(@ExpiryDate AS DATE)) < 90
            SET @Status = 'ExpiringSoon'
        ELSE
            SET @Status = 'Active'
    END;

    UPDATE [DocumentRegistries]
    SET
        [DocumentName] = @DocumentName,
        [DocumentType] = @DocumentType,
        [DocumentNumber] = @DocumentNumber,
        [IssueDate] = @IssueDate,
        [ExpiryDate] = @ExpiryDate,
        [DocumentLocation] = @DocumentLocation,
        [Notes] = @Notes,
        [IsVerified] = @IsVerified,
        [Status] = @Status,
        [UpdatedAt] = GETUTCDATE()
    WHERE [DocumentId] = @DocumentId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Access_Check]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   4. ACCESS CHECK
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_Access_Check]
    @UserId INT,
    @MenuKey NVARCHAR(50),
    @ActionCode NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT CONVERT(bit,
        CASE
            WHEN ISNULL(u.Status, N'Inactive') <> N'Active'
              OR r.IsActive = 0 THEN 0
            WHEN r.RoleName = N'Admin' THEN 1
            WHEN rp.RolePermissionId IS NULL THEN 0
            WHEN @ActionCode = N'VIEW'   AND rp.CanView = 1 THEN 1
            WHEN @ActionCode = N'ADD'    AND rp.CanAdd = 1 THEN 1
            WHEN @ActionCode = N'EDIT'   AND rp.CanEdit = 1 THEN 1
            WHEN @ActionCode = N'DELETE' AND rp.CanDelete = 1 THEN 1
            WHEN @ActionCode = N'IMPORT' AND rp.CanImport = 1 THEN 1
            WHEN @ActionCode = N'EXPORT' AND rp.CanExport = 1 THEN 1
            ELSE 0
        END) AS IsAllowed
    FROM dbo.Users AS u
    INNER JOIN dbo.Roles AS r
        ON r.RoleId = u.RoleId
    LEFT JOIN dbo.RolePermissions AS rp
        ON rp.RoleId = r.RoleId
       AND rp.MenuKey = @MenuKey
    WHERE u.UserId = @UserId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Access_GetForUser]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   5. USER ACCESS
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_Access_GetForUser]
    @UserId INT,
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;

    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51006, 'Invalid application code.', 1;

    SELECT
        mi.MenuKey,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanView, 0) END) AS CanView,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanAdd, 0) END) AS CanAdd,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanEdit, 0) END) AS CanEdit,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanDelete, 0) END) AS CanDelete,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanImport, 0) END) AS CanImport,
        CONVERT(bit,
            CASE WHEN u.Status = N'Active'
                   AND r.IsActive = 1
                   AND r.RoleName = N'Admin'
                 THEN 1 ELSE ISNULL(rp.CanExport, 0) END) AS CanExport
    FROM dbo.Users AS u
    INNER JOIN dbo.Roles AS r
        ON r.RoleId = u.RoleId
    INNER JOIN dbo.MenuItems AS mi
        ON mi.IsActive = 1
       AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH')
    LEFT JOIN dbo.RolePermissions AS rp
        ON rp.RoleId = r.RoleId
       AND rp.MenuKey = mi.MenuKey
    WHERE u.UserId = @UserId
    ORDER BY mi.DisplayOrder, mi.MenuItemId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_Delete] @Id INT
AS BEGIN SET NOCOUNT ON; DELETE dbo.ApplicationSettings WHERE ApplicationSettingId=@Id; END;
GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* --------------------------------------------------------------------------
   3. Stored procedure: active application settings
   -------------------------------------------------------------------------- */
CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SettingKey, SettingValue
    FROM dbo.ApplicationSettings
    WHERE IsActive = 1
    ORDER BY ApplicationSettingId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_GetById] @Id INT
AS BEGIN SET NOCOUNT ON; SELECT * FROM dbo.ApplicationSettings WHERE ApplicationSettingId=@Id; END;
GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_GetPaged]
    @PageNumber INT=1, @PageSize INT=25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber=CASE WHEN @PageNumber<1 THEN 1 ELSE @PageNumber END;
    SET @PageSize=CASE WHEN @PageSize<1 OR @PageSize>200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT=(SELECT COUNT(1) FROM dbo.ApplicationSettings);
    SELECT @TotalCount AS TotalCount;
    SELECT * FROM dbo.ApplicationSettings ORDER BY ApplicationSettingId OFFSET (@PageNumber-1)*@PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_Insert] @SettingKey NVARCHAR(100), @SettingValue NVARCHAR(MAX)=NULL, @IsActive BIT=1
AS BEGIN SET NOCOUNT ON; INSERT dbo.ApplicationSettings(SettingKey,SettingValue,IsActive,ModifiedDate) VALUES(@SettingKey,@SettingValue,@IsActive,SYSUTCDATETIME()); SELECT * FROM dbo.ApplicationSettings WHERE ApplicationSettingId=CONVERT(INT,SCOPE_IDENTITY()); END;
GO
/****** Object:  StoredProcedure [dbo].[usp_ApplicationSettings_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_ApplicationSettings_Update] @Id INT, @SettingKey NVARCHAR(100), @SettingValue NVARCHAR(MAX)=NULL, @IsActive BIT=1
AS BEGIN SET NOCOUNT ON; UPDATE dbo.ApplicationSettings SET SettingKey=@SettingKey,SettingValue=@SettingValue,IsActive=@IsActive,ModifiedDate=SYSUTCDATETIME() WHERE ApplicationSettingId=@Id; IF @@ROWCOUNT=0 RETURN; SELECT * FROM dbo.ApplicationSettings WHERE ApplicationSettingId=@Id; END;
GO
/****** Object:  StoredProcedure [dbo].[usp_AuditLogs_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_AuditLogs_GetById]
    @Id BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AuditLogId,TableName,RecordId,Action,ColumnName,OldValue,NewValue,ChangedByUserId,ChangedDate
    FROM dbo.AuditLogs WHERE AuditLogId=@Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_AuditLogs_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_AuditLogs_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25,
    @TableName NVARCHAR(100) = NULL,
    @RecordId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;
    SELECT @TotalCount=COUNT(1) FROM dbo.AuditLogs WHERE (@TableName IS NULL OR TableName=@TableName) AND (@RecordId IS NULL OR RecordId=@RecordId);
    SELECT @TotalCount AS TotalCount;
    SELECT AuditLogId,TableName,RecordId,Action,ColumnName,OldValue,NewValue,ChangedByUserId,ChangedDate
    FROM dbo.AuditLogs
    WHERE (@TableName IS NULL OR TableName=@TableName) AND (@RecordId IS NULL OR RecordId=@RecordId)
    ORDER BY ChangedDate DESC, AuditLogId DESC
    OFFSET (@PageNumber-1)*@PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Customers_Delete] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.Customers WHERE CustomerId=@Id;
    SELECT @@ROWCOUNT AS AffectedCount;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Customers_GetById] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, CustomerCode, CustomerTypeId, CustomerName, CustomerPhone, CustomerEmail, Address, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Customers WHERE CustomerId = @Id;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_GetNextCode]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


CREATE   PROCEDURE [dbo].[usp_Customers_GetNextCode]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @MaxExisting BIGINT = ISNULL(
        (SELECT MAX(TRY_CONVERT(BIGINT, SUBSTRING(CustomerCode, 9, 30)))
         FROM dbo.Customers
         WHERE CustomerCode LIKE N'BP-CUST-[0-9]%'), 0);

    DECLARE @CurrentSequence BIGINT = ISNULL(
        (SELECT CONVERT(BIGINT, current_value)
         FROM sys.sequences
         WHERE object_id = OBJECT_ID(N'dbo.CustomerCodeSequence')), 0);

    DECLARE @Next BIGINT = CASE
        WHEN @CurrentSequence + 1 > @MaxExisting + 1 THEN @CurrentSequence + 1
        ELSE @MaxExisting + 1
    END;

    SELECT N'BP-CUST-' + RIGHT(N'000' + CONVERT(NVARCHAR(20), @Next), 3) AS CustomerCode;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Customers_GetPaged]
    @PageNumber INT = 1, @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1) FROM dbo.Customers;
    SELECT @TotalCount AS TotalCount;
    SELECT CustomerId, CustomerCode, CustomerTypeId, CustomerName, CustomerPhone, CustomerEmail, Address, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Customers
    ORDER BY CustomerId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Customers_Insert]
    @CustomerCode NVARCHAR(50) = NULL, @CustomerTypeId INT, @CustomerName NVARCHAR(150),
    @CustomerPhone NVARCHAR(20) = NULL, @CustomerEmail NVARCHAR(150) = NULL, @Address NVARCHAR(255) = NULL,
    @IsActive BIT, @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId=@CustomerTypeId AND IsActive=1)
        THROW 50002, 'Invalid or inactive Customer Type.', 1;
    -- CustomerCode is server generated. Ignore any client-supplied value.
    SET @CustomerCode = N'BP-CUST-' + RIGHT(N'000' + CONVERT(NVARCHAR(20), NEXT VALUE FOR dbo.CustomerCodeSequence), 3);
    INSERT dbo.Customers(CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate)
    VALUES(@CustomerCode,@CustomerTypeId,@CustomerName,@CustomerPhone,@CustomerEmail,@Address,@IsActive,@CreatedBy,SYSUTCDATETIME());
    DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    SELECT CustomerId,CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate
    FROM dbo.Customers WHERE CustomerId=@NewId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Customers_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Customers_Update]
    @Id INT, @CustomerCode NVARCHAR(50), @CustomerTypeId INT, @CustomerName NVARCHAR(150),
    @CustomerPhone NVARCHAR(20)=NULL, @CustomerEmail NVARCHAR(150)=NULL, @Address NVARCHAR(255)=NULL,
    @IsActive BIT, @ModifiedBy INT=NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId=@Id) RETURN;
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId=@CustomerTypeId AND IsActive=1)
        THROW 50002, 'Invalid or inactive Customer Type.', 1;
    UPDATE dbo.Customers SET CustomerTypeId=@CustomerTypeId, CustomerName=@CustomerName, CustomerPhone=@CustomerPhone,
        CustomerEmail=@CustomerEmail, Address=@Address, IsActive=@IsActive, ModifiedBy=@ModifiedBy, ModifiedDate=SYSUTCDATETIME()
    WHERE CustomerId=@Id;
    SELECT CustomerId,CustomerCode,CustomerTypeId,CustomerName,CustomerPhone,CustomerEmail,Address,IsActive,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate
    FROM dbo.Customers WHERE CustomerId=@Id;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_CustomerTypes_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================================
   Added 2026-09-15 (follow-up #2): usp_CustomerTypes_Delete returned no value.
   CrudControllerBase.Delete() reads a scalar (rows affected) to know whether
   the delete succeeded; this proc deleted the row but returned nothing, so
   ExecuteScalarAsync() got null and the API reported 404 Not Found even on a
   successful delete - the row was really gone, but the frontend never saw a
   success response, so no confirmation message and no proper 204/404 signal.
   This never surfaced before because CustomerTypesController only had GetAll
   until the earlier fix in this patch wired up Insert/Update/Delete.
   ============================================================================ */

CREATE   PROCEDURE [dbo].[usp_CustomerTypes_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId = @Id)
        THROW 51043, 'Customer Type was not found.', 1;

    IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerTypeId = @Id)
        THROW 51044, 'Cannot delete this customer type because it is referenced by one or more customers.', 1;

    DELETE FROM dbo.CustomerTypes
    WHERE CustomerTypeId = @Id;

    SELECT @@ROWCOUNT;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_CustomerTypes_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

    CREATE   PROCEDURE [dbo].[usp_CustomerTypes_GetById]
        @Id INT
    AS
    BEGIN
        SET NOCOUNT ON;
        SELECT CustomerTypeId, CustomerTypeName, IsActive
        FROM dbo.CustomerTypes
        WHERE CustomerTypeId = @Id;
    END
GO
/****** Object:  StoredProcedure [dbo].[usp_CustomerTypes_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

        CREATE PROCEDURE [dbo].[usp_CustomerTypes_GetPaged]
            @PageNumber INT = 1,
            @PageSize INT = 25
        AS
        BEGIN
            SET NOCOUNT ON;
            SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
            SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

            SELECT COUNT(1) AS TotalCount
            FROM dbo.CustomerTypes;

            SELECT CustomerTypeId, CustomerTypeName, IsActive
            FROM dbo.CustomerTypes
            ORDER BY CustomerTypeId
            OFFSET (@PageNumber - 1) * @PageSize ROWS
            FETCH NEXT @PageSize ROWS ONLY;
        END
GO
/****** Object:  StoredProcedure [dbo].[usp_CustomerTypes_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

    CREATE   PROCEDURE [dbo].[usp_CustomerTypes_Insert]
        @CustomerTypeName NVARCHAR(100),
        @IsActive BIT = 1
    AS
    BEGIN
        SET NOCOUNT ON;
        SET @CustomerTypeName = LTRIM(RTRIM(@CustomerTypeName));

        IF NULLIF(@CustomerTypeName, N'') IS NULL
            THROW 51041, 'Customer Type name is required.', 1;

        IF EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE LOWER(LTRIM(RTRIM(CustomerTypeName))) = LOWER(@CustomerTypeName))
            THROW 51042, 'A customer type with the same name already exists.', 1;

        INSERT INTO dbo.CustomerTypes (CustomerTypeName, IsActive)
        VALUES (@CustomerTypeName, @IsActive);

        DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
        SELECT CustomerTypeId, CustomerTypeName, IsActive
        FROM dbo.CustomerTypes
        WHERE CustomerTypeId = @NewId;
    END
GO
/****** Object:  StoredProcedure [dbo].[usp_CustomerTypes_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

    CREATE   PROCEDURE [dbo].[usp_CustomerTypes_Update]
        @Id INT,
        @CustomerTypeName NVARCHAR(100),
        @IsActive BIT
    AS
    BEGIN
        SET NOCOUNT ON;
        SET @CustomerTypeName = LTRIM(RTRIM(@CustomerTypeName));

        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerTypes WHERE CustomerTypeId = @Id)
            RETURN;

        IF NULLIF(@CustomerTypeName, N'') IS NULL
            THROW 51041, 'Customer Type name is required.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.CustomerTypes
            WHERE CustomerTypeId <> @Id
              AND LOWER(LTRIM(RTRIM(CustomerTypeName))) = LOWER(@CustomerTypeName)
        )
            THROW 51042, 'A customer type with the same name already exists.', 1;

        UPDATE dbo.CustomerTypes
        SET CustomerTypeName = @CustomerTypeName,
            IsActive = @IsActive
        WHERE CustomerTypeId = @Id;

        SELECT CustomerTypeId, CustomerTypeName, IsActive
        FROM dbo.CustomerTypes
        WHERE CustomerTypeId = @Id;
    END
GO
/****** Object:  StoredProcedure [dbo].[usp_Dashboard_GetData]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 3. Dashboard ---------- */
CREATE   PROCEDURE [dbo].[usp_Dashboard_GetData]
    @Months INT = 6
AS
BEGIN
    SET NOCOUNT ON;
    IF @Months NOT IN (3, 6, 12) SET @Months = 6;

    DECLARE @Today DATE = CONVERT(DATE, GETDATE());
    DECLARE @MonthStart DATE = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1);
    DECLARE @From DATE = DATEADD(MONTH, 1 - @Months, @MonthStart);   -- first day of the window
    DECLARE @PrevFrom DATE = DATEADD(MONTH, -@Months, @From);        -- previous window of the same length
    DECLARE @Tomorrow DATE = DATEADD(DAY, 1, @Today);

    SELECT * INTO #q FROM dbo.vw_BP_Quotation;
    SELECT * INTO #inv FROM dbo.vw_BP_Invoice;
    SELECT * INTO #pay FROM dbo.vw_BP_Payment;
    SELECT * INTO #doc FROM dbo.vw_BP_Document;
    SELECT * INTO #trk FROM dbo.vw_BP_Tracker;
    SELECT * INTO #spec FROM dbo.vw_BP_Spec;

    /* ---- scalars ---- */
    DECLARE @TotalTrackers INT = (SELECT COUNT(*) FROM #trk);
    DECLARE @ActiveTrackers INT = (SELECT COUNT(*) FROM #trk WHERE IsOpen = 1);
    DECLARE @CreatedThisMonth INT = (SELECT COUNT(*) FROM #trk WHERE CreatedDate >= @MonthStart);
    DECLARE @OpenQuotations INT = (SELECT COUNT(*) FROM #q WHERE StatusKey IN ('DRAFT', 'SENT', 'NEGOTIATE'));
    DECLARE @OpenPipeline DECIMAL(18,2) = ISNULL((SELECT SUM(Grand) FROM #q WHERE StatusKey IN ('DRAFT', 'SENT', 'NEGOTIATE')), 0);
    DECLARE @Accepted INT = (SELECT COUNT(*) FROM #q WHERE StatusKey = 'ACCEPTED');
    DECLARE @Rejected INT = (SELECT COUNT(*) FROM #q WHERE StatusKey = 'REJECTED');
    DECLARE @WinRate INT = CASE WHEN @Accepted + @Rejected = 0 THEN 0 ELSE CONVERT(INT, ROUND(@Accepted * 100.0 / (@Accepted + @Rejected), 0)) END;
    DECLARE @QuotedWin DECIMAL(18,2) = ISNULL((SELECT SUM(Grand) FROM #q WHERE CreatedDate >= @From AND CreatedDate < @Tomorrow), 0);
    DECLARE @QuotedPrev DECIMAL(18,2) = ISNULL((SELECT SUM(Grand) FROM #q WHERE CreatedDate >= @PrevFrom AND CreatedDate < @From), 0);
    DECLARE @AcceptedWin DECIMAL(18,2) = ISNULL((SELECT SUM(Grand) FROM #q WHERE StatusKey = 'ACCEPTED' AND LastTouched >= @From AND LastTouched < @Tomorrow), 0);
    DECLARE @AcceptedPrev DECIMAL(18,2) = ISNULL((SELECT SUM(Grand) FROM #q WHERE StatusKey = 'ACCEPTED' AND LastTouched >= @PrevFrom AND LastTouched < @From), 0);
    DECLARE @CollectedWin DECIMAL(18,2) = ISNULL((SELECT SUM(PaymentAmount) FROM #pay WHERE PaymentDate >= @From AND PaymentDate < @Tomorrow), 0);
    DECLARE @CollectedPrev DECIMAL(18,2) = ISNULL((SELECT SUM(PaymentAmount) FROM #pay WHERE PaymentDate >= @PrevFrom AND PaymentDate < @From), 0);
    DECLARE @Invoiced DECIMAL(18,2) = ISNULL((SELECT SUM(GrandTotal) FROM #inv), 0);
    DECLARE @Paid DECIMAL(18,2) = ISNULL((SELECT SUM(Paid) FROM #inv), 0);
    DECLARE @Outstanding DECIMAL(18,2) = ISNULL((SELECT SUM(Balance) FROM #inv WHERE Balance > 0.004), 0);
    DECLARE @OverdueAmt DECIMAL(18,2) = ISNULL((SELECT SUM(Balance) FROM #inv WHERE Balance > 0.004 AND DueDate < @Today), 0);
    DECLARE @OverdueInv INT = (SELECT COUNT(*) FROM #inv WHERE Balance > 0.004 AND DueDate < @Today);
    DECLARE @OpenInvoices INT = (SELECT COUNT(*) FROM #inv WHERE Balance > 0.004);
    DECLARE @CollRate INT = CASE WHEN @Invoiced <= 0 THEN 0 ELSE CONVERT(INT, ROUND(@Paid * 100.0 / @Invoiced, 0)) END;
    DECLARE @FuOverdue INT = (SELECT COUNT(*) FROM #trk WHERE IsOpen = 1 AND NextFollowUpDate < @Today);
    DECLARE @FuToday INT = (SELECT COUNT(*) FROM #trk WHERE IsOpen = 1 AND NextFollowUpDate = @Today);
    DECLARE @FuWeek INT = (SELECT COUNT(*) FROM #trk WHERE IsOpen = 1 AND NextFollowUpDate <= DATEADD(DAY, 7, @Today));
    DECLARE @Waiting INT = (SELECT COUNT(*) FROM #q WHERE StatusKey IN ('SENT', 'NEGOTIATE') AND LastTouched <= DATEADD(DAY, -7, @Today));
    DECLARE @DocPending INT = (SELECT COUNT(*) FROM #doc WHERE StatusName = N'Pending');
    DECLARE @DocNew INT = (SELECT COUNT(*) FROM #doc WHERE StatusName = N'New');
    DECLARE @DocSuccess INT = (SELECT COUNT(*) FROM #doc WHERE StatusName = N'Success');
    DECLARE @DocExpiring INT = (SELECT COUNT(*) FROM #doc WHERE ExpiryDate >= @Today AND ExpiryDate <= DATEADD(DAY, 30, @Today));
    DECLARE @DocExpired INT = (SELECT COUNT(*) FROM #doc WHERE ExpiryDate < @Today);
    DECLARE @RenewDue30 INT = (SELECT COUNT(*) FROM #spec WHERE IsOpen = 1 AND DueDate >= @Today AND DueDate <= DATEADD(DAY, 30, @Today));
    DECLARE @RenewOverdue INT = (SELECT COUNT(*) FROM #spec WHERE IsOpen = 1 AND DueDate < @Today);

    DECLARE @Kpis NVARCHAR(MAX), @Trend NVARCHAR(MAX), @Stages NVARCHAR(MAX), @QStatus NVARCHAR(MAX), @Ageing NVARCHAR(MAX),
            @DocStatus NVARCHAR(MAX), @Upcoming NVARCHAR(MAX), @FollowUps NVARCHAR(MAX), @ExpDocs NVARCHAR(MAX),
            @OutCust NVARCHAR(MAX), @Team NVARCHAR(MAX), @Activity NVARCHAR(MAX);

    SET @Kpis = (SELECT @Months AS months, @TotalTrackers AS totalTrackers, @ActiveTrackers AS activeTrackers, @CreatedThisMonth AS createdThisMonth,
                        @OpenQuotations AS openQuotations, @OpenPipeline AS openPipelineValue, @WinRate AS winRate,
                        @QuotedWin AS quotedValue, @QuotedPrev AS quotedValuePrev,
                        @AcceptedWin AS acceptedValue, @AcceptedPrev AS acceptedValuePrev,
                        @CollectedWin AS collectedValue, @CollectedPrev AS collectedValuePrev,
                        @Invoiced AS invoicedTotal, @Paid AS paidTotal, @Outstanding AS outstanding,
                        @OverdueAmt AS overdueAmount, @OverdueInv AS overdueInvoices, @OpenInvoices AS openInvoices, @CollRate AS collectionRate,
                        @FuOverdue AS followUpsOverdue, @FuToday AS followUpsToday, @FuWeek AS followUpsWeek, @Waiting AS quotesWaiting,
                        @DocPending AS docsPending, @DocNew AS docsNew, @DocSuccess AS docsSuccess, @DocExpiring AS docsExpiring, @DocExpired AS docsExpired,
                        @RenewDue30 AS renewalsDue30, @RenewOverdue AS renewalsOverdue,
                        CONVERT(VARCHAR(10), @Today, 23) AS today
                 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    /* ---- money flow by month ---- */
    SET @Trend = (
        SELECT FORMAT(m.d, N'MMM', N'en-US') AS label,
               CONVERT(VARCHAR(7), m.d, 126) AS [month],
               (SELECT ISNULL(SUM(x.Grand), 0) FROM #q x WHERE x.CreatedDate >= m.d AND x.CreatedDate < DATEADD(MONTH, 1, m.d)) AS quoted,
               (SELECT ISNULL(SUM(x.Grand), 0) FROM #q x WHERE x.StatusKey = 'ACCEPTED' AND x.LastTouched >= m.d AND x.LastTouched < DATEADD(MONTH, 1, m.d)) AS accepted,
               (SELECT ISNULL(SUM(x.PaymentAmount), 0) FROM #pay x WHERE x.PaymentDate >= m.d AND x.PaymentDate < DATEADD(MONTH, 1, m.d)) AS collected,
               (SELECT COUNT(*) FROM #trk x WHERE x.CreatedDate >= m.d AND x.CreatedDate < DATEADD(MONTH, 1, m.d)) AS trackers
        FROM (SELECT DATEADD(MONTH, v.n, @From) AS d
              FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11)) v(n)
              WHERE v.n < @Months) m
        ORDER BY m.d
        FOR JSON PATH);

    /* ---- tracker pipeline ---- */
    SET @Stages = (
        SELECT s.StatusCode AS code, s.StatusName AS name, COUNT(t.TrackerId) AS [count], ISNULL(SUM(qv.Grand), 0) AS quoted
        FROM dbo.TrackerStatuses s
        LEFT JOIN #trk t ON t.StatusCode = s.StatusCode
        LEFT JOIN (SELECT TrackerId, SUM(Grand) AS Grand FROM #q GROUP BY TrackerId) qv ON qv.TrackerId = t.TrackerId
        WHERE s.IsActive = 1
        GROUP BY s.StatusCode, s.StatusName, s.DisplayOrder
        ORDER BY s.DisplayOrder
        FOR JSON PATH);

    /* ---- quotation conversion ---- */
    SET @QStatus = (
        SELECT k.code, k.name, COUNT(x.QuotationId) AS [count], ISNULL(SUM(x.Grand), 0) AS value
        FROM (VALUES ('DRAFT', N'Draft', 1), ('SENT', N'Sent', 2), ('NEGOTIATE', N'Negotiation', 3), ('ACCEPTED', N'Accepted', 4), ('REJECTED', N'Rejected', 5)) k(code, name, ord)
        LEFT JOIN #q x ON x.StatusKey = k.code
        GROUP BY k.code, k.name, k.ord
        ORDER BY k.ord
        FOR JSON PATH);

    /* ---- receivables ageing (days since invoice date) ---- */
    SET @Ageing = (
        SELECT b.label, ISNULL(SUM(i.Balance), 0) AS value, COUNT(i.InvoiceId) AS [count]
        FROM (VALUES (N'0-30 days', 0, 30, 1), (N'31-60 days', 31, 60, 2), (N'61-90 days', 61, 90, 3), (N'90+ days', 91, 100000, 4)) b(label, lo, hi, ord)
        LEFT JOIN #inv i ON i.Balance > 0.004 AND DATEDIFF(DAY, i.InvoiceDate, @Today) BETWEEN b.lo AND b.hi
        GROUP BY b.label, b.ord
        ORDER BY b.ord
        FOR JSON PATH);

    SET @DocStatus = (
        SELECT k.name AS label, COUNT(d.DocumentId) AS value
        FROM (VALUES (N'Success', 1), (N'Pending', 2), (N'New', 3)) k(name, ord)
        LEFT JOIN #doc d ON d.StatusName = k.name
        GROUP BY k.name, k.ord
        ORDER BY k.ord
        FOR JSON PATH);

    /* ---- coming up lists ---- */
    SET @Upcoming = (
        SELECT TOP (8) s.CustomerName AS customer, s.TrackerId AS trackerId, s.CategoryName AS category, s.SubCategoryName AS subcategory,
               s.PersonName AS person, s.DueDate AS dueDate, DATEDIFF(DAY, @Today, s.DueDate) AS days, s.EmployeeName AS employee, s.StatusName AS status
        FROM #spec s
        WHERE s.IsOpen = 1 AND s.DueDate >= @Today AND s.DueDate <= DATEADD(DAY, 30, @Today)
        ORDER BY s.DueDate, s.TrackerId
        FOR JSON PATH);

    SET @FollowUps = (
        SELECT TOP (8) t.CustomerName AS customer, t.TrackerId AS trackerId, t.NextFollowUpDate AS dueDate,
               DATEDIFF(DAY, @Today, t.NextFollowUpDate) AS days, t.EmployeeName AS employee, t.StatusName AS status
        FROM #trk t
        WHERE t.IsOpen = 1 AND t.NextFollowUpDate IS NOT NULL AND t.NextFollowUpDate <= DATEADD(DAY, 7, @Today)
        ORDER BY t.NextFollowUpDate, t.TrackerId
        FOR JSON PATH);

    SET @ExpDocs = (
        SELECT TOP (8) d.DocumentName AS document, d.ReferenceNo AS reference, d.CustomerName AS customer, d.TrackerId AS trackerId,
               d.ExpiryDate AS dueDate, DATEDIFF(DAY, @Today, d.ExpiryDate) AS days, d.StatusName AS status
        FROM #doc d
        WHERE d.ExpiryDate IS NOT NULL AND d.ExpiryDate <= DATEADD(DAY, 30, @Today) AND d.ExpiryDate >= DATEADD(DAY, -30, @Today)
        ORDER BY d.ExpiryDate, d.DocumentId
        FOR JSON PATH);

    /* ---- customers with money outstanding ---- */
    SET @OutCust = (
        SELECT TOP (6) i.CustomerName AS customer, SUM(i.Balance) AS outstanding,
               SUM(CASE WHEN i.DueDate < @Today THEN i.Balance ELSE 0 END) AS overdue, COUNT(*) AS invoices
        FROM #inv i
        WHERE i.Balance > 0.004
        GROUP BY i.CustomerName
        ORDER BY SUM(i.Balance) DESC, i.CustomerName
        FOR JSON PATH);

    /* ---- team ---- */
    SET @Team = (
        SELECT TOP (10) x.employee, x.trackers, x.completed, x.acceptedValue, x.collected, x.invoiced,
               CASE WHEN x.invoiced <= 0 THEN 0 ELSE CONVERT(INT, ROUND(x.collected * 100.0 / x.invoiced, 0)) END AS collectionRate,
               x.lateFollowUps
        FROM (SELECT t.EmployeeName AS employee,
                     COUNT(*) AS trackers,
                     SUM(CASE WHEN t.StatusCode = N'COMPLETED' THEN 1 ELSE 0 END) AS completed,
                     ISNULL(SUM(a.AcceptedValue), 0) AS acceptedValue,
                     ISNULL(SUM(p.Paid), 0) AS collected,
                     ISNULL(SUM(p.Invoiced), 0) AS invoiced,
                     SUM(CASE WHEN t.IsOpen = 1 AND t.NextFollowUpDate < @Today THEN 1 ELSE 0 END) AS lateFollowUps
              FROM #trk t
              LEFT JOIN (SELECT TrackerId, SUM(Grand) AS AcceptedValue FROM #q WHERE StatusKey = 'ACCEPTED' GROUP BY TrackerId) a ON a.TrackerId = t.TrackerId
              LEFT JOIN (SELECT TrackerId, SUM(Paid) AS Paid, SUM(GrandTotal) AS Invoiced FROM #inv GROUP BY TrackerId) p ON p.TrackerId = t.TrackerId
              GROUP BY t.EmployeeName) x
        ORDER BY x.collected DESC, x.trackers DESC, x.employee
        FOR JSON PATH);

    /* ---- recent activity ---- */
    CREATE TABLE #ev (EventAt DATETIME2(0) NOT NULL, EventType NVARCHAR(20) NOT NULL, Title NVARCHAR(300) NOT NULL, Subtitle NVARCHAR(300) NULL);
    INSERT #ev (EventAt, EventType, Title, Subtitle)
    SELECT TOP (8) p.CreatedDate, N'payment', CONCAT(N'Payment ', dbo.fn_BP_Money(p.PaymentAmount), N' received'), CONCAT(p.CustomerName, N' - ', p.PaymentNumber)
    FROM #pay p ORDER BY p.CreatedDate DESC;
    INSERT #ev (EventAt, EventType, Title, Subtitle)
    SELECT TOP (8) v.CreatedDate, N'invoice', CONCAT(N'Invoice ', v.InvoiceNumber, N' created'), CONCAT(c.CustomerName, N' - ', dbo.fn_BP_Money(v.GrandTotal))
    FROM dbo.Invoices v INNER JOIN #trk c ON c.TrackerId = v.TrackerId ORDER BY v.CreatedDate DESC;
    INSERT #ev (EventAt, EventType, Title, Subtitle)
    SELECT TOP (8) CONVERT(DATETIME2(0), COALESCE(q0.ModifiedDate, q0.CreatedDate)),
           CASE WHEN x.StatusKey = 'ACCEPTED' THEN N'accepted' ELSE N'rejected' END,
           CONCAT(N'Quotation BP-QT-', x.QuotationId, CASE WHEN x.StatusKey = 'ACCEPTED' THEN N' accepted' ELSE N' rejected' END),
           CONCAT(x.CustomerName, N' - ', dbo.fn_BP_Money(x.Grand))
    FROM #q x INNER JOIN dbo.Quotations q0 ON q0.QuotationId = x.QuotationId
    WHERE x.StatusKey IN ('ACCEPTED', 'REJECTED')
    ORDER BY COALESCE(q0.ModifiedDate, q0.CreatedDate) DESC;
    INSERT #ev (EventAt, EventType, Title, Subtitle)
    SELECT TOP (8) CONVERT(DATETIME2(0), d.LastTouched), N'document', CONCAT(N'Document ', d.DocumentName, N' marked Success'), d.CustomerName
    FROM #doc d WHERE d.StatusName = N'Success' ORDER BY d.LastTouched DESC;

    SET @Activity = (
        SELECT TOP (8) e.EventType AS [type], e.Title AS [title], e.Subtitle AS [subtitle], e.EventAt AS [at]
        FROM #ev e
        ORDER BY e.EventAt DESC
        FOR JSON PATH);

    SELECT ISNULL(@Kpis, N'{}')       AS kpis,
           ISNULL(@Trend, N'[]')      AS trend,
           ISNULL(@Stages, N'[]')     AS stages,
           ISNULL(@QStatus, N'[]')    AS quotationStatus,
           ISNULL(@Ageing, N'[]')     AS ageing,
           ISNULL(@DocStatus, N'[]')  AS docStatus,
           ISNULL(@Upcoming, N'[]')   AS upcomingRenewals,
           ISNULL(@FollowUps, N'[]')  AS followUps,
           ISNULL(@ExpDocs, N'[]')    AS expiringDocuments,
           ISNULL(@OutCust, N'[]')    AS outstandingCustomers,
           ISNULL(@Team, N'[]')       AS team,
           ISNULL(@Activity, N'[]')   AS activity;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_EmailTemplates_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_EmailTemplates_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 2000 THEN 1000 ELSE @PageSize END;
    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1) FROM dbo.EmailTemplates WHERE IsActive = 1;
    SELECT @TotalCount AS TotalCount;
    SELECT EmailTemplateId, TemplateName, Subject, Body, IsActive, CreatedDate, ModifiedDate
    FROM dbo.EmailTemplates
    WHERE IsActive = 1
    ORDER BY EmailTemplateId
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Employees_Delete]
    @Id int
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.Employees
    WHERE EmployeeId = @Id;

    SELECT @@ROWCOUNT AS AffectedRows;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_GetActiveForAutocomplete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 8. usp_Employees_GetActiveForAutocomplete
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_Employees_GetActiveForAutocomplete]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [EmployeeId], [FullName], [Mobile], [Email]
    FROM [dbo].[Employees]
    WHERE [IsActive] = 1
    ORDER BY [FullName];
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Employees_GetById]
    @Id int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        EmployeeId,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Employees
    WHERE EmployeeId = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Employees_GetPaged]
    @PageNumber int = 1,
    @PageSize int = 25
AS
BEGIN
    SET NOCOUNT ON;

    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 200 SET @PageSize = 200;

    SELECT COUNT(1) AS TotalCount
    FROM dbo.Employees;

    SELECT
        EmployeeId,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Employees
    ORDER BY EmployeeId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   Employee stored procedures aligned with current dbo.Employees
   UserId has been removed from the table and all procedures.
   ============================================================ */

CREATE   PROCEDURE [dbo].[usp_Employees_Insert]
    @FullName nvarchar(150),
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @RoleId int = NULL,
    @JoinDate date = NULL,
    @IsActive bit = 1
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
        IsActive
    )
    VALUES
    (
        @FullName,
        @Mobile,
        @Email,
        @RoleId,
        @JoinDate,
        @IsActive
    );

    DECLARE @EmployeeId int = CONVERT(int, SCOPE_IDENTITY());

    SELECT
        EmployeeId,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Employees
    WHERE EmployeeId = @EmployeeId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Employees_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Employees_Update]
    @Id int,
    @FullName nvarchar(150),
    @Mobile nvarchar(20) = NULL,
    @Email nvarchar(150) = NULL,
    @RoleId int = NULL,
    @JoinDate date = NULL,
    @IsActive bit = 1
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
        ModifiedDate = SYSUTCDATETIME()
    WHERE EmployeeId = @Id;

    SELECT
        EmployeeId,
        FullName,
        Mobile,
        Email,
        RoleId,
        JoinDate,
        IsActive,
        CreatedDate,
        ModifiedDate
    FROM dbo.Employees
    WHERE EmployeeId = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_Delete] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM dbo.Trackers WHERE FollowUpFrequencyId=@Id)
        THROW 51035,'This frequency is in use by one or more trackers.',1;
    IF EXISTS(SELECT 1 FROM dbo.FollowUps WHERE FollowUpFrequencyId=@Id)
        THROW 51036,'This frequency is referenced by follow-up history.',1;
    DELETE FROM dbo.FollowUpFrequencies WHERE FollowUpFrequencyId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT FollowUpFrequencyId,FrequencyCode,FrequencyName,FrequencyDays,IsManual,DisplayOrder,AutoSendEmail,EmailTemplateId
    FROM dbo.FollowUpFrequencies WHERE IsActive=1 ORDER BY DisplayOrder,FrequencyName,FollowUpFrequencyId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_GetById] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT FollowUpFrequencyId,FrequencyCode,FrequencyName,FrequencyDays,IsManual,DisplayOrder,IsDefault,AutoSendEmail,EmailTemplateId,IsActive,CreatedDate,ModifiedDate
    FROM dbo.FollowUpFrequencies WHERE FollowUpFrequencyId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Re-create frequency procedures including automatic-email settings. */
CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_GetPaged]
    @PageNumber INT=1,@PageSize INT=25
AS
BEGIN
    SET NOCOUNT ON; SET @PageNumber=CASE WHEN @PageNumber<1 THEN 1 ELSE @PageNumber END;
    SET @PageSize=CASE WHEN @PageSize<1 OR @PageSize>200 THEN 25 ELSE @PageSize END;
    SELECT COUNT(1) AS TotalCount FROM dbo.FollowUpFrequencies;
    SELECT FollowUpFrequencyId,FrequencyCode,FrequencyName,FrequencyDays,IsManual,DisplayOrder,IsDefault,AutoSendEmail,EmailTemplateId,IsActive,CreatedDate,ModifiedDate
    FROM dbo.FollowUpFrequencies ORDER BY DisplayOrder,FollowUpFrequencyId OFFSET (@PageNumber-1)*@PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_Insert]
    @FrequencyCode NVARCHAR(50),@FrequencyName NVARCHAR(100),@FrequencyDays INT=NULL,@IsManual BIT,
    @DisplayOrder INT,@IsDefault BIT,@AutoSendEmail BIT=0,@EmailTemplateId INT=NULL,@IsActive BIT=1
AS
BEGIN
    SET NOCOUNT ON; IF @IsManual=1 BEGIN SET @FrequencyDays=NULL; SET @AutoSendEmail=0; SET @EmailTemplateId=NULL; END;
    IF @IsManual=0 AND (@FrequencyDays IS NULL OR @FrequencyDays<=0) THROW 51031,'FrequencyDays must be greater than zero for an automatic frequency.',1;
    IF EXISTS(SELECT 1 FROM dbo.FollowUpFrequencies WHERE FrequencyCode=@FrequencyCode OR FrequencyName=@FrequencyName) THROW 51032,'Frequency code or name already exists.',1;
    IF @IsDefault=1 UPDATE dbo.FollowUpFrequencies SET IsDefault=0;
    INSERT dbo.FollowUpFrequencies(FrequencyCode,FrequencyName,FrequencyDays,IsManual,DisplayOrder,IsDefault,AutoSendEmail,EmailTemplateId,IsActive)
    VALUES(@FrequencyCode,@FrequencyName,@FrequencyDays,@IsManual,@DisplayOrder,@IsDefault,@AutoSendEmail,@EmailTemplateId,@IsActive);
    DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    EXEC dbo.usp_FollowUpFrequencies_GetById @NewId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUpFrequencies_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_FollowUpFrequencies_Update]
    @Id INT,@FrequencyCode NVARCHAR(50),@FrequencyName NVARCHAR(100),@FrequencyDays INT=NULL,@IsManual BIT,
    @DisplayOrder INT,@IsDefault BIT,@AutoSendEmail BIT=0,@EmailTemplateId INT=NULL,@IsActive BIT=1
AS
BEGIN
    SET NOCOUNT ON; IF @IsManual=1 BEGIN SET @FrequencyDays=NULL; SET @AutoSendEmail=0; SET @EmailTemplateId=NULL; END;
    IF @IsManual=0 AND (@FrequencyDays IS NULL OR @FrequencyDays<=0) THROW 51033,'FrequencyDays must be greater than zero for an automatic frequency.',1;
    IF EXISTS(SELECT 1 FROM dbo.FollowUpFrequencies WHERE (FrequencyCode=@FrequencyCode OR FrequencyName=@FrequencyName) AND FollowUpFrequencyId<>@Id) THROW 51034,'Frequency code or name already exists.',1;
    IF @IsDefault=1 UPDATE dbo.FollowUpFrequencies SET IsDefault=0 WHERE FollowUpFrequencyId<>@Id;
    UPDATE dbo.FollowUpFrequencies SET FrequencyCode=@FrequencyCode,FrequencyName=@FrequencyName,FrequencyDays=@FrequencyDays,IsManual=@IsManual,
      DisplayOrder=@DisplayOrder,IsDefault=@IsDefault,AutoSendEmail=@AutoSendEmail,EmailTemplateId=@EmailTemplateId,IsActive=@IsActive,ModifiedDate=SYSUTCDATETIME()
    WHERE FollowUpFrequencyId=@Id;
    IF @@ROWCOUNT=0 RETURN;
    EXEC dbo.usp_FollowUpFrequencies_GetById @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[FollowUps] WHERE [FollowUpId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [FollowUpId], [CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[FollowUps]
    WHERE [FollowUpId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_GetByTracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_GetByTracker] @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT fu.FollowUpId,fu.CustomerId,fu.TrackerId,fu.TrackerCategorySubCategorySpecId,
           fu.Notes,fu.SenderInfo,fu.CreatedBy,fu.CreatedDate,fu.ModifiedBy,fu.ModifiedDate,
           fu.ActivityType,fu.CommunicationType,fu.FollowUpDate,fu.NextFollowUpDate,
           fu.FollowUpFrequencyId,ff.FrequencyName,fu.EmailTemplateId,fu.EmailSubject,
           fu.RecipientEmail,fu.SentStatus,fu.AutoGenerated
    FROM dbo.FollowUps fu
    LEFT JOIN dbo.FollowUpFrequencies ff ON ff.FollowUpFrequencyId=fu.FollowUpFrequencyId
    WHERE fu.TrackerId=@TrackerId
    ORDER BY ISNULL(fu.FollowUpDate,CAST(fu.CreatedDate AS DATE)) DESC,fu.FollowUpId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[FollowUps];
    SELECT @TotalCount AS TotalCount;

    SELECT [FollowUpId], [CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[FollowUps]
    ORDER BY [FollowUpId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_Insert]
    @CustomerId INT,
    @TrackerId INT,
    @TrackerCategorySubCategorySpecId INT,
    @Notes NVARCHAR(1000),
    @SenderInfo NVARCHAR(200),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[FollowUps] ([CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate])
    VALUES (@CustomerId, @TrackerId, @TrackerCategorySubCategorySpecId, @Notes, @SenderInfo, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [FollowUpId], [CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[FollowUps]
    WHERE [FollowUpId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_RecordAndReschedule]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 7. usp_FollowUps_RecordAndReschedule
--    Logs a follow-up occurrence and reschedules the tracker's next due
--    date using the given frequency (in days).
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_FollowUps_RecordAndReschedule]
    @TrackerId INT,
    @CustomerId INT,
    @Notes NVARCHAR(1000) = NULL,
    @SenderInfo NVARCHAR(200) = NULL,
    @FollowUpFrequencyDays INT,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[FollowUps] ([CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate])
    VALUES (@CustomerId, @TrackerId, NULL, @Notes, @SenderInfo, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());

    UPDATE [dbo].[Trackers]
    SET [FollowUpFrequencyDays] = @FollowUpFrequencyDays,
        [NextFollowUpDate] = DATEADD(DAY, @FollowUpFrequencyDays, CAST(SYSUTCDATETIME() AS DATE)),
        [ModifiedBy] = @CreatedBy,
        [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerId] = @TrackerId;

    SELECT [FollowUpId], [CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[FollowUps] WHERE [FollowUpId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_RecordEmailEvent]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_RecordEmailEvent]
    @TrackerId INT,@CustomerId INT,@FollowUpDate DATE,@Notes NVARCHAR(1000),
    @EmailTemplateId INT=NULL,@EmailSubject NVARCHAR(255)=NULL,@RecipientEmail NVARCHAR(200)=NULL,
    @SentStatus NVARCHAR(20)=N'Sent',@NextFollowUpDate DATE=NULL,@FollowUpFrequencyId INT=NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.Trackers WHERE TrackerId=@TrackerId AND CustomerId=@CustomerId)
        THROW 51039,'Tracker/customer combination was not found.',1;

    INSERT dbo.FollowUps(CustomerId,TrackerId,TrackerCategorySubCategorySpecId,Notes,SenderInfo,CreatedBy,CreatedDate,
                         ActivityType,CommunicationType,FollowUpDate,NextFollowUpDate,FollowUpFrequencyId,EmailTemplateId,
                         EmailSubject,RecipientEmail,SentStatus,AutoGenerated)
    VALUES(@CustomerId,@TrackerId,NULL,@Notes,N'System',NULL,SYSUTCDATETIME(),N'Automatic Email',N'Email',@FollowUpDate,
           @NextFollowUpDate,@FollowUpFrequencyId,@EmailTemplateId,@EmailSubject,@RecipientEmail,@SentStatus,1);

    DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    UPDATE dbo.Trackers SET NextFollowUpDate=@NextFollowUpDate,ModifiedDate=SYSUTCDATETIME() WHERE TrackerId=@TrackerId;

    SELECT fu.FollowUpId,fu.CustomerId,fu.TrackerId,fu.TrackerCategorySubCategorySpecId,fu.Notes,fu.SenderInfo,
           fu.CreatedBy,fu.CreatedDate,fu.ModifiedBy,fu.ModifiedDate,fu.ActivityType,fu.CommunicationType,fu.FollowUpDate,
           fu.NextFollowUpDate,fu.FollowUpFrequencyId,ff.FrequencyName,fu.EmailTemplateId,fu.EmailSubject,
           fu.RecipientEmail,fu.SentStatus,fu.AutoGenerated
    FROM dbo.FollowUps fu LEFT JOIN dbo.FollowUpFrequencies ff ON ff.FollowUpFrequencyId=fu.FollowUpFrequencyId
    WHERE fu.FollowUpId=@NewId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_RecordManual]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_RecordManual]
    @TrackerId INT,@CustomerId INT,@FollowUpDate DATE,@CommunicationType NVARCHAR(30),
    @Notes NVARCHAR(1000),@SenderInfo NVARCHAR(200),@NextFollowUpDate DATE=NULL,
    @FollowUpFrequencyId INT=NULL,@CreatedBy INT=NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.Trackers WHERE TrackerId=@TrackerId AND CustomerId=@CustomerId)
        THROW 51038,'Tracker/customer combination was not found.',1;

    INSERT dbo.FollowUps(CustomerId,TrackerId,TrackerCategorySubCategorySpecId,Notes,SenderInfo,CreatedBy,CreatedDate,
                         ActivityType,CommunicationType,FollowUpDate,NextFollowUpDate,FollowUpFrequencyId,AutoGenerated)
    VALUES(@CustomerId,@TrackerId,NULL,@Notes,@SenderInfo,@CreatedBy,SYSUTCDATETIME(),N'Manual',
           @CommunicationType,@FollowUpDate,@NextFollowUpDate,@FollowUpFrequencyId,0);

    DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    UPDATE dbo.Trackers
    SET NextFollowUpDate=@NextFollowUpDate,
        FollowUpFrequencyId=COALESCE(@FollowUpFrequencyId,FollowUpFrequencyId),
        FollowUpFrequencyDays=COALESCE((SELECT FrequencyDays FROM dbo.FollowUpFrequencies WHERE FollowUpFrequencyId=@FollowUpFrequencyId),FollowUpFrequencyDays),
        ModifiedBy=@CreatedBy,ModifiedDate=SYSUTCDATETIME()
    WHERE TrackerId=@TrackerId;

    SELECT fu.FollowUpId,fu.CustomerId,fu.TrackerId,fu.TrackerCategorySubCategorySpecId,fu.Notes,fu.SenderInfo,
           fu.CreatedBy,fu.CreatedDate,fu.ModifiedBy,fu.ModifiedDate,fu.ActivityType,fu.CommunicationType,fu.FollowUpDate,
           fu.NextFollowUpDate,fu.FollowUpFrequencyId,ff.FrequencyName,fu.EmailTemplateId,fu.EmailSubject,
           fu.RecipientEmail,fu.SentStatus,fu.AutoGenerated
    FROM dbo.FollowUps fu LEFT JOIN dbo.FollowUpFrequencies ff ON ff.FollowUpFrequencyId=fu.FollowUpFrequencyId
    WHERE fu.FollowUpId=@NewId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_FollowUps_Update]
    @Id INT,
    @CustomerId INT,
    @TrackerId INT,
    @TrackerCategorySubCategorySpecId INT,
    @Notes NVARCHAR(1000),
    @SenderInfo NVARCHAR(200),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[FollowUps]
    SET [CustomerId] = @CustomerId, [TrackerId] = @TrackerId, [TrackerCategorySubCategorySpecId] = @TrackerCategorySubCategorySpecId, [Notes] = @Notes, [SenderInfo] = @SenderInfo, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [FollowUpId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [FollowUpId], [CustomerId], [TrackerId], [TrackerCategorySubCategorySpecId], [Notes], [SenderInfo], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[FollowUps]
    WHERE [FollowUpId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_FollowUps_UpdateHistory]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* History edit support for the nested Follow-Up screen. */
CREATE   PROCEDURE [dbo].[usp_FollowUps_UpdateHistory]
    @FollowUpId INT,
    @Notes NVARCHAR(1000)=NULL,
    @CommunicationType NVARCHAR(30)=N'Other',
    @NextFollowUpDate DATE=NULL,
    @ModifiedBy INT=NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.FollowUps
    SET Notes=@Notes,
        CommunicationType=@CommunicationType,
        NextFollowUpDate=@NextFollowUpDate,
        ModifiedBy=@ModifiedBy,
        ModifiedDate=SYSUTCDATETIME()
    WHERE FollowUpId=@FollowUpId;

    IF @@ROWCOUNT=0 RETURN;

    DECLARE @TrackerId INT;
    SELECT @TrackerId=TrackerId FROM dbo.FollowUps WHERE FollowUpId=@FollowUpId;
    UPDATE dbo.Trackers SET NextFollowUpDate=@NextFollowUpDate,ModifiedBy=@ModifiedBy,ModifiedDate=SYSUTCDATETIME()
    WHERE TrackerId=@TrackerId;

    SELECT fu.FollowUpId,fu.CustomerId,fu.TrackerId,fu.TrackerCategorySubCategorySpecId,fu.Notes,fu.SenderInfo,
           fu.CreatedBy,fu.CreatedDate,fu.ModifiedBy,fu.ModifiedDate,fu.ActivityType,fu.CommunicationType,fu.FollowUpDate,
           fu.NextFollowUpDate,fu.FollowUpFrequencyId,ff.FrequencyName,fu.EmailTemplateId,fu.EmailSubject,
           fu.RecipientEmail,fu.SentStatus,fu.AutoGenerated
    FROM dbo.FollowUps fu
    LEFT JOIN dbo.FollowUpFrequencies ff ON ff.FollowUpFrequencyId=fu.FollowUpFrequencyId
    WHERE fu.FollowUpId=@FollowUpId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Invoices_CreateFromQuotation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Invoices_CreateFromQuotation]
    @QuotationId INT,
    @InvoiceDate DATE = NULL,
    @DueDate DATE = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @TrackerId INT, @CustomerCode NVARCHAR(50), @Status NVARCHAR(50);
    SELECT @TrackerId = TrackerId, @CustomerCode = CustomerCode, @Status = QuotationStatus FROM dbo.Quotations WHERE QuotationId = @QuotationId;
    IF @TrackerId IS NULL THROW 50050, 'Quotation was not found.', 1;
    IF dbo.fn_QuotationStatusKey(@Status) <> 'ACCEPTED' THROW 50051, 'An invoice can be created only for an Accepted quotation.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Invoices WHERE QuotationId = @QuotationId) THROW 50052, 'An invoice already exists for this quotation.', 1;

    DECLARE @Grand DECIMAL(18,2), @Vat DECIMAL(18,2);
    SELECT @Grand = ISNULL(SUM((ISNULL(Amount,0)+ISNULL(VAT,0)) * ISNULL(Quantity,1)), 0),
           @Vat   = ISNULL(SUM(ISNULL(VAT,0) * ISNULL(Quantity,1)), 0)
    FROM dbo.QuotationItems WHERE QuotationId = @QuotationId;
    IF @Grand <= 0 THROW 50053, 'The quotation has no payable amount.', 1;
    IF @DueDate IS NOT NULL AND @DueDate < ISNULL(@InvoiceDate, CAST(SYSUTCDATETIME() AS DATE)) THROW 50054, 'Due date cannot be before the invoice date.', 1;

    DECLARE @NewNo INT = NEXT VALUE FOR dbo.InvoiceNumberSequence;
    DECLARE @Number NVARCHAR(30) = CONCAT(N'BP-INV-', @NewNo);
    INSERT dbo.Invoices (InvoiceNumber, TrackerId, QuotationId, CustomerCode, InvoiceDate, DueDate, NetAmount, VatAmount, GrandTotal, Notes, CreatedBy)
    VALUES (@Number, @TrackerId, @QuotationId, @CustomerCode, ISNULL(@InvoiceDate, CAST(SYSUTCDATETIME() AS DATE)), @DueDate,
            @Grand - @Vat, @Vat, @Grand, NULLIF(LTRIM(RTRIM(@Notes)), N''), @CreatedBy);

    SELECT * FROM dbo.Invoices WHERE InvoiceId = CONVERT(INT, SCOPE_IDENTITY());
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Invoices_GetByQuotation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Invoices_GetByQuotation] @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM dbo.Invoices WHERE QuotationId = @QuotationId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Invoices_GetByTracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Invoices_GetByTracker] @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM dbo.Invoices WHERE TrackerId = @TrackerId ORDER BY InvoiceId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Invoices_GetSendData]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Invoices_GetSendData] @InvoiceId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT i.*,
           COALESCE(NULLIF(LTRIM(RTRIM(c.CustomerName)), N''), NULLIF(LTRIM(RTRIM(t.CustomerName)), N''), i.CustomerCode) AS CustomerName,
           COALESCE(NULLIF(LTRIM(RTRIM(c.CustomerEmail)), N''), NULLIF(LTRIM(RTRIM(t.Email)), N''), N'') AS CustomerEmail,
           COALESCE(NULLIF(LTRIM(RTRIM(t.Mobile)), N''), NULLIF(LTRIM(RTRIM(c.CustomerPhone)), N''), N'') AS CustomerMobile,
           COALESCE(NULLIF(LTRIM(RTRIM(q.CustomerAddress)), N''), NULLIF(LTRIM(RTRIM(c.Address)), N''), N'') AS CustomerAddress,
           -- STUFF / FOR XML PATH instead of STRING_AGG: the server is SQL Server 2016 (STRING_AGG needs 2017+)
           ISNULL(STUFF((SELECT N', ' + CAST(f.[Name] AS NVARCHAR(MAX))
                         FROM dbo.TrackerFamilyMembers f
                         WHERE f.TrackerId = i.TrackerId AND f.[Name] IS NOT NULL
                         ORDER BY f.TrackerFamilyMemberId
                         FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N''), N'') AS FamilyName
    FROM dbo.Invoices i
    INNER JOIN dbo.Quotations q ON q.QuotationId = i.QuotationId
    LEFT JOIN dbo.Customers c ON c.CustomerCode = i.CustomerCode
    LEFT JOIN dbo.Trackers t ON t.TrackerId = i.TrackerId
    WHERE i.InvoiceId = @InvoiceId;

    SELECT qi.ServiceName,
           COALESCE(s.CategoryName, spec.CategoryName, N'')       AS CategoryName,
           COALESCE(s.SubCategoryName, spec.SubCategoryName, N'') AS SubCategoryName,
           qi.Quantity, qi.Amount, qi.VAT,
           (ISNULL(qi.Amount,0)+ISNULL(qi.VAT,0)) * ISNULL(qi.Quantity,1) AS LineTotal
    FROM dbo.QuotationItems qi
    INNER JOIN dbo.Invoices i ON i.QuotationId = qi.QuotationId
    LEFT JOIN dbo.TrackerServiceSpecs s ON s.TrackerServiceSpecId = qi.TrackerServiceSpecId
    LEFT JOIN dbo.TrackerCategorySubCategorySpecs spec ON spec.TrackerCategorySubCategorySpecId = qi.TrackerCategorySubCategorySpecId
    WHERE i.InvoiceId = @InvoiceId
    ORDER BY COALESCE(s.CategoryName, spec.CategoryName, N''), COALESCE(s.SubCategoryName, spec.SubCategoryName, N''), qi.QuotationItemId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Invoices_MarkSent]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Invoices_MarkSent]
    @InvoiceId INT,
    @Via NVARCHAR(20) = NULL,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @TrackerId INT, @Number NVARCHAR(30);
    SELECT @TrackerId = TrackerId, @Number = InvoiceNumber FROM dbo.Invoices WHERE InvoiceId = @InvoiceId;
    IF @TrackerId IS NULL THROW 50055, 'Invoice was not found.', 1;

    UPDATE dbo.Invoices
       SET InvoiceStatus = N'Sent', SentDate = SYSUTCDATETIME(), SentVia = @Via, SentCount = SentCount + 1,
           ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
     WHERE InvoiceId = @InvoiceId;

    -- Tracker moves to Invoice Sent, but never backwards from In Progress or later.
    IF EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId AND Status IN (N'QUOTATION', N'QUOTATION_SENT', N'NEGOTIATE', N'NEW', N'PENDING'))
    BEGIN
        DECLARE @Note NVARCHAR(500) = CONCAT(@Number, N' sent to customer', CASE WHEN @Via IS NULL THEN N'' ELSE CONCAT(N' by ', @Via) END);
        EXEC dbo.usp_Tracker_ApplyStatus @TrackerId = @TrackerId, @StatusCode = N'INVOICE_SENT', @ModifiedBy = @ModifiedBy, @Note = @Note;
    END;
    SELECT * FROM dbo.Invoices WHERE InvoiceId = @InvoiceId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_MenuAccessTree_Get]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   1. ROLE ACCESS TREE
   No Modules dependency.
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_MenuAccessTree_Get]
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;

    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51001, 'Invalid application code.', 1;

    SELECT
        mi.MenuItemId,
        mi.MenuKey,
        mi.DisplayName,
        mi.Icon,
        mi.Route,
        mi.ParentMenuItemId,
        mi.DisplayOrder,
        mi.MenuGroup,
        mi.ApplicationCode,
        mi.IsSystemMenu
    FROM dbo.MenuItems AS mi
    WHERE mi.IsActive = 1
      AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH')
    ORDER BY
        mi.MenuGroup,
        mi.DisplayOrder,
        mi.DisplayName,
        mi.MenuItemId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_MenuItems_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE MenuItemId = @Id)
        RETURN;
    IF EXISTS (SELECT 1 FROM dbo.MenuItems WHERE ParentMenuItemId = @Id AND IsActive = 1)
        THROW 51035, 'Cannot delete menu item because child menu items refer to it.', 1;
    IF EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE MenuKey = (SELECT MenuKey FROM dbo.MenuItems WHERE MenuItemId = @Id))
        THROW 51036, 'Cannot delete menu item because role permissions refer to it.', 1;
    IF EXISTS (SELECT 1 FROM dbo.MenuItems WHERE MenuItemId = @Id AND IsSystemMenu = 1)
        THROW 51037, 'System menu items cannot be deleted.', 1;

    UPDATE dbo.MenuItems SET IsActive = 0, ModifiedDate = SYSUTCDATETIME() WHERE MenuItemId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_MenuItems_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT MenuItemId, MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup,
           Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId,
           CreatedDate, ModifiedDate
    FROM dbo.MenuItems
    WHERE MenuItemId = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_GetForNavigation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* --------------------------------------------------------------------------
   4. Stored procedure: role-aware tracker navigation
   -------------------------------------------------------------------------- */
CREATE   PROCEDURE [dbo].[usp_MenuItems_GetForNavigation]
    @RoleName NVARCHAR(100),
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        MenuItemId,
        MenuKey,
        DisplayName,
        Icon,
        Route,
        ParentMenuItemId,
        DisplayOrder,
        MenuGroup,
        ApplicationCode,
        IsSystemMenu
    FROM dbo.MenuItems
    WHERE IsActive = 1
      AND (ApplicationCode = @ApplicationCode OR ApplicationCode IS NULL)
      AND (
            RoleRestriction IS NULL
            OR LTRIM(RTRIM(RoleRestriction)) = N''
            OR LOWER(RoleRestriction) = N'all'
            OR LOWER(RoleRestriction) = LOWER(@RoleName)
            OR (LOWER(@RoleName) = N'admin' AND LOWER(RoleRestriction) IN (N'admin only', N'admin'))
          )
    ORDER BY DisplayOrder, MenuItemId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_MenuItems_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    SELECT COUNT(1) AS TotalCount FROM dbo.MenuItems;
    SELECT MenuItemId, MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup,
           Route, DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId,
           CreatedDate, ModifiedDate
    FROM dbo.MenuItems
    ORDER BY MenuGroup, DisplayOrder, DisplayName, MenuItemId
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_MenuItems_Insert]
    @MenuKey NVARCHAR(50),
    @DisplayName NVARCHAR(100),
    @Icon NVARCHAR(100),
    @RoleRestriction NVARCHAR(50),
    @IsActive BIT,
    @MenuGroup NVARCHAR(30),
    @Route NVARCHAR(200),
    @DisplayOrder INT,
    @ApplicationCode NVARCHAR(30),
    @IsSystemMenu BIT,
    @ParentMenuItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER', N'BOTH')
        THROW 51030, 'Invalid application code.', 1;
    IF @ParentMenuItemId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE MenuItemId = @ParentMenuItemId)
        THROW 51031, 'Parent menu does not exist.', 1;
    IF @RoleRestriction IS NULL SET @RoleRestriction = N'All';

    INSERT INTO dbo.MenuItems(MenuKey, DisplayName, Icon, RoleRestriction, IsActive, MenuGroup, Route,
                              DisplayOrder, ApplicationCode, IsSystemMenu, ParentMenuItemId, CreatedDate)
    VALUES(@MenuKey, @DisplayName, @Icon, @RoleRestriction, @IsActive, @MenuGroup, @Route,
           @DisplayOrder, @ApplicationCode, @IsSystemMenu, @ParentMenuItemId, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    EXEC dbo.usp_MenuItems_GetById @NewId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_MenuItems_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_MenuItems_Update]
    @Id INT,
    @MenuKey NVARCHAR(50),
    @DisplayName NVARCHAR(100),
    @Icon NVARCHAR(100),
    @RoleRestriction NVARCHAR(50),
    @IsActive BIT,
    @MenuGroup NVARCHAR(30),
    @Route NVARCHAR(200),
    @DisplayOrder INT,
    @ApplicationCode NVARCHAR(30),
    @IsSystemMenu BIT,
    @ParentMenuItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER', N'BOTH')
        THROW 51032, 'Invalid application code.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE MenuItemId = @Id)
        RETURN;
    IF @ParentMenuItemId = @Id
        THROW 51033, 'A menu item cannot be its own parent.', 1;
    IF @ParentMenuItemId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.MenuItems WHERE MenuItemId = @ParentMenuItemId)
        THROW 51034, 'Parent menu does not exist.', 1;

    UPDATE dbo.MenuItems
    SET MenuKey = @MenuKey,
        DisplayName = @DisplayName,
        Icon = @Icon,
        RoleRestriction = ISNULL(@RoleRestriction, N'All'),
        IsActive = @IsActive,
        MenuGroup = @MenuGroup,
        Route = @Route,
        DisplayOrder = @DisplayOrder,
        ApplicationCode = @ApplicationCode,
        IsSystemMenu = @IsSystemMenu,
        ParentMenuItemId = @ParentMenuItemId,
        ModifiedDate = SYSUTCDATETIME()
    WHERE MenuItemId = @Id;

    EXEC dbo.usp_MenuItems_GetById @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_Navigation_GetForUser]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   6. NAVIGATION
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_Navigation_GetForUser]
    @UserId INT,
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;

    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51007, 'Invalid application code.', 1;

    SELECT
        mi.MenuItemId,
        mi.MenuKey,
        mi.DisplayName,
        mi.Icon,
        mi.Route,
        mi.ParentMenuItemId,
        mi.DisplayOrder,
        mi.MenuGroup,
        mi.ApplicationCode,
        mi.IsSystemMenu
    FROM dbo.MenuItems AS mi
    LEFT JOIN dbo.Users AS u
        ON u.UserId = @UserId
    LEFT JOIN dbo.Roles AS r
        ON r.RoleId = u.RoleId
    LEFT JOIN dbo.RolePermissions AS rp
        ON rp.RoleId = r.RoleId
       AND rp.MenuKey = mi.MenuKey
    WHERE mi.IsActive = 1
      AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH')
      AND (r.RoleName = N'Admin' OR ISNULL(rp.CanView, 0) = 1)
    ORDER BY
        mi.MenuGroup,
        mi.DisplayOrder,
        mi.DisplayName,
        mi.MenuItemId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_NotificationRules_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_NotificationRules_Delete] @RuleId INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.NotificationRules WHERE RuleId = @RuleId)
        THROW 50210, 'The rule was not found. It may have been deleted.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Notifications WHERE RuleId = @RuleId)
    BEGIN
        -- reminders were already created from this rule: keep the history, just stop the rule
        UPDATE dbo.NotificationRules SET IsActive = 0, ModifiedDate = SYSUTCDATETIME() WHERE RuleId = @RuleId;
        SELECT N'deactivated' AS result;
    END
    ELSE
    BEGIN
        DELETE FROM dbo.NotificationRules WHERE RuleId = @RuleId;
        SELECT N'deleted' AS result;
    END;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_NotificationRules_GetAll]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 4. Rules: list / save / delete ---------- */
CREATE   PROCEDURE [dbo].[usp_NotificationRules_GetAll]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (
        SELECT r.RuleId AS id,
               r.CategoryId AS categoryId, c.CategoryName AS categoryName,
               r.SubCategoryId AS subCategoryId, sc.SubCategoryName AS subCategoryName,
               r.DaysBefore AS daysBefore,
               r.NotifyEmail AS notifyEmail, r.NotifyWhatsApp AS notifyWhatsApp, r.NotifyInApp AS notifyInApp,
               r.ToCustomer AS toCustomer, r.ToEmployee AS toEmployee,
               r.[Subject] AS [subject], r.Body AS body, r.IsActive AS isActive,
               (SELECT COUNT(1)
                FROM dbo.TrackerCategorySubCategorySpecs sp
                INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
                WHERE sp.CategoryId = r.CategoryId
                  AND (r.SubCategoryId IS NULL OR sp.SubCategoryId = r.SubCategoryId)
                  AND sp.DueDate IS NOT NULL
                  AND UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'COMPLETED', N'REJECTED')) AS appliesTo,
               (SELECT COUNT(1) FROM dbo.Notifications n WHERE n.RuleId = r.RuleId) AS created,
               JSON_QUERY(ISNULL((SELECT fr.RecipientId AS id, fr.RecipientName AS name, fr.Email AS email, fr.Mobile AS mobile
                                  FROM dbo.NotificationRuleRecipients fr
                                  WHERE fr.RuleId = r.RuleId
                                  ORDER BY fr.RecipientId
                                  FOR JSON PATH), N'[]')) AS recipients
        FROM dbo.NotificationRules r
        INNER JOIN dbo.ServiceCategories c ON c.CategoryId = r.CategoryId
        LEFT JOIN dbo.ServiceSubCategories sc ON sc.SubCategoryId = r.SubCategoryId
        ORDER BY c.CategoryName, ISNULL(sc.SubCategoryName, N''), r.DaysBefore DESC
        FOR JSON PATH
    ) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_NotificationRules_Save]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_NotificationRules_Save]
    @RuleId         INT = NULL,
    @CategoryId     INT,
    @SubCategoryId  INT = NULL,
    @DaysBefore     INT,
    @NotifyEmail    BIT,
    @NotifyWhatsApp BIT,
    @NotifyInApp    BIT,
    @ToCustomer     BIT,
    @ToEmployee     BIT,
    @Subject        NVARCHAR(200) = NULL,
    @Body           NVARCHAR(2000) = NULL,
    @IsActive       BIT = 1,
    @RecipientsJson NVARCHAR(MAX) = NULL,
    @UserId         INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RuleId = 0 SET @RuleId = NULL;
    IF @SubCategoryId = 0 SET @SubCategoryId = NULL;
    SET @Subject = NULLIF(LTRIM(RTRIM(@Subject)), N'');
    SET @Body = NULLIF(LTRIM(RTRIM(@Body)), N'');

    IF NOT EXISTS (SELECT 1 FROM dbo.ServiceCategories WHERE CategoryId = @CategoryId)
        THROW 50201, 'Choose a category.', 1;
    IF @SubCategoryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.ServiceSubCategories WHERE SubCategoryId = @SubCategoryId AND CategoryId = @CategoryId)
        THROW 50202, 'That sub category does not belong to the selected category.', 1;
    IF @DaysBefore IS NULL OR @DaysBefore < -365 OR @DaysBefore > 365
        THROW 50203, 'Days before due date must be between -365 and 365.', 1;
    IF ISNULL(@NotifyEmail, 0) = 0 AND ISNULL(@NotifyWhatsApp, 0) = 0 AND ISNULL(@NotifyInApp, 0) = 0
        THROW 50204, 'Choose at least one channel: Email, WhatsApp or In-app.', 1;
    IF ISNULL(@NotifyInApp, 0) = 1 AND ISNULL(@ToEmployee, 0) = 0
        THROW 50205, 'In-app reminders go to the assigned employee. Tick "Assigned employee" or untick In-app.', 1;

    DECLARE @Rec TABLE (RecipientName NVARCHAR(150) NOT NULL, Email NVARCHAR(150) NULL, Mobile NVARCHAR(30) NULL);
    IF NULLIF(@RecipientsJson, N'') IS NOT NULL
    BEGIN
        IF ISJSON(@RecipientsJson) <> 1 THROW 50206, 'The fixed recipient list is not valid.', 1;
        INSERT @Rec (RecipientName, Email, Mobile)
        SELECT LEFT(LTRIM(RTRIM(j.[name])), 150), NULLIF(LEFT(LTRIM(RTRIM(j.email)), 150), N''), NULLIF(LEFT(LTRIM(RTRIM(j.mobile)), 30), N'')
        FROM OPENJSON(@RecipientsJson) WITH ([name] NVARCHAR(150) '$.name', email NVARCHAR(150) '$.email', mobile NVARCHAR(30) '$.mobile') j
        WHERE NULLIF(LTRIM(RTRIM(j.email)), N'') IS NOT NULL OR NULLIF(LTRIM(RTRIM(j.mobile)), N'') IS NOT NULL;
        IF EXISTS (SELECT 1 FROM @Rec WHERE Email IS NOT NULL AND (Email NOT LIKE N'%_@_%._%' OR Email LIKE N'% %'))
            THROW 50207, 'A fixed recipient has an invalid email address.', 1;
        UPDATE @Rec SET RecipientName = ISNULL(NULLIF(RecipientName, N''), ISNULL(Email, Mobile));
    END;

    IF ISNULL(@ToCustomer, 0) = 0 AND ISNULL(@ToEmployee, 0) = 0 AND NOT EXISTS (SELECT 1 FROM @Rec)
        THROW 50208, 'Choose who is notified: the customer, the assigned employee or a fixed recipient.', 1;

    IF EXISTS (SELECT 1 FROM dbo.NotificationRules
               WHERE CategoryId = @CategoryId AND ISNULL(SubCategoryId, 0) = ISNULL(@SubCategoryId, 0)
                 AND DaysBefore = @DaysBefore AND (@RuleId IS NULL OR RuleId <> @RuleId))
        THROW 50209, 'A rule for this category / sub category and number of days already exists. Edit that rule instead.', 1;
    IF @RuleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.NotificationRules WHERE RuleId = @RuleId)
        THROW 50210, 'The rule was not found. It may have been deleted.', 1;

    BEGIN TRAN;
    IF @RuleId IS NULL
    BEGIN
        INSERT dbo.NotificationRules (CategoryId, SubCategoryId, DaysBefore, NotifyEmail, NotifyWhatsApp, NotifyInApp, ToCustomer, ToEmployee, [Subject], Body, IsActive, CreatedBy)
        VALUES (@CategoryId, @SubCategoryId, @DaysBefore, ISNULL(@NotifyEmail, 0), ISNULL(@NotifyWhatsApp, 0), ISNULL(@NotifyInApp, 0),
                ISNULL(@ToCustomer, 0), ISNULL(@ToEmployee, 0), @Subject, @Body, ISNULL(@IsActive, 1), @UserId);
        SET @RuleId = CONVERT(INT, SCOPE_IDENTITY());
    END
    ELSE
    BEGIN
        UPDATE dbo.NotificationRules
        SET CategoryId = @CategoryId, SubCategoryId = @SubCategoryId, DaysBefore = @DaysBefore,
            NotifyEmail = ISNULL(@NotifyEmail, 0), NotifyWhatsApp = ISNULL(@NotifyWhatsApp, 0), NotifyInApp = ISNULL(@NotifyInApp, 0),
            ToCustomer = ISNULL(@ToCustomer, 0), ToEmployee = ISNULL(@ToEmployee, 0),
            [Subject] = @Subject, Body = @Body, IsActive = ISNULL(@IsActive, 1),
            ModifiedBy = @UserId, ModifiedDate = SYSUTCDATETIME()
        WHERE RuleId = @RuleId;
        DELETE FROM dbo.NotificationRuleRecipients WHERE RuleId = @RuleId;
    END;
    INSERT dbo.NotificationRuleRecipients (RuleId, RecipientName, Email, Mobile)
    SELECT @RuleId, RecipientName, Email, Mobile FROM @Rec;
    COMMIT;

    SELECT @RuleId AS RuleId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Notifications_GetEmailQueue]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Notifications_GetEmailQueue]
    @Ids           NVARCHAR(MAX) = NULL,    -- NULL = every pending email
    @IncludeFailed BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SELECT n.NotificationId, n.RecipientName, n.RecipientAddress, n.[Subject], n.Body, n.CustomerName
    FROM dbo.Notifications n
    WHERE n.Channel = N'EMAIL'
      AND (n.[Status] = N'PENDING' OR (@IncludeFailed = 1 AND n.[Status] = N'FAILED'))
      AND (NULLIF(@Ids, N'') IS NULL OR n.NotificationId IN
            (SELECT TRY_CONVERT(BIGINT, LTRIM(RTRIM(value))) FROM STRING_SPLIT(@Ids, N',') WHERE TRY_CONVERT(BIGINT, LTRIM(RTRIM(value))) IS NOT NULL))
    ORDER BY n.NotificationId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Notifications_GetLog]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 6. Log ---------- */
CREATE   PROCEDURE [dbo].[usp_Notifications_GetLog]
    @Status     NVARCHAR(12) = NULL,
    @Channel    NVARCHAR(10) = NULL,
    @CategoryId INT = NULL,
    @CustomerId INT = NULL,
    @FromDate   DATE = NULL,
    @ToDate     DATE = NULL,
    @Search     NVARCHAR(100) = NULL,
    @Page       INT = 1,
    @PageSize   INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @Status = NULLIF(UPPER(LTRIM(RTRIM(@Status))), N'');
    SET @Channel = NULLIF(UPPER(LTRIM(RTRIM(@Channel))), N'');
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');
    IF @Page IS NULL OR @Page < 1 SET @Page = 1;
    IF @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 200 SET @PageSize = 25;
    DECLARE @Today DATE = CONVERT(DATE, SYSDATETIME());

    SELECT n.NotificationId, n.RuleId, n.TrackerId, n.CustomerName, n.CategoryName, n.SubCategoryName, n.PersonName, n.DueDate, n.DaysBefore,
           n.Channel, n.RecipientType, n.RecipientName, n.RecipientAddress, n.[Subject], n.Body, n.[Status], n.StatusMessage,
           n.CreatedDate, n.SentDate, n.ReadDate
    INTO #nl_f
    FROM dbo.Notifications n
    WHERE (@Channel IS NULL OR n.Channel = @Channel)
      AND (@CategoryId IS NULL OR n.CategoryId = @CategoryId)
      AND (@CustomerId IS NULL OR n.CustomerId = @CustomerId)
      AND (@FromDate IS NULL OR n.DueDate >= @FromDate)
      AND (@ToDate IS NULL OR n.DueDate <= @ToDate)
      AND (@Search IS NULL OR n.CustomerName LIKE N'%' + @Search + N'%' OR n.RecipientName LIKE N'%' + @Search + N'%'
           OR n.RecipientAddress LIKE N'%' + @Search + N'%' OR n.CategoryName LIKE N'%' + @Search + N'%'
           OR n.SubCategoryName LIKE N'%' + @Search + N'%' OR n.[Subject] LIKE N'%' + @Search + N'%');

    DECLARE @Total INT = (SELECT COUNT(1) FROM #nl_f WHERE @Status IS NULL OR [Status] = @Status);

    DECLARE @Rows NVARCHAR(MAX) = (
        SELECT f.NotificationId AS id, f.RuleId AS ruleId, f.TrackerId AS trackerId, f.CustomerName AS customer,
               f.CategoryName AS category, f.SubCategoryName AS subCategory, f.PersonName AS person,
               f.DueDate AS dueDate, DATEDIFF(DAY, @Today, f.DueDate) AS daysLeft, f.DaysBefore AS daysBefore,
               f.Channel AS channel, f.RecipientType AS recipientType, f.RecipientName AS recipient, f.RecipientAddress AS address,
               f.[Subject] AS [subject], f.Body AS body, f.[Status] AS [status], f.StatusMessage AS [message],
               f.CreatedDate AS createdDate, f.SentDate AS sentDate, f.ReadDate AS readDate
        FROM #nl_f f
        WHERE @Status IS NULL OR f.[Status] = @Status
        ORDER BY f.CreatedDate DESC, f.NotificationId DESC
        OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
        FOR JSON PATH);

    SELECT (
        SELECT @Total AS total, @Page AS [page], @PageSize AS pageSize,
               (SELECT COUNT(1) AS [all],
                       ISNULL(SUM(CASE WHEN [Status] = N'PENDING' THEN 1 ELSE 0 END), 0) AS pending,
                       ISNULL(SUM(CASE WHEN [Status] = N'SENT' THEN 1 ELSE 0 END), 0) AS sent,
                       ISNULL(SUM(CASE WHEN [Status] = N'FAILED' THEN 1 ELSE 0 END), 0) AS failed,
                       ISNULL(SUM(CASE WHEN [Status] = N'SKIPPED' THEN 1 ELSE 0 END), 0) AS skipped,
                       ISNULL(SUM(CASE WHEN [Status] = N'DISMISSED' THEN 1 ELSE 0 END), 0) AS dismissed
                FROM #nl_f FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS counts,
               JSON_QUERY(ISNULL(@Rows, N'[]')) AS [rows]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Notifications_Lookups]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 3. Lookups: categories with their sub categories ---------- */
CREATE   PROCEDURE [dbo].[usp_Notifications_Lookups]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (
        SELECT (
            SELECT c.CategoryId AS id, c.CategoryName AS name,
                   JSON_QUERY(ISNULL((SELECT s.SubCategoryId AS id, s.SubCategoryName AS name
                                      FROM dbo.ServiceSubCategories s
                                      WHERE s.CategoryId = c.CategoryId AND s.IsActive = 1
                                      ORDER BY s.SubCategoryName
                                      FOR JSON PATH), N'[]')) AS subCategories
            FROM dbo.ServiceCategories c
            WHERE c.IsActive = 1
            ORDER BY c.CategoryName
            FOR JSON PATH
        ) AS categories
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Notifications_Run]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 5. Preview / generate ---------- */
CREATE   PROCEDURE [dbo].[usp_Notifications_Run]
    @Mode        NVARCHAR(10) = N'PREVIEW',     -- PREVIEW (nothing saved) or GENERATE
    @CatchUpDays INT = 7,
    @UserId      INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Mode = UPPER(LTRIM(RTRIM(ISNULL(@Mode, N'PREVIEW'))));
    IF @Mode NOT IN (N'PREVIEW', N'GENERATE') THROW 50220, 'Mode must be PREVIEW or GENERATE.', 1;
    IF @CatchUpDays IS NULL OR @CatchUpDays < 0 SET @CatchUpDays = 7;
    IF @CatchUpDays > 60 SET @CatchUpDays = 60;
    DECLARE @Today DATE = CONVERT(DATE, SYSDATETIME());

    /* open tracker lines that have a due date */
    SELECT sp.TrackerCategorySubCategorySpecId AS SpecId, sp.TrackerId, sp.CategoryId, sp.CategoryName, sp.SubCategoryId, sp.SubCategoryName,
           sp.PersonName, sp.DueDate, DATEDIFF(DAY, @Today, sp.DueDate) AS DaysLeft,
           t.CustomerId, t.CustomerName,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Email)), N''), NULLIF(LTRIM(RTRIM(c.CustomerEmail)), N'')) AS CustEmail,
           ISNULL(NULLIF(LTRIM(RTRIM(t.Mobile)), N''), NULLIF(LTRIM(RTRIM(c.CustomerPhone)), N'')) AS CustMobile,
           t.AssignedToEmployeeId AS EmployeeId,
           ISNULL(NULLIF(LTRIM(RTRIM(e.FullName)), N''), NULLIF(LTRIM(RTRIM(t.AssignedToEmployeeName)), N'')) AS EmployeeName,
           NULLIF(LTRIM(RTRIM(e.Email)), N'') AS EmpEmail,
           NULLIF(LTRIM(RTRIM(e.Mobile)), N'') AS EmpMobile
    INTO #np_spec
    FROM dbo.TrackerCategorySubCategorySpecs sp
    INNER JOIN dbo.Trackers t ON t.TrackerId = sp.TrackerId
    LEFT JOIN dbo.Customers c ON c.CustomerId = t.CustomerId
    LEFT JOIN dbo.Employees e ON e.EmployeeId = t.AssignedToEmployeeId
    WHERE sp.DueDate IS NOT NULL
      AND UPPER(LTRIM(RTRIM(t.[Status]))) NOT IN (N'COMPLETED', N'REJECTED')
      AND sp.DueDate <= DATEADD(DAY, 366, @Today);

    /* the current step of each line: smallest "days before" that is still >= days left, and not too late */
    ;WITH r AS (
        SELECT s.SpecId, ru.RuleId, ru.DaysBefore, ru.NotifyEmail, ru.NotifyWhatsApp, ru.NotifyInApp, ru.ToCustomer, ru.ToEmployee,
               ru.[Subject] AS TSubject, ru.Body AS TBody, s.DaysLeft,
               ROW_NUMBER() OVER (PARTITION BY s.SpecId, ru.DaysBefore
                                  ORDER BY CASE WHEN ru.SubCategoryId IS NULL THEN 1 ELSE 0 END, ru.RuleId) AS rn1
        FROM #np_spec s
        INNER JOIN dbo.NotificationRules ru
                ON ru.IsActive = 1 AND ru.CategoryId = s.CategoryId
               AND (ru.SubCategoryId IS NULL OR ru.SubCategoryId = s.SubCategoryId)
        WHERE ru.DaysBefore >= s.DaysLeft
    ),
    r2 AS (
        SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.SpecId ORDER BY r.DaysBefore) AS rn2
        FROM r
        WHERE r.rn1 = 1
    )
    SELECT r2.SpecId, r2.RuleId, r2.DaysBefore, r2.NotifyEmail, r2.NotifyWhatsApp, r2.NotifyInApp, r2.ToCustomer, r2.ToEmployee,
           ISNULL(r2.TSubject, N'{{Category}} - {{SubCategory}} {{DaysLeft}}') AS TSubject,
           ISNULL(r2.TBody, N'Dear {{Recipient}}, this is a reminder that the {{SubCategory}} ({{Category}}) for {{Person}} is due on {{DueDate}} ({{DaysLeft}}). Please contact {{Employee}} to arrange the renewal.') AS TBody
    INTO #np_step
    FROM r2
    WHERE r2.rn2 = 1 AND r2.DaysBefore - r2.DaysLeft <= @CatchUpDays;

    /* one row per channel x recipient */
    CREATE TABLE #np_cand
    (
        RuleId INT NOT NULL, SpecId INT NOT NULL, TrackerId INT NOT NULL, CustomerId INT NULL, CustomerName NVARCHAR(150) NULL,
        CategoryId INT NULL, CategoryName NVARCHAR(100) NULL, SubCategoryName NVARCHAR(100) NULL, PersonName NVARCHAR(200) NULL,
        DueDate DATE NOT NULL, DaysBefore INT NOT NULL, DaysLeft INT NOT NULL,
        Channel NVARCHAR(10) NOT NULL, RecipientType NVARCHAR(10) NOT NULL, RecipientName NVARCHAR(150) NULL,
        RecipientAddress NVARCHAR(150) NOT NULL, RecipientEmployeeId INT NULL,
        TSubject NVARCHAR(200) NULL, TBody NVARCHAR(2000) NULL,
        [Subject] NVARCHAR(200) NULL, Body NVARCHAR(2000) NULL,
        [Status] NVARCHAR(12) NOT NULL, StatusMessage NVARCHAR(400) NULL
    );

    /* customer: email + whatsapp */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'CUSTOMER', sp.CustomerName, ISNULL(ch.Addr, N''), NULL, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN CASE ch.Channel WHEN N'EMAIL' THEN N'The customer has no email address.' ELSE N'The customer has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    CROSS APPLY (VALUES (N'EMAIL', sp.CustEmail, st.NotifyEmail), (N'WHATSAPP', sp.CustMobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToCustomer = 1 AND ch.Enabled = 1;

    /* assigned employee: email + whatsapp + in-app */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'EMPLOYEE', sp.EmployeeName,
           ISNULL(ch.Addr, N''), sp.EmployeeId, st.TSubject, st.TBody,
           CASE WHEN ch.Addr IS NULL THEN N'SKIPPED' WHEN ch.Channel = N'INAPP' THEN N'SENT' ELSE N'PENDING' END,
           CASE WHEN ch.Addr IS NULL THEN
                CASE WHEN sp.EmployeeId IS NULL THEN N'No employee is assigned to this tracker.'
                     WHEN ch.Channel = N'EMAIL' THEN N'The employee has no email address.'
                     ELSE N'The employee has no mobile number.' END END
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    CROSS APPLY (VALUES (N'EMAIL',    sp.EmpEmail, st.NotifyEmail),
                        (N'WHATSAPP', sp.EmpMobile, st.NotifyWhatsApp),
                        (N'INAPP',    CASE WHEN sp.EmployeeId IS NULL THEN NULL ELSE CONVERT(NVARCHAR(150), sp.EmployeeId) END, st.NotifyInApp)) ch (Channel, Addr, Enabled)
    WHERE st.ToEmployee = 1 AND ch.Enabled = 1;

    /* the fixed list of the rule: email + whatsapp (only the details that were entered) */
    INSERT #np_cand (RuleId, SpecId, TrackerId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore, DaysLeft,
                     Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, TSubject, TBody, [Status], StatusMessage)
    SELECT st.RuleId, sp.SpecId, sp.TrackerId, sp.CustomerId, sp.CustomerName, sp.CategoryId, sp.CategoryName, sp.SubCategoryName, sp.PersonName, sp.DueDate, st.DaysBefore, sp.DaysLeft,
           ch.Channel, N'FIXED', fr.RecipientName, ch.Addr, NULL, st.TSubject, st.TBody, N'PENDING', NULL
    FROM #np_step st
    INNER JOIN #np_spec sp ON sp.SpecId = st.SpecId
    INNER JOIN dbo.NotificationRuleRecipients fr ON fr.RuleId = st.RuleId
    CROSS APPLY (VALUES (N'EMAIL', fr.Email, st.NotifyEmail), (N'WHATSAPP', fr.Mobile, st.NotifyWhatsApp)) ch (Channel, Addr, Enabled)
    WHERE ch.Enabled = 1 AND ch.Addr IS NOT NULL;

    /* same address twice in one run (e.g. two fixed entries) -> keep one */
    ;WITH d AS (
        SELECT ROW_NUMBER() OVER (PARTITION BY RuleId, SpecId, Channel, RecipientType, RecipientAddress, DueDate ORDER BY (SELECT 1)) AS rn
        FROM #np_cand
    )
    DELETE FROM d WHERE rn > 1;

    /* already created before -> not due again */
    DELETE c
    FROM #np_cand c
    WHERE EXISTS (SELECT 1 FROM dbo.Notifications n
                  WHERE n.RuleId = c.RuleId AND n.SpecId = c.SpecId AND n.Channel = c.Channel
                    AND n.RecipientType = c.RecipientType AND n.RecipientAddress = c.RecipientAddress AND n.DueDate = c.DueDate);

    /* write the message for each recipient */
    UPDATE c
    SET [Subject] = dbo.fn_Notification_Render(c.TSubject, c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName),
        Body      = dbo.fn_Notification_Render(c.TBody,    c.CustomerName, c.CategoryName, c.SubCategoryName, c.PersonName, c.DueDate, c.DaysLeft, sp.EmployeeName, c.RecipientName)
    FROM #np_cand c
    INNER JOIN #np_spec sp ON sp.SpecId = c.SpecId;

    DECLARE @Pending INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'PENDING');
    DECLARE @Skipped INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'SKIPPED');
    DECLARE @InApp   INT = (SELECT COUNT(1) FROM #np_cand WHERE [Status] = N'SENT');
    DECLARE @Created INT = 0;

    IF @Mode = N'GENERATE'
    BEGIN
        INSERT dbo.Notifications (RuleId, TrackerId, SpecId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore,
                                  Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, [Subject], Body, [Status], StatusMessage, SentDate, ModifiedBy)
        SELECT RuleId, TrackerId, SpecId, CustomerId, CustomerName, CategoryId, CategoryName, SubCategoryName, PersonName, DueDate, DaysBefore,
               Channel, RecipientType, RecipientName, RecipientAddress, RecipientEmployeeId, [Subject], Body, [Status], StatusMessage,
               CASE WHEN [Status] = N'SENT' THEN SYSUTCDATETIME() END, @UserId
        FROM #np_cand;
        SET @Created = @@ROWCOUNT;
    END;

    DECLARE @Rows NVARCHAR(MAX) = (
        SELECT c.RuleId AS ruleId, c.SpecId AS specId, c.TrackerId AS trackerId, c.CustomerName AS customer,
               c.CategoryName AS category, c.SubCategoryName AS subCategory, c.PersonName AS person,
               c.DueDate AS dueDate, c.DaysLeft AS daysLeft, c.DaysBefore AS daysBefore,
               c.Channel AS channel, c.RecipientType AS recipientType, c.RecipientName AS recipient, c.RecipientAddress AS address,
               c.[Subject] AS [subject], c.Body AS body, c.[Status] AS [status], c.StatusMessage AS [message]
        FROM #np_cand c
        ORDER BY c.DueDate, c.CustomerName, c.SpecId, c.Channel, c.RecipientType
        FOR JSON PATH);

    SELECT (
        SELECT @Mode AS mode, @Today AS today, @CatchUpDays AS catchUpDays,
               @Pending AS pending, @Skipped AS skipped, @InApp AS inApp, @Created AS created,
               JSON_QUERY(ISNULL(CASE WHEN @Mode = N'PREVIEW' THEN @Rows END, N'[]')) AS [rows]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Notifications_SetStatus]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 7. Status changes and the email queue ---------- */
CREATE   PROCEDURE [dbo].[usp_Notifications_SetStatus]
    @Ids     NVARCHAR(MAX),                 -- comma separated NotificationIds
    @Status  NVARCHAR(12),                  -- SENT / PENDING / FAILED / DISMISSED / READ
    @Message NVARCHAR(400) = NULL,
    @UserId  INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Status = UPPER(LTRIM(RTRIM(ISNULL(@Status, N''))));
    IF @Status NOT IN (N'SENT', N'PENDING', N'FAILED', N'DISMISSED', N'READ')
        THROW 50230, 'Status must be SENT, PENDING, FAILED, DISMISSED or READ.', 1;

    DECLARE @Pick TABLE (NotificationId BIGINT PRIMARY KEY);
    INSERT @Pick (NotificationId)
    SELECT DISTINCT TRY_CONVERT(BIGINT, LTRIM(RTRIM(value))) FROM STRING_SPLIT(ISNULL(@Ids, N''), N',')
    WHERE TRY_CONVERT(BIGINT, LTRIM(RTRIM(value))) IS NOT NULL;

    IF @Status = N'READ'
        UPDATE n SET ReadDate = ISNULL(n.ReadDate, SYSUTCDATETIME()), ModifiedBy = @UserId
        FROM dbo.Notifications n INNER JOIN @Pick p ON p.NotificationId = n.NotificationId;
    ELSE
        UPDATE n
        SET [Status] = @Status,
            StatusMessage = CASE WHEN @Status IN (N'SENT', N'PENDING') THEN NULLIF(@Message, N'') ELSE ISNULL(NULLIF(@Message, N''), n.StatusMessage) END,
            SentDate = CASE WHEN @Status = N'SENT' THEN SYSUTCDATETIME() WHEN @Status = N'PENDING' THEN NULL ELSE n.SentDate END,
            ModifiedBy = @UserId
        FROM dbo.Notifications n INNER JOIN @Pick p ON p.NotificationId = n.NotificationId;

    SELECT @@ROWCOUNT AS updated;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_PaymentMethods_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_PaymentMethods_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT PaymentMethodId, MethodName
    FROM dbo.PaymentMethods
    WHERE IsActive=1
    ORDER BY MethodName;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Payments_CreateFromQuotation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
/* ============================================================================
   Payments and Documents are available only after the invoice is generated
   ----------------------------------------------------------------------------
   - A payment can be recorded once the quotation has an invoice (generated; it no longer has to be
     sent first).  Setting Payments.RequireInvoiceSent still switches the rule on (1) or off (0).
   - A document can be added against a quotation only once that quotation has an invoice.
     Existing documents are not affected.
   Safe to run more than once.
   ============================================================================ */

CREATE   PROCEDURE [dbo].[usp_Payments_CreateFromQuotation]
    @QuotationId INT,
    @PaymentDate DATE,
    @PaymentAmount DECIMAL(18,2),
    @PaymentMethodId INT,
    @TransactionReference NVARCHAR(150) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @TrackerId INT, @CustomerId INT, @CustomerCode NVARCHAR(50), @QuotationStatus NVARCHAR(30);
    DECLARE @GrandTotal DECIMAL(18,2), @Paid DECIMAL(18,2), @Balance DECIMAL(18,2);

    SELECT @TrackerId=q.TrackerId, @CustomerCode=q.CustomerCode, @QuotationStatus=q.QuotationStatus,
           @CustomerId=t.CustomerId
    FROM dbo.Quotations q
    INNER JOIN dbo.Trackers t ON t.TrackerId=q.TrackerId
    WHERE q.QuotationId=@QuotationId;

    IF @TrackerId IS NULL THROW 50021, 'Quotation was not found.', 1;
    IF UPPER(LTRIM(RTRIM(ISNULL(@QuotationStatus,N'')))) <> N'ACCEPTED'
        THROW 50022, 'Only an Accepted quotation can be converted to payment.', 1;
    IF ISNULL((SELECT TOP (1) SettingValue FROM dbo.ApplicationSettings WHERE SettingKey = N'Payments.RequireInvoiceSent' AND IsActive = 1), N'1') = N'1'
       AND NOT EXISTS (SELECT 1 FROM dbo.Invoices WHERE QuotationId = @QuotationId)
        THROW 50028, 'The invoice must be generated before a payment can be recorded.', 1;
    IF @PaymentAmount IS NULL OR @PaymentAmount <= 0
        THROW 50023, 'Payment amount must be greater than zero.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.PaymentMethods WHERE PaymentMethodId=@PaymentMethodId AND IsActive=1)
        THROW 50024, 'Selected payment method is not active.', 1;

    SELECT @GrandTotal = ISNULL(SUM((ISNULL(Amount,0)+ISNULL(VAT,0)) * ISNULL(Quantity,1)),0)
    FROM dbo.QuotationItems WHERE QuotationId=@QuotationId;
    SELECT @Paid = ISNULL(SUM(PaymentAmount),0) FROM dbo.Payments WITH (UPDLOCK,HOLDLOCK) WHERE QuotationId=@QuotationId;
    SET @Balance = @GrandTotal - @Paid;

    IF @GrandTotal <= 0 THROW 50025, 'Quotation has no payable amount.', 1;
    IF @Balance <= 0 THROW 50026, 'This quotation is already fully paid.', 1;
    IF @PaymentAmount > @Balance THROW 50027, 'Payment amount cannot exceed the outstanding balance.', 1;

    DECLARE @NewId INT = NEXT VALUE FOR dbo.PaymentNumberSequence;
    DECLARE @PaymentNumber NVARCHAR(50) = CONCAT(N'BP-PAY-', @NewId);
    DECLARE @NewStatus NVARCHAR(20) = CASE WHEN @PaymentAmount = @Balance THEN N'Paid' ELSE N'Partial' END;

    INSERT dbo.Payments
    (PaymentNumber,TrackerId,QuotationId,CustomerId,CustomerCode,PaymentDate,AmountDue,PaymentAmount,PaymentMethodId,TransactionReference,PaymentStatus,Notes,CreatedBy,CreatedDate)
    VALUES
    (@PaymentNumber,@TrackerId,@QuotationId,@CustomerId,@CustomerCode,@PaymentDate,@Balance,@PaymentAmount,@PaymentMethodId,NULLIF(LTRIM(RTRIM(@TransactionReference)),N''),@NewStatus,@Notes,@CreatedBy,SYSUTCDATETIME());

    SELECT
        p.PaymentId, p.PaymentNumber, p.TrackerId, p.QuotationId, p.CustomerId, p.CustomerCode,
        p.PaymentDate, p.AmountDue, p.PaymentAmount, p.PaymentMethodId,
        pm.MethodName AS PaymentMethodName, p.TransactionReference, p.PaymentStatus, p.Notes,
        p.CreatedBy, p.CreatedDate, p.ModifiedBy, p.ModifiedDate,
        CONCAT(N'BP-QT-', p.QuotationId) AS QuotationNumber,
        t.CustomerName,
        @GrandTotal AS GrandTotal,
        (@Paid + @PaymentAmount) AS TotalPaid,
        CASE WHEN @GrandTotal - (@Paid + @PaymentAmount) < 0 THEN 0 ELSE @GrandTotal - (@Paid + @PaymentAmount) END AS BalanceAmount
    FROM dbo.Payments p
    INNER JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId = p.PaymentMethodId
    INNER JOIN dbo.Trackers t ON t.TrackerId = p.TrackerId
    WHERE p.PaymentId = CONVERT(INT, SCOPE_IDENTITY());

    COMMIT TRANSACTION;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Payments_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Payments_Delete]
    @PaymentId INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.Payments WITH (UPDLOCK,HOLDLOCK) WHERE PaymentId=@PaymentId)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT 0 AS AffectedCount;
        RETURN;
    END;

    DELETE FROM dbo.Payments WHERE PaymentId=@PaymentId;
    DECLARE @AffectedCount INT = @@ROWCOUNT;

    COMMIT TRANSACTION;
    SELECT @AffectedCount AS AffectedCount;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Payments_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Payments_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        p.PaymentId, p.PaymentNumber, p.TrackerId, p.QuotationId, p.CustomerId, p.CustomerCode,
        p.PaymentDate, p.AmountDue, p.PaymentAmount, p.PaymentMethodId,
        pm.MethodName AS PaymentMethodName, p.TransactionReference, p.PaymentStatus, p.Notes,
        p.CreatedBy, p.CreatedDate, p.ModifiedBy, p.ModifiedDate,
        CONCAT(N'BP-QT-', p.QuotationId) AS QuotationNumber,
        t.CustomerName
    FROM dbo.Payments p
    INNER JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId = p.PaymentMethodId
    INNER JOIN dbo.Trackers t ON t.TrackerId = p.TrackerId
    WHERE p.PaymentId = @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Payments_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Payments_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1) FROM dbo.Payments;
    SELECT @TotalCount AS TotalCount;

    SELECT
        p.PaymentId, p.PaymentNumber, p.TrackerId, p.QuotationId, p.CustomerId, p.CustomerCode,
        p.PaymentDate, p.AmountDue, p.PaymentAmount, p.PaymentMethodId,
        pm.MethodName AS PaymentMethodName, p.TransactionReference, p.PaymentStatus, p.Notes,
        p.CreatedBy, p.CreatedDate, p.ModifiedBy, p.ModifiedDate,
        CONCAT(N'BP-QT-', p.QuotationId) AS QuotationNumber,
        t.CustomerName
    FROM dbo.Payments p
    INNER JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId = p.PaymentMethodId
    INNER JOIN dbo.Trackers t ON t.TrackerId = p.TrackerId
    ORDER BY p.PaymentId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Payments_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_Payments_Update]
    @PaymentId INT,
    @PaymentDate DATE,
    @PaymentAmount DECIMAL(18,2),
    @PaymentMethodId INT,
    @TransactionReference NVARCHAR(150) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @QuotationId INT, @TrackerId INT, @QuotationStatus NVARCHAR(30);
    DECLARE @GrandTotal DECIMAL(18,2), @PaidOther DECIMAL(18,2), @Balance DECIMAL(18,2);

    SELECT
        @QuotationId = p.QuotationId,
        @TrackerId = p.TrackerId,
        @QuotationStatus = q.QuotationStatus
    FROM dbo.Payments p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Quotations q ON q.QuotationId = p.QuotationId
    WHERE p.PaymentId = @PaymentId;

    IF @QuotationId IS NULL THROW 50030, 'Payment was not found.', 1;
    IF UPPER(LTRIM(RTRIM(ISNULL(@QuotationStatus,N'')))) <> N'ACCEPTED'
        THROW 50031, 'Only an Accepted quotation can have its payment changed.', 1;
    IF @PaymentAmount IS NULL OR @PaymentAmount <= 0
        THROW 50032, 'Payment amount must be greater than zero.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.PaymentMethods WHERE PaymentMethodId=@PaymentMethodId AND IsActive=1)
        THROW 50033, 'Selected payment method is not active.', 1;

    SELECT @GrandTotal = ISNULL(SUM((ISNULL(Amount,0)+ISNULL(VAT,0)) * ISNULL(Quantity,1)),0)
    FROM dbo.QuotationItems
    WHERE QuotationId=@QuotationId;

    SELECT @PaidOther = ISNULL(SUM(PaymentAmount),0)
    FROM dbo.Payments WITH (UPDLOCK, HOLDLOCK)
    WHERE QuotationId=@QuotationId AND PaymentId<>@PaymentId;

    SET @Balance = @GrandTotal - @PaidOther;

    IF @GrandTotal <= 0 THROW 50034, 'Quotation has no payable amount.', 1;
    IF @Balance <= 0 THROW 50035, 'The other payments already fully cover this quotation.', 1;
    IF @PaymentAmount > @Balance THROW 50036, 'Payment amount cannot exceed the outstanding balance.', 1;

    UPDATE dbo.Payments
    SET PaymentDate=@PaymentDate,
        AmountDue=@Balance,
        PaymentAmount=@PaymentAmount,
        PaymentMethodId=@PaymentMethodId,
        TransactionReference=NULLIF(LTRIM(RTRIM(@TransactionReference)),N''),
        PaymentStatus=CASE WHEN @PaymentAmount=@Balance THEN N'Paid' ELSE N'Partial' END,
        Notes=@Notes,
        ModifiedBy=@ModifiedBy,
        ModifiedDate=SYSUTCDATETIME()
    WHERE PaymentId=@PaymentId;

    SELECT
        p.PaymentId, p.PaymentNumber, p.TrackerId, p.QuotationId, p.CustomerId, p.CustomerCode,
        p.PaymentDate, p.AmountDue, p.PaymentAmount, p.PaymentMethodId,
        pm.MethodName AS PaymentMethodName, p.TransactionReference, p.PaymentStatus, p.Notes,
        p.CreatedBy, p.CreatedDate, p.ModifiedBy, p.ModifiedDate,
        CONCAT(N'BP-QT-', p.QuotationId) AS QuotationNumber,
        t.CustomerName
    FROM dbo.Payments p
    INNER JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId=p.PaymentMethodId
    INNER JOIN dbo.Trackers t ON t.TrackerId=p.TrackerId
    WHERE p.PaymentId=@PaymentId;

    COMMIT TRANSACTION;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationCommunications_Create]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationCommunications_Create]
    @QuotationId INT,
    @CommunicationType NVARCHAR(30),
    @Recipient NVARCHAR(100),
    @Cc NVARCHAR(2000) = NULL,
    @Subject NVARCHAR(500) = NULL,
    @Message NVARCHAR(MAX) = NULL,
    @AttachmentName NVARCHAR(255) = NULL,
    @ProviderMessageId NVARCHAR(200) = NULL,
    @Status NVARCHAR(30) = N'Initiated',
    @CreatedBy INT = NULL,
    @FailureReason NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TrackerId INT;
    SELECT @TrackerId = TrackerId FROM dbo.Quotations WHERE QuotationId = @QuotationId;
    IF @TrackerId IS NULL THROW 50001, 'Quotation was not found.', 1;

    IF @CommunicationType NOT IN (N'Email',N'WhatsApp') THROW 50002, 'Invalid communication type.', 1;
    IF @Status NOT IN (N'Initiated',N'Opened',N'Sent',N'Delivered',N'Read',N'Failed') SET @Status=N'Initiated';

    INSERT INTO dbo.QuotationCommunications
    (
        QuotationId,TrackerId,CommunicationType,Recipient,Cc,Subject,Message,AttachmentName,
        ProviderMessageId,Status,CreatedDate,SentDate,FailedDate,FailureReason,CreatedBy
    )
    VALUES
    (
        @QuotationId,@TrackerId,@CommunicationType,@Recipient,@Cc,@Subject,@Message,@AttachmentName,
        @ProviderMessageId,@Status,SYSUTCDATETIME(),
        CASE WHEN @Status IN (N'Sent',N'Delivered',N'Read') THEN SYSUTCDATETIME() ELSE NULL END,
        CASE WHEN @Status=N'Failed' THEN SYSUTCDATETIME() ELSE NULL END,
        @FailureReason,@CreatedBy
    );

    SELECT CONVERT(INT,SCOPE_IDENTITY()) AS CommunicationId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationCommunications_Get]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationCommunications_Get]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        qc.CommunicationId,
        qc.QuotationId,
        qc.TrackerId,
        qc.CommunicationType,
        qc.Recipient,
        ISNULL(qc.Cc,N'') AS Cc,
        ISNULL(qc.Subject,N'') AS Subject,
        ISNULL(qc.Message,N'') AS Message,
        ISNULL(qc.AttachmentName,N'') AS AttachmentName,
        ISNULL(qc.ProviderMessageId,N'') AS ProviderMessageId,
        qc.Status,
        qc.CreatedDate,
        qc.SentDate,
        qc.DeliveredDate,
        qc.ReadDate,
        qc.FailedDate,
        qc.FailureReason,
        qc.CreatedBy,
        NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(u.FirstName,N''),N' ',ISNULL(u.LastName,N'')))),N'') AS CreatedByName
    FROM dbo.QuotationCommunications qc
    LEFT JOIN dbo.Users u ON u.UserId = qc.CreatedBy
    WHERE qc.QuotationId = @QuotationId
    ORDER BY qc.CreatedDate DESC, qc.CommunicationId DESC;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationEmail_GetData]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Ensure the existing quotation email data also exposes customer WhatsApp/mobile. */
CREATE   PROCEDURE [dbo].[usp_QuotationEmail_GetData]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        q.QuotationId,
        q.CustomerCode,
        COALESCE(NULLIF(LTRIM(RTRIM(c.CustomerName)), N''), NULLIF(LTRIM(RTRIM(t.CustomerName)), N''), q.CustomerCode) AS CustomerName,
        COALESCE(NULLIF(LTRIM(RTRIM(c.CustomerEmail)), N''), NULLIF(LTRIM(RTRIM(t.Email)), N''), N'') AS CustomerEmail,
        COALESCE(NULLIF(LTRIM(RTRIM(t.Mobile)), N''), NULLIF(LTRIM(RTRIM(c.CustomerPhone)), N''), N'') AS CustomerMobile,
        COALESCE(NULLIF(LTRIM(RTRIM(q.CustomerAddress)),N''), NULLIF(LTRIM(RTRIM(c.Address)),N''), N'') AS CustomerAddress,
        COALESCE(q.QuotationStatus, N'') AS QuotationStatus,
        q.CreatedDate
    FROM dbo.Quotations q
    LEFT JOIN dbo.Customers c ON c.CustomerCode = q.CustomerCode
    LEFT JOIN dbo.Trackers t ON t.TrackerId = q.TrackerId
    WHERE q.QuotationId = @QuotationId;

    SELECT
        COALESCE(NULLIF(LTRIM(RTRIM(ts.ServiceName)), N''), N'') AS ServiceName,
        COALESCE(NULLIF(LTRIM(RTRIM(sc.CategoryName)), N''), ts.CategoryName, N'') AS CategoryName,
        COALESCE(NULLIF(LTRIM(RTRIM(ssc.SubCategoryName)), N''), ts.SubCategoryName, N'') AS SubCategoryName,
        1 AS Quantity,
        COALESCE(ts.Amount, 0) AS Amount,
        COALESCE(ts.VAT, 0) AS VAT,
        COALESCE(ts.Amount, 0) + COALESCE(ts.VAT, 0) AS TaxableValue
    FROM dbo.QuotationItems qi
    INNER JOIN dbo.TrackerServiceSpecs ts ON ts.TrackerServiceSpecId = qi.TrackerServiceSpecId
    LEFT JOIN dbo.ServiceCategories sc ON sc.CategoryId = ts.CategoryId
    LEFT JOIN dbo.ServiceSubCategories ssc ON ssc.SubCategoryId = ts.SubCategoryId
    WHERE qi.QuotationId = @QuotationId
    ORDER BY qi.QuotationItemId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationEmailHistory_Create]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationEmailHistory_Create]
    @QuotationId INT,
    @EmailTemplateId INT = NULL,
    @ToEmail NVARCHAR(1000),
    @CcEmail NVARCHAR(2000) = NULL,
    @Subject NVARCHAR(500),
    @Body NVARCHAR(MAX),
    @AttachmentName NVARCHAR(255) = NULL,
    @SendType NVARCHAR(30) = N'Manual',
    @SentByUserId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.QuotationEmailHistory
    (QuotationId,EmailTemplateId,ToEmail,CcEmail,Subject,Body,AttachmentName,SendType,Status,CreatedDate,SentByUserId)
    VALUES
    (@QuotationId,@EmailTemplateId,@ToEmail,@CcEmail,@Subject,@Body,@AttachmentName,
     CASE WHEN @SendType IN (N'Resend',N'Automatic') THEN @SendType ELSE N'Manual' END,
     N'Pending',SYSUTCDATETIME(),@SentByUserId);
    SELECT CONVERT(INT,SCOPE_IDENTITY()) AS QuotationEmailHistoryId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationEmailHistory_Get]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationEmailHistory_Get]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        h.QuotationEmailHistoryId,
        h.QuotationId,
        h.EmailTemplateId,
        h.ToEmail,
        h.CcEmail,
        h.Subject,
        h.Body,
        h.AttachmentName,
        h.SendType,
        h.Status,
        h.CreatedDate,
        h.SentDate,
        h.SentByUserId,
        LTRIM(RTRIM(COALESCE(u.FirstName,N'') + CASE WHEN NULLIF(u.LastName,N'') IS NULL THEN N'' ELSE N' ' + u.LastName END)) AS SentByUserName,
        h.ErrorMessage
    FROM dbo.QuotationEmailHistory h
    LEFT JOIN dbo.Users u ON u.UserId=h.SentByUserId
    WHERE h.QuotationId=@QuotationId
    ORDER BY h.CreatedDate DESC, h.QuotationEmailHistoryId DESC;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationEmailHistory_UpdateStatus]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationEmailHistory_UpdateStatus]
    @QuotationEmailHistoryId INT,
    @Status NVARCHAR(30),
    @ErrorMessage NVARCHAR(MAX) = NULL,
    @SentByUserId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.QuotationEmailHistory
    SET Status = @Status,
        ErrorMessage = @ErrorMessage,
        SentDate = CASE WHEN @Status = N'Sent' THEN SYSUTCDATETIME() ELSE SentDate END,
        SentByUserId = COALESCE(@SentByUserId,SentByUserId)
    WHERE QuotationEmailHistoryId = @QuotationEmailHistoryId;
    IF @@ROWCOUNT = 0 RETURN;
    SELECT * FROM dbo.QuotationEmailHistory WHERE QuotationEmailHistoryId=@QuotationEmailHistoryId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationItems_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationItems_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.QuotationItems
    WHERE QuotationItemId = @Id;

    SELECT @@ROWCOUNT AS AffectedCount;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationItems_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationItems_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        QuotationItemId,
        QuotationId,
        TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId,
        ServiceName,
        Quantity,
        Amount,
        VAT,
        TaxableValue,
        CreatedDate
    FROM dbo.QuotationItems
    WHERE QuotationItemId = @Id;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationItems_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationItems_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;

    SET @PageNumber =
        CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;

    SET @PageSize =
        CASE
            WHEN @PageSize < 1 OR @PageSize > 200 THEN 25
            ELSE @PageSize
        END;

    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1)
    FROM dbo.QuotationItems;

    SELECT @TotalCount AS TotalCount;

    SELECT
        QuotationItemId,
        QuotationId,
        TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId,
        ServiceName,
        Quantity,
        Amount,
        VAT,
        TaxableValue,
        CreatedDate
    FROM dbo.QuotationItems
    ORDER BY QuotationItemId
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationItems_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationItems_Insert]
    @QuotationId INT,
    @TrackerCategorySubCategorySpecId INT,
    @TrackerServiceSpecId INT,
    @ServiceName NVARCHAR(150),
    @Quantity INT,
    @Amount DECIMAL(18,2),
    @VAT DECIMAL(18,2),
    @TaxableValue DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;

    IF @Quantity < 1
        THROW 50003, 'Quotation quantity must be at least 1.', 1;

    IF NULLIF(LTRIM(RTRIM(@ServiceName)), N'') IS NULL
        THROW 50004, 'ServiceName is required.', 1;

    INSERT INTO dbo.QuotationItems
    (
        QuotationId,
        TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId,
        ServiceName,
        Quantity,
        Amount,
        VAT,
        TaxableValue,
        CreatedDate
    )
    VALUES
    (
        @QuotationId,
        @TrackerCategorySubCategorySpecId,
        @TrackerServiceSpecId,
        LTRIM(RTRIM(@ServiceName)),
        @Quantity,
        @Amount,
        @VAT,
        @TaxableValue,
        SYSUTCDATETIME()
    );

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());

    SELECT
        QuotationItemId,
        QuotationId,
        TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId,
        ServiceName,
        Quantity,
        Amount,
        VAT,
        TaxableValue,
        CreatedDate
    FROM dbo.QuotationItems
    WHERE QuotationItemId = @NewId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationItems_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationItems_Update]
    @Id INT,
    @QuotationId INT,
    @TrackerCategorySubCategorySpecId INT,
    @TrackerServiceSpecId INT,
    @ServiceName NVARCHAR(150),
    @Quantity INT,
    @Amount DECIMAL(18,2),
    @VAT DECIMAL(18,2),
    @TaxableValue DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;

    IF @Quantity < 1
        THROW 50003, 'Quotation quantity must be at least 1.', 1;

    IF NULLIF(LTRIM(RTRIM(@ServiceName)), N'') IS NULL
        THROW 50004, 'ServiceName is required.', 1;

    UPDATE dbo.QuotationItems
    SET
        QuotationId = @QuotationId,
        TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId = @TrackerServiceSpecId,
        ServiceName = LTRIM(RTRIM(@ServiceName)),
        Quantity = @Quantity,
        Amount = @Amount,
        VAT = @VAT,
        TaxableValue = @TaxableValue
    WHERE QuotationItemId = @Id;

    IF @@ROWCOUNT = 0
        RETURN;

    SELECT
        QuotationItemId,
        QuotationId,
        TrackerCategorySubCategorySpecId,
        TrackerServiceSpecId,
        ServiceName,
        Quantity,
        Amount,
        VAT,
        TaxableValue,
        CreatedDate
    FROM dbo.QuotationItems
    WHERE QuotationItemId = @Id;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationPayment_GetSummary]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationPayment_GetSummary]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @GrandTotal DECIMAL(18,2) = 0;
    DECLARE @Paid DECIMAL(18,2) = 0;
    DECLARE @Status NVARCHAR(30);

    SELECT @Status = QuotationStatus FROM dbo.Quotations WHERE QuotationId = @QuotationId;
    IF @Status IS NULL THROW 50020, 'Quotation was not found.', 1;

    SELECT @GrandTotal = ISNULL(SUM((ISNULL(Amount,0) + ISNULL(VAT,0)) * ISNULL(Quantity,1)),0)
    FROM dbo.QuotationItems WHERE QuotationId = @QuotationId;

    SELECT @Paid = ISNULL(SUM(PaymentAmount),0) FROM dbo.Payments WHERE QuotationId = @QuotationId;

    SELECT @QuotationId AS QuotationId,
           CONCAT(N'BP-QT-', @QuotationId) AS QuotationNumber,
           @Status AS QuotationStatus,
           @GrandTotal AS GrandTotal,
           @Paid AS PaidAmount,
           CASE WHEN @GrandTotal - @Paid < 0 THEN 0 ELSE @GrandTotal - @Paid END AS BalanceAmount;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_AssertEditable]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
/* ============================================================================
   Quotation mode rules
   ----------------------------------------------------------------------------
   A tracker uses ONE quotation mode, chosen by its first quotation:
     ALL          one "All Categories" quotation for the whole tracker.  No other quotation can be added.
     INDEPENDENT  one quotation per family member (per Category / Sub Category).
                  Each family member's quotation is accepted, invoiced and paid on its own.
   The two modes cannot be mixed on one tracker.

   Accepting a quotation locks only the quotations that cover the SAME scope:
     - an All Categories quotation overlaps everything
     - two independent quotations overlap only for the same Category / Sub Category AND the same family member
   Rejected quotations do not count, so a rejected one can be replaced by a new one.
   Safe to run more than once.
   ============================================================================ */

/* ---------- 1. A quotation is locked only by an accepted quotation of the same scope ---------- */
CREATE   PROCEDURE [dbo].[usp_Quotations_AssertEditable]
    @QuotationId INT,
    @NewStatus NVARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Current NVARCHAR(50), @TrackerId INT;
    SELECT @Current = QuotationStatus, @TrackerId = TrackerId FROM dbo.Quotations WHERE QuotationId = @QuotationId;
    IF @TrackerId IS NULL RETURN;   -- the calling procedure reports "not found"

    IF EXISTS (SELECT 1
               FROM dbo.Quotations me
               INNER JOIN dbo.Quotations other ON other.TrackerId = me.TrackerId AND other.QuotationId <> me.QuotationId
               WHERE me.QuotationId = @QuotationId
                 AND dbo.fn_QuotationStatusKey(other.QuotationStatus) = 'ACCEPTED'
                 AND ( UPPER(me.QuotationMode) <> N'INDEPENDENT' OR UPPER(other.QuotationMode) <> N'INDEPENDENT'
                       OR ( me.TrackerCategorySubCategorySpecId = other.TrackerCategorySubCategorySpecId
                            AND ISNULL(me.TrackerCategorySubCategorySpecPersonId, 0) = ISNULL(other.TrackerCategorySubCategorySpecPersonId, 0) ) ))
        THROW 50041, 'This quotation is locked because another quotation for the same family member / sub category was accepted.', 1;

    DECLARE @Key VARCHAR(20) = dbo.fn_QuotationStatusKey(@Current);
    DECLARE @NewKey VARCHAR(20) = dbo.fn_QuotationStatusKey(@NewStatus);
    IF @Key = '' OR @Key IN ('DRAFT', 'SENT', 'NEGOTIATE') RETURN;

    DECLARE @Msg NVARCHAR(400) = CONCAT(N'A ', @Current, N' quotation is final and cannot be edited.');
    THROW 50042, @Msg, 1;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Status NVARCHAR(50);
    SELECT @Status = QuotationStatus FROM dbo.Quotations WHERE QuotationId = @Id;
    IF @Status IS NOT NULL
    BEGIN
        IF dbo.fn_QuotationStatusKey(@Status) NOT IN ('DRAFT', '')
            THROW 50044, 'Only a Draft quotation can be deleted.', 1;
        IF EXISTS (SELECT 1 FROM dbo.Payments WHERE QuotationId = @Id)
            THROW 50045, 'A quotation with payments cannot be deleted.', 1;
    END;
    DELETE FROM dbo.Quotations
    WHERE QuotationId = @Id;
    SELECT @@ROWCOUNT AS AffectedCount;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_GetAvailableServicesForEdit]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_GetAvailableServicesForEdit]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @TrackerId INT,
            @QuotationMode NVARCHAR(20),
            @CategorySpecId INT;

    SELECT
        @TrackerId = q.TrackerId,
        @QuotationMode = UPPER(LTRIM(RTRIM(ISNULL(q.QuotationMode, N'')))),
        @CategorySpecId = q.TrackerCategorySubCategorySpecId
    FROM dbo.Quotations q
    WHERE q.QuotationId = @QuotationId;

    IF @TrackerId IS NULL
        THROW 50060, 'Quotation was not found.', 1;

    /*
      1) Shared tracker service specs and service specs already created for this quotation.
         A spec created for another quotation is intentionally not offered here.
    */
    SELECT
        s.TrackerServiceSpecId,
        s.TrackerCategorySubCategorySpecId,
        s.TrackerId,
        s.CategoryId,
        s.CategoryName,
        s.SubCategoryId,
        s.SubCategoryName,
        s.ServiceId,
        s.ServiceName,
        s.Amount,
        s.VAT,
        s.Status
    FROM dbo.TrackerServiceSpecs s
    WHERE s.TrackerId = @TrackerId
      AND (s.QuotationId IS NULL OR s.QuotationId = @QuotationId)
      AND ISNULL(s.Status, N'Active') <> N'Deleted'
      AND (@QuotationMode <> N'INDEPENDENT' OR s.TrackerCategorySubCategorySpecId = @CategorySpecId)
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.QuotationItems qi
          WHERE qi.QuotationId = @QuotationId
            AND qi.TrackerServiceSpecId = s.TrackerServiceSpecId
      )

    UNION ALL

    /*
      2) Services from the Services master that are valid for this Tracker's
         Category/Sub Category but do not yet have a TrackerServiceSpec.
         They are returned with TrackerServiceSpecId = 0. Save creates a
         quotation-specific spec only when the user actually saves the quotation.
    */
    SELECT
        CAST(0 AS INT) AS TrackerServiceSpecId,
        cs.TrackerCategorySubCategorySpecId,
        @TrackerId AS TrackerId,
        svc.CategoryId,
        cs.CategoryName,
        svc.SubCategoryId,
        cs.SubCategoryName,
        svc.ServiceId,
        svc.ServiceName,
        svc.Amount,
        svc.VAT,
        CASE WHEN svc.IsActive = 1 THEN N'Active' ELSE N'Inactive' END AS Status
    FROM dbo.Services svc
    INNER JOIN dbo.TrackerCategorySubCategorySpecs cs
      ON cs.TrackerId = @TrackerId
     AND cs.CategoryId = svc.CategoryId
     AND cs.SubCategoryId = svc.SubCategoryId
    WHERE svc.IsActive = 1
      AND ISNULL(cs.Status, N'Active') <> N'Deleted'
      AND (@QuotationMode <> N'INDEPENDENT' OR cs.TrackerCategorySubCategorySpecId = @CategorySpecId)
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.QuotationItems qi
          INNER JOIN dbo.TrackerServiceSpecs qs
            ON qs.TrackerServiceSpecId = qi.TrackerServiceSpecId
          WHERE qi.QuotationId = @QuotationId
            AND qs.ServiceId = svc.ServiceId
            AND qi.TrackerCategorySubCategorySpecId = cs.TrackerCategorySubCategorySpecId
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.TrackerServiceSpecs ts
          WHERE ts.TrackerId = @TrackerId
            AND ts.TrackerCategorySubCategorySpecId = cs.TrackerCategorySubCategorySpecId
            AND ts.ServiceId = svc.ServiceId
            AND (ts.QuotationId IS NULL OR ts.QuotationId = @QuotationId)
      )
    ORDER BY CategoryName, SubCategoryName, ServiceName, TrackerServiceSpecId, ServiceId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Keep the person relationship available to every quotation read path. */
CREATE   PROCEDURE [dbo].[usp_Quotations_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QuotationId, TrackerId, QuotationMode, TrackerCategorySubCategorySpecId,
           TrackerCategorySubCategorySpecPersonId, CustomerCode, CustomerAddress,
           QuotationStatus, SenderInfo, Notes, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Quotations
    WHERE QuotationId = @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT = (SELECT COUNT(1) FROM dbo.Quotations);
    SELECT @TotalCount AS TotalCount;
    SELECT QuotationId, TrackerId, QuotationMode, TrackerCategorySubCategorySpecId,
           TrackerCategorySubCategorySpecPersonId, CustomerCode, CustomerAddress,
           QuotationStatus, SenderInfo, Notes, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Quotations
    ORDER BY QuotationId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_GetPaged_WithPaymentHistory]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_GetPaged_WithPaymentHistory]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    DECLARE @TotalCount INT = (SELECT COUNT(1) FROM dbo.Quotations);
    SELECT @TotalCount AS TotalCount;

    DECLARE @PageQuotationIds TABLE (QuotationId INT NOT NULL PRIMARY KEY);
    INSERT INTO @PageQuotationIds(QuotationId)
    SELECT QuotationId FROM dbo.Quotations
    ORDER BY QuotationId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT q.QuotationId, q.TrackerId, q.QuotationMode, q.TrackerCategorySubCategorySpecId,
           q.TrackerCategorySubCategorySpecPersonId, q.CustomerCode, q.CustomerAddress,
           q.QuotationStatus, q.SenderInfo, q.Notes, q.CreatedBy, q.CreatedDate,
           q.ModifiedBy, q.ModifiedDate, totals.GrandTotal, totals.PaidAmount,
           CASE WHEN totals.GrandTotal - totals.PaidAmount > 0 THEN totals.GrandTotal - totals.PaidAmount ELSE 0 END AS PendingAmount,
           CASE WHEN totals.GrandTotal > 0 AND totals.PaidAmount >= totals.GrandTotal THEN N'Completed' ELSE N'Pending' END AS PaymentStatus
    FROM dbo.Quotations q
    INNER JOIN @PageQuotationIds pageIds ON pageIds.QuotationId = q.QuotationId
    OUTER APPLY
    (
        SELECT ISNULL(SUM((ISNULL(qi.Amount,0) + ISNULL(qi.VAT,0)) * ISNULL(qi.Quantity,1)),0) AS GrandTotal,
               ISNULL((SELECT SUM(p.PaymentAmount) FROM dbo.Payments p WHERE p.QuotationId = q.QuotationId),0) AS PaidAmount
        FROM dbo.QuotationItems qi WHERE qi.QuotationId = q.QuotationId
    ) totals
    ORDER BY q.QuotationId DESC;

    SELECT p.PaymentId, p.PaymentNumber, p.TrackerId, p.QuotationId, p.CustomerId, p.CustomerCode,
           p.PaymentDate, p.AmountDue, p.PaymentAmount, p.PaymentMethodId, pm.MethodName AS PaymentMethodName,
           p.TransactionReference, p.PaymentStatus, p.Notes, p.CreatedBy, p.CreatedDate, p.ModifiedBy,
           p.ModifiedDate, CONCAT(N'BP-QT-', p.QuotationId) AS QuotationNumber,
           t.CustomerName, LTRIM(RTRIM(CONCAT(ISNULL(u.FirstName,N''), N' ', ISNULL(u.LastName,N'')))) AS CreatedByName
    FROM dbo.Payments p
    INNER JOIN @PageQuotationIds pageIds ON pageIds.QuotationId = p.QuotationId
    LEFT JOIN dbo.PaymentMethods pm ON pm.PaymentMethodId = p.PaymentMethodId
    LEFT JOIN dbo.Trackers t ON t.TrackerId = p.TrackerId
    LEFT JOIN dbo.Users u ON u.UserId = p.CreatedBy
    ORDER BY p.QuotationId DESC, p.PaymentDate DESC, p.PaymentId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_GetPrintData]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_Quotations_GetPrintData]
    @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Quotations WHERE QuotationId = @QuotationId)
        RETURN;

    SELECT
        q.QuotationId,
        q.TrackerId,
        COALESCE(NULLIF(t.CustomerName, N''), c.CustomerName, q.CustomerCode) AS CustomerName,
        q.CustomerCode,
        COALESCE(NULLIF(q.CustomerAddress, N''), NULLIF(c.Address, N''), N'') AS CustomerAddress,
        CASE WHEN UPPER(ISNULL(q.QuotationMode, N'ALL')) = N'INDEPENDENT' THEN N'Independent' ELSE N'All' END AS QuotationType,
        q.QuotationStatus,
        q.CreatedDate,
        CAST(ISNULL(SUM(ISNULL(qi.Amount, 0) * ISNULL(qi.Quantity, 1)), 0) AS DECIMAL(18,2)) AS Net,
        CAST(ISNULL(SUM(ISNULL(qi.VAT, 0) * ISNULL(qi.Quantity, 1)), 0) AS DECIMAL(18,2)) AS Vat,
        CAST(ISNULL(SUM((ISNULL(qi.Amount, 0) + ISNULL(qi.VAT, 0)) * ISNULL(qi.Quantity, 1)), 0) AS DECIMAL(18,2)) AS GrandTotal
    FROM dbo.Quotations q
    LEFT JOIN dbo.Trackers t ON t.TrackerId = q.TrackerId
    LEFT JOIN dbo.Customers c ON c.CustomerCode = q.CustomerCode
    LEFT JOIN dbo.QuotationItems qi ON qi.QuotationId = q.QuotationId
    WHERE q.QuotationId = @QuotationId
    GROUP BY
        q.QuotationId, q.TrackerId, t.CustomerName, c.CustomerName,
        q.CustomerCode, q.CustomerAddress, c.Address,
        q.QuotationMode, q.QuotationStatus, q.CreatedDate;

    SELECT
        qi.QuotationItemId,
        qi.TrackerCategorySubCategorySpecId,
        qi.TrackerServiceSpecId,
        qi.ServiceName,
        COALESCE(s.CategoryName, spec.CategoryName, N'') AS CategoryName,
        COALESCE(s.SubCategoryName, spec.SubCategoryName, N'') AS SubCategoryName,
        qi.Quantity,
        qi.Amount,
        qi.VAT,
        qi.TaxableValue
    FROM dbo.QuotationItems qi
    LEFT JOIN dbo.TrackerServiceSpecs s ON s.TrackerServiceSpecId = qi.TrackerServiceSpecId
    LEFT JOIN dbo.TrackerCategorySubCategorySpecs spec ON spec.TrackerCategorySubCategorySpecId = qi.TrackerCategorySubCategorySpecId
    WHERE qi.QuotationId = @QuotationId
    ORDER BY qi.QuotationItemId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


/* Existing quotation insert proc: default is resolved from the master when the caller does not supply one. */
CREATE   PROCEDURE [dbo].[usp_Quotations_Insert]
    @TrackerId INT,
    @QuotationMode NVARCHAR(20),
    @TrackerCategorySubCategorySpecId INT = NULL,
    @TrackerCategorySubCategorySpecPersonId INT = NULL,
    @CustomerCode NVARCHAR(50),
    @CustomerAddress NVARCHAR(255) = NULL,
    @QuotationStatus NVARCHAR(30),
    @SenderInfo NVARCHAR(200),
    @Notes NVARCHAR(1000),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @QuotationMode=UPPER(LTRIM(RTRIM(@QuotationMode)));

    IF @QuotationMode NOT IN (N'ALL',N'INDEPENDENT')
        THROW 50001,'QuotationMode must be ALL or INDEPENDENT.',1;
    IF @QuotationMode=N'INDEPENDENT' AND @TrackerCategorySubCategorySpecId IS NULL
        THROW 50002,'Independent quotation requires Category/Sub Category.',1;
    IF @QuotationMode=N'ALL'
    BEGIN
        SET @TrackerCategorySubCategorySpecId=NULL;
        SET @TrackerCategorySubCategorySpecPersonId=NULL;
    END;
    IF @TrackerCategorySubCategorySpecPersonId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecPersons p WHERE p.TrackerCategorySubCategorySpecPersonId=@TrackerCategorySubCategorySpecPersonId AND p.TrackerId=@TrackerId AND p.TrackerCategorySubCategorySpecId=@TrackerCategorySubCategorySpecId)
        THROW 50003,'The selected person does not belong to the selected Tracker Category/Sub Category.',1;

    IF NULLIF(LTRIM(RTRIM(@QuotationStatus)),N'') IS NULL
        SELECT TOP(1) @QuotationStatus=StatusName FROM dbo.QuotationStatuses WHERE IsActive=1 ORDER BY IsDefault DESC,DisplayOrder,QuotationStatusId;
    ELSE
        SELECT TOP(1) @QuotationStatus=StatusName FROM dbo.QuotationStatuses WHERE IsActive=1 AND (StatusName=@QuotationStatus OR StatusCode=UPPER(LTRIM(RTRIM(@QuotationStatus)))) ORDER BY DisplayOrder,QuotationStatusId;
    IF NULLIF(LTRIM(RTRIM(@QuotationStatus)),N'') IS NULL
        THROW 50004,'The selected quotation status is not active or no default quotation status is configured.',1;

    IF @CustomerAddress IS NULL
        SELECT @CustomerAddress=c.Address FROM dbo.Trackers t LEFT JOIN dbo.Customers c ON c.CustomerId=t.CustomerId WHERE t.TrackerId=@TrackerId;

    INSERT dbo.Quotations(TrackerId,QuotationMode,TrackerCategorySubCategorySpecId,TrackerCategorySubCategorySpecPersonId,CustomerCode,CustomerAddress,QuotationStatus,SenderInfo,Notes,CreatedBy,CreatedDate)
    VALUES(@TrackerId,@QuotationMode,@TrackerCategorySubCategorySpecId,@TrackerCategorySubCategorySpecPersonId,@CustomerCode,@CustomerAddress,@QuotationStatus,@SenderInfo,@Notes,@CreatedBy,SYSUTCDATETIME());

    DECLARE @NewId INT=CONVERT(INT,SCOPE_IDENTITY());
    SELECT QuotationId,TrackerId,QuotationMode,TrackerCategorySubCategorySpecId,TrackerCategorySubCategorySpecPersonId,CustomerCode,CustomerAddress,QuotationStatus,SenderInfo,Notes,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate
    FROM dbo.Quotations WHERE QuotationId=@NewId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_MarkCommunicationSent]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Keep the quotation communication status transition aligned with the new master code. */
CREATE   PROCEDURE [dbo].[usp_Quotations_MarkCommunicationSent]
    @QuotationId INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @TrackerId INT, @NextStatus NVARCHAR(30);
    SELECT @TrackerId=TrackerId FROM dbo.Quotations WHERE QuotationId=@QuotationId;
    IF @TrackerId IS NULL THROW 50001,'Quotation was not found.',1;
    SET @NextStatus=N'QUOTATION_SENT';
    IF NOT EXISTS (SELECT 1 FROM dbo.TrackerStatuses WHERE StatusCode=@NextStatus AND IsActive=1)
        SET @NextStatus=N'QUOTATION';
    UPDATE dbo.Trackers SET Status=@NextStatus,ModifiedBy=@ModifiedBy,ModifiedDate=SYSUTCDATETIME() WHERE TrackerId=@TrackerId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_MarkSent]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_MarkSent]
    @Id INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Quotations
    SET QuotationStatus = N'Sent',
        ModifiedBy = @ModifiedBy,
        ModifiedDate = SYSUTCDATETIME()
    WHERE QuotationId = @Id;

    IF @@ROWCOUNT = 0
        RETURN;

    SELECT QuotationId, QuotationStatus, ModifiedBy, ModifiedDate
    FROM dbo.Quotations
    WHERE QuotationId = @Id;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Quotations_Update]
    @Id INT,
    @TrackerId INT,
    @QuotationMode NVARCHAR(20),
    @TrackerCategorySubCategorySpecId INT = NULL,
    @TrackerCategorySubCategorySpecPersonId INT = NULL,
    @CustomerCode NVARCHAR(50),
    @CustomerAddress NVARCHAR(255) = NULL,
    @QuotationStatus NVARCHAR(30),
    @SenderInfo NVARCHAR(200) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @QuotationMode = UPPER(LTRIM(RTRIM(@QuotationMode)));
    IF @QuotationMode NOT IN (N'ALL', N'INDEPENDENT') THROW 50001, 'QuotationMode must be ALL or INDEPENDENT.', 1;
    IF @QuotationMode = N'INDEPENDENT' AND @TrackerCategorySubCategorySpecId IS NULL THROW 50002, 'Independent quotation requires Category/Sub Category.', 1;
    IF @QuotationMode = N'ALL'
    BEGIN SET @TrackerCategorySubCategorySpecId = NULL; SET @TrackerCategorySubCategorySpecPersonId = NULL; END;
    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId) THROW 50005, 'Tracker was not found.', 1;
    EXEC dbo.usp_Quotations_AssertEditable @QuotationId = @Id, @NewStatus = @QuotationStatus;
    IF @TrackerCategorySubCategorySpecId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs WHERE TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId AND TrackerId = @TrackerId)
        THROW 50006, 'Category / Sub Category does not belong to the selected Tracker.', 1;
    IF @TrackerCategorySubCategorySpecPersonId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecPersons WHERE TrackerCategorySubCategorySpecPersonId = @TrackerCategorySubCategorySpecPersonId AND TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId AND TrackerId = @TrackerId)
        THROW 50007, 'Person does not belong to the selected Category / Sub Category and Tracker.', 1;

    UPDATE dbo.Quotations
    SET TrackerId = @TrackerId, QuotationMode = @QuotationMode,
        TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId,
        TrackerCategorySubCategorySpecPersonId = @TrackerCategorySubCategorySpecPersonId,
        CustomerCode = @CustomerCode, CustomerAddress = @CustomerAddress,
        QuotationStatus = @QuotationStatus, SenderInfo = @SenderInfo, Notes = @Notes,
        ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
    WHERE QuotationId = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT QuotationId, TrackerId, QuotationMode, TrackerCategorySubCategorySpecId,
           TrackerCategorySubCategorySpecPersonId, CustomerCode, CustomerAddress,
           QuotationStatus, SenderInfo, Notes, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Quotations WHERE QuotationId = @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_UpdateStatus]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* --------------------------------------------------------------------------
   4. Dynamic quotation status update endpoint procedure
   Accepts either StatusName or StatusCode from callers and stores StatusName,
   preserving the existing Quotations.QuotationStatus data contract.
   -------------------------------------------------------------------------- */
CREATE   PROCEDURE [dbo].[usp_Quotations_UpdateStatus]
    @QuotationId INT,
    @QuotationStatus NVARCHAR(30),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ResolvedStatusName NVARCHAR(50);
    SELECT TOP (1) @ResolvedStatusName=StatusName
    FROM dbo.QuotationStatuses
    WHERE IsActive=1
      AND (StatusName=@QuotationStatus OR StatusCode=UPPER(LTRIM(RTRIM(@QuotationStatus))))
    ORDER BY DisplayOrder, QuotationStatusId;

    IF @ResolvedStatusName IS NULL
        THROW 50031,'The selected quotation status is not active or does not exist in the Quotation Status master.',1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Quotations WHERE QuotationId=@QuotationId)
        RETURN;

    UPDATE dbo.Quotations
       SET QuotationStatus=@ResolvedStatusName,
           ModifiedBy=@ModifiedBy,
           ModifiedDate=SYSUTCDATETIME()
     WHERE QuotationId=@QuotationId;

    SELECT QuotationId, QuotationStatus, ModifiedBy, ModifiedDate
    FROM dbo.Quotations WHERE QuotationId=@QuotationId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Quotations_UpdateWithItems]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- Existing procedures, re-issued with the rules added ---------- */
CREATE   PROCEDURE [dbo].[usp_Quotations_UpdateWithItems]
    @QuotationId INT,
    @TrackerId INT,
    @QuotationMode NVARCHAR(20),
    @TrackerCategorySubCategorySpecId INT = NULL,
    @TrackerCategorySubCategorySpecPersonId INT = NULL,
    @CustomerCode NVARCHAR(50),
    @CustomerAddress NVARCHAR(255) = NULL,
    @QuotationStatus NVARCHAR(30),
    @SenderInfo NVARCHAR(200) = NULL,
    @Notes NVARCHAR(1000) = NULL,
    @ModifiedBy INT = NULL,
    @ItemsJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @QuotationMode = UPPER(LTRIM(RTRIM(@QuotationMode)));

    IF @QuotationMode NOT IN (N'ALL', N'INDEPENDENT')
        THROW 50001, 'QuotationMode must be ALL or INDEPENDENT.', 1;
    IF @QuotationMode = N'INDEPENDENT' AND @TrackerCategorySubCategorySpecId IS NULL
        THROW 50002, 'Independent quotation requires Category / Sub Category.', 1;
    IF @QuotationMode = N'ALL'
    BEGIN
        SET @TrackerCategorySubCategorySpecId = NULL;
        SET @TrackerCategorySubCategorySpecPersonId = NULL;
    END;
    IF NOT EXISTS (SELECT 1 FROM dbo.Quotations WHERE QuotationId = @QuotationId)
        THROW 50010, 'Quotation was not found.', 1;
    EXEC dbo.usp_Quotations_AssertEditable @QuotationId = @QuotationId, @NewStatus = @QuotationStatus;
    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId)
        THROW 50005, 'Tracker was not found.', 1;
    IF @TrackerCategorySubCategorySpecId IS NOT NULL
       AND NOT EXISTS (
            SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs
            WHERE TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId
              AND TrackerId = @TrackerId
       )
        THROW 50006, 'Category / Sub Category does not belong to the selected Tracker.', 1;
    IF @TrackerCategorySubCategorySpecPersonId IS NOT NULL
       AND NOT EXISTS (
            SELECT 1 FROM dbo.TrackerCategorySubCategorySpecPersons
            WHERE TrackerCategorySubCategorySpecPersonId = @TrackerCategorySubCategorySpecPersonId
              AND TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId
              AND TrackerId = @TrackerId
       )
        THROW 50007, 'Person does not belong to the selected Category / Sub Category and Tracker.', 1;
    IF ISJSON(@ItemsJson) <> 1
        THROW 50011, 'Quotation items payload is not valid JSON.', 1;

    DECLARE @Items TABLE
    (
        QuotationItemId INT NOT NULL,
        TrackerCategorySubCategorySpecId INT NULL,
        TrackerServiceSpecId INT NULL,
        ServiceId INT NULL,
        ServiceName NVARCHAR(150) NULL,
        Quantity INT NULL,
        Amount DECIMAL(18,2) NULL,
        VAT DECIMAL(18,2) NULL,
        TaxableValue DECIMAL(18,2) NULL
    );

    INSERT INTO @Items
    (
        QuotationItemId, TrackerCategorySubCategorySpecId, TrackerServiceSpecId, ServiceId,
        ServiceName, Quantity, Amount, VAT, TaxableValue
    )
    SELECT
        COALESCE(j.QuotationItemId, j.QuotationItemIdPascal, 0),
        COALESCE(j.TrackerCategorySubCategorySpecId, j.TrackerCategorySubCategorySpecIdPascal, s.TrackerCategorySubCategorySpecId),
        NULLIF(COALESCE(j.TrackerServiceSpecId, j.TrackerServiceSpecIdPascal), 0),
        NULLIF(COALESCE(j.ServiceId, j.ServiceIdPascal), 0),
        COALESCE(j.ServiceName, j.ServiceNamePascal),
        COALESCE(j.Quantity, j.QuantityPascal),
        COALESCE(j.Amount, j.AmountPascal),
        COALESCE(j.VAT, j.VATPascal),
        COALESCE(j.TaxableValue, j.TaxableValuePascal)
    FROM OPENJSON(@ItemsJson)
    WITH
    (
        QuotationItemId INT '$.quotationItemId',
        QuotationItemIdPascal INT '$.QuotationItemId',
        TrackerCategorySubCategorySpecId INT '$.trackerCategorySubCategorySpecId',
        TrackerCategorySubCategorySpecIdPascal INT '$.TrackerCategorySubCategorySpecId',
        TrackerServiceSpecId INT '$.trackerServiceSpecId',
        TrackerServiceSpecIdPascal INT '$.TrackerServiceSpecId',
        ServiceId INT '$.serviceId',
        ServiceIdPascal INT '$.ServiceId',
        ServiceName NVARCHAR(150) '$.serviceName',
        ServiceNamePascal NVARCHAR(150) '$.ServiceName',
        Quantity INT '$.quantity',
        QuantityPascal INT '$.Quantity',
        Amount DECIMAL(18,2) '$.amount',
        AmountPascal DECIMAL(18,2) '$.Amount',
        VAT DECIMAL(18,2) '$.vat',
        VATPascal DECIMAL(18,2) '$.VAT',
        TaxableValue DECIMAL(18,2) '$.taxableValue',
        TaxableValuePascal DECIMAL(18,2) '$.TaxableValue'
    ) j
    LEFT JOIN dbo.TrackerServiceSpecs s
      ON s.TrackerServiceSpecId = COALESCE(j.TrackerServiceSpecId, j.TrackerServiceSpecIdPascal)
     AND s.TrackerId = @TrackerId;

    IF NOT EXISTS (SELECT 1 FROM @Items)
        THROW 50012, 'At least one quotation service is required.', 1;
    IF EXISTS (SELECT 1 FROM @Items WHERE TrackerCategorySubCategorySpecId IS NULL)
        THROW 50013, 'Category / Sub Category is required for every quotation service.', 1;
    IF EXISTS (SELECT 1 FROM @Items WHERE TrackerServiceSpecId IS NULL AND ServiceId IS NULL)
        THROW 50014, 'Service reference is required for every quotation service.', 1;
    IF EXISTS (SELECT 1 FROM @Items WHERE NULLIF(LTRIM(RTRIM(ServiceName)), N'') IS NULL)
        THROW 50015, 'Service name is required.', 1;
    IF EXISTS (SELECT 1 FROM @Items WHERE Quantity IS NULL OR Quantity < 1)
        THROW 50016, 'Quotation quantity must be at least 1.', 1;
    IF EXISTS (SELECT 1 FROM @Items WHERE Amount IS NULL OR Amount < 0 OR VAT IS NULL OR VAT < 0)
        THROW 50017, 'Amount and VAT must be zero or greater.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        /* Resolve service-master selections to a tracker service spec. Existing shared specs are reused;
           otherwise a quotation-specific spec is created. This is the key isolation rule. */
        DECLARE @Resolve TABLE
        (
            QuotationItemId INT PRIMARY KEY,
            TrackerServiceSpecId INT NOT NULL
        );

        INSERT INTO @Resolve(QuotationItemId, TrackerServiceSpecId)
        SELECT i.QuotationItemId, MIN(s.TrackerServiceSpecId)
        FROM @Items i
        INNER JOIN dbo.TrackerServiceSpecs s
          ON s.TrackerId = @TrackerId
         AND s.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId
         AND s.ServiceId = i.ServiceId
         AND (s.QuotationId IS NULL OR s.QuotationId = @QuotationId)
         AND ISNULL(s.Status, N'Active') <> N'Deleted'
        WHERE i.TrackerServiceSpecId IS NULL
          AND i.ServiceId IS NOT NULL
        GROUP BY i.QuotationItemId;

        DECLARE @Create TABLE
        (
            QuotationItemId INT PRIMARY KEY,
            TrackerServiceSpecId INT NULL
        );

        INSERT INTO @Create(QuotationItemId)
        SELECT i.QuotationItemId
        FROM @Items i
        WHERE i.TrackerServiceSpecId IS NULL
          AND i.ServiceId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM @Resolve r WHERE r.QuotationItemId = i.QuotationItemId);

        DECLARE @CreateItemId INT,
                @CreateSpecId INT,
                @CreateServiceId INT,
                @CreateCategorySpecId INT,
                @CreateName NVARCHAR(150),
                @CreateAmount DECIMAL(18,2),
                @CreateVat DECIMAL(18,2),
                @CreateCategoryId INT,
                @CreateCategoryName NVARCHAR(100),
                @CreateSubCategoryId INT,
                @CreateSubCategoryName NVARCHAR(100);

        DECLARE create_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT c.QuotationItemId, i.ServiceId, i.TrackerCategorySubCategorySpecId,
                   i.ServiceName, i.Amount, i.VAT,
                   cs.CategoryId, cs.CategoryName, cs.SubCategoryId, cs.SubCategoryName
            FROM @Create c
            INNER JOIN @Items i ON i.QuotationItemId = c.QuotationItemId
            INNER JOIN dbo.TrackerCategorySubCategorySpecs cs
              ON cs.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId
             AND cs.TrackerId = @TrackerId;

        OPEN create_cursor;
        FETCH NEXT FROM create_cursor INTO @CreateItemId, @CreateServiceId, @CreateCategorySpecId,
            @CreateName, @CreateAmount, @CreateVat, @CreateCategoryId, @CreateCategoryName,
            @CreateSubCategoryId, @CreateSubCategoryName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM dbo.Services
                WHERE ServiceId = @CreateServiceId
                  AND CategoryId = @CreateCategoryId
                  AND SubCategoryId = @CreateSubCategoryId
                  AND IsActive = 1
            )
                THROW 50021, 'Selected service is not an active Service master record for the selected Category / Sub Category.', 1;

            INSERT INTO dbo.TrackerServiceSpecs
            (
                TrackerCategorySubCategorySpecId, TrackerId, CategoryId, CategoryName,
                SubCategoryId, SubCategoryName, ServiceId, ServiceName, Amount, VAT,
                Status, CreatedBy, CreatedDate, QuotationId
            )
            VALUES
            (
                @CreateCategorySpecId, @TrackerId, @CreateCategoryId, @CreateCategoryName,
                @CreateSubCategoryId, @CreateSubCategoryName, @CreateServiceId,
                LTRIM(RTRIM(@CreateName)), @CreateAmount, @CreateVat,
                N'Active', @ModifiedBy, SYSUTCDATETIME(), @QuotationId
            );

            SET @CreateSpecId = CONVERT(INT, SCOPE_IDENTITY());
            INSERT INTO @Resolve(QuotationItemId, TrackerServiceSpecId)
            VALUES (@CreateItemId, @CreateSpecId);

            FETCH NEXT FROM create_cursor INTO @CreateItemId, @CreateServiceId, @CreateCategorySpecId,
                @CreateName, @CreateAmount, @CreateVat, @CreateCategoryId, @CreateCategoryName,
                @CreateSubCategoryId, @CreateSubCategoryName;
        END;
        CLOSE create_cursor;
        DEALLOCATE create_cursor;

        UPDATE i
        SET TrackerServiceSpecId = r.TrackerServiceSpecId
        FROM @Items i
        INNER JOIN @Resolve r ON r.QuotationItemId = i.QuotationItemId
        WHERE i.TrackerServiceSpecId IS NULL;

        IF EXISTS (SELECT 1 FROM @Items WHERE TrackerServiceSpecId IS NULL)
            THROW 50014, 'Service reference could not be resolved.', 1;

        IF EXISTS (
            SELECT 1 FROM @Items i
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.TrackerServiceSpecs s
                WHERE s.TrackerServiceSpecId = i.TrackerServiceSpecId
                  AND s.TrackerId = @TrackerId
                  AND s.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId
                  AND (s.QuotationId IS NULL OR s.QuotationId = @QuotationId)
            )
        )
            THROW 50018, 'One or more selected services do not belong to this Tracker and Category / Sub Category.', 1;

        IF EXISTS (SELECT TrackerServiceSpecId FROM @Items GROUP BY TrackerServiceSpecId HAVING COUNT(*) > 1)
            THROW 50019, 'A service cannot be added more than once to the same quotation.', 1;
        IF EXISTS (
            SELECT 1 FROM @Items i
            WHERE i.QuotationItemId > 0
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.QuotationItems qi
                  WHERE qi.QuotationItemId = i.QuotationItemId
                    AND qi.QuotationId = @QuotationId
              )
        )
            THROW 50020, 'One or more quotation item IDs are invalid for this quotation.', 1;

        UPDATE dbo.Quotations
        SET TrackerId = @TrackerId,
            QuotationMode = @QuotationMode,
            TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId,
            TrackerCategorySubCategorySpecPersonId = @TrackerCategorySubCategorySpecPersonId,
            CustomerCode = LTRIM(RTRIM(@CustomerCode)),
            CustomerAddress = @CustomerAddress,
            QuotationStatus = LTRIM(RTRIM(@QuotationStatus)),
            SenderInfo = @SenderInfo,
            Notes = @Notes,
            ModifiedBy = @ModifiedBy,
            ModifiedDate = SYSUTCDATETIME()
        WHERE QuotationId = @QuotationId;

        UPDATE qi
        SET qi.TrackerCategorySubCategorySpecId = i.TrackerCategorySubCategorySpecId,
            qi.TrackerServiceSpecId = i.TrackerServiceSpecId,
            qi.ServiceName = LTRIM(RTRIM(i.ServiceName)),
            qi.Quantity = i.Quantity,
            qi.Amount = i.Amount,
            qi.VAT = i.VAT,
            qi.TaxableValue = CAST(i.Quantity * (i.Amount + i.VAT) AS DECIMAL(18,2))
        FROM dbo.QuotationItems qi
        INNER JOIN @Items i ON i.QuotationItemId = qi.QuotationItemId
        WHERE qi.QuotationId = @QuotationId;

        DELETE qi
        FROM dbo.QuotationItems qi
        WHERE qi.QuotationId = @QuotationId
          AND NOT EXISTS (SELECT 1 FROM @Items i WHERE i.QuotationItemId = qi.QuotationItemId);

        INSERT INTO dbo.QuotationItems
        (
            QuotationId, TrackerCategorySubCategorySpecId, TrackerServiceSpecId,
            ServiceName, Quantity, Amount, VAT, TaxableValue, CreatedDate
        )
        SELECT
            @QuotationId, i.TrackerCategorySubCategorySpecId, i.TrackerServiceSpecId,
            LTRIM(RTRIM(i.ServiceName)), i.Quantity, i.Amount, i.VAT, CAST(i.Quantity * (i.Amount + i.VAT) AS DECIMAL(18,2)),
            SYSUTCDATETIME()
        FROM @Items i
        WHERE i.QuotationItemId = 0;

        /* Remove only quotation-specific service specs that are no longer used by this quotation. */
        DELETE s
        FROM dbo.TrackerServiceSpecs s
        WHERE s.QuotationId = @QuotationId
          AND NOT EXISTS
          (
              SELECT 1 FROM dbo.QuotationItems qi
              WHERE qi.QuotationId = @QuotationId
                AND qi.TrackerServiceSpecId = s.TrackerServiceSpecId
          );

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF CURSOR_STATUS('local', 'create_cursor') >= -1
        BEGIN
            IF CURSOR_STATUS('local', 'create_cursor') > -1 CLOSE create_cursor;
            DEALLOCATE create_cursor;
        END;
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT QuotationId, TrackerId, QuotationMode, TrackerCategorySubCategorySpecId,
           TrackerCategorySubCategorySpecPersonId, CustomerCode, CustomerAddress,
           QuotationStatus, SenderInfo, Notes, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Quotations
    WHERE QuotationId = @QuotationId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @StatusName NVARCHAR(50);
    SELECT @StatusName=StatusName FROM dbo.QuotationStatuses WHERE QuotationStatusId=@Id;
    IF @StatusName IS NULL RETURN;

    IF EXISTS (SELECT 1 FROM dbo.Quotations WHERE LTRIM(RTRIM(QuotationStatus))=@StatusName)
        THROW 50030,'This quotation status cannot be deleted because it is already used by a quotation.',1;

    DELETE dbo.QuotationStatuses WHERE QuotationStatusId=@Id;
    SELECT @@ROWCOUNT AS Affected;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QuotationStatusId, StatusCode, StatusName, DisplayOrder, IsDefault, IsTerminal
    FROM dbo.QuotationStatuses
    WHERE IsActive=1
    ORDER BY DisplayOrder, QuotationStatusId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QuotationStatusId, StatusCode, StatusName, DisplayOrder,
           IsDefault, IsTerminal, IsActive, CreatedDate, ModifiedDate
    FROM dbo.QuotationStatuses
    WHERE QuotationStatusId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* --------------------------------------------------------------------------
   3. Stored procedures - CRUD
   -------------------------------------------------------------------------- */
CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    SELECT COUNT(1) AS TotalCount FROM dbo.QuotationStatuses;

    SELECT QuotationStatusId, StatusCode, StatusName, DisplayOrder,
           IsDefault, IsTerminal, IsActive, CreatedDate, ModifiedDate
    FROM dbo.QuotationStatuses
    ORDER BY DisplayOrder, QuotationStatusId
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_Insert]
    @StatusCode NVARCHAR(30),
    @StatusName NVARCHAR(50),
    @DisplayOrder INT,
    @IsDefault BIT,
    @IsTerminal BIT,
    @IsActive BIT
AS
BEGIN
    SET NOCOUNT ON;

    SET @StatusCode=UPPER(LTRIM(RTRIM(@StatusCode)));
    SET @StatusName=LTRIM(RTRIM(@StatusName));

    IF @StatusCode=N'' OR @StatusName=N'' THROW 50020,'Status Code and Display Name are required.',1;
    IF @DisplayOrder <= 0 THROW 50021,'Display Order must be greater than zero.',1;

    IF EXISTS (SELECT 1 FROM dbo.QuotationStatuses WHERE StatusCode=@StatusCode)
        THROW 50022,'Quotation Status Code already exists.',1;
    IF EXISTS (SELECT 1 FROM dbo.QuotationStatuses WHERE StatusName=@StatusName)
        THROW 50023,'Quotation Status Display Name already exists.',1;

    IF @IsDefault=1
        UPDATE dbo.QuotationStatuses SET IsDefault=0, ModifiedDate=SYSUTCDATETIME();

    INSERT dbo.QuotationStatuses(StatusCode,StatusName,DisplayOrder,IsDefault,IsTerminal,IsActive)
    VALUES(@StatusCode,@StatusName,@DisplayOrder,@IsDefault,@IsTerminal,@IsActive);

    SELECT QuotationStatusId, StatusCode, StatusName, DisplayOrder,
           IsDefault, IsTerminal, IsActive, CreatedDate, ModifiedDate
    FROM dbo.QuotationStatuses WHERE QuotationStatusId=CONVERT(INT,SCOPE_IDENTITY());
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatuses_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_QuotationStatuses_Update]
    @Id INT,
    @StatusCode NVARCHAR(30),
    @StatusName NVARCHAR(50),
    @DisplayOrder INT,
    @IsDefault BIT,
    @IsTerminal BIT,
    @IsActive BIT
AS
BEGIN
    SET NOCOUNT ON;

    SET @StatusCode=UPPER(LTRIM(RTRIM(@StatusCode)));
    SET @StatusName=LTRIM(RTRIM(@StatusName));

    DECLARE @OldCode NVARCHAR(30), @OldName NVARCHAR(50);
    SELECT @OldCode=StatusCode, @OldName=StatusName FROM dbo.QuotationStatuses WHERE QuotationStatusId=@Id;
    IF @OldCode IS NULL THROW 50024,'Quotation Status not found.',1;
    IF @StatusCode=N'' OR @StatusName=N'' THROW 50025,'Status Code and Display Name are required.',1;
    IF @DisplayOrder <= 0 THROW 50026,'Display Order must be greater than zero.',1;

    IF EXISTS (SELECT 1 FROM dbo.QuotationStatuses WHERE StatusCode=@StatusCode AND QuotationStatusId<>@Id)
        THROW 50027,'Quotation Status Code already exists.',1;
    IF EXISTS (SELECT 1 FROM dbo.QuotationStatuses WHERE StatusName=@StatusName AND QuotationStatusId<>@Id)
        THROW 50028,'Quotation Status Display Name already exists.',1;

    /* Once a status has been used by a quotation, its stored display value is
       historical data. Do not silently rename it and orphan that value. */
    IF EXISTS (SELECT 1 FROM dbo.Quotations WHERE LTRIM(RTRIM(QuotationStatus))=@OldName)
       AND @StatusName<>@OldName
        THROW 50029,'This status is already used by quotations. Display Name cannot be changed.',1;

    IF @IsDefault=1
        UPDATE dbo.QuotationStatuses SET IsDefault=0, ModifiedDate=SYSUTCDATETIME()
        WHERE QuotationStatusId<>@Id;

    UPDATE dbo.QuotationStatuses
       SET StatusCode=@StatusCode,
           StatusName=@StatusName,
           DisplayOrder=@DisplayOrder,
           IsDefault=@IsDefault,
           IsTerminal=@IsTerminal,
           IsActive=@IsActive,
           ModifiedDate=SYSUTCDATETIME()
     WHERE QuotationStatusId=@Id;

    SELECT QuotationStatusId, StatusCode, StatusName, DisplayOrder,
           IsDefault, IsTerminal, IsActive, CreatedDate, ModifiedDate
    FROM dbo.QuotationStatuses WHERE QuotationStatusId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatusHistory_GetByQuotation]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_QuotationStatusHistory_GetByQuotation] @QuotationId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.QuotationStatusHistoryId AS HistoryId, h.QuotationId,
           h.OldStatus, h.NewStatus,
           COALESCE(os.StatusName, h.OldStatus) AS OldStatusName,
           COALESCE(ns.StatusName, h.NewStatus) AS NewStatusName,
           h.ChangedBy, LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) AS ChangedByName,
           h.ChangedDate, h.Note
    FROM dbo.QuotationStatusHistory h
    -- Quotations.QuotationStatus stores the status NAME (e.g. 'Draft'); accept a code too.
    OUTER APPLY (SELECT TOP (1) q.StatusName FROM dbo.QuotationStatuses q
                 WHERE q.StatusName = h.OldStatus OR q.StatusCode = h.OldStatus
                 ORDER BY CASE WHEN q.StatusName = h.OldStatus THEN 0 ELSE 1 END) os
    OUTER APPLY (SELECT TOP (1) q.StatusName FROM dbo.QuotationStatuses q
                 WHERE q.StatusName = h.NewStatus OR q.StatusCode = h.NewStatus
                 ORDER BY CASE WHEN q.StatusName = h.NewStatus THEN 0 ELSE 1 END) ns
    LEFT JOIN dbo.Users u ON u.UserId = h.ChangedBy
    WHERE h.QuotationId = @QuotationId
    ORDER BY h.ChangedDate DESC, h.QuotationStatusHistoryId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_QuotationStatusHistory_GetByTracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE   PROCEDURE [dbo].[usp_QuotationStatusHistory_GetByTracker] @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.QuotationStatusHistoryId AS HistoryId, h.QuotationId,
           h.OldStatus, h.NewStatus,
           COALESCE(os.StatusName, h.OldStatus) AS OldStatusName,
           COALESCE(ns.StatusName, h.NewStatus) AS NewStatusName,
           h.ChangedBy, LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) AS ChangedByName,
           h.ChangedDate, h.Note
    FROM dbo.QuotationStatusHistory h
    -- Quotations.QuotationStatus stores the status NAME (e.g. 'Draft'); accept a code too.
    OUTER APPLY (SELECT TOP (1) q.StatusName FROM dbo.QuotationStatuses q
                 WHERE q.StatusName = h.OldStatus OR q.StatusCode = h.OldStatus
                 ORDER BY CASE WHEN q.StatusName = h.OldStatus THEN 0 ELSE 1 END) os
    OUTER APPLY (SELECT TOP (1) q.StatusName FROM dbo.QuotationStatuses q
                 WHERE q.StatusName = h.NewStatus OR q.StatusCode = h.NewStatus
                 ORDER BY CASE WHEN q.StatusName = h.NewStatus THEN 0 ELSE 1 END) ns
    LEFT JOIN dbo.Users u ON u.UserId = h.ChangedBy
    WHERE h.TrackerId = @TrackerId
    ORDER BY h.ChangedDate DESC, h.QuotationStatusHistoryId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_Delete] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM dbo.TrackerFamilyMembers WHERE RelationshipId=@Id)
        THROW 50072,'This Relationship is used by one or more family members and cannot be deleted.',1;
    DELETE FROM dbo.Relationships WHERE RelationshipId=@Id;
    IF @@ROWCOUNT=0 RETURN;
    SELECT 1;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT RelationshipId, RelationshipName, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Relationships WHERE IsActive=1 ORDER BY RelationshipName, RelationshipId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_GetById] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT RelationshipId, RelationshipName, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Relationships WHERE RelationshipId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    SELECT COUNT(1) AS TotalCount FROM dbo.Relationships;
    SELECT RelationshipId, RelationshipName, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Relationships
    ORDER BY RelationshipName, RelationshipId
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_Insert]
    @RelationshipName NVARCHAR(100), @IsActive BIT, @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @RelationshipName = LTRIM(RTRIM(@RelationshipName));
    IF NULLIF(@RelationshipName,N'') IS NULL THROW 50070,'Relationship Name is required.',1;
    IF EXISTS(SELECT 1 FROM dbo.Relationships WHERE LOWER(RelationshipName)=LOWER(@RelationshipName)) THROW 50071,'Relationship Name already exists.',1;
    INSERT dbo.Relationships(RelationshipName,IsActive,CreatedBy,CreatedDate)
    VALUES(@RelationshipName,@IsActive,@CreatedBy,SYSUTCDATETIME());
    DECLARE @Id INT=CONVERT(INT,SCOPE_IDENTITY());
    SELECT RelationshipId, RelationshipName, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Relationships WHERE RelationshipId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Relationships_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Relationships_Update]
    @Id INT, @RelationshipName NVARCHAR(100), @IsActive BIT, @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @RelationshipName = LTRIM(RTRIM(@RelationshipName));
    IF NOT EXISTS(SELECT 1 FROM dbo.Relationships WHERE RelationshipId=@Id) RETURN;
    IF NULLIF(@RelationshipName,N'') IS NULL THROW 50070,'Relationship Name is required.',1;
    IF EXISTS(SELECT 1 FROM dbo.Relationships WHERE RelationshipId<>@Id AND LOWER(RelationshipName)=LOWER(@RelationshipName)) THROW 50071,'Relationship Name already exists.',1;
    UPDATE dbo.Relationships SET RelationshipName=@RelationshipName,IsActive=@IsActive,ModifiedBy=@ModifiedBy,ModifiedDate=SYSUTCDATETIME() WHERE RelationshipId=@Id;
    SELECT RelationshipId, RelationshipName, IsActive, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
    FROM dbo.Relationships WHERE RelationshipId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Reports_GetDetailed]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 4. Reports ----------
   One procedure, 15 reports.  Each branch: filter rows into a temp table, compute the tiles / chart / insights from
   those same rows, and return one JSON document:
     { key, title, description, summary[], chart{}, insights[], columns[], rows[] }                                  */
CREATE   PROCEDURE [dbo].[usp_Reports_GetDetailed]
    @ReportKey  NVARCHAR(50),
    @CustomerId INT = NULL,
    @EmployeeId INT = NULL,
    @CategoryId INT = NULL,
    @Status     NVARCHAR(50) = NULL,
    @FromDate   DATE = NULL,
    @ToDate     DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Today DATE = CONVERT(DATE, GETDATE());
    DECLARE @k NVARCHAR(50) = LOWER(LTRIM(RTRIM(ISNULL(@ReportKey, N''))));
    SET @Status = NULLIF(LTRIM(RTRIM(ISNULL(@Status, N''))), N'');

    DECLARE @Title NVARCHAR(200) = N'', @Desc NVARCHAR(500) = N'';
    DECLARE @Summary NVARCHAR(MAX) = N'[]', @Chart NVARCHAR(MAX) = NULL, @Insights NVARCHAR(MAX) = N'[]',
            @Cols NVARCHAR(MAX) = N'[]', @Rows NVARCHAR(MAX) = N'[]';
    DECLARE @i1 INT, @i2 INT, @i3 INT, @i4 INT, @i5 INT, @i6 INT;
    DECLARE @d1 DECIMAL(18,2), @d2 DECIMAL(18,2), @d3 DECIMAL(18,2), @d4 DECIMAL(18,2), @d5 DECIMAL(18,2), @d6 DECIMAL(18,2);
    DECLARE @t1 NVARCHAR(300), @t2 NVARCHAR(300), @t3 NVARCHAR(300);

    /* ===================================================== 1. renewal-register */
    IF @k = N'renewal-register'
    BEGIN
        SELECT s.SpecId AS renewalId, CONCAT(N'#', s.TrackerId) AS tracker, s.CustomerName AS customer,
               CONCAT(s.CategoryName, N' / ', s.SubCategoryName) AS category, s.PersonName AS person,
               s.DueDate AS dueDate, DATEDIFF(DAY, @Today, s.DueDate) AS daysLeft, s.EmployeeName AS employee,
               s.StatusName AS status, s.StatusCode, s.IsOpen
        INTO #r1
        FROM dbo.vw_BP_Spec s
        WHERE (@CustomerId IS NULL OR s.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR s.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR s.CategoryId = @CategoryId) AND (@Status IS NULL OR s.StatusName = @Status) AND (@FromDate IS NULL OR s.DueDate >= @FromDate) AND (@ToDate IS NULL OR s.DueDate <= @ToDate);

        SELECT @i1 = COUNT(*),
               @i2 = ISNULL(SUM(CASE WHEN IsOpen = 1 AND daysLeft BETWEEN 0 AND 30 THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN IsOpen = 1 AND daysLeft < 0 THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN StatusCode = N'COMPLETED' THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN IsOpen = 1 AND daysLeft BETWEEN 0 AND 30 AND StatusCode IN (N'NEW', N'QUOTATION') THEN 1 ELSE 0 END), 0)
        FROM #r1;

        SET @Title = N'Renewal register';
        SET @Desc = N'Every category / subcategory with its due date and tracker status.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Renewals', CONVERT(NVARCHAR(40), @i1), N'in the current filter', N'', 1),
              (N'Due in 30 days', CONVERT(NVARCHAR(40), @i2), N'still open', N'amber', 2),
              (N'Overdue', CONVERT(NVARCHAR(40), @i3), N'past due date, not completed', N'red', 3),
              (N'Completed', CONVERT(NVARCHAR(40), @i4), N'tracker finished', N'green', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'By tracker status' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT ts.StatusName AS label, COUNT(r.renewalId) AS value
                        FROM dbo.TrackerStatuses ts LEFT JOIN #r1 r ON r.StatusCode = ts.StatusCode
                        WHERE ts.IsActive = 1 GROUP BY ts.StatusName, ts.DisplayOrder ORDER BY ts.DisplayOrder FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i3 > 0 THEN CONCAT(@i3, N' open renewals are already past their due date.') ELSE N'No open renewal is past its due date.' END, CASE WHEN @i3 > 0 THEN N'bad' ELSE N'good' END, 1),
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' renewals due within 30 days are still at New or Quotation stage.') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"tracker","label":"Tracker","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"category","label":"Category / Subcategory","type":"text"},{"key":"person","label":"Person","type":"text"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"daysLeft","label":"Days left","type":"days"},{"key":"employee","label":"Employee","type":"text"},{"key":"status","label":"Status","type":"status"}]';
        SET @Rows = (SELECT renewalId, tracker, customer, category, person, dueDate, daysLeft, employee, status FROM #r1 ORDER BY dueDate, renewalId FOR JSON PATH);
    END
    /* ===================================================== 2. upcoming-renewals */
    ELSE IF @k = N'upcoming-renewals'
    BEGIN
        SELECT s.SpecId AS renewalId, s.CustomerName AS customer, CONCAT(N'#', s.TrackerId) AS tracker,
               CONCAT(s.CategoryName, N' / ', s.SubCategoryName) AS category, s.PersonName AS person, s.DueDate AS dueDate,
               DATEDIFF(DAY, @Today, s.DueDate) AS daysLeft,
               CASE WHEN DATEDIFF(DAY, @Today, s.DueDate) <= 7 THEN N'High' WHEN DATEDIFF(DAY, @Today, s.DueDate) <= 30 THEN N'Medium' ELSE N'Low' END AS priority,
               s.EmployeeName AS employee, s.StatusCode
        INTO #r2
        FROM dbo.vw_BP_Spec s
        WHERE s.IsOpen = 1 AND s.DueDate >= @Today AND s.DueDate <= DATEADD(DAY, 90, @Today)
          AND (@CustomerId IS NULL OR s.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR s.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR s.CategoryId = @CategoryId) AND (@FromDate IS NULL OR s.DueDate >= @FromDate) AND (@ToDate IS NULL OR s.DueDate <= @ToDate);

        SELECT @i1 = ISNULL(SUM(CASE WHEN daysLeft = 0 THEN 1 ELSE 0 END), 0),
               @i2 = ISNULL(SUM(CASE WHEN daysLeft <= 7 THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN daysLeft <= 30 THEN 1 ELSE 0 END), 0),
               @i4 = COUNT(*),
               @i5 = ISNULL(SUM(CASE WHEN daysLeft <= 7 AND StatusCode IN (N'NEW', N'QUOTATION') THEN 1 ELSE 0 END), 0)
        FROM #r2;
        SET @t1 = (SELECT TOP (1) CONCAT(employee, N' (', COUNT(*), N')') FROM #r2 GROUP BY employee ORDER BY COUNT(*) DESC, employee);

        SET @Title = N'Upcoming renewals';
        SET @Desc = N'Open renewals due in the next 90 days, with priority.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Today', CONVERT(NVARCHAR(40), @i1), N'due today', N'red', 1),
              (N'Next 7 days', CONVERT(NVARCHAR(40), @i2), N'high priority', N'amber', 2),
              (N'Next 30 days', CONVERT(NVARCHAR(40), @i3), N'medium and high', N'', 3),
              (N'Next 90 days', CONVERT(NVARCHAR(40), @i4), N'all upcoming', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Renewals by time window' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT b.label, COUNT(r.renewalId) AS value
                        FROM (VALUES (N'0-7 days', 0, 7, 1), (N'8-30 days', 8, 30, 2), (N'31-60 days', 31, 60, 3), (N'61-90 days', 61, 90, 4)) b(label, lo, hi, ord)
                        LEFT JOIN #r2 r ON r.daysLeft BETWEEN b.lo AND b.hi
                        GROUP BY b.label, b.ord ORDER BY b.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CONCAT(@i2, N' renewals fall in the next 7 days; ', @i5, N' of them have not reached an accepted quotation.'), CASE WHEN @i5 > 0 THEN N'warn' ELSE N'info' END, 1),
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(N'Busiest owner: ', @t1, N'.') END, N'info', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"category","label":"Category / Subcategory","type":"text"},{"key":"person","label":"Person","type":"text"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"daysLeft","label":"Days left","type":"days"},{"key":"priority","label":"Priority","type":"priority"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT customer, tracker, category, person, dueDate, daysLeft, priority, employee FROM #r2 ORDER BY dueDate, renewalId FOR JSON PATH);
    END
    /* ===================================================== 3. overdue-ageing */
    ELSE IF @k = N'overdue-ageing'
    BEGIN
        SELECT s.SpecId AS renewalId, s.TrackerId, s.CustomerName AS customer, CONCAT(N'#', s.TrackerId) AS tracker,
               CONCAT(s.CategoryName, N' / ', s.SubCategoryName) AS category, s.DueDate AS dueDate,
               DATEDIFF(DAY, @Today, s.DueDate) AS daysLeft, ISNULL(qv.g, 0) AS quotedValue,
               s.StatusName AS status, s.StatusCode, s.EmployeeName AS employee
        INTO #r3
        FROM dbo.vw_BP_Spec s
        LEFT JOIN (SELECT TrackerId, SUM(Grand) AS g FROM dbo.vw_BP_Quotation GROUP BY TrackerId) qv ON qv.TrackerId = s.TrackerId
        WHERE s.IsOpen = 1 AND s.DueDate < @Today AND (@CustomerId IS NULL OR s.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR s.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR s.CategoryId = @CategoryId) AND (@FromDate IS NULL OR s.DueDate >= @FromDate) AND (@ToDate IS NULL OR s.DueDate <= @ToDate);

        SELECT @i1 = COUNT(*), @i2 = COUNT(DISTINCT TrackerId),
               @i3 = ISNULL(MIN(daysLeft), 0),
               @i4 = ISNULL(SUM(CASE WHEN daysLeft < -60 THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN StatusCode IN (N'NEW', N'QUOTATION') THEN 1 ELSE 0 END), 0)
        FROM #r3;
        SET @d1 = ISNULL((SELECT SUM(g) FROM (SELECT DISTINCT TrackerId, quotedValue AS g FROM #r3) z), 0);
        SET @t1 = (SELECT TOP (1) customer FROM #r3 ORDER BY dueDate, renewalId);

        SET @Title = N'Overdue and ageing';
        SET @Desc = N'Open renewals past their due date, grouped by how late they are.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Overdue renewals', CONVERT(NVARCHAR(40), @i1), CONCAT(@i2, N' trackers'), N'red', 1),
              (N'Quoted value at risk', dbo.fn_BP_Money(@d1), N'quotations on those trackers', N'', 2),
              (N'Longest overdue', CASE WHEN @i1 = 0 THEN N'-' ELSE CONCAT(-@i3, N' days') END, ISNULL(@t1, N''), N'amber', 3),
              (N'More than 60 days late', CONVERT(NVARCHAR(40), @i4), N'escalate', N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'How late' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT b.label, COUNT(r.renewalId) AS value
                        FROM (VALUES (N'1-15 days', 1, 15, 1), (N'16-30 days', 16, 30, 2), (N'31-60 days', 31, 60, 3), (N'60+ days', 61, 100000, 4)) b(label, lo, hi, ord)
                        LEFT JOIN #r3 r ON -r.daysLeft BETWEEN b.lo AND b.hi
                        GROUP BY b.label, b.ord ORDER BY b.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i1 = 0 THEN N'Nothing is overdue.' ELSE CONCAT(@i1, N' renewals are overdue across ', @i2, N' trackers.') END, CASE WHEN @i1 = 0 THEN N'good' ELSE N'bad' END, 1),
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' of them never reached an accepted quotation (still New or Quotation).') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"category","label":"Category / Subcategory","type":"text"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"daysLeft","label":"Days overdue","type":"days"},{"key":"quotedValue","label":"Tracker quoted value","type":"money"},{"key":"status","label":"Status","type":"status"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT customer, tracker, category, dueDate, daysLeft, quotedValue, status, employee FROM #r3 ORDER BY dueDate, renewalId FOR JSON PATH);
    END
    /* ===================================================== 4. renewal-performance  (tracker outcomes) */
    ELSE IF @k = N'renewal-performance'
    BEGIN
        SELECT t.EmployeeName AS employee, COUNT(*) AS total,
               SUM(CASE WHEN t.StatusCode = N'COMPLETED' THEN 1 ELSE 0 END) AS completed,
               SUM(CASE WHEN t.StatusCode = N'REJECTED' THEN 1 ELSE 0 END) AS rejected,
               SUM(CASE WHEN t.IsOpen = 1 THEN 1 ELSE 0 END) AS openCount
        INTO #r4
        FROM dbo.vw_BP_Tracker t
        WHERE (@CustomerId IS NULL OR t.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR t.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = t.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR t.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR t.CreatedDate <= @ToDate)
        GROUP BY t.EmployeeName;

        SELECT @i1 = ISNULL(SUM(total), 0), @i2 = ISNULL(SUM(completed), 0), @i3 = ISNULL(SUM(rejected), 0), @i4 = ISNULL(SUM(openCount), 0) FROM #r4;
        SET @t1 = (SELECT TOP (1) CONCAT(employee, N' has the highest completion rate at ', CONVERT(INT, ROUND(completed * 100.0 / NULLIF(total, 0), 0)), N'%.') FROM #r4 WHERE total > 0 ORDER BY completed * 1.0 / NULLIF(total, 0) DESC, employee);

        SET @Title = N'Tracker outcomes';
        SET @Desc = N'Completed, rejected and open trackers by employee.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Trackers', CONVERT(NVARCHAR(40), @i1), N'created in range', N'', 1),
              (N'Completed', CONVERT(NVARCHAR(40), @i2), CONCAT(CASE WHEN @i1 = 0 THEN 0 ELSE CONVERT(INT, ROUND(@i2 * 100.0 / @i1, 0)) END, N'% of all'), N'green', 2),
              (N'Rejected', CONVERT(NVARCHAR(40), @i3), CONCAT(CASE WHEN @i1 = 0 THEN 0 ELSE CONVERT(INT, ROUND(@i3 * 100.0 / @i1, 0)) END, N'% of all'), N'red', 3),
              (N'Still open', CONVERT(NVARCHAR(40), @i4), N'in progress', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Completion rate by employee' AS title, N'bars' AS kind, 0 AS money, 1 AS pct,
            JSON_QUERY((SELECT employee AS label, CASE WHEN total = 0 THEN 0 ELSE CONVERT(INT, ROUND(completed * 100.0 / total, 0)) END AS value
                        FROM #r4 ORDER BY completed * 1.0 / NULLIF(total, 0) DESC, employee FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (ISNULL(@t1, N''), N'good', 1),
              (CASE WHEN @i3 > 0 THEN CONCAT(@i3, N' trackers ended Rejected - review those quotations for the reason.') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"employee","label":"Employee","type":"text"},{"key":"total","label":"Trackers","type":"num","total":true},{"key":"completed","label":"Completed","type":"num","total":true},{"key":"rejected","label":"Rejected","type":"num","total":true},{"key":"openCount","label":"Open","type":"num","total":true},{"key":"rate","label":"Completion %","type":"pct"}]';
        SET @Rows = (SELECT employee, total, completed, rejected, openCount,
                            CASE WHEN total = 0 THEN 0 ELSE CONVERT(INT, ROUND(completed * 100.0 / total, 0)) END AS rate
                     FROM #r4 ORDER BY total DESC, employee FOR JSON PATH);
    END
    /* ===================================================== 5. quotation-pipeline */
    ELSE IF @k = N'quotation-pipeline'
    BEGIN
        SELECT q.QuotationId, CONCAT(N'BP-QT-', q.QuotationId) AS quotation, q.CustomerName AS customer, CONCAT(N'#', q.TrackerId) AS tracker,
               COALESCE(q.PersonName, CASE WHEN q.SubMin = q.SubMax THEN q.SubMin ELSE N'Multiple' END) AS forWho,
               q.CreatedDate AS created, DATEDIFF(DAY, q.CreatedDate, @Today) AS ageDays, q.Grand AS value,
               CASE q.StatusKey WHEN 'DRAFT' THEN N'Draft' WHEN 'SENT' THEN N'Sent' WHEN 'NEGOTIATE' THEN N'Negotiation' WHEN 'ACCEPTED' THEN N'Accepted' ELSE N'Rejected' END AS status,
               q.StatusKey, q.EmployeeName AS employee, q.LastTouched
        INTO #r5
        FROM dbo.vw_BP_Quotation q
        WHERE (@CustomerId IS NULL OR q.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR q.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.QuotationItems zi INNER JOIN dbo.TrackerCategorySubCategorySpecs zs ON zs.TrackerCategorySubCategorySpecId = zi.TrackerCategorySubCategorySpecId WHERE zi.QuotationId = q.QuotationId AND zs.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR q.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR q.CreatedDate <= @ToDate);
        DELETE FROM #r5 WHERE @Status IS NOT NULL AND status <> @Status;

        SELECT @i1 = COUNT(*),
               @i2 = ISNULL(SUM(CASE WHEN StatusKey IN ('DRAFT', 'SENT', 'NEGOTIATE') THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN StatusKey = 'ACCEPTED' THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN StatusKey = 'REJECTED' THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN StatusKey IN ('SENT', 'NEGOTIATE') AND DATEDIFF(DAY, LastTouched, @Today) >= 7 THEN 1 ELSE 0 END), 0),
               @d1 = ISNULL(SUM(value), 0),
               @d2 = ISNULL(SUM(CASE WHEN StatusKey IN ('DRAFT', 'SENT', 'NEGOTIATE') THEN value ELSE 0 END), 0),
               @d3 = ISNULL(SUM(CASE WHEN StatusKey = 'ACCEPTED' THEN value ELSE 0 END), 0)
        FROM #r5;

        SET @Title = N'Quotation pipeline';
        SET @Desc = N'Every quotation with its status, value and age.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Quotations', CONVERT(NVARCHAR(40), @i1), CONCAT(dbo.fn_BP_Money(@d1), N' quoted'), N'', 1),
              (N'Open pipeline', dbo.fn_BP_Money(@d2), CONCAT(@i2, N' draft / sent / negotiating'), N'amber', 2),
              (N'Accepted', dbo.fn_BP_Money(@d3), CONCAT(@i3, N' quotations'), N'green', 3),
              (N'Win rate', CONCAT(CASE WHEN @i3 + @i4 = 0 THEN 0 ELSE CONVERT(INT, ROUND(@i3 * 100.0 / (@i3 + @i4), 0)) END, N'%'), N'accepted / decided', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Quotations by status' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT k.name AS label, COUNT(r.QuotationId) AS value, dbo.fn_BP_Money(ISNULL(SUM(r.value), 0)) AS sub
                        FROM (VALUES (N'Draft', 1), (N'Sent', 2), (N'Negotiation', 3), (N'Accepted', 4), (N'Rejected', 5)) k(name, ord)
                        LEFT JOIN #r5 r ON r.status = k.name GROUP BY k.name, k.ord ORDER BY k.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' sent quotations have waited 7+ days for an answer.') ELSE N'No sent quotation is waiting more than 7 days.' END, CASE WHEN @i5 > 0 THEN N'warn' ELSE N'good' END, 1),
              (CASE WHEN @i3 > 0 THEN CONCAT(N'Average accepted quotation is ', dbo.fn_BP_Money(@d3 / @i3), N'.') ELSE N'' END, N'info', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"quotation","label":"Quotation","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"forWho","label":"For","type":"text"},{"key":"created","label":"Created","type":"date"},{"key":"ageDays","label":"Age (days)","type":"num"},{"key":"value","label":"Value (incl. VAT)","type":"money","total":true},{"key":"status","label":"Status","type":"qstatus"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT quotation, customer, tracker, forWho, created, ageDays, value, status, employee FROM #r5 ORDER BY created DESC, QuotationId DESC FOR JSON PATH);
    END
    /* ===================================================== 6. quotation-category */
    ELSE IF @k = N'quotation-category'
    BEGIN
        SELECT sp.CategoryName AS cat, sp.SubCategoryName AS sub, q.QuotationId, q.StatusKey,
               SUM((qi.Amount + qi.VAT) * qi.Quantity) AS val
        INTO #r6a
        FROM dbo.QuotationItems qi
        INNER JOIN dbo.vw_BP_Quotation q ON q.QuotationId = qi.QuotationId
        INNER JOIN dbo.TrackerCategorySubCategorySpecs sp ON sp.TrackerCategorySubCategorySpecId = qi.TrackerCategorySubCategorySpecId
        WHERE (@CustomerId IS NULL OR q.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR q.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR sp.CategoryId = @CategoryId) AND (@FromDate IS NULL OR q.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR q.CreatedDate <= @ToDate)
        GROUP BY sp.CategoryName, sp.SubCategoryName, q.QuotationId, q.StatusKey;

        SELECT cat, sub, COUNT(*) AS quotations,
               SUM(CASE WHEN StatusKey = 'ACCEPTED' THEN 1 ELSE 0 END) AS accepted,
               SUM(CASE WHEN StatusKey IN ('ACCEPTED', 'REJECTED') THEN 1 ELSE 0 END) AS decided,
               SUM(val) AS quoted,
               SUM(CASE WHEN StatusKey = 'ACCEPTED' THEN val ELSE 0 END) AS acceptedValue
        INTO #r6
        FROM #r6a GROUP BY cat, sub;

        SELECT @i1 = COUNT(DISTINCT cat), @i2 = COUNT(*), @d1 = ISNULL(SUM(acceptedValue), 0) FROM #r6;
        SELECT TOP (1) @t1 = sub, @d2 = quoted FROM #r6 ORDER BY quoted DESC, sub;
        SET @t2 = (SELECT TOP (1) CONCAT(sub, N' has the best win rate at ', CONVERT(INT, ROUND(accepted * 100.0 / decided, 0)), N'%.') FROM #r6 WHERE decided > 0 ORDER BY accepted * 1.0 / decided DESC, sub);

        SET @Title = N'Quotation by category';
        SET @Desc = N'Count, value and win rate per category and subcategory.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Categories', CONVERT(NVARCHAR(40), @i1), N'with quotations', N'', 1),
              (N'Subcategories', CONVERT(NVARCHAR(40), @i2), N'in use', N'', 2),
              (N'Top by value', ISNULL(@t1, N'-'), CASE WHEN @t1 IS NULL THEN N'' ELSE dbo.fn_BP_Money(@d2) END, N'', 3),
              (N'Accepted value', dbo.fn_BP_Money(@d1), N'across all categories', N'green', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Quoted value by subcategory' AS title, N'bars' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT TOP (8) sub AS label, quoted AS value FROM #r6 ORDER BY quoted DESC, sub FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'No quotations in this range.' ELSE CONCAT(@t1, N' brings the most quoted value (', dbo.fn_BP_Money(@d2), N').') END, N'info', 1),
              (ISNULL(@t2, N''), N'good', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"category","label":"Category","type":"text"},{"key":"subcategory","label":"Subcategory","type":"text"},{"key":"quotations","label":"Quotations","type":"num","total":true},{"key":"accepted","label":"Accepted","type":"num","total":true},{"key":"quoted","label":"Quoted value","type":"money","total":true},{"key":"acceptedValue","label":"Accepted value","type":"money","total":true},{"key":"winRate","label":"Win rate","type":"pct"}]';
        SET @Rows = (SELECT cat AS category, sub AS subcategory, quotations, accepted, quoted, acceptedValue,
                            CASE WHEN decided = 0 THEN 0 ELSE CONVERT(INT, ROUND(accepted * 100.0 / decided, 0)) END AS winRate
                     FROM #r6 ORDER BY quoted DESC, sub FOR JSON PATH);
    END
    /* ===================================================== 7. quotation-performance */
    ELSE IF @k = N'quotation-performance'
    BEGIN
        SELECT q.EmployeeName AS employee, COUNT(*) AS quotations,
               SUM(CASE WHEN q.StatusKey = 'ACCEPTED' THEN 1 ELSE 0 END) AS accepted,
               SUM(CASE WHEN q.StatusKey = 'REJECTED' THEN 1 ELSE 0 END) AS rejected,
               SUM(q.Grand) AS quoted,
               SUM(CASE WHEN q.StatusKey = 'ACCEPTED' THEN q.Grand ELSE 0 END) AS acceptedValue,
               CONVERT(DECIMAL(9,1), ISNULL(AVG(CASE WHEN q.StatusKey = 'ACCEPTED' THEN DATEDIFF(DAY, q.CreatedDate, q.LastTouched) * 1.0 END), 0)) AS avgDays
        INTO #r7
        FROM dbo.vw_BP_Quotation q
        WHERE (@CustomerId IS NULL OR q.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR q.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.QuotationItems zi INNER JOIN dbo.TrackerCategorySubCategorySpecs zs ON zs.TrackerCategorySubCategorySpecId = zi.TrackerCategorySubCategorySpecId WHERE zi.QuotationId = q.QuotationId AND zs.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR q.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR q.CreatedDate <= @ToDate)
        GROUP BY q.EmployeeName;

        SELECT @i1 = ISNULL(SUM(quotations), 0), @i2 = ISNULL(SUM(accepted), 0), @i3 = ISNULL(SUM(rejected), 0), @d1 = ISNULL(SUM(acceptedValue), 0) FROM #r7;
        SET @t1 = (SELECT TOP (1) CONCAT(employee, N' closes fastest: ', avgDays, N' days from creation to acceptance.') FROM #r7 WHERE avgDays > 0 ORDER BY avgDays, employee);

        SET @Title = N'Quotation performance';
        SET @Desc = N'Quotations, wins and turnaround by employee.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Quotations', CONVERT(NVARCHAR(40), @i1), N'created', N'', 1),
              (N'Accepted', CONVERT(NVARCHAR(40), @i2), dbo.fn_BP_Money(@d1), N'green', 2),
              (N'Rejected', CONVERT(NVARCHAR(40), @i3), N'lost', N'red', 3),
              (N'Win rate', CONCAT(CASE WHEN @i2 + @i3 = 0 THEN 0 ELSE CONVERT(INT, ROUND(@i2 * 100.0 / (@i2 + @i3), 0)) END, N'%'), N'accepted / decided', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Accepted value by employee' AS title, N'bars' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT employee AS label, acceptedValue AS value FROM #r7 ORDER BY acceptedValue DESC, employee FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES (ISNULL(@t1, N''), N'good', 1)) v([text], tone, ord) WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"employee","label":"Employee","type":"text"},{"key":"quotations","label":"Quotations","type":"num","total":true},{"key":"accepted","label":"Accepted","type":"num","total":true},{"key":"rejected","label":"Rejected","type":"num","total":true},{"key":"quoted","label":"Quoted","type":"money","total":true},{"key":"acceptedValue","label":"Accepted value","type":"money","total":true},{"key":"avgDays","label":"Avg days to accept","type":"num"},{"key":"winRate","label":"Win rate","type":"pct"}]';
        SET @Rows = (SELECT employee, quotations, accepted, rejected, quoted, acceptedValue, avgDays,
                            CASE WHEN accepted + rejected = 0 THEN 0 ELSE CONVERT(INT, ROUND(accepted * 100.0 / (accepted + rejected), 0)) END AS winRate
                     FROM #r7 ORDER BY acceptedValue DESC, employee FOR JSON PATH);
    END
    /* ===================================================== 8. invoice-register */
    ELSE IF @k = N'invoice-register'
    BEGIN
        SELECT x.* INTO #r8
        FROM (SELECT i.InvoiceId, i.InvoiceNumber AS invoice, i.CustomerName AS customer, CONCAT(N'#', i.TrackerId) AS tracker,
                     i.InvoiceDate AS invoiceDate, i.DueDate AS dueDate, i.NetAmount AS net, i.VatAmount AS vat, i.GrandTotal AS total,
                     i.Paid AS paid, i.Balance AS balance,
                     CASE WHEN i.Balance <= 0.004 THEN N'Paid' WHEN i.DueDate < @Today THEN N'Overdue' WHEN i.Paid > 0 THEN N'Partly paid' ELSE N'Unpaid' END AS state
              FROM dbo.vw_BP_Invoice i
              WHERE (@CustomerId IS NULL OR i.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR i.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = i.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR i.InvoiceDate >= @FromDate) AND (@ToDate IS NULL OR i.InvoiceDate <= @ToDate)) x
        WHERE (@Status IS NULL OR x.state = @Status);

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(CASE WHEN state = N'Overdue' THEN 1 ELSE 0 END), 0),
               @d1 = ISNULL(SUM(total), 0), @d2 = ISNULL(SUM(vat), 0), @d3 = ISNULL(SUM(paid), 0),
               @d4 = ISNULL(SUM(CASE WHEN balance > 0 THEN balance ELSE 0 END), 0),
               @d5 = ISNULL(SUM(CASE WHEN state = N'Overdue' THEN balance ELSE 0 END), 0)
        FROM #r8;

        SET @Title = N'Invoice register';
        SET @Desc = N'Invoices with paid amount, balance and payment state.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Invoices', CONVERT(NVARCHAR(40), @i1), N'in the current filter', N'', 1),
              (N'Invoiced', dbo.fn_BP_Money(@d1), CONCAT(N'VAT ', dbo.fn_BP_Money(@d2)), N'', 2),
              (N'Collected', dbo.fn_BP_Money(@d3), CONCAT(CASE WHEN @d1 <= 0 THEN 0 ELSE CONVERT(INT, ROUND(@d3 * 100.0 / @d1, 0)) END, N'% of invoiced'), N'green', 3),
              (N'Outstanding', dbo.fn_BP_Money(@d4), CONCAT(@i2, N' overdue'), N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Invoices by state' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT k.name AS label, COUNT(r.InvoiceId) AS value
                        FROM (VALUES (N'Paid', 1), (N'Partly paid', 2), (N'Unpaid', 3), (N'Overdue', 4)) k(name, ord)
                        LEFT JOIN #r8 r ON r.state = k.name GROUP BY k.name, k.ord ORDER BY k.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i2 > 0 THEN CONCAT(@i2, N' invoices are past their due date, worth ', dbo.fn_BP_Money(@d5), N'.') ELSE N'No invoice is overdue.' END, CASE WHEN @i2 > 0 THEN N'bad' ELSE N'good' END, 1),
              (CONCAT(CASE WHEN @d1 <= 0 THEN 0 ELSE CONVERT(INT, ROUND(@d3 * 100.0 / @d1, 0)) END, N'% of invoiced value has been collected.'), N'info', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"invoice","label":"Invoice","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"invoiceDate","label":"Invoice date","type":"date"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"net","label":"Net","type":"money","total":true},{"key":"vat","label":"VAT","type":"money","total":true},{"key":"total","label":"Total","type":"money","total":true},{"key":"paid","label":"Paid","type":"money","total":true},{"key":"balance","label":"Balance","type":"money","total":true},{"key":"state","label":"State","type":"invstate"}]';
        SET @Rows = (SELECT invoice, customer, tracker, invoiceDate, dueDate, net, vat, total, paid, balance, state FROM #r8 ORDER BY invoiceDate DESC, InvoiceId DESC FOR JSON PATH);
    END
    /* ===================================================== 9. payment-collection */
    ELSE IF @k = N'payment-collection'
    BEGIN
        SELECT p.PaymentId, p.PaymentNumber AS receipt, p.PaymentDate AS paymentDate, p.CustomerName AS customer,
               CONCAT(N'BP-QT-', p.QuotationId) AS quotation, p.MethodName AS method, p.AmountDue AS balanceBefore,
               p.PaymentAmount AS received, p.PaymentStatus AS status
        INTO #r9
        FROM dbo.vw_BP_Payment p
        WHERE (@CustomerId IS NULL OR p.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR p.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = p.TrackerId AND z.CategoryId = @CategoryId)) AND (@Status IS NULL OR p.PaymentStatus = @Status) AND (@FromDate IS NULL OR p.PaymentDate >= @FromDate) AND (@ToDate IS NULL OR p.PaymentDate <= @ToDate);

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(CASE WHEN status = N'Partial' THEN 1 ELSE 0 END), 0), @d1 = ISNULL(SUM(received), 0) FROM #r9;
        SELECT @d2 = ISNULL(SUM(i.GrandTotal), 0), @d3 = ISNULL(SUM(i.Paid), 0)
        FROM dbo.vw_BP_Invoice i WHERE (@CustomerId IS NULL OR i.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR i.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = i.TrackerId AND z.CategoryId = @CategoryId));
        SET @t1 = (SELECT TOP (1) CONCAT(method, N' (', dbo.fn_BP_Money(SUM(received)), N')') FROM #r9 GROUP BY method ORDER BY SUM(received) DESC, method);

        SET @Title = N'Payment collection';
        SET @Desc = N'Receipts, billed versus received, and collection rate.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Billed', dbo.fn_BP_Money(@d2), N'all invoices', N'', 1),
              (N'Received (receipts shown)', dbo.fn_BP_Money(@d1), CONCAT(@i1, N' receipts'), N'green', 2),
              (N'Outstanding', dbo.fn_BP_Money(@d2 - @d3), N'billed minus received', N'red', 3),
              (N'Collection rate', CONCAT(CASE WHEN @d2 <= 0 THEN 0 ELSE CONVERT(INT, ROUND(@d3 * 100.0 / @d2, 0)) END, N'%'), N'received / billed', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Received per month' AS title, N'cols' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT FORMAT(m.d, N'MMM', N'en-US') AS label,
                               (SELECT ISNULL(SUM(r.received), 0) FROM #r9 r WHERE r.paymentDate >= m.d AND r.paymentDate < DATEADD(MONTH, 1, m.d)) AS value
                        FROM (SELECT DATEADD(MONTH, v.n, DATEADD(MONTH, -5, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1))) AS d FROM (VALUES (0),(1),(2),(3),(4),(5)) v(n)) m
                        ORDER BY m.d FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(N'Most-used payment method: ', @t1, N'.') END, N'info', 1),
              (CASE WHEN @i2 > 0 THEN CONCAT(@i2, N' receipts were part-payments.') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"receipt","label":"Receipt","type":"text"},{"key":"paymentDate","label":"Date","type":"date"},{"key":"customer","label":"Customer","type":"text"},{"key":"quotation","label":"Quotation","type":"text"},{"key":"method","label":"Method","type":"text"},{"key":"balanceBefore","label":"Balance before","type":"money"},{"key":"received","label":"Received","type":"money","total":true},{"key":"status","label":"Status","type":"paystate"}]';
        SET @Rows = (SELECT receipt, paymentDate, customer, quotation, method, balanceBefore, received, status FROM #r9 ORDER BY paymentDate DESC, PaymentId DESC FOR JSON PATH);
    END
    /* ===================================================== 10. customer-outstanding */
    ELSE IF @k = N'customer-outstanding'
    BEGIN
        SELECT i.CustomerName AS customer, COUNT(*) AS openInvoices, SUM(i.GrandTotal) AS billed, SUM(i.Balance) AS outstanding,
               SUM(CASE WHEN i.DueDate < @Today THEN i.Balance ELSE 0 END) AS overdue,
               MAX(DATEDIFF(DAY, i.InvoiceDate, @Today)) AS oldestDays
        INTO #r10a
        FROM dbo.vw_BP_Invoice i
        WHERE i.Balance > 0.004 AND (@CustomerId IS NULL OR i.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR i.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = i.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR i.InvoiceDate >= @FromDate) AND (@ToDate IS NULL OR i.InvoiceDate <= @ToDate)
        GROUP BY i.CustomerName;
        SELECT a.*, CASE WHEN a.overdue > 0 AND a.oldestDays > 60 THEN N'High' WHEN a.overdue > 0 THEN N'Medium' ELSE N'Low' END AS risk
        INTO #r10 FROM #r10a a;
        DELETE FROM #r10 WHERE @Status IS NOT NULL AND risk <> @Status;

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(CASE WHEN risk = N'High' THEN 1 ELSE 0 END), 0), @i3 = ISNULL(SUM(CASE WHEN overdue > 0 THEN 1 ELSE 0 END), 0),
               @d1 = ISNULL(SUM(outstanding), 0), @d2 = ISNULL(SUM(overdue), 0)
        FROM #r10;
        SELECT TOP (1) @t1 = customer, @d3 = outstanding FROM #r10 ORDER BY outstanding DESC, customer;

        SET @Title = N'Customer outstanding';
        SET @Desc = N'Unpaid balance per customer, with overdue amount and risk.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Customers owing', CONVERT(NVARCHAR(40), @i1), N'with an open balance', N'', 1),
              (N'Outstanding', dbo.fn_BP_Money(@d1), N'unpaid invoices', N'amber', 2),
              (N'Overdue', dbo.fn_BP_Money(@d2), N'past due date', N'red', 3),
              (N'High risk', CONVERT(NVARCHAR(40), @i2), N'overdue and older than 60 days', N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Top balances' AS title, N'bars' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT TOP (8) customer AS label, outstanding AS value, CASE WHEN overdue > 0 THEN N'overdue' ELSE N'' END AS sub FROM #r10 ORDER BY outstanding DESC, customer FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'Nothing is outstanding.' ELSE CONCAT(@t1, N' owes the most: ', dbo.fn_BP_Money(@d3), N'.') END, CASE WHEN @t1 IS NULL THEN N'good' ELSE N'warn' END, 1),
              (CASE WHEN @i1 > 0 THEN CONCAT(@i3, N' of ', @i1, N' customers have at least one overdue invoice.') ELSE N'' END, N'bad', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"customer","label":"Customer","type":"text"},{"key":"openInvoices","label":"Open invoices","type":"num","total":true},{"key":"billed","label":"Billed","type":"money","total":true},{"key":"outstanding","label":"Outstanding","type":"money","total":true},{"key":"overdue","label":"Overdue","type":"money","total":true},{"key":"oldestDays","label":"Oldest (days)","type":"num"},{"key":"risk","label":"Risk","type":"priority"}]';
        SET @Rows = (SELECT customer, openInvoices, billed, outstanding, overdue, oldestDays, risk FROM #r10 ORDER BY outstanding DESC, customer FOR JSON PATH);
    END
    /* ===================================================== 11. payment-ageing */
    ELSE IF @k = N'payment-ageing'
    BEGIN
        SELECT i.InvoiceId, i.InvoiceNumber AS invoice, i.CustomerName AS customer, i.InvoiceDate AS invoiceDate, i.DueDate AS dueDate,
               DATEDIFF(DAY, i.InvoiceDate, @Today) AS ageDays, i.Balance AS balance,
               CASE WHEN DATEDIFF(DAY, i.InvoiceDate, @Today) <= 30 THEN N'0-30 days' WHEN DATEDIFF(DAY, i.InvoiceDate, @Today) <= 60 THEN N'31-60 days'
                    WHEN DATEDIFF(DAY, i.InvoiceDate, @Today) <= 90 THEN N'61-90 days' ELSE N'90+ days' END AS bucket
        INTO #r11
        FROM dbo.vw_BP_Invoice i
        WHERE i.Balance > 0.004 AND (@CustomerId IS NULL OR i.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR i.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = i.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR i.InvoiceDate >= @FromDate) AND (@ToDate IS NULL OR i.InvoiceDate <= @ToDate);
        DELETE FROM #r11 WHERE @Status IS NOT NULL AND bucket <> @Status;

        SELECT @d1 = ISNULL(SUM(CASE WHEN bucket = N'0-30 days' THEN balance END), 0), @d2 = ISNULL(SUM(CASE WHEN bucket = N'31-60 days' THEN balance END), 0),
               @d3 = ISNULL(SUM(CASE WHEN bucket = N'61-90 days' THEN balance END), 0), @d4 = ISNULL(SUM(CASE WHEN bucket = N'90+ days' THEN balance END), 0),
               @i1 = ISNULL(SUM(CASE WHEN bucket = N'0-30 days' THEN 1 END), 0), @i2 = ISNULL(SUM(CASE WHEN bucket = N'31-60 days' THEN 1 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN bucket = N'61-90 days' THEN 1 END), 0), @i4 = ISNULL(SUM(CASE WHEN bucket = N'90+ days' THEN 1 END), 0)
        FROM #r11;
        SET @d5 = @d1 + @d2 + @d3 + @d4;

        SET @Title = N'Receivables ageing';
        SET @Desc = N'Open invoice balances by days since the invoice date.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'0-30 days', dbo.fn_BP_Money(@d1), CONCAT(@i1, N' invoices'), N'', 1),
              (N'31-60 days', dbo.fn_BP_Money(@d2), CONCAT(@i2, N' invoices'), N'amber', 2),
              (N'61-90 days', dbo.fn_BP_Money(@d3), CONCAT(@i3, N' invoices'), N'red', 3),
              (N'90+ days', dbo.fn_BP_Money(@d4), CONCAT(@i4, N' invoices'), N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Balance by age bucket' AS title, N'cols' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT label, value FROM (VALUES (N'0-30 days', @d1, 1), (N'31-60 days', @d2, 2), (N'61-90 days', @d3, 3), (N'90+ days', @d4, 4)) v(label, value, ord) ORDER BY ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @d5 <= 0 THEN N'No open receivables.' ELSE CONCAT(CONVERT(INT, ROUND((@d3 + @d4) * 100.0 / @d5, 0)), N'% of the open balance is older than 60 days.') END, CASE WHEN @d3 + @d4 > 0 THEN N'bad' ELSE N'good' END, 1),
              (CONCAT(N'Open receivables total ', dbo.fn_BP_Money(@d5), N' across ', @i1 + @i2 + @i3 + @i4, N' invoices.'), N'info', 2)) v([text], tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"invoice","label":"Invoice","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"invoiceDate","label":"Invoice date","type":"date"},{"key":"dueDate","label":"Due date","type":"date"},{"key":"ageDays","label":"Age (days)","type":"num"},{"key":"bucket","label":"Bucket","type":"text"},{"key":"balance","label":"Balance","type":"money","total":true}]';
        SET @Rows = (SELECT invoice, customer, invoiceDate, dueDate, ageDays, bucket, balance FROM #r11 ORDER BY invoiceDate, InvoiceId FOR JSON PATH);
    END
    /* ===================================================== 12. document-register */
    ELSE IF @k = N'document-register'
    BEGIN
        SELECT d.DocumentId, d.DocumentName AS document, d.CustomerName AS customer, d.TrackerId, CONCAT(N'#', d.TrackerId) AS tracker,
               d.ReferenceNo AS reference, d.DocumentDate AS documentDate, d.ExpiryDate AS expiryDate, d.StatusName AS status
        INTO #r12
        FROM dbo.vw_BP_Document d
        WHERE (@CustomerId IS NULL OR d.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR d.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = d.TrackerId AND z.CategoryId = @CategoryId)) AND (@Status IS NULL OR d.StatusName = @Status) AND (@FromDate IS NULL OR d.DocumentDate >= @FromDate) AND (@ToDate IS NULL OR d.DocumentDate <= @ToDate);

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(CASE WHEN status = N'Success' THEN 1 ELSE 0 END), 0), @i3 = ISNULL(SUM(CASE WHEN status = N'Pending' THEN 1 ELSE 0 END), 0),
               @i4 = ISNULL(SUM(CASE WHEN status = N'New' THEN 1 ELSE 0 END), 0), @i5 = ISNULL(SUM(CASE WHEN expiryDate IS NULL THEN 1 ELSE 0 END), 0),
               @i6 = COUNT(DISTINCT CASE WHEN status = N'Pending' THEN TrackerId END)
        FROM #r12;

        SET @Title = N'Document register';
        SET @Desc = N'All tracker documents with reference, dates and status.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Documents', CONVERT(NVARCHAR(40), @i1), N'in the current filter', N'', 1),
              (N'Success', CONVERT(NVARCHAR(40), @i2), CONCAT(CASE WHEN @i1 = 0 THEN 0 ELSE CONVERT(INT, ROUND(@i2 * 100.0 / @i1, 0)) END, N'% done'), N'green', 2),
              (N'Pending', CONVERT(NVARCHAR(40), @i3), N'block completion', N'amber', 3),
              (N'New', CONVERT(NVARCHAR(40), @i4), N'not yet submitted', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Documents by status' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT k.name AS label, COUNT(r.DocumentId) AS value
                        FROM (VALUES (N'Success', 1), (N'Pending', 2), (N'New', 3)) k(name, ord)
                        LEFT JOIN #r12 r ON r.status = k.name GROUP BY k.name, k.ord ORDER BY k.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i6 > 0 THEN CONCAT(@i6, N' trackers cannot complete because a document is still Pending.') ELSE N'No tracker is blocked by a Pending document.' END, CASE WHEN @i6 > 0 THEN N'warn' ELSE N'good' END, 1),
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' documents have no expiry date recorded.') ELSE N'' END, N'info', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"document","label":"Document","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"reference","label":"Reference","type":"text"},{"key":"documentDate","label":"Document date","type":"date"},{"key":"expiryDate","label":"Expiry","type":"date"},{"key":"status","label":"Status","type":"docstatus"}]';
        SET @Rows = (SELECT document, customer, tracker, reference, documentDate, expiryDate, status FROM #r12 ORDER BY documentDate DESC, DocumentId DESC FOR JSON PATH);
    END
    /* ===================================================== 13. document-expiry */
    ELSE IF @k = N'document-expiry'
    BEGIN
        SELECT d.DocumentId, d.DocumentName AS document, d.CustomerName AS customer, CONCAT(N'#', d.TrackerId) AS tracker,
               d.ExpiryDate AS expiryDate, DATEDIFF(DAY, @Today, d.ExpiryDate) AS daysLeft, d.StatusName AS status, d.EmployeeName AS employee
        INTO #r13
        FROM dbo.vw_BP_Document d
        WHERE d.ExpiryDate IS NOT NULL AND d.ExpiryDate <= DATEADD(DAY, 90, @Today)
          AND (@CustomerId IS NULL OR d.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR d.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = d.TrackerId AND z.CategoryId = @CategoryId)) AND (@Status IS NULL OR d.StatusName = @Status) AND (@FromDate IS NULL OR d.ExpiryDate >= @FromDate) AND (@ToDate IS NULL OR d.ExpiryDate <= @ToDate);

        SELECT @i1 = ISNULL(SUM(CASE WHEN daysLeft < 0 THEN 1 ELSE 0 END), 0), @i2 = ISNULL(SUM(CASE WHEN daysLeft BETWEEN 0 AND 7 THEN 1 ELSE 0 END), 0),
               @i3 = ISNULL(SUM(CASE WHEN daysLeft BETWEEN 0 AND 30 THEN 1 ELSE 0 END), 0), @i4 = ISNULL(SUM(CASE WHEN daysLeft >= 0 THEN 1 ELSE 0 END), 0),
               @i5 = ISNULL(SUM(CASE WHEN daysLeft BETWEEN 0 AND 30 AND status <> N'Success' THEN 1 ELSE 0 END), 0),
               @i6 = ISNULL(SUM(CASE WHEN daysLeft BETWEEN 8 AND 30 THEN 1 ELSE 0 END), 0)
        FROM #r13;

        SET @Title = N'Document expiry';
        SET @Desc = N'Documents already expired or expiring within 90 days.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Expired', CONVERT(NVARCHAR(40), @i1), N'already past expiry', N'red', 1),
              (N'Next 7 days', CONVERT(NVARCHAR(40), @i2), N'urgent', N'amber', 2),
              (N'Next 30 days', CONVERT(NVARCHAR(40), @i3), N'renew soon', N'', 3),
              (N'Next 90 days', CONVERT(NVARCHAR(40), @i4), N'planning window', N'', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Documents by expiry window' AS title, N'bars' AS kind, 0 AS money, 0 AS pct,
            JSON_QUERY((SELECT b.label, COUNT(r.DocumentId) AS value
                        FROM (VALUES (N'Expired', -100000, -1, 1), (N'0-7 days', 0, 7, 2), (N'8-30 days', 8, 30, 3), (N'31-90 days', 31, 90, 4)) b(label, lo, hi, ord)
                        LEFT JOIN #r13 r ON r.daysLeft BETWEEN b.lo AND b.hi GROUP BY b.label, b.ord ORDER BY b.ord FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @i1 > 0 THEN CONCAT(@i1, N' documents have already expired and need replacing.') ELSE N'No document has expired.' END, CASE WHEN @i1 > 0 THEN N'bad' ELSE N'good' END, 1),
              (CASE WHEN @i5 > 0 THEN CONCAT(@i5, N' documents expiring within 30 days are not marked Success.') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"document","label":"Document","type":"text"},{"key":"customer","label":"Customer","type":"text"},{"key":"tracker","label":"Tracker","type":"text"},{"key":"expiryDate","label":"Expiry date","type":"date"},{"key":"daysLeft","label":"Days left","type":"days"},{"key":"status","label":"Status","type":"docstatus"},{"key":"employee","label":"Employee","type":"text"}]';
        SET @Rows = (SELECT document, customer, tracker, expiryDate, daysLeft, status, employee FROM #r13 ORDER BY expiryDate, DocumentId FOR JSON PATH);
    END
    /* ===================================================== 14. employee-performance */
    ELSE IF @k = N'employee-performance'
    BEGIN
        SELECT t.EmployeeName AS employee, COUNT(*) AS trackers,
               SUM(CASE WHEN t.StatusCode = N'COMPLETED' THEN 1 ELSE 0 END) AS completed,
               ISNULL(SUM(a.AcceptedValue), 0) AS acceptedValue,
               ISNULL(SUM(p.Paid), 0) AS collected,
               ISNULL(SUM(p.Invoiced), 0) AS invoiced,
               SUM(CASE WHEN t.IsOpen = 1 AND t.NextFollowUpDate < @Today THEN 1 ELSE 0 END) AS lateFollowUps
        INTO #r14
        FROM dbo.vw_BP_Tracker t
        LEFT JOIN (SELECT TrackerId, SUM(Grand) AS AcceptedValue FROM dbo.vw_BP_Quotation WHERE StatusKey = 'ACCEPTED' GROUP BY TrackerId) a ON a.TrackerId = t.TrackerId
        LEFT JOIN (SELECT TrackerId, SUM(Paid) AS Paid, SUM(GrandTotal) AS Invoiced FROM dbo.vw_BP_Invoice GROUP BY TrackerId) p ON p.TrackerId = t.TrackerId
        WHERE (@CustomerId IS NULL OR t.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR t.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = t.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR t.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR t.CreatedDate <= @ToDate)
        GROUP BY t.EmployeeName;

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(trackers), 0), @i3 = ISNULL(SUM(completed), 0), @i4 = ISNULL(SUM(lateFollowUps), 0), @d1 = ISNULL(SUM(collected), 0) FROM #r14;
        SELECT TOP (1) @t1 = employee, @d2 = collected FROM #r14 ORDER BY collected DESC, employee;

        SET @Title = N'Employee performance';
        SET @Desc = N'Trackers, quotations, collections and follow-ups per employee.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Employees', CONVERT(NVARCHAR(40), @i1), N'with trackers', N'', 1),
              (N'Trackers', CONVERT(NVARCHAR(40), @i2), CONCAT(@i3, N' completed'), N'', 2),
              (N'Collected', dbo.fn_BP_Money(@d1), N'on their trackers', N'green', 3),
              (N'Late follow-ups', CONVERT(NVARCHAR(40), @i4), N'need action', N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Collected by employee' AS title, N'bars' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT employee AS label, collected AS value FROM #r14 ORDER BY collected DESC, employee FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(@t1, N' has collected the most (', dbo.fn_BP_Money(@d2), N').') END, N'good', 1),
              (CASE WHEN @i4 > 0 THEN CONCAT(@i4, N' follow-ups are overdue across the team.') ELSE N'No follow-up is overdue.' END, CASE WHEN @i4 > 0 THEN N'warn' ELSE N'good' END, 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"employee","label":"Employee","type":"text"},{"key":"trackers","label":"Trackers","type":"num","total":true},{"key":"completed","label":"Completed","type":"num","total":true},{"key":"acceptedValue","label":"Accepted value","type":"money","total":true},{"key":"collected","label":"Collected","type":"money","total":true},{"key":"collectionRate","label":"Collection %","type":"pct"},{"key":"lateFollowUps","label":"Late follow-ups","type":"num","total":true}]';
        SET @Rows = (SELECT employee, trackers, completed, acceptedValue, collected,
                            CASE WHEN invoiced <= 0 THEN 0 ELSE CONVERT(INT, ROUND(collected * 100.0 / invoiced, 0)) END AS collectionRate, lateFollowUps
                     FROM #r14 ORDER BY collected DESC, employee FOR JSON PATH);
    END
    /* ===================================================== 15. customer-360 */
    ELSE IF @k = N'customer-360'
    BEGIN
        SELECT t.CustomerName AS customer, t.CustomerCode AS code, COUNT(*) AS trackers,
               SUM(CASE WHEN t.IsOpen = 1 THEN 1 ELSE 0 END) AS activeTrackers,
               ISNULL(SUM(qn.Cnt), 0) AS quotations,
               ISNULL(SUM(qn.AcceptedValue), 0) AS acceptedValue,
               ISNULL(SUM(iv.Paid), 0) AS paid,
               ISNULL(SUM(iv.Balance), 0) AS outstanding,
               ISNULL(SUM(dc.Pending), 0) AS docsPending
        INTO #r15
        FROM dbo.vw_BP_Tracker t
        LEFT JOIN (SELECT TrackerId, COUNT(*) AS Cnt, SUM(CASE WHEN StatusKey = 'ACCEPTED' THEN Grand ELSE 0 END) AS AcceptedValue FROM dbo.vw_BP_Quotation GROUP BY TrackerId) qn ON qn.TrackerId = t.TrackerId
        LEFT JOIN (SELECT TrackerId, SUM(Paid) AS Paid, SUM(CASE WHEN Balance > 0 THEN Balance ELSE 0 END) AS Balance FROM dbo.vw_BP_Invoice GROUP BY TrackerId) iv ON iv.TrackerId = t.TrackerId
        LEFT JOIN (SELECT TrackerId, SUM(CASE WHEN StatusName = N'Pending' THEN 1 ELSE 0 END) AS Pending FROM dbo.vw_BP_Document GROUP BY TrackerId) dc ON dc.TrackerId = t.TrackerId
        WHERE (@CustomerId IS NULL OR t.CustomerId = @CustomerId) AND (@EmployeeId IS NULL OR t.EmployeeId = @EmployeeId) AND (@CategoryId IS NULL OR EXISTS (SELECT 1 FROM dbo.TrackerCategorySubCategorySpecs z WHERE z.TrackerId = t.TrackerId AND z.CategoryId = @CategoryId)) AND (@FromDate IS NULL OR t.CreatedDate >= @FromDate) AND (@ToDate IS NULL OR t.CreatedDate <= @ToDate)
        GROUP BY t.CustomerId, t.CustomerName, t.CustomerCode;

        SELECT @i1 = COUNT(*), @i2 = ISNULL(SUM(activeTrackers), 0), @i3 = ISNULL(SUM(CASE WHEN docsPending > 0 THEN 1 ELSE 0 END), 0),
               @d1 = ISNULL(SUM(acceptedValue), 0), @d2 = ISNULL(SUM(outstanding), 0)
        FROM #r15;
        SELECT TOP (1) @t1 = customer, @d3 = acceptedValue FROM #r15 ORDER BY acceptedValue DESC, customer;

        SET @Title = N'Customer 360';
        SET @Desc = N'One row per customer: trackers, money and documents together.';
        SET @Summary = (SELECT label, value, hint, tone FROM (VALUES
              (N'Customers', CONVERT(NVARCHAR(40), @i1), N'with trackers', N'', 1),
              (N'Active trackers', CONVERT(NVARCHAR(40), @i2), N'not completed or rejected', N'', 2),
              (N'Accepted value', dbo.fn_BP_Money(@d1), N'lifetime', N'green', 3),
              (N'Outstanding', dbo.fn_BP_Money(@d2), N'unpaid invoices', N'red', 4)) v(label, value, hint, tone, ord)
            ORDER BY ord FOR JSON PATH);
        SET @Chart = (SELECT N'Accepted value by customer' AS title, N'bars' AS kind, 1 AS money, 0 AS pct,
            JSON_QUERY((SELECT TOP (8) customer AS label, acceptedValue AS value FROM #r15 ORDER BY acceptedValue DESC, customer FOR JSON PATH)) AS data
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        SET @Insights = (SELECT [text], tone FROM (VALUES
              (CASE WHEN @t1 IS NULL THEN N'' ELSE CONCAT(@t1, N' is the largest customer by accepted value (', dbo.fn_BP_Money(@d3), N').') END, N'info', 1),
              (CASE WHEN @i3 > 0 THEN CONCAT(@i3, N' customers have documents still Pending.') ELSE N'' END, N'warn', 2)) v([text], tone, ord)
            WHERE [text] <> N'' ORDER BY ord FOR JSON PATH);
        SET @Cols = N'[{"key":"customer","label":"Customer","type":"text"},{"key":"code","label":"Code","type":"text"},{"key":"trackers","label":"Trackers","type":"num","total":true},{"key":"activeTrackers","label":"Active","type":"num","total":true},{"key":"quotations","label":"Quotations","type":"num","total":true},{"key":"acceptedValue","label":"Accepted value","type":"money","total":true},{"key":"paid","label":"Paid","type":"money","total":true},{"key":"outstanding","label":"Outstanding","type":"money","total":true},{"key":"docsPending","label":"Docs pending","type":"num","total":true}]';
        SET @Rows = (SELECT customer, code, trackers, activeTrackers, quotations, acceptedValue, paid, outstanding, docsPending FROM #r15 ORDER BY acceptedValue DESC, customer FOR JSON PATH);
    END
    ELSE
    BEGIN
        SET @Title = N'Report';
        SET @Desc = N'No report definition was found.';
    END;

    SELECT (SELECT @k AS [key], @Title AS title, @Desc AS description,
                   JSON_QUERY(ISNULL(@Summary, N'[]')) AS summary,
                   JSON_QUERY(@Chart) AS chart,
                   JSON_QUERY(ISNULL(@Insights, N'[]')) AS insights,
                   JSON_QUERY(ISNULL(@Cols, N'[]')) AS columns,
                   JSON_QUERY(ISNULL(@Rows, N'[]')) AS rows
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Reports_GetLookups]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 5. Filter lists for the Reports screen (real customers / employees / categories) ---------- */
CREATE   PROCEDURE [dbo].[usp_Reports_GetLookups]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (SELECT
              JSON_QUERY((SELECT c.CustomerId AS id, c.CustomerName AS name FROM dbo.Customers c WHERE c.IsActive = 1 ORDER BY c.CustomerName FOR JSON PATH)) AS customers,
              JSON_QUERY((SELECT e.EmployeeId AS id, e.FullName AS name FROM dbo.Employees e WHERE e.IsActive = 1 ORDER BY e.FullName FOR JSON PATH)) AS employees,
              JSON_QUERY((SELECT sc.CategoryId AS id, sc.CategoryName AS name FROM dbo.ServiceCategories sc WHERE sc.IsActive = 1 ORDER BY sc.CategoryName FOR JSON PATH)) AS categories
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS json;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_RoleAccess_Get]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   2. ROLE ACCESS GET
   No Modules dependency.
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_RoleAccess_Get]
    @RoleId INT,
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;

    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51002, 'Invalid application code.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleId = @RoleId)
        RETURN;

    SELECT RoleId, RoleName, Description, IsActive
    FROM dbo.Roles
    WHERE RoleId = @RoleId;

    SELECT
        mi.MenuItemId,
        mi.MenuKey,
        mi.DisplayName,
        mi.Icon,
        mi.Route,
        mi.ParentMenuItemId,
        mi.DisplayOrder,
        mi.MenuGroup,
        mi.ApplicationCode,
        mi.IsSystemMenu,

        CONVERT(bit, ISNULL(rp.CanView, 0))   AS CanView,
        CONVERT(bit, ISNULL(rp.CanAdd, 0))    AS CanAdd,
        CONVERT(bit, ISNULL(rp.CanEdit, 0))   AS CanEdit,
        CONVERT(bit, ISNULL(rp.CanDelete, 0)) AS CanDelete,
        CONVERT(bit, ISNULL(rp.CanImport, 0)) AS CanImport,
        CONVERT(bit, ISNULL(rp.CanExport, 0)) AS CanExport
    FROM dbo.MenuItems AS mi
    INNER JOIN dbo.Roles AS r
        ON r.RoleId = @RoleId
    LEFT JOIN dbo.RolePermissions AS rp
        ON rp.RoleId = @RoleId
       AND rp.MenuKey = mi.MenuKey
    WHERE mi.IsActive = 1
      AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH')
    ORDER BY
        mi.MenuGroup,
        mi.DisplayOrder,
        mi.DisplayName,
        mi.MenuItemId;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_RoleAccess_Save]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   3. ROLE ACCESS SAVE
   Persists explicit zero values too.
   ============================================================ */
CREATE   PROCEDURE [dbo].[usp_RoleAccess_Save]
    @RoleId INT,
    @ApplicationCode NVARCHAR(30) = N'TRACKER',
    @PermissionsJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;

    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51003, 'Invalid application code.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleId = @RoleId)
        THROW 51004, 'Role was not found.', 1;

    IF ISJSON(@PermissionsJson) <> 1
        THROW 51005, 'PermissionsJson must be valid JSON.', 1;

    DECLARE @Permissions TABLE
    (
        MenuItemId INT NOT NULL PRIMARY KEY,
        CanView BIT NOT NULL,
        CanAdd BIT NOT NULL,
        CanEdit BIT NOT NULL,
        CanDelete BIT NOT NULL,
        CanImport BIT NOT NULL,
        CanExport BIT NOT NULL
    );

    INSERT INTO @Permissions
    (
        MenuItemId,
        CanView,
        CanAdd,
        CanEdit,
        CanDelete,
        CanImport,
        CanExport
    )
    SELECT
        MenuItemId,
        ISNULL(CanView, 0),
        ISNULL(CanAdd, 0),
        ISNULL(CanEdit, 0),
        ISNULL(CanDelete, 0),
        ISNULL(CanImport, 0),
        ISNULL(CanExport, 0)
    FROM OPENJSON(@PermissionsJson)
    WITH
    (
        MenuItemId INT '$.menuItemId',
        CanView BIT '$.canView',
        CanAdd BIT '$.canAdd',
        CanEdit BIT '$.canEdit',
        CanDelete BIT '$.canDelete',
        CanImport BIT '$.canImport',
        CanExport BIT '$.canExport'
    )
    WHERE MenuItemId IS NOT NULL;

    DELETE rp
    FROM dbo.RolePermissions AS rp
    INNER JOIN dbo.MenuItems AS mi
        ON mi.MenuKey = rp.MenuKey
    WHERE rp.RoleId = @RoleId
      AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH');

    INSERT INTO dbo.RolePermissions
    (
        RoleId,
        MenuKey,
        CanView,
        CanAdd,
        CanEdit,
        CanDelete,
        CanImport,
        CanExport
    )
    SELECT
        @RoleId,
        mi.MenuKey,
        p.CanView,
        p.CanAdd,
        p.CanEdit,
        p.CanDelete,
        p.CanImport,
        p.CanExport
    FROM @Permissions AS p
    INNER JOIN dbo.MenuItems AS mi
        ON mi.MenuItemId = p.MenuItemId
    WHERE mi.IsActive = 1
      AND mi.ApplicationCode IN (@ApplicationCode, N'BOTH');
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_RoleAccess_Verify]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleAccess_Verify]
    @RoleId INT,
    @ApplicationCode NVARCHAR(30) = N'TRACKER'
AS
BEGIN
    SET NOCOUNT ON;
    IF @ApplicationCode NOT IN (N'ADMIN', N'TRACKER')
        THROW 51014, 'Invalid application code.', 1;

    SELECT
        rp.RolePermissionId,
        rp.RoleId,
        r.RoleName,
        mi.MenuItemId,
        rp.MenuKey,
        mi.DisplayName,
        rp.CanView,
        rp.CanAdd,
        rp.CanEdit,
        rp.CanDelete,
        rp.CanImport,
        rp.CanExport
    FROM dbo.RolePermissions rp
    INNER JOIN dbo.Roles r ON r.RoleId = rp.RoleId
    LEFT JOIN dbo.MenuItems mi ON mi.MenuKey = rp.MenuKey
    WHERE rp.RoleId = @RoleId
      AND (mi.ApplicationCode IN (@ApplicationCode, N'BOTH') OR mi.MenuItemId IS NULL)
    ORDER BY ISNULL(mi.DisplayOrder, 2147483647), rp.MenuKey;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_RoleModules_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleModules_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[RoleModules] WHERE [RoleModuleId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_RoleModules_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleModules_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [RoleModuleId], [RoleId], [ModuleId]
    FROM [dbo].[RoleModules]
    WHERE [RoleModuleId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_RoleModules_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleModules_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[RoleModules];
    SELECT @TotalCount AS TotalCount;

    SELECT [RoleModuleId], [RoleId], [ModuleId]
    FROM [dbo].[RoleModules]
    ORDER BY [RoleModuleId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_RoleModules_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleModules_Insert]
    @RoleId INT,
    @ModuleId INT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[RoleModules] ([RoleId], [ModuleId])
    VALUES (@RoleId, @ModuleId);

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [RoleModuleId], [RoleId], [ModuleId]
    FROM [dbo].[RoleModules]
    WHERE [RoleModuleId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_RoleModules_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_RoleModules_Update]
    @Id INT,
    @RoleId INT,
    @ModuleId INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[RoleModules]
    SET [RoleId] = @RoleId, [ModuleId] = @ModuleId
    WHERE [RoleModuleId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [RoleModuleId], [RoleId], [ModuleId]
    FROM [dbo].[RoleModules]
    WHERE [RoleModuleId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Roles_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Roles_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleId = @Id)
        RETURN;

    IF EXISTS (SELECT 1 FROM dbo.Users WHERE RoleId = @Id)
        THROW 51020, 'Role cannot be deleted because users are assigned to this role.', 1;

    IF OBJECT_ID(N'dbo.Employees', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM dbo.Employees WHERE RoleId = @Id)
        THROW 51021, 'Role cannot be deleted because employees are assigned to this role.', 1;

    IF EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @Id)
        THROW 51022, 'Role cannot be deleted because menu permissions are assigned to this role.', 1;

    DELETE FROM dbo.Roles WHERE RoleId = @Id;
    SELECT @@ROWCOUNT AS AffectedRows;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_Roles_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Roles_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [RoleId], [RoleName], [Description], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Roles]
    WHERE [RoleId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Roles_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Roles_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[Roles];
    SELECT @TotalCount AS TotalCount;

    SELECT [RoleId], [RoleName], [Description], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Roles]
    ORDER BY [RoleId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Roles_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Roles_Insert]
    @RoleName NVARCHAR(50),
    @Description NVARCHAR(255),
    @IsActive BIT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[Roles] ([RoleName], [Description], [IsActive], [CreatedDate])
    VALUES (@RoleName, @Description, @IsActive, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [RoleId], [RoleName], [Description], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Roles]
    WHERE [RoleId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Roles_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Roles_Update]
    @Id INT,
    @RoleName NVARCHAR(50),
    @Description NVARCHAR(255),
    @IsActive BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[Roles]
    SET [RoleName] = @RoleName, [Description] = @Description, [IsActive] = @IsActive, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [RoleId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [RoleId], [RoleName], [Description], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Roles]
    WHERE [RoleId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceCategories_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceCategories_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[ServiceCategories] WHERE [CategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceCategories_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceCategories_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [CategoryId], [CategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceCategories]
    WHERE [CategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceCategories_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceCategories_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[ServiceCategories];
    SELECT @TotalCount AS TotalCount;

    SELECT [CategoryId], [CategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceCategories]
    ORDER BY [CategoryId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceCategories_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceCategories_Insert]
    @CategoryName NVARCHAR(100),
    @IsActive BIT,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[ServiceCategories] ([CategoryName], [IsActive], [CreatedBy], [CreatedDate])
    VALUES (@CategoryName, @IsActive, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [CategoryId], [CategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceCategories]
    WHERE [CategoryId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceCategories_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceCategories_Update]
    @Id INT,
    @CategoryName NVARCHAR(100),
    @IsActive BIT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[ServiceCategories]
    SET [CategoryName] = @CategoryName, [IsActive] = @IsActive, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [CategoryId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [CategoryId], [CategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceCategories]
    WHERE [CategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Services_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Services_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.Services WHERE ServiceId=@Id;
    IF @@ROWCOUNT=0 THROW 50005, 'Service was not found.', 1;
    SELECT @@ROWCOUNT + 0;
END;

GO
/****** Object:  StoredProcedure [dbo].[usp_Services_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Services_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [ServiceId], [CategoryId], [SubCategoryId], [ServiceName], [IsActive], [Count], [Amount], [VAT], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Services]
    WHERE [ServiceId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_Services_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Services_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    SELECT COUNT(1) FROM [dbo].[Services];

    SELECT [ServiceId], [CategoryId], [SubCategoryId], [ServiceName], [IsActive], [Count], [Amount], [VAT], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Services]
    ORDER BY [CategoryId], [SubCategoryId], [ServiceName]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_Services_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Services_Insert]
    @CategoryId INT,
    @SubCategoryId INT,
    @ServiceName NVARCHAR(150),
    @IsActive BIT,
    @Count INT = 1,
    @Amount DECIMAL(18, 2),
    @VAT DECIMAL(5, 2),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[Services] ([CategoryId], [SubCategoryId], [ServiceName], [IsActive], [Count], [Amount], [VAT], [CreatedBy], [CreatedDate])
    VALUES (@CategoryId, @SubCategoryId, @ServiceName, @IsActive, @Count, @Amount, @VAT, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [ServiceId], [CategoryId], [SubCategoryId], [ServiceName], [IsActive], [Count], [Amount], [VAT], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Services]
    WHERE [ServiceId] = @NewId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_Services_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Services_Update]
    @Id INT,
    @CategoryId INT,
    @SubCategoryId INT,
    @ServiceName NVARCHAR(150),
    @IsActive BIT,
    @Count INT = 1,
    @Amount DECIMAL(18, 2),
    @VAT DECIMAL(5, 2),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[Services]
    SET [CategoryId] = @CategoryId, [SubCategoryId] = @SubCategoryId, [ServiceName] = @ServiceName,
        [IsActive] = @IsActive, [Count] = @Count, [Amount] = @Amount, [VAT] = @VAT, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [ServiceId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [ServiceId], [CategoryId], [SubCategoryId], [ServiceName], [IsActive], [Count], [Amount], [VAT], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Services]
    WHERE [ServiceId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceSubCategories_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceSubCategories_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[ServiceSubCategories] WHERE [SubCategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceSubCategories_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceSubCategories_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [SubCategoryId], [CategoryId], [SubCategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceSubCategories]
    WHERE [SubCategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceSubCategories_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceSubCategories_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[ServiceSubCategories];
    SELECT @TotalCount AS TotalCount;

    SELECT [SubCategoryId], [CategoryId], [SubCategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceSubCategories]
    ORDER BY [SubCategoryId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceSubCategories_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceSubCategories_Insert]
    @CategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @IsActive BIT,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[ServiceSubCategories] ([CategoryId], [SubCategoryName], [IsActive], [CreatedBy], [CreatedDate])
    VALUES (@CategoryId, @SubCategoryName, @IsActive, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [SubCategoryId], [CategoryId], [SubCategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceSubCategories]
    WHERE [SubCategoryId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_ServiceSubCategories_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_ServiceSubCategories_Update]
    @Id INT,
    @CategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @IsActive BIT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[ServiceSubCategories]
    SET [CategoryId] = @CategoryId, [SubCategoryName] = @SubCategoryName, [IsActive] = @IsActive, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [SubCategoryId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [SubCategoryId], [CategoryId], [SubCategoryName], [IsActive], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[ServiceSubCategories]
    WHERE [SubCategoryId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[SupportingDocuments] WHERE [SupportingDocumentId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments]
    WHERE [SupportingDocumentId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_GetByTracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 5. usp_SupportingDocuments_GetByTracker
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_GetByTracker]
    @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments]
    WHERE [TrackerId] = @TrackerId
    ORDER BY [SupportingDocumentId];
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[SupportingDocuments];
    SELECT @TotalCount AS TotalCount;

    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments]
    ORDER BY [SupportingDocumentId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_Insert]
    @TrackerId INT,
    @TrackerCategorySubCategorySpecId INT,
    @CustomerCode NVARCHAR(50),
    @DocumentName NVARCHAR(255),
    @Notes NVARCHAR(1000),
    @Status NVARCHAR(20),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[SupportingDocuments] ([TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate])
    VALUES (@TrackerId, @TrackerCategorySubCategorySpecId, @CustomerCode, @DocumentName, @Notes, @Status, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments]
    WHERE [SupportingDocumentId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_MarkCollected]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_MarkCollected]
    @Id INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @TrackerId INT, @RemainingPending INT;

    SELECT @TrackerId = [TrackerId] FROM [dbo].[SupportingDocuments] WHERE [SupportingDocumentId] = @Id;
    IF @TrackerId IS NULL
    BEGIN
        RAISERROR('Supporting document not found.', 16, 1);
        RETURN;
    END

    UPDATE [dbo].[SupportingDocuments]
    SET [Status] = 'Collected', [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [SupportingDocumentId] = @Id;

    SELECT @RemainingPending = COUNT(1) FROM [dbo].[SupportingDocuments]
    WHERE [TrackerId] = @TrackerId AND [Status] <> 'Collected';

    -- Once every document is collected AND a quotation is Accepted, the tracker moves to In Progress
    -- (master code IN_PROGRESS - the old 'InProgress' value is not a master code).
    IF @RemainingPending = 0
       AND EXISTS (SELECT 1 FROM dbo.Quotations WHERE TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(QuotationStatus) = 'ACCEPTED')
        EXEC dbo.usp_Tracker_ApplyStatus @TrackerId = @TrackerId, @StatusCode = N'IN_PROGRESS', @ModifiedBy = @ModifiedBy, @Note = N'All supporting documents collected';

    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments] WHERE [SupportingDocumentId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_SupportingDocuments_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_SupportingDocuments_Update]
    @Id INT,
    @TrackerId INT,
    @TrackerCategorySubCategorySpecId INT,
    @CustomerCode NVARCHAR(50),
    @DocumentName NVARCHAR(255),
    @Notes NVARCHAR(1000),
    @Status NVARCHAR(20),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[SupportingDocuments]
    SET [TrackerId] = @TrackerId, [TrackerCategorySubCategorySpecId] = @TrackerCategorySubCategorySpecId, [CustomerCode] = @CustomerCode, [DocumentName] = @DocumentName, [Notes] = @Notes, [Status] = @Status, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [SupportingDocumentId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [SupportingDocumentId], [TrackerId], [TrackerCategorySubCategorySpecId], [CustomerCode], [DocumentName], [Notes], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[SupportingDocuments]
    WHERE [SupportingDocumentId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Tracker_ApplyStatus]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Called by the quotation / invoice / payment / supporting-document triggers to move the tracker.
   While a document is Pending the tracker stays PENDING and only the status to resume is remembered. */
CREATE   PROCEDURE [dbo].[usp_Tracker_ApplyStatus]
    @TrackerId INT,
    @StatusCode NVARCHAR(30),
    @ModifiedBy INT = NULL,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Code NVARCHAR(30), @TargetTerminal BIT;
    SELECT TOP (1) @Code = StatusCode, @TargetTerminal = IsTerminal FROM dbo.TrackerStatuses
    WHERE IsActive = 1 AND UPPER(StatusCode) = UPPER(LTRIM(RTRIM(@StatusCode)));
    IF @Code IS NULL RETURN;

    DECLARE @Current NVARCHAR(30);
    SELECT @Current = t.Status FROM dbo.Trackers t WHERE t.TrackerId = @TrackerId;
    IF @Current IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM dbo.TrackerStatuses WHERE StatusCode = @Current AND IsTerminal = 1) RETURN;

    -- Documents still Pending: stay PENDING, remember where to go afterwards.
    -- (A Rejected / Not Completed result is not held; Completed is held until the documents are Success.)
    IF @Code <> N'PENDING'
       AND EXISTS (SELECT 1 FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId AND Status = N'Pending')
       AND NOT (@TargetTerminal = 1 AND @Code <> N'COMPLETED')
    BEGIN
        MERGE dbo.TrackerDocumentHold AS h
        USING (SELECT @TrackerId AS TrackerId) AS s ON h.TrackerId = s.TrackerId
        WHEN MATCHED THEN UPDATE SET ResumeStatus = @Code
        WHEN NOT MATCHED THEN INSERT (TrackerId, ResumeStatus) VALUES (@TrackerId, @Code);
        IF @Current <> N'PENDING'
            EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = N'PENDING', @ModifiedBy = @ModifiedBy,
                 @Note = N'Held in Pending: documents are still Pending';
        RETURN;
    END;

    -- Completed only when every document is Success and the accepted quotation is paid in full.
    IF @Code = N'COMPLETED' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL RETURN;

    IF @TargetTerminal = 1 DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
    EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = @Code, @ModifiedBy = @ModifiedBy, @Note = @Note;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Tracker_AutoProgress]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------- 2. The rule: promote from ANY open status ---------- */
CREATE   PROCEDURE [dbo].[usp_Tracker_AutoProgress]
    @TrackerId INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Current NVARCHAR(30);
    SELECT @Current = UPPER(LTRIM(RTRIM(Status))) FROM dbo.Trackers WHERE TrackerId = @TrackerId;
    IF @Current IS NULL RETURN;
    IF @Current IN (N'COMPLETED', N'REJECTED') RETURN;

    DECLARE @PendingDocs INT = (SELECT COUNT(1) FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId AND UPPER(Status) = N'PENDING');
    DECLARE @HasAccepted BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.Quotations q WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED') THEN 1 ELSE 0 END;
    DECLARE @InvoiceGenerated BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.Quotations q
                                                      INNER JOIN dbo.Invoices i ON i.QuotationId = q.QuotationId
                                                      WHERE q.TrackerId = @TrackerId AND dbo.fn_QuotationStatusKey(q.QuotationStatus) = 'ACCEPTED')
                                         THEN 1 ELSE 0 END;

    -- invoice generated, no document Pending  ->  IN_PROGRESS (from NEW / QUOTATION / ACCEPTED / INVOICE_SENT / PENDING ...)
    IF @InvoiceGenerated = 1 AND @PendingDocs = 0 AND @Current <> N'IN_PROGRESS'
    BEGIN
        DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
        EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = N'IN_PROGRESS', @ModifiedBy = @ModifiedBy,
             @Note = N'Invoice generated';
        RETURN;
    END;

    -- a held PENDING tracker with an accepted quotation but no invoice yet comes back to ACCEPTED
    IF @Current = N'PENDING' AND @PendingDocs = 0 AND @HasAccepted = 1
    BEGIN
        DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
        EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = N'ACCEPTED', @ModifiedBy = @ModifiedBy,
             @Note = N'No document is Pending';
        RETURN;
    END;

    -- every invoice removed -> back to ACCEPTED
    IF @Current = N'IN_PROGRESS' AND @InvoiceGenerated = 0
        EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = N'ACCEPTED', @ModifiedBy = @ModifiedBy,
             @Note = N'No invoice generated';
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Tracker_SetStatusRaw]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Sets the tracker status (history note via SESSION_CONTEXT), with no hold / completion logic. */
CREATE   PROCEDURE [dbo].[usp_Tracker_SetStatusRaw]
    @TrackerId INT,
    @StatusCode NVARCHAR(30),
    @ModifiedBy INT = NULL,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Code NVARCHAR(30);
    SELECT TOP (1) @Code = StatusCode FROM dbo.TrackerStatuses
    WHERE IsActive = 1 AND UPPER(StatusCode) = UPPER(LTRIM(RTRIM(@StatusCode)));
    IF @Code IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId AND Status = @Code) RETURN;

    DECLARE @Prev NVARCHAR(500) = CAST(SESSION_CONTEXT(N'StatusNote') AS NVARCHAR(500));
    IF @Note IS NOT NULL EXEC sys.sp_set_session_context @key = N'StatusNote', @value = @Note;
    UPDATE dbo.Trackers
       SET Status = @Code, ModifiedBy = COALESCE(@ModifiedBy, ModifiedBy), ModifiedDate = SYSUTCDATETIME()
     WHERE TrackerId = @TrackerId;
    IF @Note IS NOT NULL EXEC sys.sp_set_session_context @key = N'StatusNote', @value = @Prev;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Tracker_SyncDocumentHold]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Brings the tracker in line with its documents (called by the document trigger). */
CREATE   PROCEDURE [dbo].[usp_Tracker_SyncDocumentHold]
    @TrackerId INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Current NVARCHAR(30);
    SELECT @Current = Status FROM dbo.Trackers WHERE TrackerId = @TrackerId;
    IF @Current IS NULL
    BEGIN
        DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
        RETURN;
    END;
    IF EXISTS (SELECT 1 FROM dbo.TrackerStatuses WHERE StatusCode = @Current AND IsTerminal = 1)
    BEGIN
        DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
        RETURN;
    END;

    DECLARE @Pending INT = (SELECT COUNT(1) FROM dbo.TrackerQuotationDocuments WHERE TrackerId = @TrackerId AND Status = N'Pending');
    DECLARE @Note NVARCHAR(500);

    IF @Pending > 0
    BEGIN
        IF @Current <> N'PENDING'
        BEGIN
            MERGE dbo.TrackerDocumentHold AS h
            USING (SELECT @TrackerId AS TrackerId) AS s ON h.TrackerId = s.TrackerId
            WHEN MATCHED THEN UPDATE SET ResumeStatus = @Current
            WHEN NOT MATCHED THEN INSERT (TrackerId, ResumeStatus) VALUES (@TrackerId, @Current);
            SET @Note = CONCAT(@Pending, CASE WHEN @Pending = 1 THEN N' document is' ELSE N' documents are' END, N' Pending');
            EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = N'PENDING', @ModifiedBy = @ModifiedBy, @Note = @Note;
        END;
        RETURN;
    END;

    -- No Pending document left: go back to where the tracker was.
    DECLARE @Resume NVARCHAR(30);
    SELECT @Resume = ResumeStatus FROM dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
    IF @Resume IS NULL RETURN;
    DELETE dbo.TrackerDocumentHold WHERE TrackerId = @TrackerId;
    IF @Current <> N'PENDING' RETURN;

    IF @Resume = N'COMPLETED' AND dbo.fn_Tracker_CompletionBlocker(@TrackerId) IS NOT NULL SET @Resume = N'ACCEPTED';
    EXEC dbo.usp_Tracker_SetStatusRaw @TrackerId = @TrackerId, @StatusCode = @Resume, @ModifiedBy = @ModifiedBy,
         @Note = N'All documents are Success';
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecPersons_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecPersons_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE TrackerCategorySubCategorySpecPersonId = @Id;

    SELECT @@ROWCOUNT AS AffectedCount;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecPersons_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecPersons_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        TrackerCategorySubCategorySpecPersonId,
        TrackerCategorySubCategorySpecId,
        TrackerId,
        PersonName,
        CreatedBy,
        CreatedDate,
        ModifiedBy,
        ModifiedDate
    FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE TrackerCategorySubCategorySpecPersonId = @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecPersons_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* --------------------------------------------------------------------------
   4. Person CRUD procedures
   -------------------------------------------------------------------------- */
CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecPersons_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25,
    @TrackerId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1)
    FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE @TrackerId IS NULL OR TrackerId = @TrackerId;

    SELECT @TotalCount AS TotalCount;

    SELECT
        TrackerCategorySubCategorySpecPersonId,
        TrackerCategorySubCategorySpecId,
        TrackerId,
        PersonName,
        CreatedBy,
        CreatedDate,
        ModifiedBy,
        ModifiedDate
    FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE @TrackerId IS NULL OR TrackerId = @TrackerId
    ORDER BY TrackerCategorySubCategorySpecPersonId
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecPersons_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecPersons_Insert]
    @TrackerCategorySubCategorySpecId INT,
    @TrackerId INT,
    @PersonName NVARCHAR(200),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @PersonName = LTRIM(RTRIM(ISNULL(@PersonName, N'')));

    IF @PersonName = N''
        THROW 50040, 'PersonName is required.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.TrackerCategorySubCategorySpecs
        WHERE TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId
          AND TrackerId = @TrackerId
    )
        THROW 50041, 'The selected Category/Sub Category does not belong to the selected Tracker.', 1;

    INSERT INTO dbo.TrackerCategorySubCategorySpecPersons
    (
        TrackerCategorySubCategorySpecId,
        TrackerId,
        PersonName,
        CreatedBy,
        CreatedDate
    )
    VALUES
    (
        @TrackerCategorySubCategorySpecId,
        @TrackerId,
        @PersonName,
        @CreatedBy,
        SYSUTCDATETIME()
    );

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());

    SELECT
        TrackerCategorySubCategorySpecPersonId,
        TrackerCategorySubCategorySpecId,
        TrackerId,
        PersonName,
        CreatedBy,
        CreatedDate,
        ModifiedBy,
        ModifiedDate
    FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE TrackerCategorySubCategorySpecPersonId = @NewId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecPersons_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecPersons_Update]
    @Id INT,
    @TrackerCategorySubCategorySpecId INT,
    @TrackerId INT,
    @PersonName NVARCHAR(200),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @PersonName = LTRIM(RTRIM(ISNULL(@PersonName, N'')));

    IF @PersonName = N''
        THROW 50040, 'PersonName is required.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.TrackerCategorySubCategorySpecs
        WHERE TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId
          AND TrackerId = @TrackerId
    )
        THROW 50041, 'The selected Category/Sub Category does not belong to the selected Tracker.', 1;

    UPDATE dbo.TrackerCategorySubCategorySpecPersons
    SET TrackerCategorySubCategorySpecId = @TrackerCategorySubCategorySpecId,
        TrackerId = @TrackerId,
        PersonName = @PersonName,
        ModifiedBy = @ModifiedBy,
        ModifiedDate = SYSUTCDATETIME()
    WHERE TrackerCategorySubCategorySpecPersonId = @Id;

    IF @@ROWCOUNT = 0 RETURN;

    SELECT
        TrackerCategorySubCategorySpecPersonId,
        TrackerCategorySubCategorySpecId,
        TrackerId,
        PersonName,
        CreatedBy,
        CreatedDate,
        ModifiedBy,
        ModifiedDate
    FROM dbo.TrackerCategorySubCategorySpecPersons
    WHERE TrackerCategorySubCategorySpecPersonId = @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecs_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecs_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[TrackerCategorySubCategorySpecs] WHERE [TrackerCategorySubCategorySpecId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecs_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecs_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [PersonName], [IssueDate], [DueDate], [Status], [Notes], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerCategorySubCategorySpecs]
    WHERE [TrackerCategorySubCategorySpecId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecs_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* Tracker workspace filtering: the Angular Tracker screen loads all child
   Category/SubCategory specs and Service specs belonging to one Tracker. */
CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecs_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25,
    @TrackerId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1)
    FROM [dbo].[TrackerCategorySubCategorySpecs]
    WHERE @TrackerId IS NULL OR [TrackerId] = @TrackerId;

    SELECT @TotalCount AS TotalCount;

    SELECT [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName],
           [SubCategoryId], [SubCategoryName], [PersonName], [IssueDate], [DueDate], [Status], [Notes],
           [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerCategorySubCategorySpecs]
    WHERE @TrackerId IS NULL OR [TrackerId] = @TrackerId
    ORDER BY [TrackerCategorySubCategorySpecId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecs_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecs_Insert]
    @TrackerId INT,
    @CategoryId INT,
    @CategoryName NVARCHAR(100),
    @SubCategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @PersonName NVARCHAR(200) = NULL,
    @IssueDate DATE,
    @DueDate DATE,
    @Status NVARCHAR(30),
    @Notes NVARCHAR(1000),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[TrackerCategorySubCategorySpecs] ([TrackerId], [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [PersonName], [IssueDate], [DueDate], [Status], [Notes], [CreatedBy], [CreatedDate])
    VALUES (@TrackerId, @CategoryId, @CategoryName, @SubCategoryId, @SubCategoryName, @PersonName, @IssueDate, @DueDate, @Status, @Notes, @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [PersonName], [IssueDate], [DueDate], [Status], [Notes], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerCategorySubCategorySpecs]
    WHERE [TrackerCategorySubCategorySpecId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerCategorySubCategorySpecs_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerCategorySubCategorySpecs_Update]
    @Id INT,
    @TrackerId INT,
    @CategoryId INT,
    @CategoryName NVARCHAR(100),
    @SubCategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @PersonName NVARCHAR(200) = NULL,
    @IssueDate DATE,
    @DueDate DATE,
    @Status NVARCHAR(30),
    @Notes NVARCHAR(1000),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[TrackerCategorySubCategorySpecs]
    SET [TrackerId] = @TrackerId, [CategoryId] = @CategoryId, [CategoryName] = @CategoryName, [SubCategoryId] = @SubCategoryId, [SubCategoryName] = @SubCategoryName, [PersonName] = @PersonName, [IssueDate] = @IssueDate, [DueDate] = @DueDate, [Status] = @Status, [Notes] = @Notes, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerCategorySubCategorySpecId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [PersonName], [IssueDate], [DueDate], [Status], [Notes], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerCategorySubCategorySpecs]
    WHERE [TrackerCategorySubCategorySpecId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerFamilyMembers_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerFamilyMembers_Delete] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.TrackerFamilyMembers WHERE TrackerFamilyMemberId=@Id;
    IF @@ROWCOUNT=0 RETURN;
    SELECT 1;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerFamilyMembers_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerFamilyMembers_GetById] @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT fm.TrackerFamilyMemberId,fm.TrackerId,fm.Name,fm.RelationshipId,r.RelationshipName,fm.DateOfBirth,fm.PassportNo,fm.VisaType,fm.Status,
           fm.CreatedBy,fm.CreatedDate,fm.ModifiedBy,fm.ModifiedDate
    FROM dbo.TrackerFamilyMembers fm INNER JOIN dbo.Relationships r ON r.RelationshipId=fm.RelationshipId
    WHERE fm.TrackerFamilyMemberId=@Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerFamilyMembers_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerFamilyMembers_GetPaged]
    @PageNumber INT = 1, @PageSize INT = 25, @TrackerId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber=CASE WHEN @PageNumber<1 THEN 1 ELSE @PageNumber END;
    SET @PageSize=CASE WHEN @PageSize<1 OR @PageSize>200 THEN 25 ELSE @PageSize END;
    SELECT COUNT(1) AS TotalCount FROM dbo.TrackerFamilyMembers WHERE @TrackerId IS NULL OR TrackerId=@TrackerId;
    SELECT fm.TrackerFamilyMemberId,fm.TrackerId,fm.Name,fm.RelationshipId,r.RelationshipName,fm.DateOfBirth,fm.PassportNo,fm.VisaType,fm.Status,
           fm.CreatedBy,fm.CreatedDate,fm.ModifiedBy,fm.ModifiedDate
    FROM dbo.TrackerFamilyMembers fm
    INNER JOIN dbo.Relationships r ON r.RelationshipId=fm.RelationshipId
    WHERE @TrackerId IS NULL OR fm.TrackerId=@TrackerId
    ORDER BY fm.TrackerFamilyMemberId
    OFFSET (@PageNumber-1)*@PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerFamilyMembers_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerFamilyMembers_Insert]
    @TrackerId INT, @Name NVARCHAR(200), @RelationshipId INT, @DateOfBirth DATE = NULL,
    @PassportNo NVARCHAR(100) = NULL, @VisaType NVARCHAR(100) = NULL, @Status NVARCHAR(30) = N'Active', @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Name=LTRIM(RTRIM(@Name));
    IF NOT EXISTS(SELECT 1 FROM dbo.Trackers WHERE TrackerId=@TrackerId) THROW 50073,'Tracker was not found.',1;
    IF NULLIF(@Name,N'') IS NULL THROW 50074,'Family Member Name is required.',1;
    IF NOT EXISTS(SELECT 1 FROM dbo.Relationships WHERE RelationshipId=@RelationshipId AND IsActive=1) THROW 50075,'Selected Relationship is not active.',1;
    INSERT dbo.TrackerFamilyMembers(TrackerId,Name,RelationshipId,DateOfBirth,PassportNo,VisaType,Status,CreatedBy,CreatedDate)
    VALUES(@TrackerId,@Name,@RelationshipId,@DateOfBirth,NULLIF(LTRIM(RTRIM(@PassportNo)),N''),NULLIF(LTRIM(RTRIM(@VisaType)),N''),
           CASE WHEN NULLIF(LTRIM(RTRIM(@Status)),N'') IS NULL THEN N'Active' ELSE LTRIM(RTRIM(@Status)) END,@CreatedBy,SYSUTCDATETIME());
    DECLARE @Id INT=CONVERT(INT,SCOPE_IDENTITY());
    EXEC dbo.usp_TrackerFamilyMembers_GetById @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerFamilyMembers_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerFamilyMembers_Update]
    @Id INT, @TrackerId INT, @Name NVARCHAR(200), @RelationshipId INT, @DateOfBirth DATE = NULL,
    @PassportNo NVARCHAR(100) = NULL, @VisaType NVARCHAR(100) = NULL, @Status NVARCHAR(30) = N'Active', @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Name=LTRIM(RTRIM(@Name));
    IF NOT EXISTS(SELECT 1 FROM dbo.TrackerFamilyMembers WHERE TrackerFamilyMemberId=@Id) RETURN;
    IF NOT EXISTS(SELECT 1 FROM dbo.Trackers WHERE TrackerId=@TrackerId) THROW 50073,'Tracker was not found.',1;
    IF NULLIF(@Name,N'') IS NULL THROW 50074,'Family Member Name is required.',1;
    IF NOT EXISTS(SELECT 1 FROM dbo.Relationships WHERE RelationshipId=@RelationshipId AND IsActive=1) THROW 50075,'Selected Relationship is not active.',1;
    UPDATE dbo.TrackerFamilyMembers SET TrackerId=@TrackerId,Name=@Name,RelationshipId=@RelationshipId,DateOfBirth=@DateOfBirth,
      PassportNo=NULLIF(LTRIM(RTRIM(@PassportNo)),N''),VisaType=NULLIF(LTRIM(RTRIM(@VisaType)),N''),
      Status=CASE WHEN NULLIF(LTRIM(RTRIM(@Status)),N'') IS NULL THEN N'Active' ELSE LTRIM(RTRIM(@Status)) END,
      ModifiedBy=@ModifiedBy,ModifiedDate=SYSUTCDATETIME() WHERE TrackerFamilyMemberId=@Id;
    EXEC dbo.usp_TrackerFamilyMembers_GetById @Id;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerQuotationDocuments_Create]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerQuotationDocuments_Create]
    @TrackerId INT,
    @QuotationId INT = NULL,
    @DocumentName NVARCHAR(256),
    @ReferenceNo NVARCHAR(100) = NULL,
    @Description NVARCHAR(1000) = NULL,
    @DocumentDate DATE = NULL,
    @ExpiryDate DATE = NULL,
    @Status NVARCHAR(30) = 'Pending',
    @AttachmentStoredName NVARCHAR(260) = NULL,
    @AttachmentRelativePath NVARCHAR(500) = NULL,
    @AttachmentOriginalName NVARCHAR(260) = NULL,
    @CreatedBy INT = NULL,
    @DocumentId INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId)
        THROW 50002, 'Tracker was not found.', 1;
    IF @QuotationId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.Quotations WHERE QuotationId = @QuotationId AND TrackerId = @TrackerId)
        THROW 50001, 'Selected quotation does not belong to this tracker.', 1;
    IF NULLIF(LTRIM(RTRIM(@DocumentName)), '') IS NULL
        THROW 50003, 'Document name is required.', 1;
    IF @ExpiryDate IS NOT NULL AND @DocumentDate IS NOT NULL AND @ExpiryDate < @DocumentDate
        THROW 50004, 'Expiry date cannot be earlier than document date.', 1;
    DECLARE @DocStatus NVARCHAR(30) = dbo.fn_DocumentStatusNormalize(@Status);
    IF @DocStatus IS NULL
        THROW 50006, 'Document status must be New, Pending or Success.', 1;

    INSERT INTO dbo.TrackerQuotationDocuments
    (
        TrackerId, QuotationId, DocumentName, ReferenceNo, Description, DocumentDate, ExpiryDate,
        AttachmentOriginalName, AttachmentStoredName, AttachmentRelativePath, Status, CreatedBy
    )
    VALUES
    (
        @TrackerId, @QuotationId, LTRIM(RTRIM(@DocumentName)), NULLIF(LTRIM(RTRIM(@ReferenceNo)), ''),
        NULLIF(LTRIM(RTRIM(@Description)), ''), @DocumentDate, @ExpiryDate,
        @AttachmentOriginalName, @AttachmentStoredName, @AttachmentRelativePath,
        @DocStatus, @CreatedBy
    );
    SET @DocumentId = CONVERT(INT, SCOPE_IDENTITY());
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerQuotationDocuments_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerQuotationDocuments_Delete]
    @DocumentId INT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.TrackerQuotationDocuments
    WHERE DocumentId = @DocumentId;

    IF @@ROWCOUNT = 0
        THROW 50005, 'Document was not found.', 1;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerQuotationDocuments_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerQuotationDocuments_GetById]
    @DocumentId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        DocumentId,
        TrackerId,
        QuotationId,
        DocumentName,
        ReferenceNo,
        Description,
        DocumentDate,
        ExpiryDate,
        AttachmentOriginalName,
        AttachmentStoredName,
        AttachmentRelativePath,
        Status,
        CreatedBy,
        CreatedOn,
        ModifiedBy,
        ModifiedOn
    FROM dbo.TrackerQuotationDocuments
    WHERE DocumentId = @DocumentId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerQuotationDocuments_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerQuotationDocuments_GetPaged]
    @TrackerId INT,
    @QuotationId INT = NULL,
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;

    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.Trackers
        WHERE TrackerId = @TrackerId
    )
        THROW 50002, 'Tracker was not found.', 1;

    IF @QuotationId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM dbo.Quotations
           WHERE QuotationId = @QuotationId
             AND TrackerId = @TrackerId
       )
        THROW 50001, 'Selected quotation does not belong to this tracker.', 1;

    SELECT COUNT(1) AS TotalCount
    FROM dbo.TrackerQuotationDocuments
    WHERE TrackerId = @TrackerId
      AND
      (
          (@QuotationId IS NULL AND QuotationId IS NULL)
          OR QuotationId = @QuotationId
      );

    SELECT
        DocumentId,
        TrackerId,
        QuotationId,
        DocumentName,
        ReferenceNo,
        Description,
        DocumentDate,
        ExpiryDate,
        AttachmentOriginalName,
        AttachmentStoredName,
        AttachmentRelativePath,
        Status,
        CreatedBy,
        CreatedOn,
        ModifiedBy,
        ModifiedOn
    FROM dbo.TrackerQuotationDocuments
    WHERE TrackerId = @TrackerId
      AND
      (
          (@QuotationId IS NULL AND QuotationId IS NULL)
          OR QuotationId = @QuotationId
      )
    ORDER BY DocumentId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerQuotationDocuments_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerQuotationDocuments_Update]
    @DocumentId INT,
    @TrackerId INT,
    @QuotationId INT = NULL,
    @DocumentName NVARCHAR(256),
    @ReferenceNo NVARCHAR(100) = NULL,
    @Description NVARCHAR(1000) = NULL,
    @DocumentDate DATE = NULL,
    @ExpiryDate DATE = NULL,
    @Status NVARCHAR(30) = 'Pending',
    @AttachmentStoredName NVARCHAR(260) = NULL,
    @AttachmentRelativePath NVARCHAR(500) = NULL,
    @AttachmentOriginalName NVARCHAR(260) = NULL,
    @RemoveAttachment BIT = 0,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.TrackerQuotationDocuments WHERE DocumentId = @DocumentId)
        THROW 50005, 'Document was not found.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId = @TrackerId)
        THROW 50002, 'Tracker was not found.', 1;
    IF @QuotationId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.Quotations WHERE QuotationId = @QuotationId AND TrackerId = @TrackerId)
        THROW 50001, 'Selected quotation does not belong to this tracker.', 1;
    IF NULLIF(LTRIM(RTRIM(@DocumentName)), '') IS NULL
        THROW 50003, 'Document name is required.', 1;
    IF @ExpiryDate IS NOT NULL AND @DocumentDate IS NOT NULL AND @ExpiryDate < @DocumentDate
        THROW 50004, 'Expiry date cannot be earlier than document date.', 1;
    DECLARE @DocStatus NVARCHAR(30) = dbo.fn_DocumentStatusNormalize(@Status);
    IF @DocStatus IS NULL
        THROW 50006, 'Document status must be New, Pending or Success.', 1;

    UPDATE dbo.TrackerQuotationDocuments
    SET TrackerId = @TrackerId,
        QuotationId = @QuotationId,
        DocumentName = LTRIM(RTRIM(@DocumentName)),
        ReferenceNo = NULLIF(LTRIM(RTRIM(@ReferenceNo)), ''),
        Description = NULLIF(LTRIM(RTRIM(@Description)), ''),
        DocumentDate = @DocumentDate,
        ExpiryDate = @ExpiryDate,
        Status = @DocStatus,
        AttachmentStoredName = CASE WHEN @RemoveAttachment = 1 OR @AttachmentStoredName IS NOT NULL THEN @AttachmentStoredName ELSE AttachmentStoredName END,
        AttachmentRelativePath = CASE WHEN @RemoveAttachment = 1 OR @AttachmentRelativePath IS NOT NULL THEN @AttachmentRelativePath ELSE AttachmentRelativePath END,
        AttachmentOriginalName = CASE WHEN @RemoveAttachment = 1 OR @AttachmentOriginalName IS NOT NULL THEN @AttachmentOriginalName ELSE AttachmentOriginalName END,
        ModifiedBy = @ModifiedBy,
        ModifiedOn = SYSUTCDATETIME()
    WHERE DocumentId = @DocumentId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_AssignEmployee]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 4. usp_Trackers_AssignEmployee
--    Assigns/reassigns the employee working a tracker (autocomplete target).
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_Trackers_AssignEmployee]
    @TrackerId INT,
    @EmployeeId INT,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @EmployeeName NVARCHAR(150);
    SELECT @EmployeeName = [FullName] FROM [dbo].[Employees] WHERE [EmployeeId] = @EmployeeId AND [IsActive] = 1;
    IF @EmployeeName IS NULL
    BEGIN
        RAISERROR('Employee not found or inactive.', 16, 1);
        RETURN;
    END

    UPDATE [dbo].[Trackers]
    SET [AssignedToEmployeeId] = @EmployeeId, [AssignedToEmployeeName] = @EmployeeName, [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerId] = @TrackerId;

    IF @@ROWCOUNT = 0
    BEGIN
        RAISERROR('Tracker not found.', 16, 1);
        RETURN;
    END

    SELECT [TrackerId], [CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [NextFollowUpDate], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Trackers] WHERE [TrackerId] = @TrackerId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[Trackers] WHERE [TrackerId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 2. usp_Trackers_GetById / GetPaged / Insert / Update
--    (re-issued to include the two new columns; behaviour otherwise unchanged)
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_Trackers_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [TrackerId], [CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [NextFollowUpDate], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Trackers]
    WHERE [TrackerId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_GetFollowUpQueue]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_GetFollowUpQueue]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        t.TrackerId,t.CustomerId,t.CustomerCode,t.CustomerName,t.Mobile,t.Email,
        t.AssignedToEmployeeId,t.AssignedToEmployeeName,t.Status,
        ISNULL(f.FrequencyDays,0) AS FollowUpFrequencyDays,
        t.NextFollowUpDate,
        f.FollowUpFrequencyId,
        ISNULL(f.FrequencyName,CASE WHEN ISNULL(t.FollowUpFrequencyDays,0)<=0 THEN N'Manual' ELSE N'Custom' END) AS FollowUpFrequencyName,
        ISNULL(f.IsManual,CASE WHEN ISNULL(t.FollowUpFrequencyDays,0)<=0 THEN CONVERT(bit,1) ELSE CONVERT(bit,0) END) AS IsManualFrequency,
        ISNULL(docs.TotalDocumentCount,0) AS TotalDocumentCount,
        ISNULL(docs.PendingDocumentCount,0) AS PendingDocumentCount,
        lastf.LastFollowUpDate
    FROM dbo.Trackers t
    INNER JOIN dbo.TrackerStatuses ts ON ts.StatusCode=t.Status AND ts.IsActive=1
    LEFT JOIN dbo.FollowUpFrequencies f ON f.FollowUpFrequencyId=t.FollowUpFrequencyId
    OUTER APPLY
    (
        SELECT COUNT(1) AS TotalDocumentCount,
               SUM(CASE WHEN sd.Status<>N'Collected' THEN 1 ELSE 0 END) AS PendingDocumentCount
        FROM dbo.SupportingDocuments sd WHERE sd.TrackerId=t.TrackerId
    ) docs
    OUTER APPLY
    (
        SELECT MAX(fu.CreatedDate) AS LastFollowUpDate FROM dbo.FollowUps fu WHERE fu.TrackerId=t.TrackerId
    ) lastf
    WHERE ts.StatusCode=N'FOLLOWUP'
    ORDER BY CASE WHEN t.NextFollowUpDate IS NULL THEN 0 ELSE 1 END,t.NextFollowUpDate,t.TrackerId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[Trackers];
    SELECT @TotalCount AS TotalCount;

    SELECT [TrackerId], [CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [NextFollowUpDate], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Trackers]
    ORDER BY [TrackerId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_GetQuotationPaymentHierarchy]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_GetQuotationPaymentHierarchy]
    @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @TrackerId IS NULL OR @TrackerId <= 0
        THROW 50080, 'Tracker is required.', 1;

    /* Result set 1: quotations belonging to this tracker. */
    SELECT
        q.QuotationId,
        q.TrackerId,
        q.QuotationMode,
        q.TrackerCategorySubCategorySpecId,
        q.CustomerCode,
        q.QuotationStatus,
        q.CreatedDate,
        q.CreatedBy,
        LTRIM(RTRIM(CONCAT(ISNULL(u.FirstName, N''), N' ', ISNULL(u.LastName, N'')))) AS CreatedByName,
        inv.InvoiceId,
        inv.InvoiceNumber,
        inv.InvoiceStatus,
        ISNULL(totals.GrandTotal, 0) AS GrandTotal,
        ISNULL(totals.PaidAmount, 0) AS PaidAmount,
        CASE
            WHEN ISNULL(totals.GrandTotal, 0) - ISNULL(totals.PaidAmount, 0) > 0
                THEN ISNULL(totals.GrandTotal, 0) - ISNULL(totals.PaidAmount, 0)
            ELSE 0
        END AS PendingAmount,
        CASE
            WHEN ISNULL(totals.GrandTotal, 0) > 0
                 AND ISNULL(totals.PaidAmount, 0) >= ISNULL(totals.GrandTotal, 0)
                THEN N'Completed'
            WHEN ISNULL(totals.PaidAmount, 0) > 0
                THEN N'Partial'
            ELSE N'Pending'
        END AS PaymentStatus
    FROM dbo.Quotations q
    LEFT JOIN dbo.Users u
        ON u.UserId = q.CreatedBy
    LEFT JOIN dbo.Invoices inv
        ON inv.QuotationId = q.QuotationId
    OUTER APPLY
    (
        SELECT
            ISNULL(SUM((ISNULL(qi.Amount, 0) + ISNULL(qi.VAT, 0)) * ISNULL(qi.Quantity, 1)), 0) AS GrandTotal,
            ISNULL(
                (
                    SELECT SUM(p.PaymentAmount)
                    FROM dbo.Payments p
                    WHERE p.QuotationId = q.QuotationId
                ),
                0
            ) AS PaidAmount
        FROM dbo.QuotationItems qi
        WHERE qi.QuotationId = q.QuotationId
    ) totals
    WHERE q.TrackerId = @TrackerId
    ORDER BY q.QuotationId DESC;

    /* Result set 2: payments belonging to quotations under this tracker. */
    SELECT
        p.PaymentId,
        p.PaymentNumber,
        p.TrackerId,
        p.QuotationId,
        p.PaymentDate,
        p.AmountDue,
        p.PaymentAmount,
        pm.MethodName AS PaymentMethodName,
        p.TransactionReference,
        p.PaymentStatus,
        p.Notes,
        p.CreatedBy,
        LTRIM(RTRIM(CONCAT(ISNULL(u.FirstName, N''), N' ', ISNULL(u.LastName, N'')))) AS CreatedByName
    FROM dbo.Payments p
    LEFT JOIN dbo.PaymentMethods pm
        ON pm.PaymentMethodId = p.PaymentMethodId
    LEFT JOIN dbo.Users u
        ON u.UserId = p.CreatedBy
    WHERE p.TrackerId = @TrackerId
    ORDER BY p.QuotationId DESC, p.PaymentDate DESC, p.PaymentId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_Insert]
    @CustomerId INT,
    @CustomerCode NVARCHAR(50),
    @CustomerName NVARCHAR(150),
    @Mobile NVARCHAR(20),
    @Email NVARCHAR(150),
    @AssignedToEmployeeId INT,
    @AssignedToEmployeeName NVARCHAR(150),
    @Status NVARCHAR(30),
    @FollowUpFrequencyDays INT = 5,
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[Trackers] ([CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [CreatedBy], [CreatedDate])
    VALUES (@CustomerId, @CustomerCode, @CustomerName, @Mobile, @Email, @AssignedToEmployeeId, @AssignedToEmployeeName, @Status, ISNULL(@FollowUpFrequencyDays, 5), @CreatedBy, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [TrackerId], [CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [NextFollowUpDate], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Trackers]
    WHERE [TrackerId] = @NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_SetFollowUpSettings]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Trackers_SetFollowUpSettings]
    @TrackerId INT,
    @FollowUpFrequencyId INT,
    @NextFollowUpDate DATE=NULL,
    @ModifiedBy INT=NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Days INT,@Manual BIT;
    SELECT @Days=FrequencyDays,@Manual=IsManual FROM dbo.FollowUpFrequencies WHERE FollowUpFrequencyId=@FollowUpFrequencyId AND IsActive=1;
    IF @Days IS NULL AND @Manual IS NULL THROW 51037,'Active follow-up frequency was not found.',1;

    UPDATE dbo.Trackers
    SET FollowUpFrequencyId=@FollowUpFrequencyId,
        FollowUpFrequencyDays=ISNULL(@Days,0),
        NextFollowUpDate=@NextFollowUpDate,
        ModifiedBy=@ModifiedBy,
        ModifiedDate=SYSUTCDATETIME()
    WHERE TrackerId=@TrackerId;

    IF @@ROWCOUNT=0 RETURN;
    SELECT TrackerId,CustomerId,CustomerCode,CustomerName,Mobile,Email,AssignedToEmployeeId,AssignedToEmployeeName,
           Status,FollowUpFrequencyDays,NextFollowUpDate,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate,FollowUpFrequencyId
    FROM dbo.Trackers WHERE TrackerId=@TrackerId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- NextFollowUpDate is intentionally NOT updatable here — it only changes via
-- usp_FollowUps_RecordAndReschedule, so a routine tracker edit can never
-- clobber the computed next-due date.
CREATE   PROCEDURE [dbo].[usp_Trackers_Update]
    @Id INT,
    @CustomerId INT,
    @CustomerCode NVARCHAR(50),
    @CustomerName NVARCHAR(150),
    @Mobile NVARCHAR(20),
    @Email NVARCHAR(150),
    @AssignedToEmployeeId INT,
    @AssignedToEmployeeName NVARCHAR(150),
    @Status NVARCHAR(30),
    @FollowUpFrequencyDays INT = 5,
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[Trackers]
    SET [CustomerId] = @CustomerId, [CustomerCode] = @CustomerCode, [CustomerName] = @CustomerName, [Mobile] = @Mobile, [Email] = @Email, [AssignedToEmployeeId] = @AssignedToEmployeeId, [AssignedToEmployeeName] = @AssignedToEmployeeName, [Status] = @Status, [FollowUpFrequencyDays] = ISNULL(@FollowUpFrequencyDays, 5), [ModifiedBy] = @ModifiedBy, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [TrackerId], [CustomerId], [CustomerCode], [CustomerName], [Mobile], [Email], [AssignedToEmployeeId], [AssignedToEmployeeName], [Status], [FollowUpFrequencyDays], [NextFollowUpDate], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[Trackers]
    WHERE [TrackerId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Trackers_UpdateStatus]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


CREATE   PROCEDURE [dbo].[usp_Trackers_UpdateStatus]
    @TrackerId INT,
    @Status NVARCHAR(30),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Normalized NVARCHAR(30);
    SELECT TOP (1) @Normalized=StatusCode
    FROM dbo.TrackerStatuses
    WHERE IsActive=1 AND UPPER(StatusCode)=UPPER(LTRIM(RTRIM(@Status)));
    IF @Normalized IS NULL THROW 50010, 'Invalid or inactive tracker status.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Trackers WHERE TrackerId=@TrackerId) THROW 50011, 'Tracker not found.', 1;
    UPDATE dbo.Trackers SET Status=@Normalized, ModifiedBy=@ModifiedBy, ModifiedDate=SYSUTCDATETIME() WHERE TrackerId=@TrackerId;
    SELECT TrackerId,Status,ModifiedBy,ModifiedDate FROM dbo.Trackers WHERE TrackerId=@TrackerId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerServiceSpecs_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* usp_TrackerServiceSpecs_Delete never returned a row count, so
   CrudControllerBase.Delete()'s ExecuteScalarAsync() read null and reported the
   delete as failed even when it succeeded - same bug class fixed elsewhere in
   this project (see usp_CustomerTypes_Delete, usp_TrackerCategorySubCategorySpecPersons_Delete). */
CREATE   PROCEDURE [dbo].[usp_TrackerServiceSpecs_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[TrackerServiceSpecs] WHERE [TrackerServiceSpecId] = @Id;
    SELECT @@ROWCOUNT;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerServiceSpecs_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerServiceSpecs_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [TrackerServiceSpecId], [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [ServiceId], [ServiceName], [Count], [Amount], [VAT], [Status], [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerServiceSpecs]
    WHERE [TrackerServiceSpecId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerServiceSpecs_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerServiceSpecs_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25,
    @TrackerId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;

    DECLARE @TotalCount INT;
    SELECT @TotalCount = COUNT(1)
    FROM [dbo].[TrackerServiceSpecs]
    WHERE @TrackerId IS NULL OR [TrackerId] = @TrackerId;

    SELECT @TotalCount AS TotalCount;

    SELECT [TrackerServiceSpecId], [TrackerCategorySubCategorySpecId], [TrackerId],
           [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName],
           [ServiceId], [ServiceName], [Count], [Amount], [VAT], [Status],
           [CreatedBy], [CreatedDate], [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerServiceSpecs]
    WHERE @TrackerId IS NULL OR [TrackerId] = @TrackerId
    ORDER BY [TrackerServiceSpecId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerServiceSpecs_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerServiceSpecs_Insert]
    @TrackerCategorySubCategorySpecId INT,
    @TrackerId INT,
    @CategoryId INT,
    @CategoryName NVARCHAR(100),
    @SubCategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @ServiceId INT = NULL,
    @ServiceName NVARCHAR(150),
    @Count INT = 1,
    @Amount DECIMAL(18,2),
    @VAT DECIMAL(5,2),
    @Status NVARCHAR(30),
    @CreatedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@ServiceName)), N'') IS NULL
        THROW 50002, 'ServiceName is required.', 1;

    INSERT INTO [dbo].[TrackerServiceSpecs]
    (
        [TrackerCategorySubCategorySpecId], [TrackerId], [CategoryId], [CategoryName],
        [SubCategoryId], [SubCategoryName], [ServiceId], [ServiceName], [Count], [Amount], [VAT],
        [Status], [CreatedBy], [CreatedDate]
    )
    VALUES
    (
        @TrackerCategorySubCategorySpecId, @TrackerId, @CategoryId, @CategoryName,
        @SubCategoryId, @SubCategoryName, @ServiceId, LTRIM(RTRIM(@ServiceName)), @Count, @Amount, @VAT,
        @Status, @CreatedBy, SYSUTCDATETIME()
    );

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());

    SELECT [TrackerServiceSpecId], [TrackerCategorySubCategorySpecId], [TrackerId],
           [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [ServiceId],
           [ServiceName], [Count], [Amount], [VAT], [Status], [CreatedBy], [CreatedDate],
           [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerServiceSpecs]
    WHERE [TrackerServiceSpecId] = @NewId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerServiceSpecs_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerServiceSpecs_Update]
    @Id INT,
    @TrackerCategorySubCategorySpecId INT,
    @TrackerId INT,
    @CategoryId INT,
    @CategoryName NVARCHAR(100),
    @SubCategoryId INT,
    @SubCategoryName NVARCHAR(100),
    @ServiceId INT = NULL,
    @ServiceName NVARCHAR(150),
    @Count INT = 1,
    @Amount DECIMAL(18,2),
    @VAT DECIMAL(5,2),
    @Status NVARCHAR(30),
    @ModifiedBy INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@ServiceName)), N'') IS NULL
        THROW 50002, 'ServiceName is required.', 1;

    UPDATE [dbo].[TrackerServiceSpecs]
    SET [TrackerCategorySubCategorySpecId] = @TrackerCategorySubCategorySpecId,
        [TrackerId] = @TrackerId,
        [CategoryId] = @CategoryId,
        [CategoryName] = @CategoryName,
        [SubCategoryId] = @SubCategoryId,
        [SubCategoryName] = @SubCategoryName,
        [ServiceId] = @ServiceId,
        [ServiceName] = LTRIM(RTRIM(@ServiceName)),
        [Count] = @Count,
        [Amount] = @Amount,
        [VAT] = @VAT,
        [Status] = @Status,
        [ModifiedBy] = @ModifiedBy,
        [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerServiceSpecId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;

    SELECT [TrackerServiceSpecId], [TrackerCategorySubCategorySpecId], [TrackerId],
           [CategoryId], [CategoryName], [SubCategoryId], [SubCategoryName], [ServiceId],
           [ServiceName], [Count], [Amount], [VAT], [Status], [CreatedBy], [CreatedDate],
           [ModifiedBy], [ModifiedDate]
    FROM [dbo].[TrackerServiceSpecs]
    WHERE [TrackerServiceSpecId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[TrackerStatuses] WHERE [TrackerStatusId] = @Id;
    SELECT @@ROWCOUNT;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_GetActive]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Lightweight active list for populating dropdowns (same convention as usp_PaymentMethods_GetActive).
CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_GetActive]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [TrackerStatusId], [StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal]
    FROM [dbo].[TrackerStatuses]
    WHERE [IsActive] = 1
    ORDER BY [DisplayOrder];
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [TrackerStatusId], [StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[TrackerStatuses] WHERE [TrackerStatusId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

------------------------------------------------------------
-- 3. Standard CRUD procs (matches every other lookup entity's naming convention,
--    so it plugs straight into the existing generic CrudControllerBase pattern)
------------------------------------------------------------
CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[TrackerStatuses];
    SELECT @TotalCount AS TotalCount;

    SELECT [TrackerStatusId], [StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[TrackerStatuses]
    ORDER BY [DisplayOrder], [TrackerStatusId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_Insert]
    @StatusCode NVARCHAR(30), @StatusName NVARCHAR(50), @DisplayOrder INT, @IsDefault BIT = 0, @IsTerminal BIT = 0, @IsActive BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    IF @IsDefault = 1 UPDATE [dbo].[TrackerStatuses] SET [IsDefault] = 0; -- only one default at a time

    INSERT INTO [dbo].[TrackerStatuses] ([StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal], [IsActive], [CreatedDate])
    VALUES (@StatusCode, @StatusName, @DisplayOrder, @IsDefault, @IsTerminal, @IsActive, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT [TrackerStatusId], [StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[TrackerStatuses] WHERE [TrackerStatusId] = @NewId;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatuses_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_TrackerStatuses_Update]
    @Id INT, @StatusCode NVARCHAR(30), @StatusName NVARCHAR(50), @DisplayOrder INT, @IsDefault BIT = 0, @IsTerminal BIT = 0, @IsActive BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    IF @IsDefault = 1 UPDATE [dbo].[TrackerStatuses] SET [IsDefault] = 0 WHERE [TrackerStatusId] <> @Id;

    UPDATE [dbo].[TrackerStatuses]
    SET [StatusCode] = @StatusCode, [StatusName] = @StatusName, [DisplayOrder] = @DisplayOrder, [IsDefault] = @IsDefault, [IsTerminal] = @IsTerminal, [IsActive] = @IsActive, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [TrackerStatusId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT [TrackerStatusId], [StatusCode], [StatusName], [DisplayOrder], [IsDefault], [IsTerminal], [IsActive], [CreatedDate], [ModifiedDate]
    FROM [dbo].[TrackerStatuses] WHERE [TrackerStatusId] = @Id;
END

GO
/****** Object:  StoredProcedure [dbo].[usp_TrackerStatusHistory_GetByTracker]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---- Read procedures (newest first). Status codes are shown as their friendly names. ---- */
CREATE   PROCEDURE [dbo].[usp_TrackerStatusHistory_GetByTracker] @TrackerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.TrackerStatusHistoryId AS HistoryId, CAST(NULL AS INT) AS QuotationId,
           h.OldStatus, h.NewStatus,
           COALESCE(os.StatusName, h.OldStatus) AS OldStatusName,
           COALESCE(ns.StatusName, h.NewStatus) AS NewStatusName,
           h.ChangedBy, LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) AS ChangedByName,
           h.ChangedDate, h.Note
    FROM dbo.TrackerStatusHistory h
    LEFT JOIN dbo.TrackerStatuses os ON os.StatusCode = h.OldStatus
    LEFT JOIN dbo.TrackerStatuses ns ON ns.StatusCode = h.NewStatus
    LEFT JOIN dbo.Users u ON u.UserId = h.ChangedBy
    WHERE h.TrackerId = @TrackerId
    ORDER BY h.ChangedDate DESC, h.TrackerStatusHistoryId DESC;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_Delete]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_Delete]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [dbo].[Users] WHERE [UserId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_GetById]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_GetById]
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u
    LEFT JOIN [dbo].[Roles] r ON r.RoleId = u.RoleId
    WHERE u.[UserId] = @Id;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_GetPaged]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[Users];
    SELECT @TotalCount AS TotalCount;

    SELECT [UserId], [FirstName], [LastName], [Email], [PasswordHash], [Status], [RoleId], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Users]
    ORDER BY [UserId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_Insert]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_Insert]
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Email NVARCHAR(200),
    @PasswordHash NVARCHAR(500),
    @Status NVARCHAR(20),
    @RoleId INT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[Users] ([FirstName], [LastName], [Email], [PasswordHash], [Status], [RoleId], [CreatedDate])
    VALUES (@FirstName, @LastName, @Email, @PasswordHash, @Status, @RoleId, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u LEFT JOIN [dbo].[Roles] r ON r.RoleId=u.RoleId
    WHERE u.UserId=@NewId;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_Login]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_Login]
    @Email NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u
    LEFT JOIN [dbo].[Roles] r ON r.RoleId=u.RoleId
    WHERE u.Email=@Email;
END
GO
/****** Object:  StoredProcedure [dbo].[usp_Users_Update]    Script Date: 10/9/2026 3:24:17 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[usp_Users_Update]
    @Id INT,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Email NVARCHAR(200),
    @PasswordHash NVARCHAR(500),
    @Status NVARCHAR(20),
    @RoleId INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[Users]
    SET [FirstName] = @FirstName, [LastName] = @LastName, [Email] = @Email, [PasswordHash] = COALESCE(@PasswordHash, [PasswordHash]), [Status] = @Status, [RoleId] = @RoleId, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [UserId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u LEFT JOIN [dbo].[Roles] r ON r.RoleId=u.RoleId
    WHERE u.UserId=@Id;
END
GO
