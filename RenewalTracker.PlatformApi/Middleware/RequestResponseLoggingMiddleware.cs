using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace RenewalTracker.PlatformApi.Middleware;

/// <summary>
/// Logs every request/response that reaches the API to Logs/requests.log (and errors to Logs/errors.log):
///
///   [7F3A9C21D4] POST /api/public/web-forms/enquiry -> 500 (182ms) | user: - | ip: 94.200.1.10 |
///                origin: https://blisspointworld.givingsign.in | request: {...} | response: {...}
///
/// - every line starts with the request id (see <see cref="RequestId"/>), also sent back in the
///   X-Request-Id header and in error responses, so a reported error can be found in seconds;
/// - level follows the outcome: 5xx = Error, 4xx = Warning, otherwise Information
///   (so Logs/errors.log holds only the requests that went wrong; a 401 "not signed in" stays Information);
/// - a browser request whose Origin was NOT allowed by CORS is logged as a warning that names the
///   origin - the usual reason the website "can't reach the API";
/// - request/response bodies of JSON/text payloads are logged truncated, with credentials redacted.
///
/// Registered FIRST in the pipeline (Program.cs) so it wraps ExceptionHandlingMiddleware and always
/// logs the true final status code.
/// </summary>
public class RequestResponseLoggingMiddleware
{
    private const int MaxBodyLength = 4000;

    // Matched case-insensitively against JSON property names - anything that looks like a credential
    // never reaches the log file, whichever endpoint it came from.
    private static readonly string[] SensitiveFields =
    {
        "password", "initialpassword", "newpassword", "currentpassword", "token", "accessToken", "key", "secret",
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
        var id = RequestId.Get(context);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestId.HeaderName] = id;
            return Task.CompletedTask;
        });

        // SignalR / websocket traffic: one short line, no body buffering (it is a long-lived stream)
        if (context.Request.Path.StartsWithSegments("/hubs") || context.WebSockets.IsWebSocketRequest)
        {
            var sw = Stopwatch.StartNew();
            try { await _next(context); }
            finally
            {
                _logger.LogInformation("[{RequestId}] {Method} {Path} -> {StatusCode} ({ElapsedMs}ms) | hub connection",
                    id, context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.ElapsedMilliseconds);
            }
            return;
        }

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

            var status = context.Response.StatusCode;
            // 401 = not signed in / token expired: routine, kept out of errors.log
            var level = status >= 500 ? LogLevel.Error : status >= 400 && status != 401 ? LogLevel.Warning : LogLevel.Information;
            var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
            var origin = context.Request.Headers.Origin.ToString();
            var user = context.User?.FindFirst(ClaimTypes.Email)?.Value ?? context.User?.FindFirst("email")?.Value
                       ?? context.User?.Identity?.Name ?? "-";

            _logger.Log(level,
                "[{RequestId}] {Method} {Path}{Query} -> {StatusCode} ({ElapsedMs}ms) | user: {User} | ip: {Ip} | origin: {Origin} | request: {RequestBody} | response: {ResponseBody}",
                id,
                context.Request.Method,
                context.Request.Path,
                query,
                status,
                stopwatch.ElapsedMilliseconds,
                user,
                ClientIp(context),
                string.IsNullOrEmpty(origin) ? "-" : origin,
                string.IsNullOrEmpty(requestBody) ? "(none)" : requestBody,
                string.IsNullOrEmpty(responseBody) ? "(none)" : responseBody);

            // A cross-site browser request the CORS policy did not allow: the browser will hide the
            // response from the page, so say exactly which origin was refused.
            if (!string.IsNullOrEmpty(origin) && !context.Response.Headers.ContainsKey("Access-Control-Allow-Origin")
                && !SameHost(origin, context.Request.Host.Host))
            {
                _logger.LogWarning(
                    "[{RequestId}] CORS: origin {Origin} is NOT allowed for {Method} {Path} - the browser blocks this call. " +
                    "Add the origin to Website:AllowedOrigins (website) or Cors:AllowedOrigins (Admin Panel) and restart the API.",
                    id, origin, context.Request.Method, context.Request.Path);
            }
        }
    }

    private static bool SameHost(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase);

    private static string ClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
        return context.Connection.RemoteIpAddress?.ToString() ?? "-";
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (!IsLoggableContentType(request.ContentType) || request.ContentLength is null or 0)
        {
            return request.ContentType?.Contains("multipart", StringComparison.OrdinalIgnoreCase) == true
                ? $"(multipart, {request.ContentLength ?? 0} bytes)"
                : string.Empty;
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
            return buffer.Length == 0 ? string.Empty : $"({contentType ?? "binary"}, {buffer.Length} bytes)";
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
