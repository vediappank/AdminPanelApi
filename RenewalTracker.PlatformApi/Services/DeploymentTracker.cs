using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Settings > Deployments - which build of each part of the platform is live.
///
/// Every publish script (D:\BLISS\Prod Repos\Deployment\publish-*.ps1) writes a version.json into the build:
///   website / Admin Panel / Business Panel   -> https://{host}/version.json
///   Admin API / Business API                 -> https://{host}/version   (reads version.json next to the exe)
/// This class reads all five, and records every new build it sees in dbo.DeploymentHistory (first time seen
/// = when it went live, to within DeploymentWatcher's interval). The list of apps can be overridden with
/// "Deployments:Apps" in appsettings (Key / Label / Url); the Admin API itself is read from its own folder.
/// The table is created on first use (also in DatabaseScripts/Deployments_SuperAdminPanel.sql).
/// </summary>
public sealed class DeploymentTracker
{
    public const string HttpClientName = "deployments";
    public const string SelfKey = "adminpanelapi";

    public const string EnsureSql = @"
IF OBJECT_ID(N'dbo.DeploymentHistory', N'U') IS NULL
CREATE TABLE dbo.DeploymentHistory
(
    DeploymentHistoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeploymentHistory PRIMARY KEY,
    App           VARCHAR(40)    NOT NULL,
    Version       VARCHAR(40)    NOT NULL,
    CommitHash    VARCHAR(40)    NULL,
    Branch        NVARCHAR(100)  NULL,
    Dirty         BIT            NOT NULL CONSTRAINT DF_DeploymentHistory_Dirty DEFAULT (0),
    CommitMessage NVARCHAR(300)  NULL,
    BuiltAt       DATETIME2(0)   NULL,
    BuiltBy       NVARCHAR(100)  NULL,
    FirstSeenAt   DATETIME2(0)   NOT NULL CONSTRAINT DF_DeploymentHistory_FirstSeen DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_DeploymentHistory_AppVersion UNIQUE (App, Version)
);";

    public sealed record AppTarget(string Key, string Label, string Url);

    public sealed class AppStatus
    {
        public string App { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        /// <summary>ok | unstamped (answers but has no version.json yet) | down</summary>
        public string Status { get; set; } = "down";
        public int? HttpStatus { get; set; }
        public long ResponseMs { get; set; }
        public string? Version { get; set; }
        public string? Commit { get; set; }
        public string? ShortCommit { get; set; }
        public string? Branch { get; set; }
        public bool Dirty { get; set; }
        public string? CommitMessage { get; set; }
        public DateTime? CommitDate { get; set; }
        public DateTime? BuiltAt { get; set; }
        public string? BuiltBy { get; set; }
        public DateTime? StartedAt { get; set; }
        public string? Environment { get; set; }
        public DateTime? LiveSince { get; set; }
        public string? Error { get; set; }
    }

    public sealed class HistoryRow
    {
        public int Id { get; set; }
        public string App { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Commit { get; set; }
        public string? Branch { get; set; }
        public bool Dirty { get; set; }
        public string? CommitMessage { get; set; }
        public DateTime? BuiltAt { get; set; }
        public string? BuiltBy { get; set; }
        public DateTime FirstSeenAt { get; set; }
    }

    private static readonly AppTarget[] Defaults =
    {
        new("website",          "Website",            "https://blisspointworld.com/version.json"),
        new("adminpanel",       "Admin Panel",        "https://adminpanel.blisspointworld.com/version.json"),
        new(SelfKey,            "Admin Panel API",    "https://adminpanelapi.blisspointworld.com/version"),
        new("businesspanel",    "Business Panel",     "https://businesspanel.blisspointworld.com/version.json"),
        new("businesspanelapi", "Business Panel API", "https://businesspanelapi.blisspointworld.com/version"),
    };

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DeploymentTracker> _logger;
    private readonly string _connection;
    private static bool _ensured;

    public DeploymentTracker(IHttpClientFactory http, IConfiguration config, IWebHostEnvironment env, ILogger<DeploymentTracker> logger)
    {
        _http = http;
        _config = config;
        _env = env;
        _logger = logger;
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
    }

    public IReadOnlyList<AppTarget> Targets
    {
        get
        {
            var configured = _config.GetSection("Deployments:Apps").GetChildren()
                .Select(s => new AppTarget(s["Key"] ?? string.Empty, s["Label"] ?? s["Key"] ?? string.Empty, s["Url"] ?? string.Empty))
                .Where(t => t.Key.Length > 0 && t.Url.Length > 0)
                .ToList();
            return configured.Count > 0 ? configured : Defaults;
        }
    }

    // ------------------------------------------------------------------ this API's own stamp (GET /version)

    /// <summary>version.json next to the exe (written by publish-adminpanelapi.ps1), plus start time and environment.</summary>
    public static JsonObject ReadOwnStamp(string app, IWebHostEnvironment env)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "version.json");
        JsonObject node;
        try
        {
            node = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (Exception)
        {
            node = new JsonObject();
        }
        if (node["app"] is null) node["app"] = app;
        if (node["version"] is null) node["version"] = "unstamped";
        node["startedAt"] = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        node["environment"] = env.EnvironmentName;
        return node;
    }

    // ------------------------------------------------------------------ check all apps

    public async Task<List<AppStatus>> CheckAsync(bool record, CancellationToken ct = default)
    {
        var targets = Targets;
        var results = await Task.WhenAll(targets.Select(t => CheckOneAsync(t, ct)));
        var list = results.ToList();

        if (record)
        {
            try
            {
                await RecordAsync(list, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Deployments: history could not be saved.");
            }
        }
        return list;
    }

    private async Task<AppStatus> CheckOneAsync(AppTarget target, CancellationToken ct)
    {
        var status = new AppStatus { App = target.Key, Label = target.Label, Url = target.Url };
        var sw = Stopwatch.StartNew();

        if (string.Equals(target.Key, SelfKey, StringComparison.OrdinalIgnoreCase))
        {
            // this API: read its own file - no loopback call through the internet
            Fill(status, ReadOwnStamp(SelfKey, _env));
            status.HttpStatus = 200;
            status.ResponseMs = sw.ElapsedMilliseconds;
            return status;
        }

        try
        {
            var client = _http.CreateClient(HttpClientName);
            var sep = target.Url.Contains('?') ? '&' : '?';
            using var response = await client.GetAsync($"{target.Url}{sep}t={DateTime.UtcNow.Ticks}", ct);
            status.HttpStatus = (int)response.StatusCode;
            status.ResponseMs = sw.ElapsedMilliseconds;
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                status.Status = (int)response.StatusCode == 404 ? "unstamped" : "down";
                status.Error = (int)response.StatusCode == 404
                    ? "Site answers, but has no version file yet - publish it once with the new publish script."
                    : $"HTTP {(int)response.StatusCode}";
                return status;
            }

            JsonObject? json = null;
            try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
            if (json is null)
            {
                // SPA hosts answer unknown files with index.html (HTML, not JSON) = no version.json deployed yet
                status.Status = "unstamped";
                status.Error = "Site answers, but has no version file yet - publish it once with the new publish script.";
                return status;
            }
            Fill(status, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            status.Status = "down";
            status.ResponseMs = sw.ElapsedMilliseconds;
            status.Error = ex is TaskCanceledException ? "No answer within 10 seconds" : ex.Message;
        }
        return status;
    }

    private static void Fill(AppStatus s, JsonObject j)
    {
        string? Str(string name) => j[name] is JsonValue v && v.TryGetValue<string>(out var x) && !string.IsNullOrWhiteSpace(x) ? x : null;
        DateTime? Date(string name) =>
            Str(name) is { } x && DateTime.TryParse(x, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : j[name] is JsonValue dv && dv.TryGetValue<DateTime>(out var dd) ? dd.ToUniversalTime() : null;

        s.Version = Str("version");
        s.Commit = Str("commit");
        s.ShortCommit = Str("shortCommit") ?? (s.Commit is { Length: > 7 } c ? c[..7] : s.Commit);
        s.Branch = Str("branch");
        s.Dirty = j["dirty"] is JsonValue dirty && dirty.TryGetValue<bool>(out var b) && b;
        s.CommitMessage = Str("commitMessage");
        s.CommitDate = Date("commitDate");
        s.BuiltAt = Date("builtAt");
        s.BuiltBy = Str("builtBy");
        s.StartedAt = Date("startedAt");
        s.Environment = Str("environment");
        s.Status = s.Version is null or "unstamped" ? "unstamped" : "ok";
        if (s.Status == "unstamped") s.Error = "Running, but this build was not made with the new publish script.";
    }

    // ------------------------------------------------------------------ history

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_connection);
        await cn.OpenAsync(ct);
        if (!_ensured)
        {
            await using var ensure = new SqlCommand(EnsureSql, cn);
            await ensure.ExecuteNonQueryAsync(ct);
            _ensured = true;
        }
        return cn;
    }

    private async Task RecordAsync(List<AppStatus> list, CancellationToken ct)
    {
        var stamped = list.Where(s => s.Status == "ok" && s.Version is not null).ToList();
        if (stamped.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        foreach (var s in stamped)
        {
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.DeploymentHistory WHERE App = @App AND Version = @Version)
    INSERT dbo.DeploymentHistory (App, Version, CommitHash, Branch, Dirty, CommitMessage, BuiltAt, BuiltBy)
    VALUES (@App, @Version, @Commit, @Branch, @Dirty, @Message, @BuiltAt, @BuiltBy);
SELECT FirstSeenAt FROM dbo.DeploymentHistory WHERE App = @App AND Version = @Version;", cn);
            cmd.Parameters.AddWithValue("@App", Trim(s.App, 40)!);
            cmd.Parameters.AddWithValue("@Version", Trim(s.Version, 40)!);
            cmd.Parameters.AddWithValue("@Commit", (object?)Trim(s.Commit, 40) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Branch", (object?)Trim(s.Branch, 100) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Dirty", s.Dirty);
            cmd.Parameters.AddWithValue("@Message", (object?)Trim(s.CommitMessage, 300) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BuiltAt", (object?)s.BuiltAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BuiltBy", (object?)Trim(s.BuiltBy, 100) ?? DBNull.Value);
            var seen = await cmd.ExecuteScalarAsync(ct);
            if (seen is DateTime d) s.LiveSince = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }
    }

    public async Task<List<HistoryRow>> HistoryAsync(string? app, int take, CancellationToken ct = default)
    {
        var rows = new List<HistoryRow>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT TOP (@Take) DeploymentHistoryId, App, Version, CommitHash, Branch, Dirty, CommitMessage, BuiltAt, BuiltBy, FirstSeenAt
FROM dbo.DeploymentHistory
WHERE @App IS NULL OR App = @App
ORDER BY FirstSeenAt DESC, DeploymentHistoryId DESC;", cn);
        cmd.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 500));
        cmd.Parameters.AddWithValue("@App", string.IsNullOrWhiteSpace(app) ? DBNull.Value : app.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(new HistoryRow
            {
                Id = r.GetInt32(0),
                App = r.GetString(1),
                Version = r.GetString(2),
                Commit = Str(3),
                Branch = Str(4),
                Dirty = r.GetBoolean(5),
                CommitMessage = Str(6),
                BuiltAt = r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
                BuiltBy = Str(8),
                FirstSeenAt = DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc),
            });
        }
        return rows;
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>
/// Looks at every app every 15 minutes (and 1 minute after start), so a new build is recorded in
/// dbo.DeploymentHistory soon after it goes live even if nobody opens Settings > Deployments.
/// Switch off with "Deployments:WatchMinutes": 0.
/// </summary>
public sealed class DeploymentWatcher : BackgroundService
{
    private readonly DeploymentTracker _tracker;
    private readonly IConfiguration _config;
    private readonly ILogger<DeploymentWatcher> _logger;

    public DeploymentWatcher(DeploymentTracker tracker, IConfiguration config, ILogger<DeploymentWatcher> logger)
    {
        _tracker = tracker;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _config.GetValue("Deployments:WatchMinutes", 15);
        if (minutes <= 0) return;
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var list = await _tracker.CheckAsync(record: true, stoppingToken);
                foreach (var down in list.Where(s => s.Status == "down"))
                    _logger.LogWarning("Deployments: {App} is not answering ({Url}): {Error}", down.Label, down.Url, down.Error);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Deployments check failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
