using System.Net;
using System.Net.Mail;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Emails and WhatsApp messages for website subscribers: sign-up / login codes, the welcome message and the
/// automatic "new posts" message. English or Arabic, as the person chose.
///
/// appsettings "Newsletter" section (all optional):
///   Enabled          false = no automatic "new posts" messages (sign-up still works)
///   FromName         sender name, default "Bliss Point Group Updates"
///   ReplyTo          replies go here, default Website:NotifyEmail
///   SendFromHour / SendToHour   UAE hours the automatic message may go out (default 9 and 20)
///   Smtp             { Host, Port, EnableSsl, UserName, Password, FromEmail } - any SMTP service (Brevo, Amazon SES,
///                    Mailgun, SendGrid, Office 365 ...). Empty Host = the API's normal mailbox (Settings > Email & WhatsApp / "Smtp").
///   WhatsApp         { Enabled, OtpTemplate, NewPostsTemplate, WelcomeTemplate, ArabicSuffix, WebhookVerifyToken, AppSecret }
///                    uses the account of the "WhatsApp" section (PhoneNumberId, AccessToken). WhatsApp is used only when
///                    that account is set up AND OtpTemplate is set - until then sign-up needs the email code only.
///   Templates (create them in WhatsApp Manager; the Arabic copy has the same name + ArabicSuffix, default "_ar"):
///     OtpTemplate       AUTHENTICATION template with a Copy code button.            body {{1}} = code
///     NewPostsTemplate  MARKETING template, buttons "Chat with us" (URL) + "Call us" (phone) + quick reply "STOP".
///                       body {{1}} = name, {{2}} = post title(s), {{3}} = link
///     WelcomeTemplate   same buttons. body {{1}} = name, {{2}} = link to the blog
/// </summary>
public sealed class SubscriberMessenger
{
    private readonly IConfiguration _config;
    private readonly WhatsAppCloud _whatsApp;
    private readonly ILogger<SubscriberMessenger> _logger;

    public SubscriberMessenger(IConfiguration config, WhatsAppCloud whatsApp, ILogger<SubscriberMessenger> logger)
    {
        _config = config;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    // ------------------------------------------------------------------ settings

    private IConfigurationSection N => _config.GetSection("Newsletter");
    public bool AutoSendEnabled => !bool.TryParse(N["Enabled"], out var on) || on;
    public int SendFromHour => Math.Clamp(N.GetValue("SendFromHour", 9), 0, 23);
    public int SendToHour => Math.Clamp(N.GetValue("SendToHour", 20), 1, 24);
    public string WebsiteUrl => (_config["Website:PublicUrl"] ?? "https://blisspointworld.com").TrimEnd('/');
    public string ApiUrl => (_config["Website:ApiPublicUrl"] ?? "https://adminpanelapi.blisspointworld.com").TrimEnd('/');
    private string Phone => _config["Website:ContactPhone"] ?? "+971 4 454 0091";
    private string WhatsAppNumber => new string((_config["Website:WhatsAppNumber"] ?? "971589970732").Where(char.IsDigit).ToArray());
    private string? ReplyTo => N["ReplyTo"] is { Length: > 0 } r ? r : _config["Website:NotifyEmail"];

    public SmtpMail.Settings Smtp()
    {
        var s = N.GetSection("Smtp");
        if (string.IsNullOrWhiteSpace(s["Host"]))
        {
            var main = SmtpMail.Read(_config);
            return main with { FromName = N["FromName"] is { Length: > 0 } f ? f : "Bliss Point Group Updates" };
        }
        var user = (s["UserName"] ?? string.Empty).Trim();
        var port = int.TryParse(s["Port"], out var p) ? p : 587;
        if (port == 465) port = 587;
        return new SmtpMail.Settings(
            Host: s["Host"]!.Trim(), Port: port,
            EnableSsl: !bool.TryParse(s["EnableSsl"], out var ssl) || ssl,
            UserName: user, Password: (s["Password"] ?? string.Empty).Trim(),
            FromEmail: s["FromEmail"] is { Length: > 0 } fe ? fe.Trim() : user,
            FromName: N["FromName"] is { Length: > 0 } fn ? fn : "Bliss Point Group Updates");
    }

    public bool EmailReady => Smtp().IsConfigured;

    private (WhatsAppCloud.Settings Account, IConfigurationSection Cfg) Wa() => (_whatsApp.Read(), N.GetSection("WhatsApp"));

    /// <summary>WhatsApp codes and messages are used only when the account and the code template are set.</summary>
    public bool WhatsAppReady
    {
        get
        {
            var (account, cfg) = Wa();
            var on = !bool.TryParse(cfg["Enabled"], out var e) || e;
            return on && account.IsConfigured && !string.IsNullOrWhiteSpace(cfg["OtpTemplate"]);
        }
    }

    private WhatsAppCloud.Template Template(string key, string language)
    {
        var name = (N.GetSection("WhatsApp")[key] ?? string.Empty).Trim();
        if (name.Length == 0) return new WhatsAppCloud.Template(string.Empty, "en");
        if (language == "ar")
        {
            var suffix = N.GetSection("WhatsApp")["ArabicSuffix"] ?? "_ar";
            return new WhatsAppCloud.Template(name + suffix, "ar");
        }
        return new WhatsAppCloud.Template(name, "en");
    }

    public string BlogLink(string slug) => $"{WebsiteUrl}/blog/post.html?slug={Uri.EscapeDataString(slug)}";
    public string ImageLink(string? file) => string.IsNullOrWhiteSpace(file) ? string.Empty : $"{ApiUrl}/api/public/blog/images/{Uri.EscapeDataString(file)}";

    // ------------------------------------------------------------------ codes

    /// <summary>Sends the codes of one sign-up / login. Returns which channels got their code.</summary>
    public async Task<(bool Email, bool WhatsApp)> SendCodesAsync(string name, string email, string? whatsapp, string language,
        string? emailCode, string? whatsappCode, CancellationToken ct)
    {
        var emailOk = false;
        var waOk = false;
        if (emailCode != null)
        {
            var ar = language == "ar";
            var subject = ar ? $"رمز التحقق: {emailCode}" : $"Your verification code: {emailCode}";
            var body = Layout(language, ar ? "رمز التحقق" : "Your verification code",
                $@"<p style=""margin:0 0 14px"">{(ar ? "مرحباً" : "Hello")} {Enc(FirstName(name))},</p>
<p style=""margin:0 0 18px"">{(ar ? "استخدم هذا الرمز لإكمال التسجيل أو تسجيل الدخول على موقع بليس بوينت:" : "Use this code to finish signing up or logging in on the Bliss Point website:")}</p>
<div style=""font-size:34px;font-weight:800;letter-spacing:10px;color:#0b3d52;background:#f1f7f9;border-radius:12px;padding:16px 0;text-align:center"">{emailCode}</div>
<p style=""margin:18px 0 0;color:#5a6b7c;font-size:13px"">{(ar ? $"ينتهي الرمز خلال {SubscriberStore.CodeMinutes} دقائق. إذا لم تطلبه، تجاهل هذه الرسالة." : $"The code expires in {SubscriberStore.CodeMinutes} minutes. If you did not ask for it, ignore this email.")}</p>",
                footer: null);
            emailOk = (await SendEmailAsync(email, subject, body, headers: null, ct)).Ok;
        }
        if (whatsappCode != null && whatsapp != null && WhatsAppReady)
        {
            var (account, _) = Wa();
            waOk = await _whatsApp.SendAuthCodeAsync(account, whatsapp, Template("OtpTemplate", language), whatsappCode, ct);
        }
        return (emailOk, waOk);
    }

    // ------------------------------------------------------------------ welcome + new posts

    public async Task SendWelcomeAsync(SubscriberStore.Subscriber s, IReadOnlyList<SubscriberStore.Post> latest, CancellationToken ct)
    {
        var ar = s.Language == "ar";
        if (s.EmailOptIn && s.EmailVerifiedAt != null)
        {
            var intro = ar
                ? "<p style=\"margin:0 0 16px\">شكراً لتسجيلك. سنرسل لك كل مقال وخبر جديد عن الإمارات من بليس بوينت عبر البريد الإلكتروني" + (s.WhatsAppVerifiedAt != null ? " وواتساب" : "") + ".</p>"
                : "<p style=\"margin:0 0 16px\">Thanks for signing up. You will get every new article and UAE update from Bliss Point by email" + (s.WhatsAppVerifiedAt != null ? " and WhatsApp" : "") + ".</p>";
            var posts = latest.Count == 0 ? string.Empty
                : $"<p style=\"margin:8px 0 12px;font-weight:700\">{(ar ? "أحدث المقالات" : "Our latest posts")}</p>" + string.Concat(latest.Select(p => PostCard(p, ar)));
            var html = Layout(s.Language, ar ? $"أهلاً {Enc(FirstName(s.Name))}" : $"Welcome, {Enc(FirstName(s.Name))}", intro + posts, Footer(s, "email"));
            await SendEmailAsync(s.Email, ar ? "تم تسجيلك في تحديثات بليس بوينت" : "You're subscribed to Bliss Point updates", html, UnsubscribeHeaders(s), ct);
        }
        if (s.WhatsAppOptIn && s.WhatsAppVerifiedAt != null && s.WhatsApp != null && WhatsAppReady)
        {
            var (account, _) = Wa();
            var t = Template("WelcomeTemplate", s.Language);
            if (t.IsSet) await _whatsApp.SendTemplateAsync(account, s.WhatsApp, t, new[] { FirstName(s.Name), $"{WebsiteUrl}/blog.html" }, ct);
        }
    }

    /// <summary>null = that channel was not used for this person. EmailBounced = the mail server refused the address.</summary>
    public sealed record Result(bool? Email, bool? WhatsApp, bool EmailBounced);

    /// <summary>One "new posts" message to one person (email and / or WhatsApp, as they chose).</summary>
    public async Task<Result> SendPostsAsync(SubscriberStore.Subscriber s, IReadOnlyList<SubscriberStore.Post> posts, CancellationToken ct)
    {
        bool? email = null, wa = null;
        var bounced = false;
        var ar = s.Language == "ar";
        if (s.EmailOptIn && s.EmailVerifiedAt != null && EmailReady)
        {
            var title = posts.Count == 1
                ? posts[0].Title
                : (ar ? $"{posts.Count} مقالات جديدة من بليس بوينت" : $"{posts.Count} new posts from Bliss Point");
            var intro = $"<p style=\"margin:0 0 16px\">{(ar ? "مرحباً" : "Hello")} {Enc(FirstName(s.Name))}, " +
                        (ar ? "إليك الجديد على موقعنا:" : "here is what's new on our website:") + "</p>";
            var html = Layout(s.Language, ar ? "الجديد من بليس بوينت" : "New from Bliss Point", intro + string.Concat(posts.Select(p => PostCard(p, ar))), Footer(s, "email"));
            var sent = await SendEmailAsync(s.Email, title, html, UnsubscribeHeaders(s), ct);
            email = sent.Ok; bounced = sent.Bounced;
        }
        if (s.WhatsAppOptIn && s.WhatsAppVerifiedAt != null && s.WhatsApp != null && WhatsAppReady)
        {
            var (account, _) = Wa();
            var t = Template("NewPostsTemplate", s.Language);
            if (t.IsSet)
            {
                var titles = string.Join(" • ", posts.Select(p => p.Title));
                if (titles.Length > 500) titles = titles[..497] + "...";
                var link = posts.Count == 1 ? BlogLink(posts[0].Slug) : $"{WebsiteUrl}/blog.html";
                wa = await _whatsApp.SendTemplateAsync(account, s.WhatsApp, t, new[] { FirstName(s.Name), titles, link }, ct);
            }
        }
        return new Result(email, wa, bounced);
    }

    // ------------------------------------------------------------------ email building

    private async Task<(bool Ok, bool Bounced)> SendEmailAsync(string to, string subject, string html, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var smtp = Smtp();
        if (!smtp.IsConfigured)
        {
            _logger.LogWarning("Subscribers: no SMTP mailbox set (Newsletter:Smtp or Smtp) - email to {To} not sent", Mask(to));
            return (false, false);
        }
        try
        {
            await SmtpMail.SendHtmlWithHeadersAsync(smtp, to, subject, html, ReplyTo, headers ?? new Dictionary<string, string>(), ct);
            return (true, false);
        }
        catch (SmtpFailedRecipientException ex)
        {
            _logger.LogWarning(ex, "Subscribers: the mail server refused {To}", Mask(to));
            return (false, true);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
        {
            _logger.LogWarning(ex, "Subscribers: email to {To} failed", Mask(to));
            return (false, ex is FormatException);
        }
    }

    private Dictionary<string, string> UnsubscribeHeaders(SubscriberStore.Subscriber s) => new()
    {
        ["List-Unsubscribe"] = $"<{ApiUrl}/api/public/subscribers/unsubscribe/one-click?t={s.UnsubscribeToken}&c=email>",
        ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
    };

    private string Footer(SubscriberStore.Subscriber s, string channel)
    {
        var ar = s.Language == "ar";
        var unsub = $"{WebsiteUrl}/unsubscribe.html?t={s.UnsubscribeToken}&c={channel}";
        var manage = $"{WebsiteUrl}/my-account.html";
        var wa = $"https://wa.me/{WhatsAppNumber}";
        var tel = "tel:" + new string(Phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        return $@"<table role=""presentation"" cellspacing=""0"" cellpadding=""0"" style=""margin:22px auto 0""><tr>
<td style=""padding:0 6px""><a href=""{wa}"" style=""display:inline-block;background:#25D366;color:#fff;text-decoration:none;font-weight:700;padding:11px 18px;border-radius:999px;font-size:14px"">{(ar ? "تواصل عبر واتساب" : "Chat with us")}</a></td>
<td style=""padding:0 6px""><a href=""{tel}"" style=""display:inline-block;background:#0b3d52;color:#fff;text-decoration:none;font-weight:700;padding:11px 18px;border-radius:999px;font-size:14px"">{(ar ? "اتصل بنا" : "Call us")}</a></td>
</tr></table>
<p style=""margin:22px 0 0;font-size:12px;color:#5a6b7c;text-align:center"">{(ar ? "تصلك هذه الرسالة لأنك سجلت على موقع blisspointworld.com." : "You get this because you signed up on blisspointworld.com.")}<br>
<a href=""{manage}"" style=""color:#005670"">{(ar ? "إدارة التفضيلات" : "Manage preferences")}</a> · <a href=""{unsub}"" style=""color:#005670"">{(ar ? "إلغاء الاشتراك" : "Unsubscribe")}</a></p>";
    }

    private string PostCard(SubscriberStore.Post p, bool ar)
    {
        var img = ImageLink(p.CoverImage);
        var link = BlogLink(p.Slug);
        var summary = string.IsNullOrWhiteSpace(p.Summary) ? string.Empty : p.Summary!.Length > 220 ? p.Summary[..217] + "..." : p.Summary;
        return $@"<table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" style=""border:1px solid #e1e8ef;border-radius:12px;margin:0 0 14px;overflow:hidden"">
{(img.Length > 0 ? $@"<tr><td><a href=""{link}""><img src=""{img}"" alt="""" width=""100%"" style=""display:block;width:100%;max-height:220px;object-fit:cover;border:0""></a></td></tr>" : string.Empty)}
<tr><td style=""padding:14px 16px"">
{(string.IsNullOrWhiteSpace(p.CategoryName) ? string.Empty : $@"<div style=""font-size:11px;font-weight:700;letter-spacing:.5px;color:#e87722;text-transform:uppercase"">{Enc(p.CategoryName!)}</div>")}
<a href=""{link}"" style=""display:block;margin:4px 0 6px;font-size:17px;font-weight:800;color:#0b2e3b;text-decoration:none"">{Enc(p.Title)}</a>
{(summary.Length > 0 ? $@"<div style=""font-size:14px;color:#3d5163;line-height:1.5"">{Enc(summary)}</div>" : string.Empty)}
<a href=""{link}"" style=""display:inline-block;margin-top:10px;color:#005670;font-weight:700;text-decoration:none"">{(ar ? "اقرأ المزيد ←" : "Read more →")}</a>
</td></tr></table>";
    }

    private string Layout(string language, string heading, string content, string? footer)
    {
        var ar = language == "ar";
        return $@"<!doctype html><html lang=""{(ar ? "ar" : "en")}"" dir=""{(ar ? "rtl" : "ltr")}""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;background:#f3f6f9;font-family:Segoe UI,Arial,sans-serif;color:#1d2b3a"">
<table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" style=""background:#f3f6f9""><tr><td align=""center"" style=""padding:24px 12px"">
<table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" style=""max-width:600px;background:#ffffff;border-radius:16px;overflow:hidden"">
<tr><td style=""background:#0b3d52;padding:18px 24px""><a href=""{WebsiteUrl}"" style=""color:#ffffff;text-decoration:none;font-size:18px;font-weight:800"">Bliss Point Group</a>
<span style=""color:#ffb877;font-size:12px;{(ar ? "margin-right" : "margin-left")}:8px"">{(ar ? "تحديثات الإمارات" : "UAE updates")}</span></td></tr>
<tr><td style=""padding:24px;font-size:15px;line-height:1.55;text-align:{(ar ? "right" : "left")}"">
<h1 style=""margin:0 0 14px;font-size:22px;color:#0b2e3b"">{heading}</h1>
{content}
{footer ?? string.Empty}
</td></tr></table>
<p style=""margin:14px 0 0;font-size:11px;color:#8a98a8"">Bliss Point Group · Balooshi Building, Meena Bazar, Bur Dubai · {Enc(Phone)}</p>
</td></tr></table></body></html>";
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
    private static string FirstName(string name) => (name ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    private static string Mask(string email) { var at = email.IndexOf('@'); return at > 2 ? email[..2] + "***" + email[at..] : "***"; }
}
