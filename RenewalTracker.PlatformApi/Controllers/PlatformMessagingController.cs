using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Settings ▸ Email &amp; WhatsApp - the mailbox every email goes out from (Admin Panel and Business Panel) and the
/// WhatsApp account reminders are sent through, with instant tests, plus the daily automatic reminder run.
///   GET  api/platform/messaging                 settings (passwords / keys are never sent back - only "is set")
///   PUT  api/platform/messaging                 save (a secret left empty keeps the stored one; clearSmtpPassword / clearWhatsAppApiKey remove it)
///   POST api/platform/messaging/test-email      { to, settings? }  sends a test email now (with the values on screen, saved or not)
///   POST api/platform/messaging/test-whatsapp   { to, message?, settings? }  sends a test WhatsApp message now
/// Stored by PlatformMessagingStore; the Business Panel API reads it every minute.
/// </summary>
[ApiController]
[Route("api/platform/messaging")]
[RequirePlatformModule("MESSAGING_SETTINGS", "PLATFORM_BRANDING")]
public class PlatformMessagingController : ControllerBase
{
    private readonly PlatformMessagingStore _store;
    private readonly ICurrentPlatformUserService _user;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformMessagingController> _logger;

    public PlatformMessagingController(PlatformMessagingStore store, ICurrentPlatformUserService user, IConfiguration config, ILogger<PlatformMessagingController> logger)
    {
        _store = store;
        _user = user;
        _config = config;
        _logger = logger;
    }

    public sealed class MessagingSettingsDto
    {
        public bool EmailEnabled { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpEnableSsl { get; set; } = true;
        public string? SmtpUserName { get; set; }
        /// <summary>New password; empty = keep the stored one.</summary>
        public string? SmtpPassword { get; set; }
        public bool ClearSmtpPassword { get; set; }
        public bool HasSmtpPassword { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }

        public bool WhatsAppEnabled { get; set; }
        /// <summary>meta, twilio, gupshup, interakt or custom.</summary>
        public string WhatsAppProvider { get; set; } = "meta";
        public string? WhatsAppApiUrl { get; set; }
        public string? WhatsAppAccountId { get; set; }
        /// <summary>New API key / token; empty = keep the stored one.</summary>
        public string? WhatsAppApiKey { get; set; }
        public bool ClearWhatsAppApiKey { get; set; }
        public bool HasWhatsAppApiKey { get; set; }
        public string? WhatsAppSender { get; set; }
        public string? WhatsAppTemplate { get; set; }
        public string? WhatsAppTemplateLanguage { get; set; } = "en";
        public string? DefaultCountryCode { get; set; } = "971";

        public bool RemindersEnabled { get; set; }
        public int RemindersRunHour { get; set; } = 8;

        /// <summary>24/7 support button at the bottom right of the Business Panel.</summary>
        public bool SupportEnabled { get; set; }
        public string? SupportWhatsApp { get; set; }
        public string? SupportEmail { get; set; }
        public string? SupportHours { get; set; } = "24/7";
        public string? SupportGreeting { get; set; }

        // read-only information for the screen
        public bool Saved { get; set; }
        public bool CanEncrypt { get; set; }
        public bool SecretUnreadable { get; set; }
        /// <summary>The "Smtp" section of appsettings.json - used while no email is set up here.</summary>
        public string? AppSettingsMailbox { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }

    public sealed class TestRequest
    {
        public string To { get; set; } = string.Empty;
        public string? Message { get; set; }
        /// <summary>The values on screen (not saved yet); null = the saved settings.</summary>
        public MessagingSettingsDto? Settings { get; set; }
    }

    private string By => string.IsNullOrWhiteSpace(_user.Email) ? $"user#{_user.PlatformUserId}" : _user.Email;

    [HttpGet]
    public async Task<ActionResult<MessagingSettingsDto>> Get(CancellationToken ct)
    {
        var s = await _store.GetAsync(ct);
        var app = SmtpMail.ReadAppSettings(_config);
        return Ok(new MessagingSettingsDto
        {
            EmailEnabled = s.EmailEnabled, SmtpHost = s.SmtpHost, SmtpPort = s.SmtpPort, SmtpEnableSsl = s.SmtpEnableSsl,
            SmtpUserName = s.SmtpUserName, HasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPassword), FromEmail = s.FromEmail, FromName = s.FromName,
            WhatsAppEnabled = s.WhatsAppEnabled, WhatsAppProvider = s.WhatsAppProvider, WhatsAppApiUrl = s.WhatsAppApiUrl,
            WhatsAppAccountId = s.WhatsAppAccountId, HasWhatsAppApiKey = !string.IsNullOrEmpty(s.WhatsAppApiKey), WhatsAppSender = s.WhatsAppSender,
            WhatsAppTemplate = s.WhatsAppTemplate, WhatsAppTemplateLanguage = s.WhatsAppTemplateLanguage ?? "en",
            DefaultCountryCode = s.DefaultCountryCode ?? "971",
            RemindersEnabled = s.RemindersEnabled, RemindersRunHour = s.RemindersRunHour,
            SupportEnabled = s.SupportEnabled, SupportWhatsApp = s.SupportWhatsApp, SupportEmail = s.SupportEmail,
            SupportHours = s.SupportHours ?? "24/7", SupportGreeting = s.SupportGreeting,
            Saved = s.Saved, CanEncrypt = _store.CanEncrypt, SecretUnreadable = s.SecretUnreadable,
            AppSettingsMailbox = app.IsConfigured ? $"{app.FromEmail} via {app.Host}:{app.Port}" : null,
            ModifiedDate = s.ModifiedDate, ModifiedBy = s.ModifiedBy,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save(MessagingSettingsDto dto, CancellationToken ct)
    {
        var stored = await _store.GetAsync(ct);
        var s = Merge(dto, stored);
        var error = Validate(s);
        if (error != null) return BadRequest(new { message = error });
        await _store.SaveAsync(s, By, ct);
        _logger.LogInformation("Email & WhatsApp settings saved by {By} (email {Email}, WhatsApp {WhatsApp}/{Provider}, reminders {Reminders} at {Hour}:00)",
            By, s.EmailEnabled, s.WhatsAppEnabled, s.WhatsAppProvider, s.RemindersEnabled, s.RemindersRunHour);
        return Ok(new
        {
            message = _store.CanEncrypt ? "Settings saved. The Business Panel uses them within a minute."
                : "Settings saved - but TenantDatabase:CredentialKey is not set on this server, so the password / API key is stored unencrypted. Set the key and save again.",
        });
    }

    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail(TestRequest request, CancellationToken ct)
    {
        var to = (request.To ?? string.Empty).Trim();
        if (!new EmailAddressAttribute().IsValid(to)) return BadRequest(new { message = "Enter the e-mail address to send the test to." });
        var stored = await _store.GetAsync(ct);
        var s = request.Settings is null ? stored : Merge(request.Settings, stored);
        s.EmailEnabled = true;   // test what is on screen even before it is switched on
        var smtp = s.ToSmtp();
        if (smtp is null || !smtp.IsConfigured) return BadRequest(new { message = "Fill in the server, user name, password and sender first." });
        try
        {
            var html = $"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#17385f\"><p>This is a test email from the Bliss Point Admin Panel.</p>" +
                       $"<p>Server <b>{WebUtility.HtmlEncode(smtp.Host)}:{smtp.Port}</b>, sender <b>{WebUtility.HtmlEncode(smtp.FromEmail)}</b>.</p>" +
                       $"<p>If you can read this, quotations, invoices, receipts and reminders can be emailed from this mailbox.</p>" +
                       $"<p style=\"color:#6f8098;font-size:12px\">Sent {DateTime.Now:dd MMM yyyy HH:mm} by {WebUtility.HtmlEncode(By)}</p></div>";
            await SmtpMail.SendHtmlAsync(smtp, new[] { to }, "Test email - Bliss Point Admin Panel", html, null, ct);
            return Ok(new { message = $"Test email sent to {to}. Check the inbox (and the spam folder)." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Test email to {To} failed", to);
            return BadRequest(new { message = $"The email could not be sent: {Root(ex)}" });
        }
    }

    [HttpPost("test-whatsapp")]
    public async Task<IActionResult> TestWhatsApp(TestRequest request, CancellationToken ct)
    {
        var to = (request.To ?? string.Empty).Trim();
        if (to.Count(char.IsDigit) < 8) return BadRequest(new { message = "Enter the mobile number to send the test to." });
        var stored = await _store.GetAsync(ct);
        var s = request.Settings is null ? stored : Merge(request.Settings, stored);
        var wa = s.ToWhatsApp();
        wa.Enabled = true;   // test what is on screen even before it is switched on
        if (!wa.IsConfigured) return BadRequest(new { message = MissingWhatsApp(wa) });
        var text = string.IsNullOrWhiteSpace(request.Message)
            ? $"Test message from the Bliss Point Admin Panel ({DateTime.Now:dd MMM yyyy HH:mm}). WhatsApp reminders are working."
            : request.Message.Trim();
        try
        {
            var id = await WhatsAppSender.SendAsync(wa, to, text, ct);
            return Ok(new { message = $"Test message sent to {WhatsAppSender.NormalizeMobile(to, wa.DefaultCountryCode)} ({wa.Provider}, id {id})." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Test WhatsApp to {To} failed", to);
            return BadRequest(new { message = Root(ex) });
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The values on screen over the stored ones; an empty secret keeps the stored secret.</summary>
    private static PlatformMessagingStore.Settings Merge(MessagingSettingsDto d, PlatformMessagingStore.Settings stored) => new()
    {
        EmailEnabled = d.EmailEnabled,
        SmtpHost = d.SmtpHost, SmtpPort = d.SmtpPort, SmtpEnableSsl = d.SmtpEnableSsl, SmtpUserName = d.SmtpUserName,
        SmtpPassword = d.ClearSmtpPassword ? null : string.IsNullOrEmpty(d.SmtpPassword) ? stored.SmtpPassword : d.SmtpPassword,
        FromEmail = d.FromEmail, FromName = d.FromName,
        WhatsAppEnabled = d.WhatsAppEnabled, WhatsAppProvider = string.IsNullOrWhiteSpace(d.WhatsAppProvider) ? "meta" : d.WhatsAppProvider.Trim().ToLowerInvariant(),
        WhatsAppApiUrl = d.WhatsAppApiUrl, WhatsAppAccountId = d.WhatsAppAccountId,
        WhatsAppApiKey = d.ClearWhatsAppApiKey ? null : string.IsNullOrEmpty(d.WhatsAppApiKey) ? stored.WhatsAppApiKey : d.WhatsAppApiKey,
        WhatsAppSender = d.WhatsAppSender, WhatsAppTemplate = d.WhatsAppTemplate, WhatsAppTemplateLanguage = d.WhatsAppTemplateLanguage,
        DefaultCountryCode = d.DefaultCountryCode,
        RemindersEnabled = d.RemindersEnabled, RemindersRunHour = d.RemindersRunHour,
        SupportEnabled = d.SupportEnabled, SupportWhatsApp = d.SupportWhatsApp, SupportEmail = d.SupportEmail,
        SupportHours = d.SupportHours, SupportGreeting = d.SupportGreeting,
    };

    private static string? Validate(PlatformMessagingStore.Settings s)
    {
        var mail = new EmailAddressAttribute();
        if (s.EmailEnabled)
        {
            if (string.IsNullOrWhiteSpace(s.SmtpHost)) return "Email: enter the SMTP server.";
            if (s.SmtpPort is <= 0 or > 65535) return "Email: the port must be between 1 and 65535 (usually 587).";
            if (string.IsNullOrWhiteSpace(s.SmtpUserName)) return "Email: enter the user name (the mailbox address).";
            if (string.IsNullOrEmpty(s.SmtpPassword)) return "Email: enter the password.";
            if (!string.IsNullOrWhiteSpace(s.FromEmail) && !mail.IsValid(s.FromEmail.Trim())) return "Email: the sender address is not valid.";
        }
        if (!string.IsNullOrWhiteSpace(s.WhatsAppApiUrl) && (!Uri.TryCreate(s.WhatsAppApiUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
            return "WhatsApp: the API URL must be a full https:// address.";
        if (s.WhatsAppEnabled)
        {
            var wa = s.ToWhatsApp();
            if (!wa.IsConfigured) return MissingWhatsApp(wa);
        }
        if (s.RemindersRunHour is < 0 or > 23) return "Choose the hour the reminders run (0-23).";
        if (!string.IsNullOrWhiteSpace(s.SupportEmail) && !mail.IsValid(s.SupportEmail.Trim())) return "Support: the support e-mail address is not valid.";
        if (!string.IsNullOrWhiteSpace(s.SupportWhatsApp) && s.SupportWhatsApp.Count(char.IsDigit) is < 8 or > 15) return "Support: enter the WhatsApp number with country code, e.g. +971 50 000 0000.";
        if (s.SupportEnabled && string.IsNullOrWhiteSpace(s.SupportWhatsApp) && string.IsNullOrWhiteSpace(s.SupportEmail))
            return "Support: enter a WhatsApp number or an e-mail address for the support button.";
        return null;
    }

    private static string MissingWhatsApp(WhatsAppConfig wa) => WhatsAppSender.Normalize(wa.Provider) switch
    {
        "meta" => "WhatsApp (Meta Cloud API): enter the Phone number ID and the access token.",
        "twilio" => "WhatsApp (Twilio): enter the Account SID, the auth token and the WhatsApp sender number.",
        "gupshup" => "WhatsApp (Gupshup): enter the app name, the API key and the source number.",
        "interakt" => "WhatsApp (Interakt): enter the API key and the approved template name.",
        "custom" => "WhatsApp (custom): enter the https:// endpoint URL.",
        _ => "WhatsApp: choose a provider.",
    };

    private static string Root(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}

/// <summary>
/// Control Center ▸ Reminders - one tenant's automatic reminders by email / WhatsApp (dbo.TenantMessagingSettings).
///   GET api/platform/tenants/{tenantId}/messaging   { autoEmail, autoWhatsApp, emailReady, whatsAppReady, remindersEnabled, runAtHour }
///   PUT api/platform/tenants/{tenantId}/messaging   { autoEmail, autoWhatsApp }
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:int}/messaging")]
[RequirePlatformModule("TENANTS_LIST", "TENANT_MODULES")]
public class PlatformTenantMessagingController : ControllerBase
{
    private readonly PlatformMessagingStore _store;
    private readonly PlatformDbContext _db;
    private readonly ICurrentPlatformUserService _user;
    private readonly IConfiguration _config;

    public PlatformTenantMessagingController(PlatformMessagingStore store, PlatformDbContext db, ICurrentPlatformUserService user, IConfiguration config)
    {
        _store = store;
        _db = db;
        _user = user;
        _config = config;
    }

    public sealed class TenantMessagingDto
    {
        public bool AutoEmail { get; set; }
        public bool AutoWhatsApp { get; set; }
        public bool EmailReady { get; set; }
        public bool WhatsAppReady { get; set; }
        public bool RemindersEnabled { get; set; }
        public int RunAtHour { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<TenantMessagingDto>> Get(int tenantId, CancellationToken ct)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return NotFound(new { message = "Tenant not found." });
        var t = await _store.GetTenantAsync(tenantId, ct);
        var s = await _store.GetAsync(ct);
        return Ok(new TenantMessagingDto
        {
            AutoEmail = t.AutoEmail, AutoWhatsApp = t.AutoWhatsApp,
            EmailReady = s.ToSmtp()?.IsConfigured == true || SmtpMail.ReadAppSettings(_config).IsConfigured,
            WhatsAppReady = s.ToWhatsApp().IsConfigured,
            RemindersEnabled = s.RemindersEnabled, RunAtHour = s.RemindersRunHour,
            ModifiedDate = t.ModifiedDate, ModifiedBy = t.ModifiedBy,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save(int tenantId, TenantMessagingDto dto, CancellationToken ct)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return NotFound(new { message = "Tenant not found." });
        var by = string.IsNullOrWhiteSpace(_user.Email) ? $"user#{_user.PlatformUserId}" : _user.Email;
        await _store.SaveTenantAsync(tenantId, dto.AutoEmail, dto.AutoWhatsApp, by, ct);
        return Ok(new { message = "Saved. The Business Panel follows it within a minute." });
    }
}
