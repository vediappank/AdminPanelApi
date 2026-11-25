/* 008 - Chat memory (conversation history) for the website AI assistant.
   Replaces n8n "Simple Memory" (which is lost on every restart). One row per chat message.
   Keyed by the chat session id the website widget sends (stored in the visitor's browser).
   Only the last few messages are read back for the model (see usp_WebChatMessage_Recent). Safe to run again. */

IF OBJECT_ID('dbo.WebChatSession', 'U') IS NULL
CREATE TABLE dbo.WebChatSession
(
    SessionId      VARCHAR(64)  NOT NULL CONSTRAINT PK_WebChatSession PRIMARY KEY,   -- from the widget, e.g. w1a2b3c...
    SubscriberId   INT          NULL,                                                -- set once the visitor signs in (email + WhatsApp verified)
    FirstSeenAt    DATETIME2(0) NOT NULL CONSTRAINT DF_WebChatSession_First DEFAULT (SYSUTCDATETIME()),
    LastSeenAt     DATETIME2(0) NOT NULL CONSTRAINT DF_WebChatSession_Last  DEFAULT (SYSUTCDATETIME()),
    MessageCount   INT          NOT NULL CONSTRAINT DF_WebChatSession_Count DEFAULT (0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatSession_Subscriber' AND object_id = OBJECT_ID('dbo.WebChatSession'))
    CREATE INDEX IX_WebChatSession_Subscriber ON dbo.WebChatSession (SubscriberId) WHERE SubscriberId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatSession_LastSeen' AND object_id = OBJECT_ID('dbo.WebChatSession'))
    CREATE INDEX IX_WebChatSession_LastSeen ON dbo.WebChatSession (LastSeenAt);
GO

IF OBJECT_ID('dbo.WebChatMessage', 'U') IS NULL
CREATE TABLE dbo.WebChatMessage
(
    WebChatMessageId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WebChatMessage PRIMARY KEY,
    SessionId        VARCHAR(64)    NOT NULL CONSTRAINT FK_WebChatMessage_Session REFERENCES dbo.WebChatSession(SessionId) ON DELETE CASCADE,
    Role             VARCHAR(10)    NOT NULL CONSTRAINT CK_WebChatMessage_Role CHECK (Role IN ('user','assistant','tool')),
    Content          NVARCHAR(MAX)  NOT NULL,                      -- message text (tool calls/results as JSON when Role = 'tool')
    ModelUsed        VARCHAR(60)    NULL,                          -- which AI answered (gemini / groq / openrouter ...)
    CreatedAt        DATETIME2(0)   NOT NULL CONSTRAINT DF_WebChatMessage_Created DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WebChatMessage_Session' AND object_id = OBJECT_ID('dbo.WebChatMessage'))
    CREATE INDEX IX_WebChatMessage_Session ON dbo.WebChatMessage (SessionId, WebChatMessageId DESC);
GO

/* Add a message and touch the session (creates the session on first use). */
CREATE OR ALTER PROCEDURE dbo.usp_WebChatMessage_Add
    @SessionId VARCHAR(64), @Role VARCHAR(10), @Content NVARCHAR(MAX), @ModelUsed VARCHAR(60) = NULL, @SubscriberId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.WebChatSession WHERE SessionId = @SessionId)
        INSERT dbo.WebChatSession (SessionId, SubscriberId) VALUES (@SessionId, @SubscriberId);
    INSERT dbo.WebChatMessage (SessionId, Role, Content, ModelUsed) VALUES (@SessionId, @Role, @Content, @ModelUsed);
    UPDATE dbo.WebChatSession
       SET LastSeenAt = SYSUTCDATETIME(), MessageCount = MessageCount + 1,
           SubscriberId = COALESCE(@SubscriberId, SubscriberId)
     WHERE SessionId = @SessionId;
END
GO

/* Last N messages of a session, oldest first - what the model gets as memory. */
CREATE OR ALTER PROCEDURE dbo.usp_WebChatMessage_Recent
    @SessionId VARCHAR(64), @Take INT = 16
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Role, Content, CreatedAt
      FROM (SELECT TOP (@Take) WebChatMessageId, Role, Content, CreatedAt
              FROM dbo.WebChatMessage WHERE SessionId = @SessionId
             ORDER BY WebChatMessageId DESC) x
     ORDER BY WebChatMessageId;
END
GO

/* Housekeeping: delete chats not used for N days (run from a SQL Agent job, or call occasionally). */
CREATE OR ALTER PROCEDURE dbo.usp_WebChatSession_Purge
    @OlderThanDays INT = 90
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.WebChatSession WHERE LastSeenAt < DATEADD(DAY, -@OlderThanDays, SYSUTCDATETIME());
    SELECT @@ROWCOUNT AS DeletedSessions;
END
GO
