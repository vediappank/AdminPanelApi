using System.Data;
using Microsoft.Data.SqlClient;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Self-check of everything the API needs to work: database connection, the Website Leads /
/// Website Settings tables and stored procedures, the menu rows, the Logs folder (writable?),
/// and the allowed website origins. Run once at startup (result written to the log - see
/// Program.cs) and on demand from GET api/platform/diagnostics.
/// </summary>
public class ApiDiagnostics
{
    public static readonly string[] RequiredTables = { "WebForm", "WebEnquiry", "WebJobApplication", "WebStatusHistory", "WebSetting" };
    public static readonly string[] RequiredProcedures =
    {
        "usp_WebEnquiry_Insert", "usp_WebEnquiry_List", "usp_WebEnquiry_Get", "usp_WebJobApplication_Insert", "usp_WebJobApplication_List",
        "usp_WebLead_Summary", "usp_WebLead_Dashboard", "usp_WebForm_List", "usp_WebForm_Update", "usp_WebSetting_List", "usp_WebSetting_Save",
    };
    public static readonly string[] RequiredModules =
        { "WEBSITE", "WEBSITE_DASHBOARD", "WEBSITE_ENQUIRIES", "WEBSITE_JOBS", "WEBSITE_FORMS", "WEBSITE_SETTINGS" };

    public static readonly DateTime StartedAt = DateTime.Now;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public ApiDiagnostics(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public static string LogsFolder => Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>
    /// Short public self-check for GET /health?check=true: is log4net.config next to the exe, can the
    /// Logs folder be written, does the database answer. Plain words only - no user names or passwords.
    /// </summary>
    public async Task<object> QuickCheckAsync()
    {
        var log4netConfig = File.Exists(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        string logs;
        try
        {
            Directory.CreateDirectory(LogsFolder);
            var probe = Path.Combine(LogsFolder, ".write-test");
            await File.WriteAllTextAsync(probe, DateTime.Now.ToString("O"));
            File.Delete(probe);
            logs = "writable";
        }
        catch (UnauthorizedAccessException)
        {
            logs = "NOT writable - access denied: give the site's IIS user (Plesk IIS WP User) Modify permission on the Logs folder";
        }
        catch (Exception ex)
        {
            logs = "NOT writable - " + ex.GetType().Name;
        }

        string database, databaseName = "-";
        var cs = _config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            database = "NOT configured - ConnectionStrings:DefaultConnection is empty";
        else
        {
            try
            {
                databaseName = new SqlConnectionStringBuilder(cs).InitialCatalog;
                await using var cn = new SqlConnection(cs);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await cn.OpenAsync(timeout.Token);
                database = "connected";
            }
            catch (OperationCanceledException) { database = "FAILED - no answer within 8 seconds (server name / SQL Server not running / firewall)"; }
            catch (SqlException ex)
            {
                database = ex.Number switch
                {
                    18456 => "FAILED - login failed for the SQL user in the connection string (wrong user or password)",
                    4060 => "FAILED - the database in the connection string does not exist or the user has no access to it",
                    -1 or 0 or 2 or 53 or 26 => "FAILED - SQL Server not found (check the Server= part of the connection string)",
                    _ => $"FAILED - SQL error {ex.Number}",
                };
            }
            catch (Exception ex) { database = "FAILED - " + ex.GetType().Name; }
        }

        return new
        {
            log4netConfig = log4netConfig ? "found" : "MISSING - log4net.config is not next to the exe, so nothing is logged",
            logsFolder = logs,
            database,
            databaseName,
        };
    }

    public async Task<DiagnosticsReport> RunAsync(CancellationToken ct = default)
    {
        var r = new DiagnosticsReport
        {
            Environment = _env.EnvironmentName,
            Version = typeof(ApiDiagnostics).Assembly.GetName().Version?.ToString() ?? "-",
            StartedAt = StartedAt,
            ServerTime = DateTime.Now,
            LogsFolder = LogsFolder,
            WebsiteAllowedOrigins = _config.GetSection("Website:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>(),
            AdminAllowedOrigins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>(),
        };

        // ---- Logs folder writable? ----
        try
        {
            Directory.CreateDirectory(LogsFolder);
            var probe = Path.Combine(LogsFolder, ".write-test");
            await File.WriteAllTextAsync(probe, DateTime.Now.ToString("O"), ct);
            File.Delete(probe);
            r.LogsWritable = true;
        }
        catch (Exception ex)
        {
            r.Problems.Add($"Logs folder is not writable ({LogsFolder}): {ex.Message}. Give the IIS app-pool user Modify permission on it.");
        }

        // ---- Database ----
        var cs = _config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            r.Problems.Add("ConnectionStrings:DefaultConnection is empty.");
            return r;
        }
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            r.DatabaseServer = b.DataSource;
            r.DatabaseName = b.InitialCatalog;

            await using var cn = new SqlConnection(cs);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await cn.OpenAsync(timeout.Token);
            r.DatabaseConnected = true;

            r.MissingTables = await MissingAsync(cn, "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo')", RequiredTables, ct);
            r.MissingProcedures = await MissingAsync(cn, "SELECT name FROM sys.procedures WHERE schema_id = SCHEMA_ID('dbo')", RequiredProcedures, ct);
            if (!r.MissingTables.Contains("WebForm"))
            {
                await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.WebForm WHERE IsActive = 1", cn);
                r.ActiveWebsiteForms = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            }
            try
            {
                r.MissingMenuModules = await MissingAsync(cn, "SELECT ModuleCode FROM dbo.Modules WHERE IsActive = 1", RequiredModules, ct);
            }
            catch (SqlException) { /* Modules table not in this database - reported via the menu list */ }

            if (r.MissingTables.Count > 0 || r.MissingProcedures.Count > 0)
                r.Problems.Add($"Database '{r.DatabaseName}' is missing Website Leads objects - run DatabaseScripts/WebsiteLeads_SuperAdminPanel.sql " +
                               "and WebsiteSettings_SuperAdminPanel.sql against it.");
            if (r.MissingMenuModules.Count > 0)
                r.Problems.Add($"Menu rows missing or inactive: {string.Join(", ", r.MissingMenuModules)}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            r.Problems.Add($"Database connection failed ({r.DatabaseServer}/{r.DatabaseName}): no answer within 10 seconds - " +
                           "check the server name in the connection string, that SQL Server is running and that the firewall allows it.");
        }
        catch (Exception ex)
        {
            r.Problems.Add($"Database connection failed ({r.DatabaseServer}/{r.DatabaseName}): {ex.Message}");
        }

        if (r.WebsiteAllowedOrigins.Length == 0)
            r.Problems.Add("Website:AllowedOrigins is empty - the website's forms will be blocked (CORS).");
        return r;
    }

    private static async Task<List<string>> MissingAsync(SqlConnection cn, string sql, IEnumerable<string> required, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, cn) { CommandType = CommandType.Text };
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) found.Add(rd.GetString(0));
        return required.Where(x => !found.Contains(x)).ToList();
    }
}

public class DiagnosticsReport
{
    public bool Healthy => Problems.Count == 0;
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime ServerTime { get; set; }
    public string LogsFolder { get; set; } = "";
    public bool LogsWritable { get; set; }
    public string? DatabaseServer { get; set; }
    public string? DatabaseName { get; set; }
    public bool DatabaseConnected { get; set; }
    public List<string> MissingTables { get; set; } = new();
    public List<string> MissingProcedures { get; set; } = new();
    public List<string> MissingMenuModules { get; set; } = new();
    public int? ActiveWebsiteForms { get; set; }
    public string[] WebsiteAllowedOrigins { get; set; } = Array.Empty<string>();
    public string[] AdminAllowedOrigins { get; set; } = Array.Empty<string>();
    public List<string> Problems { get; set; } = new();
}
