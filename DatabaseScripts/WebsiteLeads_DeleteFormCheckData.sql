/* =====================================================================================
   Deletes the test enquiries / CVs created by bp-form-test.html (local form check).
   They all use the email formcheck@example.com. Run against the database the local API
   uses (appsettings.Development.json -> SuperAdminPanel). Safe to re-run.
   The test CV files themselves are in Website:CvStoragePath (default <api>\App_Data\cv) -
   the last result grid lists their stored names so you can delete them too.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Email NVARCHAR(200) = N'formcheck@example.com';

BEGIN TRAN;

DELETE h FROM dbo.WebStatusHistory h
JOIN dbo.WebEnquiry e ON h.EntityType = 'Enquiry' AND h.EntityId = e.WebEnquiryId
WHERE e.Email = @Email;

DELETE dbo.WebEnquiry WHERE Email = @Email;          -- travel / CRM / module rows cascade
PRINT CONCAT(@@ROWCOUNT, ' test enquiries deleted.');

SELECT CvFilePath AS TestCvFileToDelete FROM dbo.WebJobApplication WHERE Email = @Email;

DELETE h FROM dbo.WebStatusHistory h
JOIN dbo.WebJobApplication j ON h.EntityType = 'JobApplication' AND h.EntityId = j.WebJobApplicationId
WHERE j.Email = @Email;

DELETE dbo.WebJobApplication WHERE Email = @Email;
PRINT CONCAT(@@ROWCOUNT, ' test job applications deleted.');

COMMIT;
