using System.Data;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Settings ▸ Chatbot - the questions and answers of the Business Panel help chatbot (inside the support button at the
/// bottom right of every Business Panel page). Kept in the SuperAdminPanel database, one list for every company:
///   dbo.ChatbotSettings    one row - on/off, bot name, welcome and "no answer" messages
///   dbo.ChatbotCategories  topics (Customers, Quotations...)
///   dbo.ChatbotQuestions   question, keywords, answer, optional "open page" button, suggested chip, counters
///   dbo.ChatbotUnanswered  what users asked that had no answer - add an answer for it on the screen
/// The tables are created here on first use (also DatabaseScripts/Chatbot_SuperAdminPanel.sql) and filled with the
/// starter questions (<see cref="ChatbotStarter"/>) when there are no topics yet.
/// The Business Panel API reads them (ChatbotController) - changes show there within 30 seconds.
/// </summary>
public sealed class PlatformChatbotStore
{
    public const string EnsureSql = @"
IF OBJECT_ID(N'dbo.ChatbotSettings', N'U') IS NULL
CREATE TABLE dbo.ChatbotSettings
(
    SettingsId      INT            NOT NULL CONSTRAINT PK_ChatbotSettings PRIMARY KEY,
    Enabled         BIT            NOT NULL CONSTRAINT DF_ChatbotSettings_Enabled DEFAULT (1),
    BotName         NVARCHAR(60)   NULL,
    Welcome         NVARCHAR(400)  NULL,
    Fallback        NVARCHAR(400)  NULL,
    ShowSuggestions BIT            NOT NULL CONSTRAINT DF_ChatbotSettings_Suggest DEFAULT (1),
    ModifiedDate    DATETIME2(0)   NULL,
    ModifiedBy      NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.ChatbotCategories', N'U') IS NULL
CREATE TABLE dbo.ChatbotCategories
(
    CategoryId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotCategories PRIMARY KEY,
    Name         NVARCHAR(100)  NOT NULL,
    Icon         NVARCHAR(30)   NULL,
    DisplayOrder INT            NOT NULL CONSTRAINT DF_ChatbotCategories_Order DEFAULT (0),
    IsActive     BIT            NOT NULL CONSTRAINT DF_ChatbotCategories_Active DEFAULT (1)
);
IF OBJECT_ID(N'dbo.ChatbotQuestions', N'U') IS NULL
CREATE TABLE dbo.ChatbotQuestions
(
    QuestionId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotQuestions PRIMARY KEY,
    CategoryId      INT            NOT NULL CONSTRAINT FK_ChatbotQuestions_Categories REFERENCES dbo.ChatbotCategories (CategoryId),
    Question        NVARCHAR(300)  NOT NULL,
    Keywords        NVARCHAR(1000) NULL,
    Answer          NVARCHAR(4000) NOT NULL,
    LinkLabel       NVARCHAR(60)   NULL,
    LinkRoute       NVARCHAR(200)  NULL,
    IsSuggested     BIT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Suggested DEFAULT (0),
    DisplayOrder    INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Order DEFAULT (0),
    IsActive        BIT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Active DEFAULT (1),
    AskCount        INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Asked DEFAULT (0),
    HelpfulCount    INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_Helpful DEFAULT (0),
    NotHelpfulCount INT            NOT NULL CONSTRAINT DF_ChatbotQuestions_NotHelpful DEFAULT (0),
    CreatedDate     DATETIME2(0)   NOT NULL CONSTRAINT DF_ChatbotQuestions_Created DEFAULT (SYSUTCDATETIME()),
    ModifiedDate    DATETIME2(0)   NULL,
    ModifiedBy      NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.ChatbotUnanswered', N'U') IS NULL
CREATE TABLE dbo.ChatbotUnanswered
(
    UnansweredId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatbotUnanswered PRIMARY KEY,
    TenantId     INT            NULL,
    CompanyName  NVARCHAR(200)  NULL,
    UserName     NVARCHAR(200)  NULL,
    AskedText    NVARCHAR(500)  NOT NULL,
    Status       NVARCHAR(20)   NOT NULL CONSTRAINT DF_ChatbotUnanswered_Status DEFAULT (N'Open'),
    QuestionId   INT            NULL,
    CreatedDate  DATETIME2(0)   NOT NULL CONSTRAINT DF_ChatbotUnanswered_Created DEFAULT (SYSUTCDATETIME()),
    ClosedDate   DATETIME2(0)   NULL,
    ClosedBy     NVARCHAR(200)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ChatbotUnanswered_Status' AND object_id = OBJECT_ID(N'dbo.ChatbotUnanswered'))
    CREATE INDEX IX_ChatbotUnanswered_Status ON dbo.ChatbotUnanswered (Status, CreatedDate DESC);";

    public const string DefaultBotName = "Bliss Assistant";
    public const string DefaultWelcome = "Hi {name}! I'm {bot}. Ask me anything about the Business Panel, or pick a question below.";
    public const string DefaultFallback = "Sorry, I don't have an answer for that yet. Try other words, or contact our support team - we're happy to help.";

    public sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public string? BotName { get; set; }
        public string? Welcome { get; set; }
        public string? Fallback { get; set; }
        public bool ShowSuggestions { get; set; } = true;
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }

    public sealed class Category
    {
        public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public int QuestionCount { get; set; }
    }

    public sealed class Question
    {
        public int QuestionId { get; set; }
        public int CategoryId { get; set; }
        public string QuestionText { get; set; } = string.Empty;
        public string? Keywords { get; set; }
        public string Answer { get; set; } = string.Empty;
        public string? LinkLabel { get; set; }
        public string? LinkRoute { get; set; }
        public bool IsSuggested { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public int AskCount { get; set; }
        public int HelpfulCount { get; set; }
        public int NotHelpfulCount { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }

    public sealed class Unanswered
    {
        public int UnansweredId { get; set; }
        public int? TenantId { get; set; }
        public string? CompanyName { get; set; }
        public string? UserName { get; set; }
        public string AskedText { get; set; } = string.Empty;
        public string Status { get; set; } = "Open";
        public int? QuestionId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? ClosedDate { get; set; }
        public string? ClosedBy { get; set; }
    }

    private readonly string _connection;
    private readonly ILogger<PlatformChatbotStore> _logger;
    private static bool _ensured;
    private static readonly SemaphoreSlim EnsureLock = new(1, 1);

    public PlatformChatbotStore(IConfiguration config, ILogger<PlatformChatbotStore> logger)
    {
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
        _logger = logger;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_connection);
        await cn.OpenAsync(ct);
        if (_ensured) return cn;
        await EnsureLock.WaitAsync(ct);
        try
        {
            if (!_ensured)
            {
                await using (var ensure = new SqlCommand(EnsureSql, cn)) await ensure.ExecuteNonQueryAsync(ct);
                await using (var any = new SqlCommand("SELECT COUNT(*) FROM dbo.ChatbotCategories", cn))
                    if (Convert.ToInt32(await any.ExecuteScalarAsync(ct)) == 0)
                    {
                        var added = await AddStarterAsync(cn, "system", ct);
                        _logger.LogInformation("Chatbot: {Count} starter questions added.", added);
                    }
                _ensured = true;
            }
        }
        finally { EnsureLock.Release(); }
        return cn;
    }

    /// <summary>Creates the tables and the starter questions (called once at start-up).</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
    }

    // ---------------------------------------------------------------- settings

    public async Task<Settings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT TOP (1) Enabled, BotName, Welcome, Fallback, ShowSuggestions, ModifiedDate, ModifiedBy FROM dbo.ChatbotSettings ORDER BY SettingsId", cn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new Settings();
        return new Settings
        {
            Enabled = r.GetBoolean(0), BotName = Str(r, 1), Welcome = Str(r, 2), Fallback = Str(r, 3), ShowSuggestions = r.GetBoolean(4),
            ModifiedDate = r.IsDBNull(5) ? null : r.GetDateTime(5), ModifiedBy = Str(r, 6),
        };
    }

    public async Task SaveSettingsAsync(Settings s, string by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
UPDATE dbo.ChatbotSettings SET Enabled = @e, BotName = @n, Welcome = @w, Fallback = @f, ShowSuggestions = @s, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @by WHERE SettingsId = 1;
IF @@ROWCOUNT = 0
    INSERT dbo.ChatbotSettings (SettingsId, Enabled, BotName, Welcome, Fallback, ShowSuggestions, ModifiedDate, ModifiedBy)
    VALUES (1, @e, @n, @w, @f, @s, SYSUTCDATETIME(), @by);", cn);
        cmd.Parameters.Add("@e", SqlDbType.Bit).Value = s.Enabled;
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 60).Value = Db(s.BotName);
        cmd.Parameters.Add("@w", SqlDbType.NVarChar, 400).Value = Db(s.Welcome);
        cmd.Parameters.Add("@f", SqlDbType.NVarChar, 400).Value = Db(s.Fallback);
        cmd.Parameters.Add("@s", SqlDbType.Bit).Value = s.ShowSuggestions;
        cmd.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = by;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- topics

    public async Task<List<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"SELECT c.CategoryId, c.Name, c.Icon, c.DisplayOrder, c.IsActive,
       (SELECT COUNT(*) FROM dbo.ChatbotQuestions q WHERE q.CategoryId = c.CategoryId)
FROM dbo.ChatbotCategories c ORDER BY c.DisplayOrder, c.Name", cn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Category>();
        while (await r.ReadAsync(ct))
            list.Add(new Category { CategoryId = r.GetInt32(0), Name = r.GetString(1), Icon = Str(r, 2), DisplayOrder = r.GetInt32(3), IsActive = r.GetBoolean(4), QuestionCount = r.GetInt32(5) });
        return list;
    }

    public async Task<int> SaveCategoryAsync(Category c, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(c.CategoryId > 0
            ? "UPDATE dbo.ChatbotCategories SET Name = @n, Icon = @i, DisplayOrder = @o, IsActive = @a WHERE CategoryId = @id; SELECT CASE WHEN @@ROWCOUNT = 0 THEN 0 ELSE @id END;"
            : "INSERT dbo.ChatbotCategories (Name, Icon, DisplayOrder, IsActive) OUTPUT INSERTED.CategoryId VALUES (@n, @i, @o, @a);", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = c.CategoryId;
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 100).Value = c.Name.Trim();
        cmd.Parameters.Add("@i", SqlDbType.NVarChar, 30).Value = Db(c.Icon);
        cmd.Parameters.Add("@o", SqlDbType.Int).Value = c.DisplayOrder;
        cmd.Parameters.Add("@a", SqlDbType.Bit).Value = c.IsActive;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>False = the topic still has questions (move or delete them first).</summary>
    public async Task<bool> DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"IF EXISTS (SELECT 1 FROM dbo.ChatbotQuestions WHERE CategoryId = @id) SELECT 0;
ELSE BEGIN DELETE dbo.ChatbotCategories WHERE CategoryId = @id; SELECT 1; END", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    // ---------------------------------------------------------------- questions

    public async Task<List<Question>> GetQuestionsAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"SELECT QuestionId, CategoryId, Question, Keywords, Answer, LinkLabel, LinkRoute, IsSuggested, DisplayOrder, IsActive,
       AskCount, HelpfulCount, NotHelpfulCount, ModifiedDate, ModifiedBy
FROM dbo.ChatbotQuestions ORDER BY CategoryId, DisplayOrder, QuestionId", cn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Question>();
        while (await r.ReadAsync(ct))
            list.Add(new Question
            {
                QuestionId = r.GetInt32(0), CategoryId = r.GetInt32(1), QuestionText = r.GetString(2), Keywords = Str(r, 3), Answer = r.GetString(4),
                LinkLabel = Str(r, 5), LinkRoute = Str(r, 6), IsSuggested = r.GetBoolean(7), DisplayOrder = r.GetInt32(8), IsActive = r.GetBoolean(9),
                AskCount = r.GetInt32(10), HelpfulCount = r.GetInt32(11), NotHelpfulCount = r.GetInt32(12),
                ModifiedDate = r.IsDBNull(13) ? null : r.GetDateTime(13), ModifiedBy = Str(r, 14),
            });
        return list;
    }

    public async Task<int> SaveQuestionAsync(Question q, string by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        return await SaveQuestionAsync(cn, null, q, by, ct);
    }

    private static async Task<int> SaveQuestionAsync(SqlConnection cn, SqlTransaction? tx, Question q, string by, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(q.QuestionId > 0
            ? @"UPDATE dbo.ChatbotQuestions SET CategoryId = @c, Question = @q, Keywords = @k, Answer = @a, LinkLabel = @ll, LinkRoute = @lr,
       IsSuggested = @s, DisplayOrder = @o, IsActive = @act, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @by WHERE QuestionId = @id;
SELECT CASE WHEN @@ROWCOUNT = 0 THEN 0 ELSE @id END;"
            : @"INSERT dbo.ChatbotQuestions (CategoryId, Question, Keywords, Answer, LinkLabel, LinkRoute, IsSuggested, DisplayOrder, IsActive, ModifiedDate, ModifiedBy)
OUTPUT INSERTED.QuestionId VALUES (@c, @q, @k, @a, @ll, @lr, @s, @o, @act, SYSUTCDATETIME(), @by);", cn, tx);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = q.QuestionId;
        cmd.Parameters.Add("@c", SqlDbType.Int).Value = q.CategoryId;
        cmd.Parameters.Add("@q", SqlDbType.NVarChar, 300).Value = q.QuestionText.Trim();
        cmd.Parameters.Add("@k", SqlDbType.NVarChar, 1000).Value = Db(q.Keywords);
        cmd.Parameters.Add("@a", SqlDbType.NVarChar, 4000).Value = q.Answer.Trim();
        cmd.Parameters.Add("@ll", SqlDbType.NVarChar, 60).Value = Db(q.LinkLabel);
        cmd.Parameters.Add("@lr", SqlDbType.NVarChar, 200).Value = Db(q.LinkRoute);
        cmd.Parameters.Add("@s", SqlDbType.Bit).Value = q.IsSuggested;
        cmd.Parameters.Add("@o", SqlDbType.Int).Value = q.DisplayOrder;
        cmd.Parameters.Add("@act", SqlDbType.Bit).Value = q.IsActive;
        cmd.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = by;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> DeleteQuestionAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"UPDATE dbo.ChatbotUnanswered SET QuestionId = NULL WHERE QuestionId = @id;
DELETE dbo.ChatbotQuestions WHERE QuestionId = @id;", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Adds the starter topics and questions that are not there yet (matched by text). Returns how many questions were added.</summary>
    public async Task<int> AddStarterAsync(string by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        return await AddStarterAsync(cn, by, ct);
    }

    private static async Task<int> AddStarterAsync(SqlConnection cn, string by, CancellationToken ct)
    {
        var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var questions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand("SELECT CategoryId, Name FROM dbo.ChatbotCategories; SELECT Question FROM dbo.ChatbotQuestions;", cn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) categories[r.GetString(1)] = r.GetInt32(0);
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct)) questions.Add(r.GetString(0).Trim());
        }

        var added = 0;
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var catOrder = 0;
        foreach (var cat in ChatbotStarter.Categories)
        {
            catOrder += 10;
            if (!categories.TryGetValue(cat.Name, out var catId))
            {
                await using var ins = new SqlCommand("INSERT dbo.ChatbotCategories (Name, Icon, DisplayOrder, IsActive) OUTPUT INSERTED.CategoryId VALUES (@n, @i, @o, 1);", cn, tx);
                ins.Parameters.Add("@n", SqlDbType.NVarChar, 100).Value = cat.Name;
                ins.Parameters.Add("@i", SqlDbType.NVarChar, 30).Value = cat.Icon;
                ins.Parameters.Add("@o", SqlDbType.Int).Value = catOrder;
                catId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct));
                categories[cat.Name] = catId;
            }
            var order = 0;
            foreach (var q in cat.Questions)
            {
                order += 10;
                if (!questions.Add(q.Text)) continue;
                await SaveQuestionAsync(cn, tx, new Question
                {
                    CategoryId = catId, QuestionText = q.Text, Keywords = q.Keywords, Answer = q.Answer, LinkLabel = q.LinkLabel, LinkRoute = q.LinkRoute,
                    IsSuggested = q.Suggested, DisplayOrder = order, IsActive = true,
                }, by, ct);
                added++;
            }
        }
        await using (var s = new SqlCommand(@"IF NOT EXISTS (SELECT 1 FROM dbo.ChatbotSettings)
INSERT dbo.ChatbotSettings (SettingsId, Enabled, BotName, Welcome, Fallback, ShowSuggestions, ModifiedDate, ModifiedBy)
VALUES (1, 1, @n, @w, @f, 1, SYSUTCDATETIME(), @by);", cn, tx))
        {
            s.Parameters.Add("@n", SqlDbType.NVarChar, 60).Value = DefaultBotName;
            s.Parameters.Add("@w", SqlDbType.NVarChar, 400).Value = DefaultWelcome;
            s.Parameters.Add("@f", SqlDbType.NVarChar, 400).Value = DefaultFallback;
            s.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = by;
            await s.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return added;
    }

    // ---------------------------------------------------------------- unanswered questions

    public async Task<List<Unanswered>> GetUnansweredAsync(string? status, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"SELECT TOP (300) UnansweredId, TenantId, CompanyName, UserName, AskedText, Status, QuestionId, CreatedDate, ClosedDate, ClosedBy
FROM dbo.ChatbotUnanswered WHERE (@s IS NULL OR Status = @s) ORDER BY CreatedDate DESC, UnansweredId DESC", cn);
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 20).Value = Db(status);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Unanswered>();
        while (await r.ReadAsync(ct))
            list.Add(new Unanswered
            {
                UnansweredId = r.GetInt32(0), TenantId = r.IsDBNull(1) ? null : r.GetInt32(1), CompanyName = Str(r, 2), UserName = Str(r, 3),
                AskedText = r.GetString(4), Status = r.GetString(5), QuestionId = r.IsDBNull(6) ? null : r.GetInt32(6), CreatedDate = r.GetDateTime(7),
                ClosedDate = r.IsDBNull(8) ? null : r.GetDateTime(8), ClosedBy = Str(r, 9),
            });
        return list;
    }

    public async Task<int> CountOpenUnansweredAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.ChatbotUnanswered WHERE Status = N'Open'", cn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Status Open / Answered / Ignored; questionId = the answer added for it.</summary>
    public async Task<bool> SetUnansweredAsync(int id, string status, int? questionId, string by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"UPDATE dbo.ChatbotUnanswered
SET Status = @s, QuestionId = COALESCE(@q, QuestionId),
    ClosedDate = CASE WHEN @s = N'Open' THEN NULL ELSE SYSUTCDATETIME() END,
    ClosedBy = CASE WHEN @s = N'Open' THEN NULL ELSE @by END
WHERE UnansweredId = @id;", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 20).Value = status;
        cmd.Parameters.Add("@q", SqlDbType.Int).Value = (object?)questionId ?? DBNull.Value;
        cmd.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = by;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> DeleteClosedUnansweredAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE dbo.ChatbotUnanswered WHERE Status <> N'Open';", cn);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Business Panel pages (menu catalogue dbo.TenantMenuItems) for the "open page" button of an answer.</summary>
    public async Task<List<(string Name, string Route)>> GetPagesAsync(CancellationToken ct = default)
    {
        var list = new List<(string, string)>();
        try
        {
            await using var cn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(@"SELECT DisplayName, Route FROM dbo.TenantMenuItems
WHERE IsActive = 1 AND Route IS NOT NULL AND LTRIM(RTRIM(Route)) NOT IN (N'', N'#') ORDER BY DisplayName", cn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await r.ReadAsync(ct))
            {
                var route = r.GetString(1).Trim();
                if (!route.StartsWith('/')) route = "/" + route;
                if (seen.Add(route)) list.Add((r.IsDBNull(0) ? route : r.GetString(0), route));
            }
        }
        catch (SqlException ex) when (ex.Number is 208 or 207) { /* no menu catalogue */ }
        return list;
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static object Db(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();
}

/// <summary>Creates the chatbot tables and the starter questions when the Admin Panel API starts.</summary>
public sealed class PlatformChatbotSeeder : BackgroundService
{
    private readonly PlatformChatbotStore _store;
    private readonly ILogger<PlatformChatbotSeeder> _logger;

    public PlatformChatbotSeeder(PlatformChatbotStore store, ILogger<PlatformChatbotSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _store.EnsureAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot tables could not be prepared at start-up - they are created when Settings > Chatbot is opened (or run DatabaseScripts/Chatbot_SuperAdminPanel.sql).");
        }
    }
}
