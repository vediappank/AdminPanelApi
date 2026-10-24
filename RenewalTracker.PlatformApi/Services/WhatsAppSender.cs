using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// The WhatsApp account messages are sent from - set in the Admin Panel (Settings ▸ Email &amp; WhatsApp), stored in
/// SuperAdminPanel.dbo.PlatformMessagingSettings (see PlatformMessagingStore).
/// Provider: meta (WhatsApp Cloud API), twilio, gupshup, interakt or custom (your own HTTPS endpoint).
/// </summary>
public sealed class WhatsAppConfig
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "meta";
    /// <summary>Custom endpoint / override of the provider's default URL.</summary>
    public string? ApiUrl { get; set; }
    /// <summary>meta: Phone number ID · twilio: Account SID · gupshup: App name · interakt / custom: not used.</summary>
    public string? AccountId { get; set; }
    /// <summary>meta: access token · twilio: auth token · gupshup / interakt: API key · custom: bearer token.</summary>
    public string? ApiKey { get; set; }
    /// <summary>The WhatsApp business number messages come from, with country code (twilio / gupshup).</summary>
    public string? Sender { get; set; }
    /// <summary>Approved message template (meta / interakt). Its body has one variable {{1}} that receives the reminder text.</summary>
    public string? TemplateName { get; set; }
    public string TemplateLanguage { get; set; } = "en";
    /// <summary>Added to local mobile numbers without one, e.g. 971 (UAE) or 91 (India).</summary>
    public string DefaultCountryCode { get; set; } = "971";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && (WhatsAppSender.Normalize(Provider) switch
    {
        "meta" => !string.IsNullOrWhiteSpace(AccountId),
        "twilio" => !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(Sender),
        "gupshup" => !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(Sender),
        "interakt" => !string.IsNullOrWhiteSpace(TemplateName),
        "custom" => !string.IsNullOrWhiteSpace(ApiUrl),
        _ => false,
    });
}

/// <summary>
/// Sends one WhatsApp message through the configured provider. Throws InvalidOperationException with a readable
/// message (the provider's own error text when it gives one). The Business Panel API has the same class (Services/WhatsAppSender.cs) that sends the reminders; this copy is for the
/// "Send test message" button of Settings ▸ Email &amp; WhatsApp - keep them the same.
/// </summary>
public static class WhatsAppSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string Normalize(string? provider) => (provider ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Digits only, with the country code: "098765 43210" -> "919876543210", "+971 50..." -> "97150...".</summary>
    public static string NormalizeMobile(string mobile, string? defaultCountryCode)
    {
        var digits = new string((mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        var cc = new string((defaultCountryCode ?? string.Empty).Where(char.IsDigit).ToArray());
        var plus = (mobile ?? string.Empty).TrimStart().StartsWith('+');
        if (digits.StartsWith("00")) return digits[2..];
        if (plus) return digits;
        if (digits.StartsWith('0')) digits = digits.TrimStart('0');
        if (cc.Length > 0 && digits.Length <= 10) return cc + digits;
        return digits;
    }

    /// <summary>Returns the provider's message id (or "sent").</summary>
    public static async Task<string> SendAsync(WhatsAppConfig c, string mobile, string message, CancellationToken ct)
    {
        if (!c.IsConfigured) throw new InvalidOperationException("WhatsApp is not set up - enter the provider details in the Admin Panel (Settings ▸ Email & WhatsApp) and switch it on.");
        var to = NormalizeMobile(mobile, c.DefaultCountryCode);
        if (to.Length < 8 || to.Length > 15) throw new InvalidOperationException($"'{mobile}' is not a valid mobile number.");
        var text = message.Length > 1000 ? message[..1000] : message;
        var lang = string.IsNullOrWhiteSpace(c.TemplateLanguage) ? "en" : c.TemplateLanguage.Trim();
        using var request = Normalize(c.Provider) switch
        {
            "meta" => Meta(c, to, text, lang),
            "twilio" => Twilio(c, to, text),
            "gupshup" => Gupshup(c, to, text),
            "interakt" => Interakt(c, to, text, lang),
            "custom" => Custom(c, to, text),
            _ => throw new InvalidOperationException($"Unknown WhatsApp provider '{c.Provider}'."),
        };
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhatsApp ({c.Provider}) refused the message: {(int)response.StatusCode} {ErrorText(body)}");
        return MessageId(body) ?? "sent";
    }

    private static HttpRequestMessage Meta(WhatsAppConfig c, string to, string text, string lang)
    {
        var url = string.IsNullOrWhiteSpace(c.ApiUrl) ? $"https://graph.facebook.com/v20.0/{c.AccountId!.Trim()}/messages" : c.ApiUrl.Trim();
        object payload = string.IsNullOrWhiteSpace(c.TemplateName)
            ? new { messaging_product = "whatsapp", to, type = "text", text = new { body = text } }
            : new
            {
                messaging_product = "whatsapp", to, type = "template",
                template = new
                {
                    name = c.TemplateName!.Trim(), language = new { code = lang },
                    components = new object[] { new { type = "body", parameters = new object[] { new { type = "text", text } } } },
                },
            };
        var req = Json(url, payload);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey!.Trim());
        return req;
    }

    private static HttpRequestMessage Twilio(WhatsAppConfig c, string to, string text)
    {
        var sid = c.AccountId!.Trim();
        var url = string.IsNullOrWhiteSpace(c.ApiUrl) ? $"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json" : c.ApiUrl.Trim();
        var from = "+" + new string(c.Sender!.Where(char.IsDigit).ToArray());
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = "whatsapp:" + from, ["To"] = "whatsapp:+" + to, ["Body"] = text,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{c.ApiKey!.Trim()}")));
        return req;
    }

    private static HttpRequestMessage Gupshup(WhatsAppConfig c, string to, string text)
    {
        var url = string.IsNullOrWhiteSpace(c.ApiUrl) ? "https://api.gupshup.io/wa/api/v1/msg" : c.ApiUrl.Trim();
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["channel"] = "whatsapp",
                ["source"] = new string(c.Sender!.Where(char.IsDigit).ToArray()),
                ["destination"] = to,
                ["message"] = JsonSerializer.Serialize(new { type = "text", text }),
                ["src.name"] = c.AccountId!.Trim(),
            }),
        };
        req.Headers.Add("apikey", c.ApiKey!.Trim());
        return req;
    }

    private static HttpRequestMessage Interakt(WhatsAppConfig c, string to, string text, string lang)
    {
        var url = string.IsNullOrWhiteSpace(c.ApiUrl) ? "https://api.interakt.ai/v1/public/message/" : c.ApiUrl.Trim();
        var cc = new string((c.DefaultCountryCode ?? string.Empty).Where(char.IsDigit).ToArray());
        var local = cc.Length > 0 && to.StartsWith(cc) ? to[cc.Length..] : to;
        var req = Json(url, new
        {
            countryCode = "+" + cc, phoneNumber = local, type = "Template",
            template = new { name = c.TemplateName!.Trim(), languageCode = lang, bodyValues = new[] { text } },
        });
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", c.ApiKey!.Trim());
        return req;
    }

    private static HttpRequestMessage Custom(WhatsAppConfig c, string to, string text)
    {
        var req = Json(c.ApiUrl!.Trim(), new { to, message = text, sender = c.Sender, template = c.TemplateName });
        if (!string.IsNullOrWhiteSpace(c.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey.Trim());
        return req;
    }

    private static HttpRequestMessage Json(string url, object payload) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static string ErrorText(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(no details)";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? body : e.ToString();
            if (root.TryGetProperty("message", out var msg)) return msg.ToString();
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }

    private static string? MessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                && list[0].TryGetProperty("id", out var id)) return id.GetString();
            if (root.TryGetProperty("sid", out var sid)) return sid.GetString();
            if (root.TryGetProperty("messageId", out var mid)) return mid.GetString();
            if (root.TryGetProperty("id", out var any)) return any.ToString();
        }
        catch (JsonException) { }
        return null;
    }
}
