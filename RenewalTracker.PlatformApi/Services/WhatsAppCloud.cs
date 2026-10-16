using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Sends WhatsApp template messages through Meta's WhatsApp Business Platform (Cloud API):
/// POST https://graph.facebook.com/{ApiVersion}/{PhoneNumberId}/messages
///
/// Reads the "WhatsApp" section of appsettings:
///   Enabled            true/false - nothing is sent while false
///   ApiVersion         Graph API version, e.g. "v21.0"
///   PhoneNumberId      from Meta ▸ WhatsApp Manager ▸ API Setup (not the phone number itself)
///   AccessToken        permanent System User token with whatsapp_business_messaging permission
///   DefaultCountryCode used for local numbers such as 0501234567 (default "971")
///   StaffNumbers       comma-separated numbers that get the new-lead alert, e.g. "971589970732"
///   StaffTemplate      { Name, Language }  body params: {{1}} reference, {{2}} name, {{3}} phone, {{4}} interested in
///   CustomerTemplate   { Name, Language }  body params: {{1}} name, {{2}} reference
///
/// Business-initiated messages must use a template approved in WhatsApp Manager,
/// with the same name, language and number of {{n}} placeholders as configured here.
/// </summary>
public class WhatsAppCloud
{
    public const string HttpClientName = "WhatsAppCloud";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<WhatsAppCloud> _logger;

    public WhatsAppCloud(IHttpClientFactory http, IConfiguration config, ILogger<WhatsAppCloud> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public sealed record Template(string Name, string Language)
    {
        public bool IsSet => Name.Length > 0;
    }

    public sealed record Settings(bool Enabled, string ApiVersion, string PhoneNumberId, string AccessToken,
        string DefaultCountryCode, IReadOnlyList<string> StaffNumbers, Template StaffTemplate, Template CustomerTemplate)
    {
        public bool IsConfigured => Enabled && PhoneNumberId.Length > 0 && AccessToken.Length > 0;
    }

    public Settings Read()
    {
        var s = _config.GetSection("WhatsApp");
        var cc = Digits(s["DefaultCountryCode"]);
        if (cc.Length == 0) cc = "971";
        var staff = (s["StaffNumbers"] ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => NormalizePhone(n, cc))
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct()
            .ToList();
        return new Settings(
            Enabled: bool.TryParse(s["Enabled"], out var on) && on,
            ApiVersion: string.IsNullOrWhiteSpace(s["ApiVersion"]) ? "v21.0" : s["ApiVersion"]!.Trim(),
            PhoneNumberId: (s["PhoneNumberId"] ?? string.Empty).Trim(),
            AccessToken: (s["AccessToken"] ?? string.Empty).Trim(),
            DefaultCountryCode: cc,
            StaffNumbers: staff,
            StaffTemplate: ReadTemplate(s.GetSection("StaffTemplate")),
            CustomerTemplate: ReadTemplate(s.GetSection("CustomerTemplate")));
    }

    /// <summary>
    /// Sends one approved template. Returns true when Meta accepted the message.
    /// Never throws for API errors - they are logged, so a WhatsApp problem can't break a website submission.
    /// </summary>
    public async Task<bool> SendTemplateAsync(Settings settings, string toPhone, Template template,
        IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured || !template.IsSet) return false;

        var to = NormalizePhone(toPhone, settings.DefaultCountryCode);
        if (to == null)
        {
            _logger.LogInformation("WhatsApp: '{Phone}' is not a valid mobile number - {Template} not sent", toPhone, template.Name);
            return false;
        }

        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = to,
            ["type"] = "template",
            ["template"] = new Dictionary<string, object?>
            {
                ["name"] = template.Name,
                ["language"] = new { code = template.Language },
                ["components"] = bodyParameters.Count == 0
                    ? null
                    : new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = bodyParameters.Select(p => new { type = "text", text = Clean(p) }).ToArray(),
                        },
                    },
            },
        };

        var url = $"https://graph.facebook.com/{settings.ApiVersion}/{Uri.EscapeDataString(settings.PhoneNumberId)}/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);

        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("WhatsApp: template {Template} sent to {To}", template.Name, Mask(to));
                return true;
            }
            _logger.LogWarning("WhatsApp: template {Template} to {To} failed - HTTP {Status}: {Body}",
                template.Name, Mask(to), (int)response.StatusCode, Truncate(body, 600));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp: template {Template} to {To} could not reach graph.facebook.com", template.Name, Mask(to));
            return false;
        }
    }

    /// <summary>
    /// International digits only (no +), or null when it can't be a phone number.
    /// 00971..., +971..., 0501234567 and 501234567 all become 971501234567 for DefaultCountryCode 971.
    /// </summary>
    public static string? NormalizePhone(string? phone, string defaultCountryCode)
    {
        var d = Digits(phone);
        if (d.StartsWith("00")) d = d[2..];
        else if (d.StartsWith('0')) d = defaultCountryCode + d[1..];
        else if (d.Length <= 9) d = defaultCountryCode + d;
        return d.Length is >= 10 and <= 15 ? d : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static Template ReadTemplate(IConfigurationSection s) =>
        new((s["Name"] ?? string.Empty).Trim(), string.IsNullOrWhiteSpace(s["Language"]) ? "en" : s["Language"]!.Trim());

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    // Template parameters may not contain new lines, tabs or more than 4 spaces in a row, and can't be empty.
    private static string Clean(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) text = "-";
        return text.Length > 300 ? text[..300] : text;
    }

    private static string Mask(string phone) => phone.Length > 4 ? new string('*', phone.Length - 4) + phone[^4..] : phone;

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] + "..." : value;
}
