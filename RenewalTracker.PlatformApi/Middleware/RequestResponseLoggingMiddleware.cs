using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RenewalTracker.PlatformApi.Middleware;

/// <summary>
/// Logs every request/response that reaches the API - method, path, query,
/// status code, elapsed time, plus the request/response bodies for JSON/text
/// payloads (truncated, and with known-sensitive fields redacted before
/// anything is written out). Goes through the standard ILogger, which
/// Program.cs wires to log4net (see log4net.config, appender
/// "RequestResponseAppender" / file Logs/requests.log) - this class has no
/// direct log4net dependency itself.
///
/// Registered FIRST in the pipeline (see Program.cs), before
/// ExceptionHandlingMiddleware, so it wraps it and always logs the true
/// final status code - including a request that failed and was turned into
/// a clean 4xx/5xx by that middleware.
/// </summary>
public class RequestResponseLoggingMiddleware
{
    private const int MaxBodyLength = 4000;

    // Matched case-insensitively against JSON property names - anything
    // that looks like a credential never reaches the log file, whichever
    // endpoint it came from (login, reset-password, tenant provisioning, ...).
    private static readonly string[] SensitiveFields =
    {
        "password", "initialpassword", "newpassword", "currentpassword", "token", "key", "secret",
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestResponseLoggingMiddleware> _logger;

    public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestBody = await ReadRequestBodyAsync(context.Request);

        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            responseBuffer.Seek(0, SeekOrigin.Begin);
            var responseBody = await ReadBufferedBodyAsync(responseBuffer, context.Response.ContentType);
            responseBuffer.Seek(0, SeekOrigin.Begin);
            await responseBuffer.CopyToAsync(originalResponseBody);
            context.Response.Body = originalResponseBody;

            var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;

            _logger.LogInformation(
                "{Method} {Path}{Query} -> {StatusCode} ({ElapsedMs}ms) | request: {RequestBody} | response: {ResponseBody}",
                context.Request.Method,
                context.Request.Path,
                query,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                string.IsNullOrEmpty(requestBody) ? "(none)" : requestBody,
                string.IsNullOrEmpty(responseBody) ? "(none)" : responseBody);
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (!IsLoggableContentType(request.ContentType) || request.ContentLength is null or 0)
        {
            return string.Empty;
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return Redact(Truncate(body));
    }

    private static async Task<string> ReadBufferedBodyAsync(Stream buffer, string? contentType)
    {
        if (!IsLoggableContentType(contentType) || buffer.Length == 0)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return Redact(Truncate(body));
    }

    private static bool IsLoggableContentType(string? contentType) =>
        contentType is not null &&
        (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("text", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value) =>
        value.Length > MaxBodyLength ? value[..MaxBodyLength] + "...(truncated)" : value;

    private static string Redact(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        foreach (var field in SensitiveFields)
        {
            json = Regex.Replace(json, $"(\"{field}\"\\s*:\\s*)\"[^\"]*\"", "$1\"***\"", RegexOptions.IgnoreCase);
        }
        return json;
    }
}
