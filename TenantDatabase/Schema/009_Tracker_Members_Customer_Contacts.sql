/*
  009 - Renewal tracker members picked from the customer's contact persons
  =========================================================================
  Same script in two places:
    BusinessPanelApi/database/2026-10-23_tracker_members_from_customer_contacts.sql   run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/009_Tracker_Members_Customer_Contacts.sql      run automatically on every tenant database (BP_*)

  * TrackerFamilyMembers.CustomerContactId - the customer contact (employee) this tracker member is.
      Set when the member is picked from the customer; empty for family members and people typed in.
      If the contact is deleted on the Customers screen the link is cleared (the tracker keeps its copy).
  * TrackerFamilyMembers.SponsorContactId  - for a family member: the employee (customer contact) who sponsors them.
  * Relationship "Employee" (used for members picked from the customer's contacts).
  The tracker member procedures are not changed - the API writes the links itself.
  Safe to run more than once.
*/
SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'CustomerContactId') IS NULL
    ALTER TABLE dbo.TrackerFamilyMembers ADD CustomerContactId INT NULL;
GO
IF COL_LENGTH(N'dbo.TrackerFamilyMembers', N'SponsorContactId') IS NULL
    ALTER TABLE dbo.TrackerFamilyMembers ADD SponsorContactId INT NULL;
GO

/* Link to the contact; cleared automatically when the contact is deleted.
   (SponsorContactId has no foreign key: SQL Server allows only one SET NULL path from CustomerContacts.) */
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TrackerFamilyMembers_CustomerContacts')
    ALTER TABLE dbo.TrackerFamilyMembers WITH CHECK ADD CONSTRAINT FK_TrackerFamilyMembers_CustomerContacts
        FOREIGN KEY (CustomerContactId) REFERENCES dbo.CustomerContacts (ContactId) ON DELETE SET NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TrackerFamilyMembers_CustomerContactId' AND object_id = OBJECT_ID(N'dbo.TrackerFamilyMembers'))
    CREATE INDEX IX_TrackerFamilyMembers_CustomerContactId ON dbo.TrackerFamilyMembers (CustomerContactId) WHERE CustomerContactId IS NOT NULL;
GO

/* A sponsor that no longer exists is shown as "sponsor removed"; clear such ids. */
IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
    UPDATE f SET SponsorContactId = NULL
    FROM dbo.TrackerFamilyMembers f
    WHERE f.SponsorContactId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.CustomerContacts c WHERE c.ContactId = f.SponsorContactId);
GO

IF OBJECT_ID(N'dbo.Relationships', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Relationships WHERE LOWER(LTRIM(RTRIM(RelationshipName))) = N'employee')
    INSERT dbo.Relationships (RelationshipName, IsActive, CreatedDate) VALUES (N'Employee', 1, SYSUTCDATETIME());
GO
