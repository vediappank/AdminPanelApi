using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Conversation memory of the website AI assistant, kept in SQL Server (DatabaseScripts\Migrations\008_WebChatMemory.sql).
/// Also saves a guest's "please contact me" enquiry as a normal website lead (dbo.WebEnquiry).
/// </summary>
public sealed partial class ChatMemoryStore
{
    private readonly string _connection;
    public ChatMemoryStore(IConfiguration config) => _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;

    private SqlConnection Open() => new(_connection);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SessionIdRegex();
    public static bool ValidSessionId(string? id) => !string.IsNullOrEmpty(id) && SessionIdRegex().IsMatch(id);

    private static string Cut(string? s, int max) { var t = (s ?? string.Empty).Trim(); return t.Length > max ? t[..max] : t; }

    public sealed class MessageRow { public string Role { get; set; } = string.Empty; public string Content { get; set; } = string.Empty; }

    public async Task AddAsync(string sessionId, string role, string content, string? model, int? subscriberId, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(new CommandDefinition("dbo.usp_WebChatMessage_Add",
            new { SessionId = sessionId, Role = role, Content = Cut(content, 4000), ModelUsed = model == null ? null : Cut(model, 60), SubscriberId = subscriberId },
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
    }

    public async Task<List<MessageRow>> RecentAsync(string sessionId, int take, CancellationToken ct)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<MessageRow>(new CommandDefinition("dbo.usp_WebChatMessage_Recent",
            new { SessionId = sessionId, Take = take },
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
        return rows.ToList();
    }

    private sealed class InsertResult { public long WebEnquiryId { get; set; } public string ReferenceNo { get; set; } = string.Empty; }

    /// <summary>A guest asked the team to contact them: saved as a normal website lead (FormCode ai-agent). Returns the reference number.</summary>
    public async Task<string> InsertGuestEnquiryAsync(string fullName, string email, string phone, string? service, string? message, CancellationToken ct)
    {
        await using var cn = Open();
        var row = await cn.QuerySingleAsync<InsertResult>(new CommandDefinition("dbo.usp_WebEnquiry_Insert",
            new
            {
                FormCode = "ai-agent", PagePath = "ai-agent", PageTitle = "AI assistant chat", RequestType = "Enquiry",
                FullName = Cut(fullName, 120), Email = Cut(email, 160), Phone = Cut(phone, 30),
                ServiceInterest = Cut(service, 200),
                Message = Cut(message, 1500).Length == 0 ? "Chat enquiry from the website assistant." : Cut(message, 1500),
                ConsentGiven = true, SourcePage = "ai-agent chat",
            }, commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
        return row.ReferenceNo;
    }
}
