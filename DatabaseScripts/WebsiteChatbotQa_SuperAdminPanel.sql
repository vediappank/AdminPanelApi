/* =====================================================================================
   Website Chatbot Q&A - SuperAdminPanel
   The questions and answers the website chatbot (blisspointworld.com, the "Ask us" button)
   answers from. Staff add and publish them in the Admin Panel instead of editing js/bp-chat.js.

     Admin Panel > Website Leads > Chatbot Q&A     add / edit / publish / delete
     GET  api/public/chatbot-qa                       website: published items only
     api/platform/chatbot-qa/...                      Admin Panel (module WEBSITE_CHATBOT)

   1. Table    dbo.WebsiteChatbotQa
   2. Seed     the current website answers, published (skipped if already present)
   3. Menu     WEBSITE_CHATBOT under Website Leads, granted to every role that has WEBSITE

   Set @Email in section 3 to your Admin Panel login, then run (F5) against the database your
   AdminPanelApi connects to. Safe to re-run.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. TABLE ------------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.WebsiteChatbotQa', 'U') IS NULL
CREATE TABLE dbo.WebsiteChatbotQa
(
    ChatbotQaId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebsiteChatbotQa PRIMARY KEY,
    Category      NVARCHAR(60)   NOT NULL,
    Question      NVARCHAR(300)  NOT NULL,
    Answer        NVARCHAR(1000) NOT NULL,
    DisplayOrder  INT            NOT NULL CONSTRAINT DF_WebsiteChatbotQa_DisplayOrder DEFAULT (0),
    Status        VARCHAR(10)    NOT NULL CONSTRAINT DF_WebsiteChatbotQa_Status DEFAULT ('Draft'),
    IsDeleted     BIT            NOT NULL CONSTRAINT DF_WebsiteChatbotQa_IsDeleted DEFAULT (0),
    CreatedAt     DATETIME2(0)   NOT NULL CONSTRAINT DF_WebsiteChatbotQa_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy     NVARCHAR(100)  NULL,
    UpdatedAt     DATETIME2(0)   NULL,
    UpdatedBy     NVARCHAR(100)  NULL,
    CONSTRAINT CK_WebsiteChatbotQa_Status CHECK (Status IN ('Draft', 'Published'))
);
GO

/* 2. SEED -------------------------------------------------------------------------------- */
INSERT dbo.WebsiteChatbotQa (Category, Question, Answer, DisplayOrder, Status, CreatedBy, UpdatedBy)
SELECT s.Category, s.Question, s.Answer, s.DisplayOrder, 'Published', 'seed', 'seed'
FROM (
    SELECT N'PRO & visas' AS Category, N'What is a Corporate PRO service?' AS Question, N'A Corporate PRO service is outsourced Public Relations Officer support. We prepare, submit and follow up your company''s government transactions, such as visas, labour and work permits, Emirates ID, licence renewals and department approvals, on your behalf.' AS Answer, 0 AS DisplayOrder
    UNION ALL
    SELECT N'PRO & visas', N'Why should we outsource PRO services instead of hiring a full-time PRO?', N'You avoid the salary, visa, leave and training costs of a full-time employee, and you get an experienced team with cover when someone is away. You pay for the scope your company actually needs.', 10
    UNION ALL
    SELECT N'PRO & visas', N'Which companies can use your monthly packages?', N'Mainland and free zone companies of any size, from small businesses with a few employees to larger organisations with several licences or entities. We recommend a package based on your employees, licences and expected transactions.', 20
    UNION ALL
    SELECT N'PRO & visas', N'Are government fees included in the monthly package?', N'No. The monthly fee covers our professional service. Government fees, authority charges, medical fees, Emirates ID fees, insurance, penalties, courier costs and other third-party charges are excluded unless specifically stated in your signed quotation.', 30
    UNION ALL
    SELECT N'PRO & visas', N'Can you guarantee visa approval or processing time?', N'No. Approvals and processing times are decided by the relevant UAE government authorities. We make sure applications are complete and accurate, submit them promptly and keep you updated at every stage.', 40
    UNION ALL
    SELECT N'PRO & visas', N'Can I customise my monthly package?', N'Yes. The packages are a starting point. Your final quotation reflects your number of employees and licences, the types and volume of transactions, renewal frequency and any agreed response times or reports. Transactions outside the agreed scope are quoted separately and processed after your approval.', 50
    UNION ALL
    SELECT N'PRO & visas', N'How do renewal reminders work?', N'We record the expiry dates of the licences, visas, Emirates IDs and documents agreed with you, then send advance reminders and follow up on a schedule so renewals start well before the deadline.', 60
    UNION ALL
    SELECT N'PRO & visas', N'How do we start?', N'Request a proposal using the form on this page or message us on WhatsApp. We review your requirements, send a customised quotation, and once the agreement and onboarding documents are in place we start coordinating your transactions.', 70
    UNION ALL
    SELECT N'PRO & visas', N'Do you handle urgent requests?', N'Yes, where the relevant authority offers urgent or VIP processing. Any urgent service charges and the related government fees are confirmed with you before we proceed.', 80
    UNION ALL
    SELECT N'Trade licence', N'What is a trade license in the UAE?', N'A trade license is the formal approval that allows a business to carry out specific approved activities under the relevant UAE authority. The exact license type depends on the business model, jurisdiction, and category of activity.', 0
    UNION ALL
    SELECT N'Trade licence', N'What documents are usually required for trade license processing?', N'The required documents often include passport copies, Emirates ID or visa copy where applicable, business activity details, trade name options, NOC where required, and existing license records for amendments or renewals. Exact requirements vary by authority and activity.', 10
    UNION ALL
    SELECT N'Trade licence', N'How long does trade license processing usually take?', N'In many straightforward cases, trade license processing can move within 3 to 7 business days once the activity is confirmed and the documents are complete. The final timeline depends on the authority, approval route, and whether any extra clearances are required.', 20
    UNION ALL
    SELECT N'Trade licence', N'Does Bliss Point also help with amendments and renewals?', N'Yes. Trade license support can also include amendments, renewals, activity updates, and related document coordination depending on the case. Bliss Point positions its service around helping clients manage those practical stages with fewer delays.', 30
    UNION ALL
    SELECT N'Accounting & VAT', N'What types of businesses do you work with?', N'We support startups, SMEs, mainland companies, free zone entities, and multi-entity groups across the UAE. Bookkeeping & Reporting VAT & Payroll Support Ongoing Compliance Help', 0
    UNION ALL
    SELECT N'Accounting & VAT', N'Can you work with our existing accounting software?', N'Yes. We can work with your current setup and support businesses using QuickBooks, Xero, Zoho Books, Tally, Sage, and similar systems. Cloud & desktop software support Migration help if needed Structured monthly workflow', 10
    UNION ALL
    SELECT N'Accounting & VAT', N'What do you need from us to get started?', N'We usually start with your trade license details, VAT status, bank statements, sales and expense records, and payroll information if applicable. Sales & expense records Bank & payroll details VAT documents if registered', 20
    UNION ALL
    SELECT N'Accounting & VAT', N'Do you provide VAT filing and compliance reminders?', N'Yes. We prepare and support VAT filings and help you stay ahead of submission timelines with compliance alerts and document follow-up. VAT filing support Submission reminders Compliance tracking', 30
    UNION ALL
    SELECT N'Accounting & VAT', N'Will we receive regular reports and updates?', N'Yes. Depending on your package, we provide regular financial reports, reconciliations, and status updates so you always know where your accounts stand. Monthly reports Reconciliation updates Dedicated support contact', 40
    UNION ALL
    SELECT N'CRM software', N'Where is my data hosted?', N'Bliss Point CRM runs as a secure cloud application on our managed servers. Ask us about hosting options, including a dedicated setup for your company, during your demo.', 0
    UNION ALL
    SELECT N'CRM software', N'Is my data secure?', N'Access is protected by user accounts and role-based permissions, and every connection is encrypted (HTTPS).', 10
    UNION ALL
    SELECT N'CRM software', N'How long does onboarding take?', N'Most teams start using the system within a few days. We help you import your customers and renewals and train your staff.', 20
    UNION ALL
    SELECT N'CRM software', N'Can I try it before buying?', N'Yes. Book a demo or choose “Start a free trial” in the form – we walk you through the system and set up a guided trial for your team.', 30
    UNION ALL
    SELECT N'CRM software', N'Does it support Arabic?', N'Tell us your language needs when you book a demo and we will confirm what is available for your team.', 40
    UNION ALL
    SELECT N'CRM software', N'Can it be customised for my business?', N'Renewal types, services, reminders and user roles are configurable. Larger changes can be scoped as part of an Enterprise plan.', 50
    UNION ALL
    SELECT N'CRM software', N'How long does it take to get access?', N'Usually one working day after we confirm your details. Larger teams with data import may take a few days.', 60
    UNION ALL
    SELECT N'CRM software', N'Who receives the login details?', N'The admin named in the request. They can then add colleagues and set their roles from Users & Roles.', 70
    UNION ALL
    SELECT N'CRM software', N'Is there a free trial?', N'Yes. Mention it in your request and we will set up a trial workspace for your team.', 80
    UNION ALL
    SELECT N'CRM software', N'Can my customers log in?', N'The CRM is for your staff. A customer portal for your clients is on our roadmap.', 90
    UNION ALL
    SELECT N'CRM software', N'I forgot my password. What do I do?', N'Use Forgot password on the login page, or ask your company admin to reset it.', 100
) s
WHERE NOT EXISTS (SELECT 1 FROM dbo.WebsiteChatbotQa q WHERE q.Question = s.Question AND q.IsDeleted = 0);
GO

/* 3. MENU - Website Leads > Chatbot Q&A -------------------------------------------------- */
DECLARE @Email NVARCHAR(256) = N'your-admin-login@example.com';   -- <- set your Admin Panel login
DECLARE @Now DATETIME2(0) = SYSUTCDATETIME();

DECLARE @Parent INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE');
IF @Parent IS NULL
    RAISERROR('Module WEBSITE is missing - run WebsiteLeads_SuperAdminPanel.sql first.', 16, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_CHATBOT')
    INSERT dbo.Modules (ModuleCode, ModuleName, Description, IsActive, IsCore, DisplayOrder, CreatedDate, ParentModuleId, MenuUrl, Icon)
    VALUES ('WEBSITE_CHATBOT', N'Chatbot Q&A', N'Questions and answers the website chatbot uses', 1, 0, 6, @Now, @Parent, '/website/chatbot-qa', 'quiz');
ELSE
    UPDATE dbo.Modules SET ParentModuleId = @Parent, MenuUrl = '/website/chatbot-qa', Icon = 'quiz', DisplayOrder = 6, IsActive = 1, ModifiedDate = @Now
     WHERE ModuleCode = 'WEBSITE_CHATBOT';

DECLARE @ChatId INT = (SELECT ModuleId FROM dbo.Modules WHERE ModuleCode = 'WEBSITE_CHATBOT');

-- every role that already has WEBSITE gets the new menu item
INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
SELECT rm.PlatformRoleId, @ChatId
FROM dbo.PlatformRoleModules rm
WHERE rm.ModuleId = @Parent
  AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = rm.PlatformRoleId AND x.ModuleId = @ChatId);

-- and the admin login used above (optional)
DECLARE @RoleId INT = (SELECT TOP 1 u.PlatformRoleId FROM dbo.PlatformUsers u WHERE u.Email = @Email);
IF @RoleId IS NOT NULL
BEGIN
    INSERT dbo.PlatformRoleModules (PlatformRoleId, ModuleId)
    SELECT @RoleId, m.ModuleId FROM dbo.Modules m
    WHERE m.ModuleCode IN ('WEBSITE', 'WEBSITE_CHATBOT')
      AND NOT EXISTS (SELECT 1 FROM dbo.PlatformRoleModules x WHERE x.PlatformRoleId = @RoleId AND x.ModuleId = m.ModuleId);
    PRINT CONCAT('Granted WEBSITE and WEBSITE_CHATBOT to role id ', @RoleId, ' (', @Email, ').');
END
ELSE
    PRINT CONCAT('No platform user with email ', @Email, ' - set @Email and run section 3 again.');
GO
