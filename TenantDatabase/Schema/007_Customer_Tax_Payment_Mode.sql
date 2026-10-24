/*
  007 - Customer corporate tax / VAT dates, Payment Mode
  ======================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-23_customer_tax_dates_and_payment_mode.sql   run by hand on the shared BusinessPanel database
                                                                                    (after 2026-10-23_customer_client_id_and_contacts.sql)
    AdminPanelApi/TenantDatabase/Schema/007_Customer_Tax_Payment_Mode.sql           run automatically on every tenant database (BP_*)

  * CustomerProfiles: CorporateTaxIssueDate, CorporateTaxFilingDate, VatRegistrationDate.
  * Payments: PaymentMode = 'Advance' | 'Partial Payment' | 'Full Payment'.
    Existing payments are filled in: a quotation paid in one payment = Full Payment, otherwise Partial Payment.
  The payment stored procedures are not changed - the API saves and reads PaymentMode itself.
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.CustomerProfiles', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerProfiles', N'CorporateTaxIssueDate') IS NULL
    ALTER TABLE dbo.CustomerProfiles ADD CorporateTaxIssueDate DATE NULL;
GO
IF OBJECT_ID(N'dbo.CustomerProfiles', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerProfiles', N'CorporateTaxFilingDate') IS NULL
    ALTER TABLE dbo.CustomerProfiles ADD CorporateTaxFilingDate DATE NULL;
GO
IF OBJECT_ID(N'dbo.CustomerProfiles', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CustomerProfiles', N'VatRegistrationDate') IS NULL
    ALTER TABLE dbo.CustomerProfiles ADD VatRegistrationDate DATE NULL;
GO

IF COL_LENGTH(N'dbo.Payments', N'PaymentMode') IS NULL
    ALTER TABLE dbo.Payments ADD PaymentMode NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Payments_PaymentMode')
    ALTER TABLE dbo.Payments WITH CHECK ADD CONSTRAINT CK_Payments_PaymentMode
        CHECK (PaymentMode IS NULL OR PaymentMode IN (N'Advance', N'Partial Payment', N'Full Payment'));
GO

/* Existing payments: one payment that settled the whole quotation = Full Payment, everything else = Partial Payment. */
UPDATE p
SET PaymentMode = CASE WHEN p.PaymentStatus = N'Paid'
                            AND NOT EXISTS (SELECT 1 FROM dbo.Payments o WHERE o.QuotationId = p.QuotationId AND o.PaymentId <> p.PaymentId)
                       THEN N'Full Payment' ELSE N'Partial Payment' END
FROM dbo.Payments p
WHERE p.PaymentMode IS NULL;
GO
