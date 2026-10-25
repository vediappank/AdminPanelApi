using System.Net;
using System.Net.Mail;
using System.Text;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Reads the "Smtp" section of appsettings.json (Host, Port, EnableSsl, UserName,
/// Password, FromEmail, FromName) and sends one email - with a PDF attached
/// for the quotation Send dialog (PlatformQuotationSendController, SendAsync),
/// or plain HTML for Website Leads department alerts and visitor acknowledgements (WebLeadNotifier, SendHtmlAsync).
/// Port 465 is mapped to 587 (STARTTLS) - see Read.
/// </summary>
public static class SmtpMail
{
    public const string CompanyName = "Bliss Point Group";

    public sealed record Settings(string Host, int Port, bool EnableSsl, string UserName, string Password, string FromEmail, string FromName)
    {
        public bool IsConfigured => Host.Length > 0 && UserName.Length > 0 && Password.Length > 0 && FromEmail.Length > 0;
    }

    /// <summary>The mailbox of Settings ▸ Email & WhatsApp (PlatformMessagingStore) - used instead of the "Smtp" section while it is set and complete.</summary>
    public static Settings? PlatformOverride { get; set; }

    public static Settings Read(IConfiguration config) =>
        PlatformOverride is { IsConfigured: true } platform ? platform : ReadAppSettings(config);

    /// <summary>The "Smtp" section of appsettings.json only.</summary>
    public static Settings ReadAppSettings(IConfiguration config)
    {
        var s = config.GetSection("Smtp");
        var userName = (s["UserName"] ?? string.Empty).Trim();
        var fromEmail = (s["FromEmail"] ?? string.Empty).Trim();
        var port = int.TryParse(s["Port"], out var p) ? p : 587;
        // System.Net.Mail.SmtpClient only speaks STARTTLS, not implicit TLS - port 465 would just time out.
        // GoDaddy (smtpout.secureserver.net), Gmail and Office 365 all accept STARTTLS on 587.
        if (port == 465) port = 587;
        return new Settings(
            Host: (s["Host"] ?? string.Empty).Trim(),
            Port: port,
            EnableSsl: !bool.TryParse(s["EnableSsl"], out var ssl) || ssl,
            UserName: userName,
            Password: (s["Password"] ?? string.Empty).Trim(),
            FromEmail: fromEmail.Length > 0 ? fromEmail : userName,
            FromName: string.IsNullOrWhiteSpace(s["FromName"]) ? CompanyName : s["FromName"]!.Trim());
    }

    public static Task SendAsync(Settings smtp, string to, IEnumerable<string> cc, string subject, string htmlBody,
        byte[] pdf, string pdfFileName, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, new[] { to }, cc, subject, htmlBody, replyTo: null, pdf, pdfFileName, cancellationToken, System.Net.Mime.MediaTypeNames.Application.Pdf);

    /// <summary>HTML email with one attached file (e.g. a CV from the careers page).</summary>
    public static Task SendHtmlWithAttachmentAsync(Settings smtp, IEnumerable<string> to, string subject, string htmlBody,
        string? replyTo, byte[] file, string fileName, string contentType, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, to, Array.Empty<string>(), subject, htmlBody, replyTo, file, fileName, cancellationToken, contentType);

    /// <summary>
    /// HTML email with no attachment - used by the Website Leads alerts
    /// (WebLeadNotifier) to copy each website submission to a department inbox.
    /// <paramref name="replyTo"/> lets staff answer the website visitor straight from their mail client.
    /// </summary>
    public static Task SendHtmlAsync(Settings smtp, IEnumerable<string> to, string subject, string htmlBody,
        string? replyTo, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, to, Array.Empty<string>(), subject, htmlBody, replyTo, pdf: null, pdfFileName: null, cancellationToken, null);

    /// <summary>
    /// HTML email to one person with extra headers - the website "new posts" messages (SubscriberMessenger) add
    /// List-Unsubscribe / List-Unsubscribe-Post so Gmail and Outlook show their own one-click Unsubscribe.
    /// </summary>
    public static Task SendHtmlWithHeadersAsync(Settings smtp, string to, string subject, string htmlBody,
        string? replyTo, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, new[] { to }, Array.Empty<string>(), subject, htmlBody, replyTo, pdf: null, pdfFileName: null, cancellationToken, null, headers);

    private static async Task SendCoreAsync(Settings smtp, IEnumerable<string> to, IEnumerable<string> cc, string subject, string htmlBody,
        string? replyTo, byte[]? pdf, string? pdfFileName, CancellationToken cancellationToken, string? contentType,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(smtp.FromEmail, smtp.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        foreach (var address in to) message.To.Add(new MailAddress(address));
        foreach (var address in cc) message.CC.Add(new MailAddress(address));
        if (!string.IsNullOrWhiteSpace(replyTo)) message.ReplyToList.Add(new MailAddress(replyTo));
        if (headers != null) foreach (var h in headers) message.Headers.Add(h.Key, h.Value);

        MemoryStream? pdfStream = null;
        try
        {
            if (pdf != null)
            {
                pdfStream = new MemoryStream(pdf, writable: false);
                message.Attachments.Add(new Attachment(pdfStream, pdfFileName ?? "document.pdf", contentType ?? System.Net.Mime.MediaTypeNames.Application.Pdf));
            }

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                Credentials = new NetworkCredential(smtp.UserName, smtp.Password),
                EnableSsl = smtp.EnableSsl,
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 60000,
            };
            await client.SendMailAsync(message, cancellationToken);
        }
        finally
        {
            pdfStream?.Dispose();
        }
    }

    /// <summary>Plain text (as typed in the dialog) to simple HTML, followed by a small details table.</summary>
    public static string ToHtml(string body, IEnumerable<(string Label, string Value)> details)
    {
        var text = WebUtility.HtmlEncode(body.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");
        var rows = string.Concat(details.Select(d =>
            $"<tr><td style=\"padding:4px 12px 4px 0;color:#5d6b7e\">{WebUtility.HtmlEncode(d.Label)}</td><td style=\"padding:4px 0\"><b>{WebUtility.HtmlEncode(d.Value)}</b></td></tr>"));
        return $"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1d2b3a\"><p>{text}</p>" +
               $"<table style=\"border-collapse:collapse;margin-top:12px;font-size:13px\">{rows}</table></div>";
    }
}
