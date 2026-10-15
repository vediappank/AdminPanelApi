using System.Net;
using System.Net.Mail;
using System.Text;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Reads the "Smtp" section of appsettings.json (Host, Port, EnableSsl, UserName,
/// Password, FromEmail, FromName) and sends one email - with a PDF attached
/// for the quotation Send dialog (PlatformQuotationSendController, SendAsync),
/// or plain HTML for Website Leads department alerts (WebLeadNotifier, SendHtmlAsync).
/// </summary>
public static class SmtpMail
{
    public const string CompanyName = "Bliss Point Group";

    public sealed record Settings(string Host, int Port, bool EnableSsl, string UserName, string Password, string FromEmail, string FromName)
    {
        public bool IsConfigured => Host.Length > 0 && UserName.Length > 0 && Password.Length > 0 && FromEmail.Length > 0;
    }

    public static Settings Read(IConfiguration config)
    {
        var s = config.GetSection("Smtp");
        var userName = (s["UserName"] ?? string.Empty).Trim();
        var fromEmail = (s["FromEmail"] ?? string.Empty).Trim();
        return new Settings(
            Host: (s["Host"] ?? string.Empty).Trim(),
            Port: int.TryParse(s["Port"], out var port) ? port : 587,
            EnableSsl: !bool.TryParse(s["EnableSsl"], out var ssl) || ssl,
            UserName: userName,
            Password: (s["Password"] ?? string.Empty).Trim(),
            FromEmail: fromEmail.Length > 0 ? fromEmail : userName,
            FromName: string.IsNullOrWhiteSpace(s["FromName"]) ? CompanyName : s["FromName"]!.Trim());
    }

    public static Task SendAsync(Settings smtp, string to, IEnumerable<string> cc, string subject, string htmlBody,
        byte[] pdf, string pdfFileName, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, new[] { to }, cc, subject, htmlBody, replyTo: null, pdf, pdfFileName, cancellationToken);

    /// <summary>
    /// HTML email with no attachment - used by the Website Leads alerts
    /// (WebLeadNotifier) to copy each website submission to a department inbox.
    /// <paramref name="replyTo"/> lets staff answer the website visitor straight from their mail client.
    /// </summary>
    public static Task SendHtmlAsync(Settings smtp, IEnumerable<string> to, string subject, string htmlBody,
        string? replyTo, CancellationToken cancellationToken) =>
        SendCoreAsync(smtp, to, Array.Empty<string>(), subject, htmlBody, replyTo, pdf: null, pdfFileName: null, cancellationToken);

    private static async Task SendCoreAsync(Settings smtp, IEnumerable<string> to, IEnumerable<string> cc, string subject, string htmlBody,
        string? replyTo, byte[]? pdf, string? pdfFileName, CancellationToken cancellationToken)
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

        MemoryStream? pdfStream = null;
        try
        {
            if (pdf != null)
            {
                pdfStream = new MemoryStream(pdf, writable: false);
                message.Attachments.Add(new Attachment(pdfStream, pdfFileName ?? "document.pdf", System.Net.Mime.MediaTypeNames.Application.Pdf));
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
