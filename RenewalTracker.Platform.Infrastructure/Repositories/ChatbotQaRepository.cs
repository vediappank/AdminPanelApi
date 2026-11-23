using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using RenewalTracker.Platform.Application.DTOs.ChatbotQa;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Repositories;

/// <summary>Dapper over dbo.WebsiteChatbotQa - same "DefaultConnection" as BlogRepository.</summary>
public class ChatbotQaRepository : IChatbotQaRepository
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string _connectionString;

    public ChatbotQaRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    }

    private SqlConnection Open() => new(_connectionString);

    private static CommandDefinition Cmd(string sql, object? args, CancellationToken ct) =>
        new(sql, args, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct);

    private static string Who(string changedBy) => changedBy.Length > 100 ? changedBy[..100] : changedBy;

    private const string Columns = @"
q.ChatbotQaId, q.Category, q.Question, q.Answer, q.DisplayOrder, q.Status, q.CreatedAt, q.UpdatedAt, q.UpdatedBy";

    public async Task<List<ChatbotQaDto>> ListAsync(CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<ChatbotQaDto>(Cmd($@"
SELECT {Columns}
FROM dbo.WebsiteChatbotQa q
WHERE q.IsDeleted = 0
ORDER BY q.Category, q.DisplayOrder, q.ChatbotQaId;", null, ct));
        return rows.ToList();
    }

    public async Task<ChatbotQaDto?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<ChatbotQaDto>(Cmd($@"
SELECT {Columns}
FROM dbo.WebsiteChatbotQa q
WHERE q.ChatbotQaId = @Id AND q.IsDeleted = 0;", new { Id = id }, ct));
    }

    public async Task<ChatbotQaDto?> SaveAsync(int? id, ChatbotQaSaveRequest r, string changedBy, CancellationToken ct = default)
    {
        await using var cn = Open();
        var args = new { Id = id, r.Category, r.Question, r.Answer, r.DisplayOrder, r.Status, By = Who(changedBy) };
        int savedId;
        if (id is null)
        {
            savedId = await cn.ExecuteScalarAsync<int>(Cmd(@"
INSERT dbo.WebsiteChatbotQa (Category, Question, Answer, DisplayOrder, Status, CreatedBy, UpdatedBy)
VALUES (@Category, @Question, @Answer, @DisplayOrder, @Status, @By, @By);
SELECT CAST(SCOPE_IDENTITY() AS INT);", args, ct));
        }
        else
        {
            var n = await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.WebsiteChatbotQa
   SET Category = @Category, Question = @Question, Answer = @Answer, DisplayOrder = @DisplayOrder,
       Status = @Status, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By
 WHERE ChatbotQaId = @Id AND IsDeleted = 0;", args, ct));
            if (n == 0) return null;
            savedId = id.Value;
        }
        return await GetAsync(savedId, ct);
    }

    public async Task<bool> DeleteAsync(int id, string changedBy, CancellationToken ct = default)
    {
        await using var cn = Open();
        var n = await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.WebsiteChatbotQa
   SET IsDeleted = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By
 WHERE ChatbotQaId = @Id AND IsDeleted = 0;", new { Id = id, By = Who(changedBy) }, ct));
        return n > 0;
    }

    public async Task<List<PublicChatbotQaDto>> ListPublishedAsync(CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<PublicChatbotQaDto>(Cmd(@"
SELECT q.Category, q.Question, q.Answer
FROM dbo.WebsiteChatbotQa q
WHERE q.IsDeleted = 0 AND q.Status = 'Published'
ORDER BY q.Category, q.DisplayOrder, q.ChatbotQaId;", null, ct));
        return rows.ToList();
    }
}
