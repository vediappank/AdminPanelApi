using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Website visitor counter (dbo.WebsiteVisits / dbo.WebsitePageViews - DatabaseScripts\Migrations\004_WebsiteVisits.sql).
///
/// The website (js/bp-visits.js) sends one hit per page view with a random visitor id it keeps in the browser.
/// A visitor counts once per day; no IP address or other personal data is stored. Hits from bots are dropped,
/// and one network can add at most <see cref="MaxNewVisitorsPerIpPerDay"/> new visitors a day (the IP is only
/// held in memory for that check), so the public number cannot be pumped up by a script.
/// </summary>
public sealed class WebsiteVisitStore
{
    public const int MaxNewVisitorsPerIpPerDay = 30;

    private static readonly Regex VisitorRule = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
    private static readonly Regex BotRule = new(@"bot|crawl|spider|slurp|bingpreview|facebookexternalhit|headless|lighthouse|pingdom|uptime|monitor|curl|wget|python|java/|go-http|okhttp|axios|postman",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _connection;
    private readonly IConfiguration _config;
    private readonly ILogger<WebsiteVisitStore> _logger;

    // per-IP new-visitor budget for today (memory only)
    private readonly ConcurrentDictionary<string, int> _newPerIp = new();
    private DateOnly _budgetDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private (DateTime At, VisitSummary Value)? _summaryCache;

    public WebsiteVisitStore(IConfiguration config, ILogger<WebsiteVisitStore> logger)
    {
        _config = config;
        _logger = logger;
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
    }

    /// <summary>The public counter shows only from this many visitors this year (Website:VisitorCounterMinimum, default 1000).</summary>
    public int ShowFrom => Math.Max(0, _config.GetValue("Website:VisitorCounterMinimum", 1000));

    public static bool IsBot(string? userAgent) => string.IsNullOrWhiteSpace(userAgent) || BotRule.IsMatch(userAgent);

    public sealed record Hit(string VisitorId, string Path, string? Source, string? Device);

    public static Hit? Clean(string? visitorId, string? path, string? source, string? device)
    {
        var id = (visitorId ?? "").Trim().ToLowerInvariant();
        if (!VisitorRule.IsMatch(id)) return null;
        var p = (path ?? "/").Trim();
        var q = p.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) p = p[..q];
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 300) p = p[..300];
        if (p.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)) p = p[..^"index.html".Length];
        var s = (source ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("www.")) s = s[4..];
        if (s.Length == 0 || s.Contains("blisspointworld.com")) s = "(direct)";
        if (s.Length > 100) s = s[..100];
        var d = device is "mobile" or "desktop" ? device : null;
        return new Hit(id, p, s, d);
    }

    /// <summary>Records a page view. Returns false when the per-network limit for new visitors is reached.</summary>
    public async Task<bool> RecordAsync(Hit hit, string ip, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _budgetDay) { _newPerIp.Clear(); _budgetDay = today; }

        await using var cn = new SqlConnection(_connection);
        await cn.OpenAsync(ct);

        var updated = await cn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.WebsiteVisits SET PageViews = PageViews + 1, LastSeenAt = SYSUTCDATETIME()
WHERE VisitDate = CAST(SYSUTCDATETIME() AS DATE) AND VisitorId = @VisitorId;", new { hit.VisitorId }, cancellationToken: ct));

        if (updated == 0)
        {
            if (_newPerIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > MaxNewVisitorsPerIpPerDay) return false;
            await cn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.WebsiteVisits WHERE VisitDate = CAST(SYSUTCDATETIME() AS DATE) AND VisitorId = @VisitorId)
    INSERT dbo.WebsiteVisits (VisitDate, VisitorId, LandingPath, Source, Device)
    VALUES (CAST(SYSUTCDATETIME() AS DATE), @VisitorId, @Path, @Source, @Device);", hit, cancellationToken: ct));
        }

        await cn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.WebsitePageViews SET Views = Views + 1 WHERE ViewDate = CAST(SYSUTCDATETIME() AS DATE) AND Path = @Path;
IF @@ROWCOUNT = 0
    INSERT dbo.WebsitePageViews (ViewDate, Path, Views) VALUES (CAST(SYSUTCDATETIME() AS DATE), @Path, 1);", new { hit.Path }, cancellationToken: ct));
        return true;
    }

    public sealed class VisitSummary
    {
        public int VisitorsThisYear { get; set; }
        public int VisitorsThisMonth { get; set; }
        public int VisitorsToday { get; set; }
        public int VisitorsAllTime { get; set; }
        public DateTime? CountingSince { get; set; }
        public int Year { get; set; }
        public int ShowFrom { get; set; }
        /// <summary>true when the public counter may be shown (this year's visitors reached ShowFrom).</summary>
        public bool Show { get; set; }
    }

    /// <summary>Totals for the public counter - cached for a minute.</summary>
    public async Task<VisitSummary> SummaryAsync(CancellationToken ct)
    {
        if (_summaryCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Value;
        await using var cn = new SqlConnection(_connection);
        var now = DateTime.UtcNow;
        var s = await cn.QuerySingleAsync<VisitSummary>(new CommandDefinition(@"
SELECT
    (SELECT COUNT(DISTINCT VisitorId) FROM dbo.WebsiteVisits WHERE VisitDate >= @YearStart) AS VisitorsThisYear,
    (SELECT COUNT(DISTINCT VisitorId) FROM dbo.WebsiteVisits WHERE VisitDate >= @MonthStart) AS VisitorsThisMonth,
    (SELECT COUNT(*) FROM dbo.WebsiteVisits WHERE VisitDate = @Today) AS VisitorsToday,
    (SELECT COUNT(DISTINCT VisitorId) FROM dbo.WebsiteVisits) AS VisitorsAllTime,
    (SELECT CAST(MIN(VisitDate) AS DATETIME2) FROM dbo.WebsiteVisits) AS CountingSince;",
            new { YearStart = new DateTime(now.Year, 1, 1), MonthStart = new DateTime(now.Year, now.Month, 1), Today = now.Date }, cancellationToken: ct));
        s.Year = now.Year;
        s.ShowFrom = ShowFrom;
        s.Show = s.VisitorsThisYear >= s.ShowFrom;
        _summaryCache = (DateTime.UtcNow, s);
        return s;
    }

    public sealed record DayRow(DateTime Day, int Visitors, int PageViews);
    public sealed record NameCount(string Name, int Count);

    /// <summary>Admin Panel > Website Leads > Visitors.</summary>
    public async Task<object> ReportAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 366);
        var from = DateTime.UtcNow.Date.AddDays(-(days - 1));
        await using var cn = new SqlConnection(_connection);
        using var multi = await cn.QueryMultipleAsync(new CommandDefinition(@"
SELECT v.VisitDate AS Day, COUNT(*) AS Visitors, SUM(v.PageViews) AS PageViews
FROM dbo.WebsiteVisits v WHERE v.VisitDate >= @From GROUP BY v.VisitDate ORDER BY v.VisitDate;

SELECT TOP 15 Path AS Name, SUM(Views) AS Count FROM dbo.WebsitePageViews WHERE ViewDate >= @From GROUP BY Path ORDER BY SUM(Views) DESC;

SELECT TOP 10 ISNULL(Source, '(direct)') AS Name, COUNT(*) AS Count FROM dbo.WebsiteVisits WHERE VisitDate >= @From GROUP BY ISNULL(Source, '(direct)') ORDER BY COUNT(*) DESC;

SELECT ISNULL(Device, 'unknown') AS Name, COUNT(*) AS Count FROM dbo.WebsiteVisits WHERE VisitDate >= @From GROUP BY ISNULL(Device, 'unknown') ORDER BY COUNT(*) DESC;

SELECT TOP 10 LandingPath AS Name, COUNT(*) AS Count FROM dbo.WebsiteVisits WHERE VisitDate >= @From GROUP BY LandingPath ORDER BY COUNT(*) DESC;

SELECT COUNT(DISTINCT VisitorId) FROM dbo.WebsiteVisits WHERE VisitDate >= @From;",
            new { From = from }, cancellationToken: ct));

        var daily = (await multi.ReadAsync<DayRow>()).ToDictionary(d => d.Day.Date);
        var series = Enumerable.Range(0, days).Select(i => from.AddDays(i))
            .Select(d => daily.TryGetValue(d, out var r) ? r : new DayRow(d, 0, 0)).ToList();
        var pages = (await multi.ReadAsync<NameCount>()).ToList();
        var sources = (await multi.ReadAsync<NameCount>()).ToList();
        var devices = (await multi.ReadAsync<NameCount>()).ToList();
        var landing = (await multi.ReadAsync<NameCount>()).ToList();
        var unique = await multi.ReadSingleAsync<int>();
        var summary = await SummaryAsync(ct);
        return new
        {
            days, from, uniqueVisitors = unique,
            visits = series.Sum(s => s.Visitors), pageViews = series.Sum(s => s.PageViews),
            series, pages, sources, devices, landing, summary,
            excludeLink = (_config["Website:PublicUrl"] ?? "https://blisspointworld.com").TrimEnd('/') + "/?bp-internal=1",
        };
    }
}
