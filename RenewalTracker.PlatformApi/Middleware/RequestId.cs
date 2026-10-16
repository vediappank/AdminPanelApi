namespace RenewalTracker.PlatformApi.Middleware;

/// <summary>
/// A short id given to every request (e.g. "7F3A9C21D4"). It is written at the start of every
/// log line about that request, returned in the "X-Request-Id" response header and included in
/// every error response ({ message, requestId }) - so an error a user reports can be found in
/// Logs/requests.log / Logs/errors.log by searching for that id.
/// </summary>
public static class RequestId
{
    public const string HeaderName = "X-Request-Id";
    private const string ItemKey = "__RequestId";

    public static string Get(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is string id) return id;
        id = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        context.Items[ItemKey] = id;
        return id;
    }
}
