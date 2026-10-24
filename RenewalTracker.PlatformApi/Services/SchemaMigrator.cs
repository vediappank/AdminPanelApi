using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// SuperAdminPanel database changes, applied automatically when the Admin API starts.
///
///   AdminPanelApi\DatabaseScripts\Migrations\NNN_Description.sql   (e.g. 004_Invoice_Notes.sql)
///
/// The folder is copied into the API build (Migrations\ next to the exe), so deploy.cmd uploads new
/// scripts with the API and the restart runs them - on your PC the same happens when you run the API.
/// Rules:
///   * Each script runs ONCE, in number order, inside one transaction; "GO" lines split it into batches.
///   * Done scripts are recorded in dbo.SchemaMigrations (name, checksum, when, how long).
///   * A failing script is rolled back, logged, shown on Settings > Deployments, and the ones after it
///     wait - the API itself keeps running. Fix the script (or add a corrected one) and restart the API.
///   * Never edit a script that already ran: add a new number instead (an edited one is reported, not re-run).
///   * Write scripts so they can safely run again (IF NOT EXISTS ...) - they then also work on a database
///     where the change was already made by hand.
///   * Switch off with "DatabaseMigrations:Enabled": false.
/// </summary>
public sealed class SchemaMigrator
{
    public const string FolderName = "Migrations";

    private const string EnsureSql = @"
IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
CREATE TABLE dbo.SchemaMigrations
(
    SchemaMigrationId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
    ScriptName   NVARCHAR(200) NOT NULL CONSTRAINT UQ_SchemaMigrations_Script UNIQUE,
    Checksum     VARCHAR(64)   NOT NULL,
    AppliedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedAt DEFAULT (SYSUTCDATETIME()),
    AppliedBy    NVARCHAR(200) NULL,
    DurationMs   INT           NOT NULL CONSTRAINT DF_SchemaMigrations_Duration DEFAULT (0),
    ApiVersion   VARCHAR(40)   NULL
);";

    private static readonly Regex GoLine = new(@"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex NameRule = new(@"^\d{3,}_[A-Za-z0-9_\-]+\.sql$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class ScriptInfo
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>applied | pending | failed | changed (edited after it ran) | skipped (bad file name)</summary>
        public string Status { get; set; } = "pending";
        public DateTime? AppliedAt { get; set; }
        public int? DurationMs { get; set; }
        public string? Error { get; set; }
    }

    public sealed class Report
    {
        public bool Enabled { get; set; } = true;
        public DateTime? RanAt { get; set; }
        public string Folder { get; set; } = string.Empty;
        public int Applied { get; set; }
        public int AppliedNow { get; set; }
        public int Pending { get; set; }
        public string? Error { get; set; }
        public List<ScriptInfo> Scripts { get; set; } = new();
    }

    private readonly string _connection;
    private readonly IConfiguration _config;
    private readonly ILogger<SchemaMigrator> _logger;

    public SchemaMigrator(IConfiguration config, ILogger<SchemaMigrator> logger)
    {
        _config = config;
        _logger = logger;
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
    }

    /// <summary>Result of the last run (startup) - shown on Settings > Deployments.</summary>
    public Report Last { get; private set; } = new() { Enabled = false };

    public static string ScriptsFolder => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Runs every script that has not run yet. Never throws - problems are logged and kept in <see cref="Last"/>.</summary>
    public Report Run()
    {
        var report = new Report { RanAt = DateTime.UtcNow, Folder = ScriptsFolder, Enabled = _config.GetValue("DatabaseMigrations:Enabled", true) };
        Last = report;
        if (!report.Enabled)
        {
            _logger.LogInformation("DB MIGRATIONS -> switched off (DatabaseMigrations:Enabled = false)");
            return report;
        }

        try
        {
            var files = Directory.Exists(ScriptsFolder)
                ? Directory.GetFiles(ScriptsFolder, "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            using var cn = new SqlConnection(_connection);
            cn.Open();
            cn.InfoMessage += (_, e) => _logger.LogInformation("DB MIGRATIONS    {Message}", e.Message);
            using (var ensure = new SqlCommand(EnsureSql, cn)) ensure.ExecuteNonQuery();

            var done = new Dictionary<string, (string Checksum, DateTime At, int Ms)>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand("SELECT ScriptName, Checksum, AppliedAt, DurationMs FROM dbo.SchemaMigrations", cn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) done[r.GetString(0)] = (r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetInt32(3));
            }

            var blocked = false;
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var info = new ScriptInfo { Name = name };
                report.Scripts.Add(info);

                if (!NameRule.IsMatch(name))
                {
                    info.Status = "skipped";
                    info.Error = "File name must look like 004_Short_Description.sql";
                    _logger.LogWarning("DB MIGRATIONS -> {Script} skipped: name must look like 004_Short_Description.sql", name);
                    continue;
                }

                var sql = File.ReadAllText(file);
                var checksum = Checksum(sql);
                if (done.TryGetValue(name, out var prev))
                {
                    info.AppliedAt = prev.At;
                    info.DurationMs = prev.Ms;
                    info.Status = "applied";
                    report.Applied++;
                    if (!string.Equals(prev.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        info.Status = "changed";
                        info.Error = "Edited after it ran - not run again. Put the change in a new script with the next number.";
                        _logger.LogWarning("DB MIGRATIONS -> {Script} was edited after it ran on this database - not run again; add a new numbered script", name);
                    }
                    continue;
                }

                if (blocked)
                {
                    info.Status = "pending";
                    info.Error = "Waits for the failed script above.";
                    report.Pending++;
                    continue;
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    Apply(cn, name, sql, checksum);
                    info.Status = "applied";
                    info.AppliedAt = DateTime.UtcNow;
                    info.DurationMs = (int)sw.ElapsedMilliseconds;
                    report.Applied++;
                    report.AppliedNow++;
                    _logger.LogInformation("DB MIGRATIONS -> {Script} applied ({Ms} ms)", name, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    info.Status = "failed";
                    info.Error = ex.Message;
                    report.Error = $"{name}: {ex.Message}";
                    blocked = true;
                    _logger.LogError(ex, "DB MIGRATIONS -> {Script} FAILED and was rolled back - the scripts after it wait. Fix it and restart the API.", name);
                }
            }

            _logger.LogInformation("DB MIGRATIONS -> {Total} script(s) in {Folder}: {Applied} applied ({Now} now), {Pending} waiting{Failed}",
                files.Count, ScriptsFolder, report.Applied, report.AppliedNow, report.Pending, report.Error is null ? string.Empty : ", 1 FAILED");
        }
        catch (Exception ex)
        {
            report.Error = ex.Message;
            _logger.LogError(ex, "DB MIGRATIONS -> could not run (database not reachable?)");
        }
        return report;
    }

    private void Apply(SqlConnection cn, string name, string sql, string checksum)
    {
        var sw = Stopwatch.StartNew();
        using var tx = cn.BeginTransaction();
        // one API process at a time (e.g. an app pool recycle overlapping the old process)
        using (var lockCmd = new SqlCommand("EXEC sp_getapplock @Resource = N'SchemaMigrations', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 60000;", cn, tx))
            lockCmd.ExecuteNonQuery();

        using (var again = new SqlCommand("SELECT COUNT(*) FROM dbo.SchemaMigrations WHERE ScriptName = @Name", cn, tx))
        {
            again.Parameters.AddWithValue("@Name", name);
            if ((int)again.ExecuteScalar()! > 0) { tx.Commit(); return; }   // the other process did it
        }

        foreach (var batch in SplitBatches(sql))
        {
            using var cmd = new SqlCommand(batch, cn, tx) { CommandTimeout = 600 };
            cmd.ExecuteNonQuery();
        }

        using (var rec = new SqlCommand(@"INSERT dbo.SchemaMigrations (ScriptName, Checksum, AppliedBy, DurationMs, ApiVersion)
VALUES (@Name, @Checksum, @By, @Ms, @Version);", cn, tx))
        {
            rec.Parameters.AddWithValue("@Name", name);
            rec.Parameters.AddWithValue("@Checksum", checksum);
            rec.Parameters.AddWithValue("@By", $"{Environment.MachineName} (Admin API)");
            rec.Parameters.AddWithValue("@Ms", (int)sw.ElapsedMilliseconds);
            rec.Parameters.AddWithValue("@Version", (object?)ApiVersion() ?? DBNull.Value);
            rec.ExecuteNonQuery();
        }
        if (tx.Connection != null) tx.Commit();
    }

    /// <summary>Splits on lines that contain only GO (like SSMS); empty batches are dropped.</summary>
    public static List<string> SplitBatches(string sql) =>
        GoLine.Split(sql.Replace("\r\n", "\n"))
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !IsOnlyComments(b))
            .ToList();

    private static bool IsOnlyComments(string batch)
    {
        var noBlock = Regex.Replace(batch, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return noBlock.Split('\n').All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("--", StringComparison.Ordinal));
    }

    private static string Checksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string? ApiVersion()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "version.json");
            if (!File.Exists(file)) return null;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            return node?["version"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
