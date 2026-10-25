using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Website sign-up for new posts (blisspointworld.com: subscribe.html, my-account.html, unsubscribe.html - js/bp-subscribe.js).
///
///   GET  api/public/subscribers/options                 which channels are on (WhatsApp code needed or not)
///   POST api/public/subscribers/signup                  { name, email, whatsapp, language, consent, source, website(honeypot) }
///                                                       -> codes sent; { ticket, email, whatsapp, needsWhatsAppCode }
///   POST api/public/subscribers/verify                  { ticket, emailCode, whatsappCode } -> { session } (signed up + logged in)
///   POST api/public/subscribers/login                   { identifier }  email or WhatsApp number -> code sent; { ticket, channel, to }
///   POST api/public/subscribers/login/verify            { ticket, code } -> { session }
///   POST api/public/subscribers/resend                  { ticket } -> new codes, new ticket
///   GET  api/public/subscribers/me                      header X-BP-Session -> profile
///   POST api/public/subscribers/me                      { name, language, emailOptIn, whatsappOptIn } -> profile
///   POST api/public/subscribers/me/whatsapp             { whatsapp } -> code to that number; { ticket } (then login/verify)
///   POST api/public/subscribers/me/logout | me/delete
///   POST api/public/subscribers/unsubscribe             { token, channel } (unsubscribe.html)
///   POST api/public/subscribers/unsubscribe/one-click   ?t=&amp;c=email  (Gmail / Outlook one-click, RFC 8058)
///   GET/POST api/public/whatsapp/webhook                Meta webhook: a "STOP" reply switches WhatsApp off for that number
///
/// Anonymous, "Website" CORS policy, "website-forms" rate limit + per-network and per-person limits (SubscriberStore).
/// Answers never reveal whether an email or number is registered.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicSubscribersController : ControllerBase
{
    public const string SessionHeader = "X-BP-Session";

    private readonly SubscriberStore _store;
    private readonly SubscriberMessenger _messenger;
    private readonly IConfiguration _config;
    private readonly ILogger<PublicSubscribersController> _logger;

    public PublicSubscribersController(SubscriberStore store, SubscriberMessenger messenger, IConfiguration config, ILogger<PublicSubscribersController> logger)
    {
        _store = store;
        _messenger = messenger;
        _config = config;
        _logger = logger;
    }

    public sealed record SignupRequest(string? Name, string? Email, string? WhatsApp, string? Language, bool Consent, string? Source, string? Website);
    public sealed record VerifyRequest(string? Ticket, string? EmailCode, string? WhatsAppCode);
    public sealed record LoginRequest(string? Identifier, string? Website);
    public sealed record LoginVerifyRequest(string? Ticket, string? Code);
    public sealed record TicketRequest(string? Ticket);
    public sealed record PreferencesRequest(string? Name, string? Language, bool EmailOptIn, bool WhatsAppOptIn);
    public sealed record WhatsAppRequest(string? WhatsApp);
    public sealed record UnsubscribeRequest(string? Token, string? Channel);

    private string Ip
    {
        get
        {
            var forwarded = Request.Headers["X-Forwarded-For"].ToString();
            return !string.IsNullOrWhiteSpace(forwarded) ? forwarded.Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }

    private string? Digits(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? null : WhatsAppCloud.NormalizePhone(phone, _config["WhatsApp:DefaultCountryCode"] ?? "971");

    private static IActionResult Problem400(string message) => new BadRequestObjectResult(new { message });
    private static IActionResult TooMany() => new ObjectResult(new { message = "Too many attempts. Please wait a while and try again." }) { StatusCode = 429 };

    // ------------------------------------------------------------------ options

    [HttpGet("api/public/subscribers/options")]
    public IActionResult Options() => Ok(new
    {
        whatsappCode = _messenger.WhatsAppReady,
        emailReady = _messenger.EmailReady,
        consentText = SubscriberStore.ConsentWording,
        codeMinutes = SubscriberStore.CodeMinutes,
    });

    // ------------------------------------------------------------------ sign-up

    [HttpPost("api/public/subscribers/signup")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(PublicWebFormsController.RateLimitPolicy)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest req, CancellationToken ct)
    {
        var name = SubscriberStore.CleanName(req.Name);
        var email = SubscriberStore.CleanEmail(req.Email);
        var whatsapp = Digits(req.WhatsApp);
        var language = SubscriberStore.CleanLanguage(req.Language);
        if (name.Length < 2) return Problem400("Please enter your name.");
        if (email == null) return Problem400("Please enter a valid email address.");
        if (whatsapp == null) return Problem400("Please enter a valid WhatsApp number with country code, e.g. +971 50 123 4567.");
        if (!req.Consent) return Problem400("Please tick the box to agree to receive updates.");

        var needWa = _messenger.WhatsAppReady;
        // honeypot filled = a bot: answer like a real sign-up, send nothing
        if (!string.IsNullOrWhiteSpace(req.Website))
            return Ok(new { ticket = SubscriberStore.NewToken(), email = SubscriberStore.MaskEmail(email), whatsapp = SubscriberStore.MaskPhone(whatsapp), needsWhatsAppCode = needWa });
        if (!_messenger.EmailReady) return StatusCode(503, new { message = "Sign-up is not available right now. Please contact us on WhatsApp." });
        if (!_store.AllowIp(Ip)) return TooMany();

        var person = await _store.EnsureAsync(email, name, whatsapp, language, req.Source, ct);
        if (await _store.ChallengesLastHourAsync(person.SubscriberId, ct) >= SubscriberStore.MaxChallengesPerPersonPerHour) return TooMany();

        var challenge = await _store.CreateChallengeAsync(person.SubscriberId, "signup", emailCode: true, whatsappCode: needWa,
            name, whatsapp, language, Ip, ct);
        var sent = await _messenger.SendCodesAsync(name, email, whatsapp, language, challenge.EmailCode, challenge.WhatsAppCode, ct);
        if (!sent.Email) return StatusCode(502, new { message = "We could not send the email code. Please check the address and try again." });
        if (needWa && !sent.WhatsApp) return StatusCode(502, new { message = "We could not send the WhatsApp code. Please check the number (with country code) and try again." });

        return Ok(new { ticket = challenge.Ticket, email = SubscriberStore.MaskEmail(email), whatsapp = SubscriberStore.MaskPhone(whatsapp), needsWhatsAppCode = needWa });
    }

    [HttpPost("api/public/subscribers/verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest req, CancellationToken ct)
    {
        var c = await _store.GetChallengeAsync(req.Ticket ?? string.Empty, ct);
        if (c == null || c.Purpose != "signup") return Problem400("This sign-up has expired. Please start again.");
        var result = await _store.CheckCodesAsync(c, req.EmailCode, req.WhatsAppCode, ct);
        switch (result)
        {
            case SubscriberStore.CheckResult.Expired: return Problem400("The codes have expired. Tap \"Send new codes\".");
            case SubscriberStore.CheckResult.TooManyTries: return Problem400("Too many wrong tries. Tap \"Send new codes\".");
            case SubscriberStore.CheckResult.Wrong:
                return Problem400(c.WhatsAppCodeHash != null ? "One of the codes is not correct. Please check both and try again." : "The code is not correct. Please try again.");
        }
        var isNew = await _store.ActivateAsync(c, Ip, ct);
        var session = await _store.CreateSessionAsync(c.SubscriberId, ct);
        var person = await _store.GetAsync(c.SubscriberId, ct);
        if (isNew && person != null)
        {
            // welcome message in the background - the visitor does not wait for it
            var latest = await _store.LatestPostsAsync(3, ct);
            _ = Task.Run(async () =>
            {
                try { await _messenger.SendWelcomeAsync(person, latest, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Subscribers: welcome message failed"); }
            });
        }
        return Ok(new { session, profile = Profile(person!) });
    }

    [HttpPost("api/public/subscribers/resend")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(PublicWebFormsController.RateLimitPolicy)]
    public async Task<IActionResult> Resend([FromBody] TicketRequest req, CancellationToken ct)
    {
        var c = await _store.GetChallengeAsync(req.Ticket ?? string.Empty, ct);
        if (c == null || c.CompletedAt != null) return Problem400("Please start again.");
        if (!_store.AllowIp(Ip)) return TooMany();
        if (await _store.ChallengesLastHourAsync(c.SubscriberId, ct) >= SubscriberStore.MaxChallengesPerPersonPerHour) return TooMany();
        var person = await _store.GetAsync(c.SubscriberId, ct);
        if (person == null) return Problem400("Please start again.");
        var waTo = c.PendingWhatsApp ?? person.WhatsApp;
        var fresh = await _store.CreateChallengeAsync(c.SubscriberId, c.Purpose,
            emailCode: c.EmailCodeHash != null, whatsappCode: c.WhatsAppCodeHash != null && _messenger.WhatsAppReady,
            c.PendingName, c.PendingWhatsApp, c.PendingLanguage, Ip, ct);
        await _messenger.SendCodesAsync(c.PendingName ?? person.Name, person.Email, waTo, c.PendingLanguage ?? person.Language,
            fresh.EmailCode, fresh.WhatsAppCode, ct);
        return Ok(new { ticket = fresh.Ticket });
    }

    // ------------------------------------------------------------------ login

    [HttpPost("api/public/subscribers/login")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(PublicWebFormsController.RateLimitPolicy)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var id = (req.Identifier ?? string.Empty).Trim();
        var email = SubscriberStore.CleanEmail(id);
        var phone = email == null ? Digits(id) : null;
        if (email == null && phone == null) return Problem400("Enter the email address or WhatsApp number you signed up with.");
        var channel = email != null ? "email" : "whatsapp";
        var to = email != null ? SubscriberStore.MaskEmail(email) : SubscriberStore.MaskPhone(phone);
        var fakeTicket = new { ticket = SubscriberStore.NewToken(), channel, to };
        if (!string.IsNullOrWhiteSpace(req.Website)) return Ok(fakeTicket);
        if (channel == "whatsapp" && !_messenger.WhatsAppReady)
            return Problem400("Please log in with your email address.");
        if (!_store.AllowIp(Ip)) return TooMany();

        var person = await _store.FindAsync(email, phone, ct);
        // not registered (or never confirmed): the same answer, nothing sent - nobody can test which addresses are signed up
        if (person == null || person.EmailVerifiedAt == null) return Ok(fakeTicket);
        if (await _store.ChallengesLastHourAsync(person.SubscriberId, ct) >= SubscriberStore.MaxChallengesPerPersonPerHour) return TooMany();

        var c = await _store.CreateChallengeAsync(person.SubscriberId, "login", emailCode: channel == "email", whatsappCode: channel == "whatsapp",
            null, null, null, Ip, ct);
        await _messenger.SendCodesAsync(person.Name, person.Email, person.WhatsApp, person.Language, c.EmailCode, c.WhatsAppCode, ct);
        return Ok(new { ticket = c.Ticket, channel, to });
    }

    [HttpPost("api/public/subscribers/login/verify")]
    public async Task<IActionResult> LoginVerify([FromBody] LoginVerifyRequest req, CancellationToken ct)
    {
        var c = await _store.GetChallengeAsync(req.Ticket ?? string.Empty, ct);
        if (c == null || (c.Purpose != "login" && c.Purpose != "whatsapp")) return Problem400("The code is not correct or has expired. Please try again.");
        var code = (req.Code ?? string.Empty).Trim();
        var result = await _store.CheckCodesAsync(c, c.EmailCodeHash != null ? code : null, c.WhatsAppCodeHash != null ? code : null, ct);
        if (result != SubscriberStore.CheckResult.Ok)
            return Problem400(result == SubscriberStore.CheckResult.Wrong ? "The code is not correct. Please try again." : "The code has expired. Please ask for a new one.");
        if (c.Purpose == "whatsapp")
        {
            await _store.MarkWhatsAppVerifiedAsync(c, ct);
            var p = await _store.GetAsync(c.SubscriberId, ct);
            return Ok(new { profile = Profile(p!) });
        }
        var session = await _store.CreateSessionAsync(c.SubscriberId, ct);
        var person = await _store.GetAsync(c.SubscriberId, ct);
        return Ok(new { session, profile = Profile(person!) });
    }

    // ------------------------------------------------------------------ my account

    private async Task<SubscriberStore.Subscriber?> MeAsync(CancellationToken ct) =>
        await _store.FromSessionAsync(Request.Headers[SessionHeader].ToString().Trim(), ct);

    private object Profile(SubscriberStore.Subscriber s) => new
    {
        s.Name, s.Email, whatsapp = s.WhatsApp == null ? null : "+" + s.WhatsApp, s.Language, s.Status,
        emailVerified = s.EmailVerifiedAt != null, whatsappVerified = s.WhatsAppVerifiedAt != null,
        s.EmailOptIn, s.WhatsAppOptIn, memberSince = s.ActivatedAt ?? s.CreatedAt,
        whatsappAvailable = _messenger.WhatsAppReady,
    };

    [HttpGet("api/public/subscribers/me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var me = await MeAsync(ct);
        return me == null ? Unauthorized(new { message = "Please log in." }) : Ok(Profile(me));
    }

    [HttpPost("api/public/subscribers/me")]
    public async Task<IActionResult> SaveMe([FromBody] PreferencesRequest req, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (me == null) return Unauthorized(new { message = "Please log in." });
        await _store.UpdatePreferencesAsync(me.SubscriberId, SubscriberStore.CleanName(req.Name), SubscriberStore.CleanLanguage(req.Language),
            req.EmailOptIn, req.WhatsAppOptIn, ct);
        return Ok(Profile((await _store.GetAsync(me.SubscriberId, ct))!));
    }

    [HttpPost("api/public/subscribers/me/whatsapp")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(PublicWebFormsController.RateLimitPolicy)]
    public async Task<IActionResult> VerifyWhatsApp([FromBody] WhatsAppRequest req, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (me == null) return Unauthorized(new { message = "Please log in." });
        if (!_messenger.WhatsAppReady) return Problem400("WhatsApp is not available yet.");
        var phone = Digits(req.WhatsApp) ?? me.WhatsApp;
        if (phone == null) return Problem400("Please enter a valid WhatsApp number with country code.");
        if (!_store.AllowIp(Ip) || await _store.ChallengesLastHourAsync(me.SubscriberId, ct) >= SubscriberStore.MaxChallengesPerPersonPerHour) return TooMany();
        var c = await _store.CreateChallengeAsync(me.SubscriberId, "whatsapp", emailCode: false, whatsappCode: true, null, phone, null, Ip, ct);
        var sent = await _messenger.SendCodesAsync(me.Name, me.Email, phone, me.Language, null, c.WhatsAppCode, ct);
        if (!sent.WhatsApp) return StatusCode(502, new { message = "We could not send the WhatsApp code. Please check the number and try again." });
        return Ok(new { ticket = c.Ticket, to = SubscriberStore.MaskPhone(phone) });
    }

    [HttpPost("api/public/subscribers/me/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Headers[SessionHeader].ToString().Trim();
        if (token.Length > 0) await _store.EndSessionAsync(token, ct);
        return NoContent();
    }

    [HttpPost("api/public/subscribers/me/delete")]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (me == null) return Unauthorized(new { message = "Please log in." });
        await _store.DeleteAsync(me.SubscriberId, ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ unsubscribe

    [HttpPost("api/public/subscribers/unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeRequest req, CancellationToken ct)
    {
        var person = await _store.FindByUnsubscribeTokenAsync(req.Token ?? string.Empty, ct);
        if (person == null) return Problem400("This unsubscribe link is not valid.");
        var channel = req.Channel is "email" or "whatsapp" ? req.Channel : "all";
        await _store.UnsubscribeAsync(person.SubscriberId, channel, ct);
        return Ok(new { channel, email = SubscriberStore.MaskEmail(person.Email) });
    }

    /// <summary>Gmail / Outlook "Unsubscribe" button (List-Unsubscribe-Post). No CORS, no body needed.</summary>
    [HttpPost("api/public/subscribers/unsubscribe/one-click")]
    [DisableCors]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick([FromQuery] string? t, [FromQuery] string? c, CancellationToken ct)
    {
        var person = await _store.FindByUnsubscribeTokenAsync(t ?? string.Empty, ct);
        if (person != null) await _store.UnsubscribeAsync(person.SubscriberId, c == "whatsapp" ? "whatsapp" : "email", ct);
        return Ok();
    }

    // ------------------------------------------------------------------ WhatsApp webhook (STOP)

    [HttpGet("api/public/whatsapp/webhook")]
    [DisableCors]
    public IActionResult WebhookVerify([FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = _config["Newsletter:WhatsApp:WebhookVerifyToken"];
        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && token == expected) return Content(challenge ?? string.Empty, "text/plain");
        return Forbid();
    }

    [HttpPost("api/public/whatsapp/webhook")]
    [DisableCors]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        // Meta signs every call with the app secret (X-Hub-Signature-256) - checked when Newsletter:WhatsApp:AppSecret is set
        var secret = _config["Newsletter:WhatsApp:AppSecret"];
        if (!string.IsNullOrEmpty(secret))
        {
            var given = Request.Headers["X-Hub-Signature-256"].ToString();
            var mine = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(mine))) return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var entry in Arr(doc.RootElement, "entry"))
            foreach (var change in Arr(entry, "changes"))
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                foreach (var msg in Arr(value, "messages"))
                {
                    var from = msg.TryGetProperty("from", out var f) ? f.GetString() : null;
                    string? text = null;
                    if (msg.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b)) text = b.GetString();
                    else if (msg.TryGetProperty("button", out var btn)) text = btn.TryGetProperty("payload", out var pl) ? pl.GetString() : btn.TryGetProperty("text", out var bt) ? bt.GetString() : null;
                    else if (msg.TryGetProperty("interactive", out var it) && it.TryGetProperty("button_reply", out var br) && br.TryGetProperty("title", out var tt)) text = tt.GetString();
                    if (from == null || !IsStop(text)) continue;
                    var digits = new string(from.Where(char.IsDigit).ToArray());
                    var n = await _store.StopWhatsAppAsync(digits, ct);
                    _logger.LogInformation("WhatsApp STOP from ****{Last4}: {Count} subscriber(s) switched off", digits.Length > 4 ? digits[^4..] : digits, n);
                }
            }
        }
        catch (JsonException) { /* not ours - ignore */ }
        return Ok();
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static bool IsStop(string? text)
    {
        var t = (text ?? string.Empty).Trim().Trim('.', '!').ToUpperInvariant();
        return t is "STOP" or "UNSUBSCRIBE" or "STOP UPDATES" or "إيقاف" or "الغاء" or "إلغاء";
    }
}
