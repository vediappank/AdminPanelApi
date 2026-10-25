using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Website sign-up for new posts (DatabaseScripts\Migrations\005_WebsiteSubscribers.sql).
///
///   Sign-up   name + email + WhatsApp -> a 6-digit code to the email and one to WhatsApp (when WhatsApp is set up);
///             the person is Active only when every code sent is correct.
///   Login     email or WhatsApp number -> one code -> a "My account" session (90 days). No passwords.
///   Codes     stored only as SHA-256 hashes, valid 10 minutes, 5 wrong tries end the attempt.
///   Limits    per network (in memory) and per person (database) - so bots cannot run up email / WhatsApp costs.
/// </summary>
public sealed partial class SubscriberStore
{
    public const int CodeMinutes = 10;
    public const int MaxAttempts = 5;
    public const int SessionDays = 90;
    public const int MaxChallengesPerPersonPerHour = 5;
    public const int MaxChallengesPerIpPerHour = 12;
    public const string ConsentWording = "I agree to receive new posts and updates from Bliss Point Group by email and WhatsApp. I can stop at any time.";

    private readonly string _connection;
    private readonly ILogger<SubscriberStore> _logger;
    private readonly Dictionary<string, List<DateTime>> _perIp = new();
    private readonly object _ipLock = new();

    public SubscriberStore(IConfiguration config, ILogger<SubscriberStore> logger)
    {
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
        _logger = logger;
    }

    private SqlConnection Open() => new(_connection);
    private static CommandDefinition Cmd(string sql, object? args = null, CancellationToken ct = default) => new(sql, args, commandTimeout: 30, cancellationToken: ct);

    // ------------------------------------------------------------------ helpers

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,190}\.[^@\s]{2,}$")]
    private static partial Regex EmailRule();

    public static string? CleanEmail(string? email)
    {
        var e = (email ?? string.Empty).Trim().ToLowerInvariant();
        return e.Length <= 200 && EmailRule().IsMatch(e) ? e : null;
    }

    public static string CleanName(string? name)
    {
        var n = Regex.Replace((name ?? string.Empty).Trim(), @"\s+", " ");
        return n.Length > 120 ? n[..120] : n;
    }

    public static string CleanLanguage(string? lang) => string.Equals(lang?.Trim(), "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static string NewSessionToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CodeHash(string ticket, string channel, string code) => Hash($"{ticket}:{channel}:{code.Trim()}");

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return email;
        return email[0] + new string('•', Math.Min(6, at - 1)) + email[at..];
    }

    public static string MaskPhone(string? digits) =>
        string.IsNullOrEmpty(digits) || digits.Length < 6 ? digits ?? string.Empty : "+" + digits[..3] + " •••• " + digits[^3..];

    /// <summary>true while this network may start another sign-up / login (in memory, per API process).</summary>
    public bool AllowIp(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_ipLock)
        {
            if (_perIp.Count > 5000) _perIp.Clear();
            if (!_perIp.TryGetValue(ip, out var list)) _perIp[ip] = list = new List<DateTime>();
            list.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (list.Count >= MaxChallengesPerIpPerHour) return false;
            list.Add(now);
            return true;
        }
    }

    // ------------------------------------------------------------------ rows

    public sealed class Subscriber
    {
        public int SubscriberId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? WhatsApp { get; set; }
        public string Language { get; set; } = "en";
        public string Status { get; set; } = "Pending";
        public DateTime? EmailVerifiedAt { get; set; }
        public DateTime? WhatsAppVerifiedAt { get; set; }
        public bool EmailOptIn { get; set; }
        public bool WhatsAppOptIn { get; set; }
        public DateTime? ConsentAt { get; set; }
        public string UnsubscribeToken { get; set; } = string.Empty;
        public int EmailFailures { get; set; }
        public string? Source { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime? UnsubscribedAt { get; set; }
    }

    private const string Columns = @"SubscriberId, Name, Email, WhatsApp, Language, Status, EmailVerifiedAt, WhatsAppVerifiedAt, EmailOptIn, WhatsAppOptIn,
ConsentAt, UnsubscribeToken, EmailFailures, Source, CreatedAt, ActivatedAt, LastLoginAt, UnsubscribedAt";

    public async Task<Subscriber?> GetAsync(int id, CancellationToken ct)
    {
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<Subscriber>(Cmd($"SELECT {Columns} FROM dbo.WebsiteSubscribers WHERE SubscriberId = @id", new { id }, ct));
    }

    public async Task<Subscriber?> FindAsync(string? email, string? whatsapp, CancellationToken ct)
    {
        await using var cn = Open();
        if (email != null)
            return await cn.QuerySingleOrDefaultAsync<Subscriber>(Cmd($"SELECT {Columns} FROM dbo.WebsiteSubscribers WHERE Email = @email", new { email }, ct));
        if (whatsapp != null)
            return (await cn.QueryAsync<Subscriber>(Cmd($@"SELECT TOP (1) {Columns} FROM dbo.WebsiteSubscribers
WHERE WhatsApp = @whatsapp AND WhatsAppVerifiedAt IS NOT NULL ORDER BY CASE Status WHEN 'Active' THEN 0 ELSE 1 END, SubscriberId DESC", new { whatsapp }, ct))).FirstOrDefault();
        return null;
    }

    public async Task<Subscriber?> FindByUnsubscribeTokenAsync(string token, CancellationToken ct)
    {
        if (!Regex.IsMatch(token ?? string.Empty, "^[a-f0-9]{32}$")) return null;
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<Subscriber>(Cmd($"SELECT {Columns} FROM dbo.WebsiteSubscribers WHERE UnsubscribeToken = @token", new { token }, ct));
    }

    /// <summary>The person's row for this email - created as Pending when new (details are applied after the codes).</summary>
    public async Task<Subscriber> EnsureAsync(string email, string name, string? whatsapp, string language, string? source, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
IF NOT EXISTS (SELECT 1 FROM dbo.WebsiteSubscribers WHERE Email = @email)
    INSERT dbo.WebsiteSubscribers (Name, Email, WhatsApp, Language, UnsubscribeToken, Source)
    VALUES (@name, @email, @whatsapp, @language, @token, @source);",
            new { email, name, whatsapp, language, token = NewToken(), source = source?.Length > 300 ? source[..300] : source }, ct));
        return (await cn.QuerySingleAsync<Subscriber>(Cmd($"SELECT {Columns} FROM dbo.WebsiteSubscribers WHERE Email = @email", new { email }, ct)));
    }

    public async Task<int> ChallengesLastHourAsync(int subscriberId, CancellationToken ct)
    {
        await using var cn = Open();
        return await cn.ExecuteScalarAsync<int>(Cmd(
            "SELECT COUNT(*) FROM dbo.SubscriberChallenges WHERE SubscriberId = @subscriberId AND CreatedAt > DATEADD(HOUR, -1, SYSUTCDATETIME())",
            new { subscriberId }, ct));
    }

    // ------------------------------------------------------------------ challenges (codes)

    public sealed record NewChallenge(string Ticket, string? EmailCode, string? WhatsAppCode);

    public async Task<NewChallenge> CreateChallengeAsync(int subscriberId, string purpose, bool emailCode, bool whatsappCode,
        string? pendingName, string? pendingWhatsApp, string? pendingLanguage, string ip, CancellationToken ct)
    {
        var ticket = NewToken();
        var e = emailCode ? NewCode() : null;
        var w = whatsappCode ? NewCode() : null;
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.SubscriberChallenges SET ExpiresAt = SYSUTCDATETIME() WHERE SubscriberId = @subscriberId AND CompletedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();
INSERT dbo.SubscriberChallenges (Ticket, SubscriberId, Purpose, EmailCodeHash, WhatsAppCodeHash, PendingName, PendingWhatsApp, PendingLanguage, ExpiresAt, Ip)
VALUES (@ticket, @subscriberId, @purpose, @eh, @wh, @pendingName, @pendingWhatsApp, @pendingLanguage, DATEADD(MINUTE, @minutes, SYSUTCDATETIME()), @ip);",
            new
            {
                ticket, subscriberId, purpose,
                eh = e == null ? null : CodeHash(ticket, "email", e),
                wh = w == null ? null : CodeHash(ticket, "whatsapp", w),
                pendingName, pendingWhatsApp, pendingLanguage, minutes = CodeMinutes,
                ip = ip.Length > 64 ? ip[..64] : ip,
            }, ct));
        return new NewChallenge(ticket, e, w);
    }

    public sealed class Challenge
    {
        public int ChallengeId { get; set; }
        public string Ticket { get; set; } = string.Empty;
        public int SubscriberId { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public string? EmailCodeHash { get; set; }
        public string? WhatsAppCodeHash { get; set; }
        public string? PendingName { get; set; }
        public string? PendingWhatsApp { get; set; }
        public string? PendingLanguage { get; set; }
        public int Attempts { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public async Task<Challenge?> GetChallengeAsync(string ticket, CancellationToken ct)
    {
        if (!Regex.IsMatch(ticket ?? string.Empty, "^[a-f0-9]{32}$")) return null;
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<Challenge>(Cmd("SELECT * FROM dbo.SubscriberChallenges WHERE Ticket = @ticket", new { ticket }, ct));
    }

    public enum CheckResult { Ok, Wrong, Expired, TooManyTries }

    /// <summary>Checks every code the attempt needs; counts a wrong try; marks it completed when all are right.</summary>
    public async Task<CheckResult> CheckCodesAsync(Challenge c, string? emailCode, string? whatsappCode, CancellationToken ct)
    {
        if (c.CompletedAt != null || c.ExpiresAt <= DateTime.UtcNow) return CheckResult.Expired;
        if (c.Attempts >= MaxAttempts) return CheckResult.TooManyTries;
        var ok = (c.EmailCodeHash == null || CodeHash(c.Ticket, "email", emailCode ?? string.Empty) == c.EmailCodeHash)
              && (c.WhatsAppCodeHash == null || CodeHash(c.Ticket, "whatsapp", whatsappCode ?? string.Empty) == c.WhatsAppCodeHash);
        await using var cn = Open();
        if (!ok)
        {
            await cn.ExecuteAsync(Cmd("UPDATE dbo.SubscriberChallenges SET Attempts = Attempts + 1 WHERE ChallengeId = @id", new { id = c.ChallengeId }, ct));
            return c.Attempts + 1 >= MaxAttempts ? CheckResult.TooManyTries : CheckResult.Wrong;
        }
        var done = await cn.ExecuteAsync(Cmd("UPDATE dbo.SubscriberChallenges SET CompletedAt = SYSUTCDATETIME() WHERE ChallengeId = @id AND CompletedAt IS NULL",
            new { id = c.ChallengeId }, ct));
        return done == 1 ? CheckResult.Ok : CheckResult.Expired;
    }

    /// <summary>Sign-up confirmed: details applied, channels marked verified, consent stored, Active. Returns true when newly activated.</summary>
    public async Task<bool> ActivateAsync(Challenge c, string ip, CancellationToken ct)
    {
        await using var cn = Open();
        var wasActive = await cn.ExecuteScalarAsync<string>(Cmd("SELECT Status FROM dbo.WebsiteSubscribers WHERE SubscriberId = @id", new { id = c.SubscriberId }, ct)) == "Active";
        await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.WebsiteSubscribers SET
    Name = COALESCE(NULLIF(@name, N''), Name),
    WhatsApp = CASE WHEN @whatsappVerified = 1 THEN @whatsapp ELSE COALESCE(@whatsapp, WhatsApp) END,
    Language = COALESCE(@language, Language),
    EmailVerifiedAt = COALESCE(EmailVerifiedAt, SYSUTCDATETIME()),
    WhatsAppVerifiedAt = CASE WHEN @whatsappVerified = 1 THEN SYSUTCDATETIME()
                              WHEN @whatsapp IS NOT NULL AND ISNULL(WhatsApp, '') <> @whatsapp THEN NULL
                              ELSE WhatsAppVerifiedAt END,
    EmailOptIn = 1, WhatsAppOptIn = 1, EmailFailures = 0,
    Status = 'Active', ActivatedAt = COALESCE(ActivatedAt, SYSUTCDATETIME()), UnsubscribedAt = NULL,
    ConsentAt = SYSUTCDATETIME(), ConsentIp = @ip, ConsentText = @consent,
    LastLoginAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
WHERE SubscriberId = @id;",
            new
            {
                id = c.SubscriberId, name = c.PendingName, whatsapp = c.PendingWhatsApp, language = c.PendingLanguage,
                whatsappVerified = c.WhatsAppCodeHash != null ? 1 : 0,
                ip = ip.Length > 64 ? ip[..64] : ip, consent = ConsentWording,
            }, ct));
        return !wasActive;
    }

    public async Task MarkWhatsAppVerifiedAsync(Challenge c, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"UPDATE dbo.WebsiteSubscribers SET WhatsApp = COALESCE(@whatsapp, WhatsApp), WhatsAppVerifiedAt = SYSUTCDATETIME(),
WhatsAppOptIn = 1, UpdatedAt = SYSUTCDATETIME() WHERE SubscriberId = @id", new { id = c.SubscriberId, whatsapp = c.PendingWhatsApp }, ct));
    }

    // ------------------------------------------------------------------ sessions

    public async Task<string> CreateSessionAsync(int subscriberId, CancellationToken ct)
    {
        var token = NewSessionToken();
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
DELETE dbo.SubscriberSessions WHERE ExpiresAt < SYSUTCDATETIME();
INSERT dbo.SubscriberSessions (TokenHash, SubscriberId, ExpiresAt) VALUES (@hash, @subscriberId, DATEADD(DAY, @days, SYSUTCDATETIME()));
UPDATE dbo.WebsiteSubscribers SET LastLoginAt = SYSUTCDATETIME() WHERE SubscriberId = @subscriberId;",
            new { hash = Hash(token), subscriberId, days = SessionDays }, ct));
        return token;
    }

    public async Task<Subscriber?> FromSessionAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || !Regex.IsMatch(token, "^[a-f0-9]{64}$")) return null;
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<Subscriber>(Cmd($@"
SELECT {Columns} FROM dbo.WebsiteSubscribers
WHERE SubscriberId = (SELECT SubscriberId FROM dbo.SubscriberSessions WHERE TokenHash = @hash AND ExpiresAt > SYSUTCDATETIME());", new { hash = Hash(token) }, ct));
    }

    public async Task EndSessionAsync(string token, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd("DELETE dbo.SubscriberSessions WHERE TokenHash = @hash", new { hash = Hash(token) }, ct));
    }

    // ------------------------------------------------------------------ preferences / unsubscribe / delete

    public async Task UpdatePreferencesAsync(int id, string name, string language, bool emailOptIn, bool whatsappOptIn, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.WebsiteSubscribers SET Name = COALESCE(NULLIF(@name, N''), Name), Language = @language,
    EmailOptIn = @emailOptIn, WhatsAppOptIn = @whatsappOptIn,
    Status = CASE WHEN @emailOptIn = 0 AND @whatsappOptIn = 0 THEN 'Unsubscribed' WHEN Status = 'Unsubscribed' THEN 'Active' ELSE Status END,
    UnsubscribedAt = CASE WHEN @emailOptIn = 0 AND @whatsappOptIn = 0 THEN SYSUTCDATETIME() ELSE NULL END,
    EmailFailures = CASE WHEN @emailOptIn = 1 THEN 0 ELSE EmailFailures END,
    UpdatedAt = SYSUTCDATETIME()
WHERE SubscriberId = @id;", new { id, name, language, emailOptIn, whatsappOptIn }, ct));
    }

    /// <summary>channel: email | whatsapp | all. Returns the row after the change.</summary>
    public async Task UnsubscribeAsync(int id, string channel, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.WebsiteSubscribers SET
    EmailOptIn = CASE WHEN @channel IN ('email', 'all') THEN 0 ELSE EmailOptIn END,
    WhatsAppOptIn = CASE WHEN @channel IN ('whatsapp', 'all') THEN 0 ELSE WhatsAppOptIn END,
    UpdatedAt = SYSUTCDATETIME()
WHERE SubscriberId = @id;
UPDATE dbo.WebsiteSubscribers SET Status = 'Unsubscribed', UnsubscribedAt = SYSUTCDATETIME()
WHERE SubscriberId = @id AND Status <> 'Unsubscribed'
  AND (EmailOptIn = 0 OR EmailVerifiedAt IS NULL) AND (WhatsAppOptIn = 0 OR WhatsAppVerifiedAt IS NULL);", new { id, channel }, ct));
    }

    /// <summary>WhatsApp "STOP": every subscriber with this number stops getting WhatsApp messages.</summary>
    public async Task<int> StopWhatsAppAsync(string digits, CancellationToken ct)
    {
        await using var cn = Open();
        var ids = (await cn.QueryAsync<int>(Cmd("SELECT SubscriberId FROM dbo.WebsiteSubscribers WHERE WhatsApp = @digits AND WhatsAppOptIn = 1", new { digits }, ct))).ToList();
        foreach (var id in ids) await UnsubscribeAsync(id, "whatsapp", ct);
        return ids.Count;
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
DELETE dbo.SubscriberSessions WHERE SubscriberId = @id;
DELETE dbo.SubscriberChallenges WHERE SubscriberId = @id;
DELETE dbo.WebsiteSubscribers WHERE SubscriberId = @id;", new { id }, ct));
    }

    public async Task RecordEmailResultAsync(int id, bool ok, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(ok
            ? "UPDATE dbo.WebsiteSubscribers SET EmailFailures = 0 WHERE SubscriberId = @id AND EmailFailures <> 0"
            : @"UPDATE dbo.WebsiteSubscribers SET EmailFailures = EmailFailures + 1,
                   EmailOptIn = CASE WHEN EmailFailures + 1 >= 3 THEN 0 ELSE EmailOptIn END WHERE SubscriberId = @id", new { id }, ct));
    }

    // ------------------------------------------------------------------ newsletter data

    /// <summary>Everyone who should get the next "new posts" message on at least one channel.</summary>
    public async Task<List<Subscriber>> RecipientsAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<Subscriber>(Cmd($@"SELECT {Columns} FROM dbo.WebsiteSubscribers
WHERE Status = 'Active' AND ((EmailOptIn = 1 AND EmailVerifiedAt IS NOT NULL) OR (WhatsAppOptIn = 1 AND WhatsAppVerifiedAt IS NOT NULL))
ORDER BY SubscriberId", null, ct))).ToList();
    }

    public sealed class Post
    {
        public int BlogPostId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string? CoverImage { get; set; }
        public string? CategoryName { get; set; }
        public DateTime PublishedAt { get; set; }
    }

    private const string LivePost = "p.IsDeleted = 0 AND p.Status = 'Published' AND p.PublishedAt <= SYSUTCDATETIME()";

    /// <summary>Live posts not sent yet and not switched off - the next automatic message.</summary>
    public async Task<List<Post>> DuePostsAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<Post>(Cmd($@"
SELECT TOP (10) p.BlogPostId, p.Title, p.Slug, p.Summary, p.CoverImage, c.Name AS CategoryName, p.PublishedAt
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
LEFT JOIN dbo.NewsletterPosts n ON n.BlogPostId = p.BlogPostId
WHERE {LivePost} AND (n.BlogPostId IS NULL OR (n.SendToSubscribers = 1 AND n.SentAt IS NULL))
ORDER BY p.PublishedAt, p.BlogPostId;", null, ct))).ToList();
    }

    public async Task<List<Post>> LatestPostsAsync(int count, CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<Post>(Cmd($@"
SELECT TOP (@count) p.BlogPostId, p.Title, p.Slug, p.Summary, p.CoverImage, c.Name AS CategoryName, p.PublishedAt
FROM dbo.BlogPost p LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE {LivePost} ORDER BY p.PublishedAt DESC, p.BlogPostId DESC;", new { count }, ct))).ToList();
    }

    /// <summary>UAE date of the last automatic send (one "new posts" message a day).</summary>
    public async Task<DateTime?> LastAutoSendAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return await cn.ExecuteScalarAsync<DateTime?>(Cmd("SELECT MAX(StartedAt) FROM dbo.NewsletterSends WHERE Kind = 'posts'", null, ct));
    }

    public async Task<int> StartSendAsync(string kind, IReadOnlyList<Post> posts, int recipients, string? by, CancellationToken ct)
    {
        await using var cn = Open();
        var titles = string.Join(" | ", posts.Select(p => p.Title));
        return await cn.ExecuteScalarAsync<int>(Cmd(@"
INSERT dbo.NewsletterSends (Kind, PostIds, PostTitles, Recipients, RequestedBy) VALUES (@kind, @ids, @titles, @recipients, @by);
SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { kind, ids = string.Join(",", posts.Select(p => p.BlogPostId)), titles = titles.Length > 1000 ? titles[..1000] : titles, recipients, by }, ct));
    }

    /// <summary>Marks the posts as sent BEFORE sending, so a crash or restart can never send them twice.</summary>
    public async Task MarkPostsSentAsync(IEnumerable<int> postIds, int sendId, CancellationToken ct)
    {
        await using var cn = Open();
        foreach (var id in postIds)
            await cn.ExecuteAsync(Cmd(@"
IF EXISTS (SELECT 1 FROM dbo.NewsletterPosts WHERE BlogPostId = @id)
    UPDATE dbo.NewsletterPosts SET SentAt = SYSUTCDATETIME(), SendId = @sendId WHERE BlogPostId = @id;
ELSE
    INSERT dbo.NewsletterPosts (BlogPostId, SendToSubscribers, SentAt, SendId) VALUES (@id, 1, SYSUTCDATETIME(), @sendId);", new { id, sendId }, ct));
    }

    public async Task FinishSendAsync(int sendId, int emailSent, int emailFailed, int waSent, int waFailed, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"UPDATE dbo.NewsletterSends SET EmailSent = @emailSent, EmailFailed = @emailFailed,
WhatsAppSent = @waSent, WhatsAppFailed = @waFailed, FinishedAt = SYSUTCDATETIME() WHERE SendId = @sendId",
            new { sendId, emailSent, emailFailed, waSent, waFailed }, ct));
    }

    // ------------------------------------------------------------------ Admin Panel

    public async Task<object> ListAsync(string? search, string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 200);
        await using var cn = Open();
        var args = new
        {
            search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%",
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            skip = (page - 1) * pageSize, take = pageSize,
        };
        using var multi = await cn.QueryMultipleAsync(Cmd($@"
SELECT {Columns} FROM dbo.WebsiteSubscribers
WHERE (@status IS NULL OR Status = @status) AND (@search IS NULL OR Name LIKE @search OR Email LIKE @search OR WhatsApp LIKE @search)
ORDER BY CreatedAt DESC, SubscriberId DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;

SELECT COUNT(*) FROM dbo.WebsiteSubscribers
WHERE (@status IS NULL OR Status = @status) AND (@search IS NULL OR Name LIKE @search OR Email LIKE @search OR WhatsApp LIKE @search);

SELECT
    SUM(CASE WHEN Status = 'Active' THEN 1 ELSE 0 END) AS Active,
    SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END) AS Pending,
    SUM(CASE WHEN Status = 'Unsubscribed' THEN 1 ELSE 0 END) AS Unsubscribed,
    SUM(CASE WHEN Status = 'Active' AND EmailOptIn = 1 AND EmailVerifiedAt IS NOT NULL THEN 1 ELSE 0 END) AS EmailOn,
    SUM(CASE WHEN Status = 'Active' AND WhatsAppOptIn = 1 AND WhatsAppVerifiedAt IS NOT NULL THEN 1 ELSE 0 END) AS WhatsAppOn,
    SUM(CASE WHEN Status = 'Active' AND CreatedAt >= DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS NewLast30Days
FROM dbo.WebsiteSubscribers;", args, ct));
        var items = (await multi.ReadAsync<Subscriber>()).ToList();
        var total = await multi.ReadSingleAsync<int>();
        var stats = await multi.ReadSingleAsync<SubscriberStats>();
        return new { items, total, page, pageSize, stats };
    }

    public async Task<List<Subscriber>> AllAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<Subscriber>(Cmd($"SELECT {Columns} FROM dbo.WebsiteSubscribers ORDER BY CreatedAt DESC", null, ct))).ToList();
    }

    public sealed class SubscriberStats
    {
        public int? Active { get; set; } public int? Pending { get; set; } public int? Unsubscribed { get; set; }
        public int? EmailOn { get; set; } public int? WhatsAppOn { get; set; } public int? NewLast30Days { get; set; }
    }

    public sealed class SendRow
    {
        public int SendId { get; set; } public string Kind { get; set; } = string.Empty; public string? PostTitles { get; set; }
        public int Recipients { get; set; } public int EmailSent { get; set; } public int EmailFailed { get; set; }
        public int WhatsAppSent { get; set; } public int WhatsAppFailed { get; set; }
        public DateTime StartedAt { get; set; } public DateTime? FinishedAt { get; set; } public string? RequestedBy { get; set; }
    }

    public async Task<List<SendRow>> SendsAsync(CancellationToken ct)
    {
        await using var cn = Open();
        return (await cn.QueryAsync<SendRow>(Cmd("SELECT TOP (50) * FROM dbo.NewsletterSends ORDER BY StartedAt DESC, SendId DESC", null, ct))).ToList();
    }

    public sealed class PostSetting { public int BlogPostId { get; set; } public bool SendToSubscribers { get; set; } = true; public DateTime? SentAt { get; set; } }

    public async Task<PostSetting> GetPostSettingAsync(int blogPostId, CancellationToken ct)
    {
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<PostSetting>(Cmd("SELECT BlogPostId, SendToSubscribers, SentAt FROM dbo.NewsletterPosts WHERE BlogPostId = @blogPostId", new { blogPostId }, ct))
            ?? new PostSetting { BlogPostId = blogPostId, SendToSubscribers = true };
    }

    public async Task SetPostSettingAsync(int blogPostId, bool send, string? by, CancellationToken ct)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd(@"
IF EXISTS (SELECT 1 FROM dbo.NewsletterPosts WHERE BlogPostId = @blogPostId)
    UPDATE dbo.NewsletterPosts SET SendToSubscribers = @send, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @by WHERE BlogPostId = @blogPostId;
ELSE
    INSERT dbo.NewsletterPosts (BlogPostId, SendToSubscribers, UpdatedAt, UpdatedBy) VALUES (@blogPostId, @send, SYSUTCDATETIME(), @by);",
            new { blogPostId, send, by }, ct));
    }
}
