using Dapper;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Data access for the AI chat journey (DatabaseScripts\Migrations\007_WebChatLead.sql).
/// A chat lead is a normal website lead (dbo.WebEnquiry) created through the existing procedure, so it appears in the
/// Admin Panel inbox, keeps its status history and can be converted to a Sales Enquiry with the existing procedure.
/// </summary>
public sealed class ChatLeadStore
{
    public const int MaxQuestionsPerHour = 60;
    public const int MaxTasksPerHour = 10;

    private readonly string _connection;
    public ChatLeadStore(IConfiguration config) => _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;

    private SqlConnection Open() => new(_connection);
    private static CommandDefinition Cmd(string sql, object? args = null, CancellationToken ct = default) => new(sql, args, commandTimeout: 30, cancellationToken: ct);
    private static string Cut(string? s, int max) { var t = (s ?? string.Empty).Trim(); return t.Length > max ? t[..max] : t; }

    public sealed class Lead
    {
        public int WebChatLeadId { get; set; }
        public int SubscriberId { get; set; }
        public long WebEnquiryId { get; set; }
        public string? ServiceInterest { get; set; }
        public DateTime? DraftAt { get; set; }
        public DateTime? QuotationRequestedAt { get; set; }
        public string? Decision { get; set; }
        public string Status { get; set; } = "New";
    }

    private sealed class InsertResult { public long WebEnquiryId { get; set; } public string ReferenceNo { get; set; } = string.Empty; }

    private const string LeadSelect = @"SELECT TOP (1) l.WebChatLeadId, l.SubscriberId, l.WebEnquiryId, l.ServiceInterest, l.DraftAt, l.QuotationRequestedAt, l.Decision, e.Status
FROM dbo.WebChatLead l JOIN dbo.WebEnquiry e ON e.WebEnquiryId = l.WebEnquiryId";

    /// <summary>The visitor's open chat lead; a new one (a normal website lead) when there is none yet.</summary>
    public async Task<Lead> GetOrCreateLeadAsync(SubscriberStore.Subscriber s, CancellationToken ct)
    {
        await using var cn = Open();
        var open = await cn.QuerySingleOrDefaultAsync<Lead>(Cmd(
            $"{LeadSelect} WHERE l.SubscriberId = @id AND e.Status NOT IN ('Converted','Closed','Rejected') ORDER BY l.WebChatLeadId DESC", new { id = s.SubscriberId }, ct));
        if (open != null) return open;

        var row = await cn.QuerySingleAsync<InsertResult>(new CommandDefinition("dbo.usp_WebEnquiry_Insert",
            new
            {
                FormCode = "ai-agent", PagePath = "ai-agent", PageTitle = "AI assistant chat", RequestType = "Enquiry",
                FullName = Cut(s.Name, 120), Email = s.Email, Phone = "+" + Cut(s.WhatsApp, 28),
                Message = "Chat enquiry from the website assistant (email and WhatsApp verified).",
                ConsentGiven = true, SourcePage = "ai-agent chat",
            }, commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));

        await cn.ExecuteAsync(Cmd(@"
IF NOT EXISTS (SELECT 1 FROM dbo.WebChatLead WHERE WebEnquiryId = @enq)
    INSERT dbo.WebChatLead (SubscriberId, WebEnquiryId) VALUES (@id, @enq);", new { id = s.SubscriberId, enq = row.WebEnquiryId }, ct));
        return await cn.QuerySingleAsync<Lead>(Cmd($"{LeadSelect} WHERE l.WebEnquiryId = @enq", new { enq = row.WebEnquiryId }, ct));
    }

    private static Task Note(SqlConnection cn, long enquiryId, string status, string note, CancellationToken ct) =>
        cn.ExecuteAsync(Cmd(@"INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
VALUES ('Enquiry', @enquiryId, @status, @status, @note, N'ai-chat');", new { enquiryId, status, note }, ct));

    public async Task<bool> LogQuestionAsync(Lead lead, string question, bool matched, CancellationToken ct)
    {
        await using var cn = Open();
        var n = await cn.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM dbo.WebChatQuestion WHERE SubscriberId = @id AND CreatedAt > DATEADD(HOUR, -1, SYSUTCDATETIME())", new { id = lead.SubscriberId }, ct));
        if (n >= MaxQuestionsPerHour) return false;
        await cn.ExecuteAsync(Cmd("INSERT dbo.WebChatQuestion (WebEnquiryId, SubscriberId, Question, Matched) VALUES (@enq, @id, @q, @matched)",
            new { enq = lead.WebEnquiryId, id = lead.SubscriberId, q = Cut(question, 500), matched }, ct));
        return true;
    }

    public async Task SetPriorityAsync(Lead lead, string service, CancellationToken ct)
    {
        await using var cn = Open();
        var s = Cut(service, 200);
        await cn.ExecuteAsync(Cmd(@"UPDATE dbo.WebChatLead SET ServiceInterest = @s WHERE WebChatLeadId = @id;
UPDATE dbo.WebEnquiry SET ServiceInterest = @s, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = N'ai-chat' WHERE WebEnquiryId = @enq;",
            new { s, id = lead.WebChatLeadId, enq = lead.WebEnquiryId }, ct));
        await Note(cn, lead.WebEnquiryId, lead.Status, $"Visitor's priority service (chat): {s}", ct);
    }

    public async Task SaveDraftAsync(Lead lead, string service, string summary, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"UPDATE dbo.WebChatLead SET DraftService = @service, DraftSummary = @summary, DraftAt = SYSUTCDATETIME(),
    ServiceInterest = COALESCE(ServiceInterest, @service) WHERE WebChatLeadId = @id;", new { service = Cut(service, 200), summary = Cut(summary, 2000), id = lead.WebChatLeadId }, ct));
        await Note(cn, lead.WebEnquiryId, lead.Status, $"Draft price list shown in chat for {Cut(service, 120)}", ct);
    }

    /// <summary>type: question | offer | quotation. Shows the lead as unread in the Admin Panel inbox.</summary>
    public async Task<DateTime?> CreateTaskAsync(Lead lead, string type, int hours, string? note, CancellationToken ct)
    {
        await using var cn = Open();
        var n = await cn.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM dbo.WebChatTask WHERE SubscriberId = @id AND CreatedAt > DATEADD(HOUR, -1, SYSUTCDATETIME())", new { id = lead.SubscriberId }, ct));
        if (n >= MaxTasksPerHour) return null;
        var due = await cn.ExecuteScalarAsync<DateTime>(Cmd(@"
INSERT dbo.WebChatTask (WebEnquiryId, SubscriberId, TaskType, DueAt, Note) VALUES (@enq, @id, @type, DATEADD(HOUR, @hours, SYSUTCDATETIME()), @note);
UPDATE dbo.WebEnquiry SET IsRead = 0, ReadAt = NULL, StatusNote = LEFT(@statusNote, 500), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = N'ai-chat' WHERE WebEnquiryId = @enq;
SELECT DATEADD(HOUR, @hours, SYSUTCDATETIME());",
            new { enq = lead.WebEnquiryId, id = lead.SubscriberId, type, hours, note = Cut(note, 500), statusNote = $"Follow-up ({type}) due in {hours}h: {Cut(note, 380)}" }, ct));
        await Note(cn, lead.WebEnquiryId, lead.Status, $"Follow-up task ({type}), due in {hours}h: {Cut(note, 300)}", ct);
        return due;
    }

    public async Task MarkQuotationAsync(Lead lead, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd("UPDATE dbo.WebChatLead SET QuotationRequestedAt = SYSUTCDATETIME() WHERE WebChatLeadId = @id", new { id = lead.WebChatLeadId }, ct));
        await cn.ExecuteAsync(new CommandDefinition("dbo.usp_WebEnquiry_UpdateStatus",
            new { WebEnquiryId = lead.WebEnquiryId, Status = "Quoted", Note = "Quotation requested in chat - prepare and send", ChangedBy = "ai-chat" },
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
    }

    /// <summary>approved -> Sales Enquiry created (returns its id); rejected -> lead set to Rejected.</summary>
    public async Task<int?> RecordDecisionAsync(Lead lead, bool approved, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd("UPDATE dbo.WebChatLead SET Decision = @d, DecisionAt = SYSUTCDATETIME() WHERE WebChatLeadId = @id", new { d = approved ? "approved" : "rejected", id = lead.WebChatLeadId }, ct));
        if (approved)
            return await cn.QuerySingleAsync<int>(new CommandDefinition("dbo.usp_WebEnquiry_ConvertToSalesEnquiry",
                new { WebEnquiryId = lead.WebEnquiryId, ChangedBy = "ai-chat" }, commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("dbo.usp_WebEnquiry_UpdateStatus",
            new { WebEnquiryId = lead.WebEnquiryId, Status = "Rejected", Note = "Visitor rejected the quotation in chat", ChangedBy = "ai-chat" },
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
        return null;
    }

    // ------------------------------------------------------------------ Admin Panel (staff)

    public sealed class TaskRow
    {
        public long WebChatTaskId { get; set; }
        public long WebEnquiryId { get; set; }
        public string ReferenceNo { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string? ServiceInterest { get; set; }
        public string TaskType { get; set; } = string.Empty;
        public string? Note { get; set; }
        public DateTime DueAt { get; set; }
        public string Status { get; set; } = "Open";
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool Overdue { get; set; }
    }

    public async Task<List<TaskRow>> ListTasksAsync(string? status, string? type, int take, CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<TaskRow>(Cmd(@"
SELECT TOP (@take) t.WebChatTaskId, t.WebEnquiryId, e.ReferenceNo, e.FullName, e.Email, e.Phone, e.ServiceInterest, t.TaskType, t.Note, t.DueAt, t.Status,
       t.CreatedAt, t.CompletedAt, CAST(CASE WHEN t.Status = 'Open' AND t.DueAt < SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS Overdue
FROM dbo.WebChatTask t JOIN dbo.WebEnquiry e ON e.WebEnquiryId = t.WebEnquiryId
WHERE (@status IS NULL OR t.Status = @status) AND (@type IS NULL OR t.TaskType = @type)
ORDER BY CASE t.Status WHEN 'Open' THEN 0 ELSE 1 END, t.DueAt, t.WebChatTaskId",
            new { take = Math.Clamp(take, 1, 500), status, type }, ct))).ToList();
    }

    public sealed class TaskStats { public int Open { get; set; } public int Overdue { get; set; } public int DueToday { get; set; } }

    public async Task<TaskStats> TaskStatsAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return await cn.QuerySingleAsync<TaskStats>(Cmd(@"
SELECT ISNULL(SUM(CASE WHEN Status = 'Open' THEN 1 ELSE 0 END), 0) AS [Open],
       ISNULL(SUM(CASE WHEN Status = 'Open' AND DueAt < SYSUTCDATETIME() THEN 1 ELSE 0 END), 0) AS Overdue,
       ISNULL(SUM(CASE WHEN Status = 'Open' AND CAST(DueAt AS DATE) = CAST(SYSUTCDATETIME() AS DATE) THEN 1 ELSE 0 END), 0) AS DueToday
FROM dbo.WebChatTask", null, ct));
    }

    public async Task<bool> CompleteTaskAsync(long taskId, string by, CancellationToken ct)
    {
        await using var cn = Open();
        var enq = await cn.ExecuteScalarAsync<long?>(Cmd(@"
UPDATE dbo.WebChatTask SET Status = 'Done', CompletedAt = SYSUTCDATETIME() OUTPUT inserted.WebEnquiryId WHERE WebChatTaskId = @taskId AND Status = 'Open'", new { taskId }, ct));
        if (enq == null) return false;
        await cn.ExecuteAsync(Cmd(@"INSERT dbo.WebStatusHistory (EntityType, EntityId, FromStatus, ToStatus, Note, ChangedBy)
SELECT 'Enquiry', WebEnquiryId, Status, Status, N'Chat follow-up task completed', @by FROM dbo.WebEnquiry WHERE WebEnquiryId = @enq", new { enq, by = Cut(by, 100) }, ct));
        return true;
    }

    public sealed class QuestionRow { public string Question { get; set; } = string.Empty; public bool Matched { get; set; } public DateTime CreatedAt { get; set; } }
    public sealed class ChatSummary
    {
        public string? ServiceInterest { get; set; }
        public string? DraftService { get; set; }
        public string? DraftSummary { get; set; }
        public DateTime? DraftAt { get; set; }
        public DateTime? QuotationRequestedAt { get; set; }
        public string? Decision { get; set; }
        public DateTime? DecisionAt { get; set; }
    }

    /// <summary>Everything the chat recorded for one website lead (null when the lead has no chat journey).</summary>
    public async Task<object?> ChatDetailAsync(long webEnquiryId, CancellationToken ct)
    {
        await using var cn = Open();
        using var m = await cn.QueryMultipleAsync(Cmd(@"
SELECT ServiceInterest, DraftService, DraftSummary, DraftAt, QuotationRequestedAt, Decision, DecisionAt FROM dbo.WebChatLead WHERE WebEnquiryId = @webEnquiryId;
SELECT Question, Matched, CreatedAt FROM dbo.WebChatQuestion WHERE WebEnquiryId = @webEnquiryId ORDER BY CreatedAt, WebChatQuestionId;
SELECT t.WebChatTaskId, t.WebEnquiryId, e.ReferenceNo, e.FullName, e.Email, e.Phone, e.ServiceInterest, t.TaskType, t.Note, t.DueAt, t.Status, t.CreatedAt, t.CompletedAt,
       CAST(CASE WHEN t.Status = 'Open' AND t.DueAt < SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS Overdue
FROM dbo.WebChatTask t JOIN dbo.WebEnquiry e ON e.WebEnquiryId = t.WebEnquiryId WHERE t.WebEnquiryId = @webEnquiryId ORDER BY t.WebChatTaskId;", new { webEnquiryId }, ct));
        var chat = await m.ReadSingleOrDefaultAsync<ChatSummary>();
        if (chat == null) return null;
        return new { chat, questions = (await m.ReadAsync<QuestionRow>()).ToList(), tasks = (await m.ReadAsync<TaskRow>()).ToList() };
    }
}
