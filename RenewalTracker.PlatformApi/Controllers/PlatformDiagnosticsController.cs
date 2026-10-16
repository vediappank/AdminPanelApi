using System.Text;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Troubleshooting without RDP / File Manager (signed-in Admin Panel users only):
///
///   GET api/platform/diagnostics
///       health report - database reachable? Website Leads tables / procedures / menu rows present?
///       Logs folder writable? allowed website origins - plus a list of problems found.
///   GET api/platform/diagnostics/logs?file=errors&amp;lines=200&amp;search=7F3A9C21D4&amp;date=20261015
///       the last N lines of a log file. file = errors (default) | requests | app;
///       search = any text, e.g. the requestId from an error message; date = an older day (yyyyMMdd).
///
/// Try them from Swagger (Authorize with your Admin Panel token) or the browser's address bar
/// after signing in.
/// </summary>
[ApiController]
[Route("api/platform/diagnostics")]
[RequirePlatformAuth]
public class PlatformDiagnosticsController : ControllerBase
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["errors"] = "errors.log",
        ["requests"] = "requests.log",
        ["app"] = "platform-api.log",
    };

    private readonly ApiDiagnostics _diagnostics;

    public PlatformDiagnosticsController(ApiDiagnostics diagnostics) => _diagnostics = diagnostics;

    [HttpGet]
    public async Task<ActionResult<DiagnosticsReport>> Report(CancellationToken ct) => Ok(await _diagnostics.RunAsync(ct));

    [HttpGet("logs")]
    public async Task<IActionResult> Logs([FromQuery] string file = "errors", [FromQuery] int lines = 200,
        [FromQuery] string? search = null, [FromQuery] string? date = null, CancellationToken ct = default)
    {
        if (!Files.TryGetValue(file, out var name))
            return BadRequest(new { message = "file must be errors, requests or app." });
        if (!string.IsNullOrEmpty(date) && (date.Length != 8 || !date.All(char.IsDigit)))
            return BadRequest(new { message = "date must be yyyyMMdd, e.g. 20261015." });

        var path = Path.Combine(ApiDiagnostics.LogsFolder, string.IsNullOrEmpty(date) ? name : $"{name}.{date}.log");
        var available = Directory.Exists(ApiDiagnostics.LogsFolder)
            ? Directory.GetFiles(ApiDiagnostics.LogsFolder, "*.log").Select(Path.GetFileName).OrderByDescending(x => x).Take(40).ToArray()
            : Array.Empty<string?>();
        if (!System.IO.File.Exists(path))
            return NotFound(new { message = $"{Path.GetFileName(path)} does not exist (nothing logged yet, or a different day).", logsFolder = ApiDiagnostics.LogsFolder, available });

        lines = Math.Clamp(lines, 1, 2000);
        var tail = await ReadTailAsync(path, 4 * 1024 * 1024, ct);   // last 4 MB is plenty for any day
        var all = SplitEntries(tail);
        if (!string.IsNullOrWhiteSpace(search))
            all = all.Where(l => l.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        return Ok(new
        {
            file = Path.GetFileName(path),
            logsFolder = ApiDiagnostics.LogsFolder,
            totalMatching = all.Count,
            entries = all.Skip(Math.Max(0, all.Count - lines)).Reverse().ToList(),   // newest first
            available,
        });
    }

    /// <summary>Reads the end of a file that log4net still has open (FileShare.ReadWrite).</summary>
    private static async Task<string> ReadTailAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        if (fs.Length > maxBytes) text = text[(text.IndexOf('\n') + 1)..];   // drop the partial first line
        return text;
    }

    /// <summary>One entry per log record - a stack trace stays attached to the line that started it.</summary>
    private static List<string> SplitEntries(string text)
    {
        var entries = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var startsRecord = line.Length > 19 && char.IsDigit(line[0]) && line[4] == '-' && line[10] == ' ';
            if (startsRecord && sb.Length > 0) { entries.Add(sb.ToString()); sb.Clear(); }
            if (line.Length == 0 && sb.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) entries.Add(sb.ToString());
        return entries;
    }
}
