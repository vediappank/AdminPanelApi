/* =====================================================================================
   Travel & Tourism removed from the website (Oct 2026) - remove its forms from the
   Admin Panel (Website Leads > Website Forms).
   - Deletes every travel form that never received an enquiry.
   - A travel form that DID receive enquiries is switched off and hidden from the
     Website Forms list instead (deleting it would delete those enquiries).
   - Global Visa Services stays on the website: moved from Travel & Tourism to Typing Center.
   Run against SuperAdminPanel, local and production. Safe to re-run.
   Also re-run WebsiteLeads_SuperAdminPanel.sql (updated) so the forms list hides travel.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

-- Global Visa Services: a visa service, not travel
UPDATE dbo.WebForm SET Category = 'Typing Center', FormType = 'Service enquiry', UpdatedAt = SYSUTCDATETIME(), UpdatedBy = N'travel-removed'
 WHERE FormCode = 'service-global-visa' AND Category = 'Travel & Tourism';
UPDATE e SET e.Category = 'Typing Center'
  FROM dbo.WebEnquiry e JOIN dbo.WebForm f ON f.WebFormId = e.WebFormId
 WHERE f.FormCode = 'service-global-visa' AND e.Category = 'Travel & Tourism';

-- travel forms with no enquiries: delete
DELETE f FROM dbo.WebForm f
 WHERE f.Category = 'Travel & Tourism'
   AND NOT EXISTS (SELECT 1 FROM dbo.WebEnquiry e WHERE e.WebFormId = f.WebFormId);
PRINT CONCAT(@@ROWCOUNT, ' travel form(s) deleted.');

-- travel forms that already have enquiries: keep the history, switch the form off
UPDATE dbo.WebForm SET IsActive = 0, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = N'travel-removed'
 WHERE Category = 'Travel & Tourism' AND IsActive = 1;
PRINT CONCAT(@@ROWCOUNT, ' travel form(s) kept only for their existing enquiries (switched off, hidden from the list).');

COMMIT;

-- hide them from Admin Panel > Website Forms (same change as in WebsiteLeads_SuperAdminPanel.sql)
GO
CREATE OR ALTER PROCEDURE dbo.usp_WebForm_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT f.WebFormId, f.FormCode, f.FormName, f.PagePath, f.Category, f.FormType, f.TargetTable, f.NotifyEmail, f.IsActive,
           ISNULL(s.Total, 0) AS TotalSubmissions, ISNULL(s.Last30Days, 0) AS Last30Days, s.LastSubmittedAt
    FROM dbo.WebForm f
    OUTER APPLY (
        SELECT COUNT(*) AS Total,
               SUM(CASE WHEN e.CreatedAt >= DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Last30Days,
               MAX(e.CreatedAt) AS LastSubmittedAt
        FROM dbo.WebEnquiry e WHERE e.WebFormId = f.WebFormId AND e.IsSpam = 0
    ) s
    WHERE f.Category <> 'Travel & Tourism'          -- Travel & Tourism discontinued (Oct 2026)
    ORDER BY f.Category, f.FormName;
END
GO

SELECT COUNT(*) AS FormsShownInAdminPanel FROM dbo.WebForm WHERE Category <> 'Travel & Tourism';
