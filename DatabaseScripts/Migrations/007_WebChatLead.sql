/* 007 - AI chat journey on top of the website leads (dbo.WebEnquiry).
   One chat lead = one WebEnquiry row (so it already shows in the Admin Panel inbox and converts to a Sales Enquiry),
   plus these three small tables for what the chat adds. Safe to run again. */

IF OBJECT_ID('dbo.WebChatLead', 'U') IS NULL
CREATE TABLE dbo.WebChatLead
(
    WebChatLeadId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebChatLead PRIMARY KEY,
    SubscriberId         INT           NOT NULL,                       -- verified website account (email + WhatsApp)
    WebEnquiryId         BIGINT        NOT NULL CONSTRAINT FK_WebChatLead_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId),
    ServiceInterest      NVARCHAR(200) NULL,                           -- the one service the visitor picked
    DraftService         NVARCHAR(200) NULL,
    DraftSummary         NVARCHAR(2000) NULL,                          -- draft price list shown in the chat
    DraftAt              DATETIME2(0)  NULL,
    QuotationRequestedAt DATETIME2(0)  NULL,
    Decision             VARCHAR(20)   NULL CONSTRAINT CK_WebChatLead_Decision CHECK (Decision IN ('approved','rejected')),
    DecisionAt           DATETIME2(0)  NULL,
    CreatedAt            DATETIME2(0)  NOT NULL CONSTRAINT DF_WebChatLead_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WebChatLead_Enquiry' AND object_id = OBJECT_ID('dbo.WebChatLead'))
    CREATE UNIQUE INDEX UX_WebChatLead_Enquiry ON dbo.WebChatLead (WebEnquiryId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatLead_Subscriber' AND object_id = OBJECT_ID('dbo.WebChatLead'))
    CREATE INDEX IX_WebChatLead_Subscriber ON dbo.WebChatLead (SubscriberId, WebChatLeadId DESC);
GO

IF OBJECT_ID('dbo.WebChatQuestion', 'U') IS NULL
CREATE TABLE dbo.WebChatQuestion
(
    WebChatQuestionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebChatQuestion PRIMARY KEY,
    WebEnquiryId      BIGINT        NOT NULL CONSTRAINT FK_WebChatQuestion_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId),
    SubscriberId      INT           NOT NULL,
    Question          NVARCHAR(500) NOT NULL,
    Matched           BIT           NOT NULL,                          -- answered from the Q&A list / company information
    CreatedAt         DATETIME2(0)  NOT NULL CONSTRAINT DF_WebChatQuestion_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatQuestion_Enquiry' AND object_id = OBJECT_ID('dbo.WebChatQuestion'))
    CREATE INDEX IX_WebChatQuestion_Enquiry ON dbo.WebChatQuestion (WebEnquiryId, CreatedAt);
GO

IF OBJECT_ID('dbo.WebChatTask', 'U') IS NULL
CREATE TABLE dbo.WebChatTask
(
    WebChatTaskId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebChatTask PRIMARY KEY,
    WebEnquiryId  BIGINT        NOT NULL CONSTRAINT FK_WebChatTask_WebEnquiry REFERENCES dbo.WebEnquiry(WebEnquiryId),
    SubscriberId  INT           NOT NULL,
    TaskType      VARCHAR(20)   NOT NULL CONSTRAINT CK_WebChatTask_Type CHECK (TaskType IN ('question','offer','quotation')),
    DueAt         DATETIME2(0)  NOT NULL,
    Note          NVARCHAR(500) NULL,
    Status        VARCHAR(12)   NOT NULL CONSTRAINT DF_WebChatTask_Status DEFAULT ('Open') CONSTRAINT CK_WebChatTask_Status CHECK (Status IN ('Open','Done')),
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_WebChatTask_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CompletedAt   DATETIME2(0)  NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatTask_Open' AND object_id = OBJECT_ID('dbo.WebChatTask'))
    CREATE INDEX IX_WebChatTask_Open ON dbo.WebChatTask (Status, DueAt) INCLUDE (WebEnquiryId, TaskType);
GO
