/*
  Sales Pipeline ▸ Invoice
  Target: SuperAdminPanel
  - Creates dbo.SalesInvoices: one invoice per Accepted quotation (unique QuotationId).
    Amounts are copied from the quotation when the invoice is created.
  - Status: Created -> Sent (Email / WhatsApp). Paid / balance are computed from dbo.Payments
    (QuotationId), not stored here.
  - From now on PlatformApi only accepts a sale-side payment once the quotation has an invoice.
  Safe to re-run.
*/
USE [SuperAdminPanel];
GO

IF OBJECT_ID(N'dbo.SalesInvoices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesInvoices
    (
        InvoiceId     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesInvoices PRIMARY KEY,
        InvoiceNumber NVARCHAR(30)   NOT NULL,
        QuotationId   INT            NOT NULL,
        InvoiceDate   DATETIME2(7)   NOT NULL,
        DueDate       DATETIME2(7)   NULL,
        NetAmount     DECIMAL(18,2)  NOT NULL,
        VatAmount     DECIMAL(18,2)  NOT NULL,
        GrandTotal    DECIMAL(18,2)  NOT NULL,
        Status        NVARCHAR(20)   NOT NULL CONSTRAINT DF_SalesInvoices_Status      DEFAULT (N'Created'),
        SentDate      DATETIME2(7)   NULL,
        SentVia       NVARCHAR(20)   NULL,
        SentCount     INT            NOT NULL CONSTRAINT DF_SalesInvoices_SentCount   DEFAULT (0),
        Notes         NVARCHAR(MAX)  NULL,
        CreatedDate   DATETIME2(7)   NOT NULL CONSTRAINT DF_SalesInvoices_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy     NVARCHAR(100)  NULL,
        ModifiedDate  DATETIME2(7)   NULL,
        CONSTRAINT FK_SalesInvoices_Quotations FOREIGN KEY (QuotationId) REFERENCES dbo.Quotations (QuotationId),
        CONSTRAINT CK_SalesInvoices_Status CHECK (Status IN (N'Created', N'Sent')),
        CONSTRAINT CK_SalesInvoices_Amounts CHECK (NetAmount >= 0 AND VatAmount >= 0 AND GrandTotal >= 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SalesInvoices') AND name = N'IX_SalesInvoices_QuotationId')
    CREATE UNIQUE INDEX IX_SalesInvoices_QuotationId ON dbo.SalesInvoices (QuotationId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SalesInvoices') AND name = N'IX_SalesInvoices_InvoiceNumber')
    CREATE UNIQUE INDEX IX_SalesInvoices_InvoiceNumber ON dbo.SalesInvoices (InvoiceNumber);
GO

/*
  Existing sale-side payments recorded BEFORE invoices existed stay as they are - they still
  count towards the quotation's paid amount. To issue invoices for quotations that already
  have payments, mark them Accepted and use "Create invoice" in Sales Pipeline.

  Check:
SELECT i.InvoiceNumber, q.QuoteNo, i.GrandTotal,
       ISNULL((SELECT SUM(p.Amount) FROM dbo.Payments p WHERE p.QuotationId = i.QuotationId), 0) AS Paid,
       i.Status, i.SentVia, i.SentDate
FROM dbo.SalesInvoices i JOIN dbo.Quotations q ON q.QuotationId = i.QuotationId
ORDER BY i.InvoiceId DESC;
*/
