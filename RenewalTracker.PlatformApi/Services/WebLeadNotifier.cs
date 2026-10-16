using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.SignalR;
using RenewalTracker.Platform.Application.DTOs.WebLeads;
using RenewalTracker.PlatformApi.Hubs;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Tells everyone about website submissions the moment they are saved:
///   1. SignalR "WebLeadReceived" to every connected operator with Website Leads access
///      (bell badge + toast + sound + browser notification - see web-lead-bell.component.ts);
///   2. email to the form's department inbox (dbo.WebForm.NotifyEmail),
///      falling back to "Website:NotifyEmail" in appsettings;
///   3. a "thank you, we received your enquiry" email to the visitor
///      ("Website:SendAcknowledgement", on by default once Smtp is configured);
///   4. WhatsApp template messages - a new-lead alert to "WhatsApp:StaffNumbers" and a
///      confirmation to the visitor - when the "WhatsApp" section is enabled (see WhatsAppCloud).
/// Email and WhatsApp run in the background, so the website visitor never waits on SMTP or Meta.
/// Spam (honeypot filled) and duplicate resubmissions are never alerted.
/// </summary>
public class WebLeadNotifier
{
    private readonly IHubContext<WebLeadsHub> _hub;
    private readonly IConfiguration _config;
    private readonly WhatsAppCloud _whatsApp;
    private readonly ILogger<WebLeadNotifier> _logger;

    public WebLeadNotifier(IHubContext<WebLeadsHub> hub, IConfiguration config, WhatsAppCloud whatsApp, ILogger<WebLeadNotifier> logger)
    {
        _hub = hub;
        _config = config;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    public async Task EnquiryReceivedAsync(WebEnquiryInsertResult r, PublicEnquiryRequest request)
    {
        if (r.IsSpam || r.IsDuplicate) return;

        var alert = new WebLeadAlertDto
        {
            Kind = "Enquiry",
            Id = r.WebEnquiryId,
            ReferenceNo = r.ReferenceNo,
            Title = string.IsNullOrWhiteSpace(r.CompanyName) ? r.FullName : $"{r.CompanyName} ({r.FullName})",
            SubTitle = string.IsNullOrWhiteSpace(r.ServiceInterest) ? r.FormName : r.ServiceInterest,
            Category = r.Category,
            FormName = r.FormName,
            RequestType = r.RequestType,
            Phone = r.Phone,
            Email = r.Email,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        };
        await PushAsync(alert);

        var details = new List<(string, string)>
        {
            ("Reference", r.ReferenceNo),
            ("Form", r.FormName),
            ("Type", r.RequestType),
            ("Name", r.FullName),
            ("Company", r.CompanyName ?? ""),
            ("Email", r.Email),
            ("Phone", r.Phone),
            ("Interested in", r.ServiceInterest ?? ""),
            ("Destination", request.Destination ?? ""),
            ("Travel dates", request.TravelDatesText ?? ""),
            ("Travellers", request.Travellers?.ToString() ?? ""),
            ("Team size", request.TeamSize ?? ""),
            ("Business type", request.BusinessType ?? ""),
            ("Modules", request.Modules is { Count: > 0 } ? string.Join(", ", request.Modules) : ""),
        };
        var departmentInbox = QueueEmail(r.NotifyEmail,
            $"New website {r.RequestType.ToLowerInvariant()} {r.ReferenceNo} - {alert.Title}",
            request.Message ?? "", details, replyTo: r.Email);

        QueueAcknowledgement(r, departmentInbox);

        var interest = string.IsNullOrWhiteSpace(r.ServiceInterest) ? r.FormName : r.ServiceInterest!;
        QueueWhatsApp(r.ReferenceNo,
            staffParameters: new[] { r.ReferenceNo, alert.Title, r.Phone, interest },
            customerPhone: r.Phone,
            customerParameters: new[] { FirstName(r.FullName), r.ReferenceNo });
    }

    public async Task JobApplicationReceivedAsync(WebJobApplicationInsertResult r)
    {
        if (r.IsSpam) return;

        await PushAsync(new WebLeadAlertDto
        {
            Kind = "JobApplication",
            Id = r.WebJobApplicationId,
            ReferenceNo = r.ReferenceNo,
            Title = r.FullName,
            SubTitle = $"CV: {r.RoleApplied}",
            Category = "Careers",
            FormName = "Careers",
            Phone = r.Phone,
            Email = r.Email,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        });

        QueueEmail(_config["Website:CareersNotifyEmail"],
            $"New CV {r.ReferenceNo} - {r.FullName} ({r.RoleApplied})",
            "A new job application was received from the careers page. Open Admin Panel ▸ Website Leads ▸ Job Applications to download the CV.",
            new List<(string, string)>
            {
                ("Reference", r.ReferenceNo), ("Name", r.FullName), ("Email", r.Email), ("Phone", r.Phone),
                ("Role", r.RoleApplied), ("Experience", r.Experience ?? ""),
            },
            replyTo: r.Email);

        // Staff alert only - applicants are not messaged on WhatsApp.
        QueueWhatsApp(r.ReferenceNo,
            staffParameters: new[] { r.ReferenceNo, r.FullName, r.Phone, $"CV: {r.RoleApplied}" },
            customerPhone: null,
            customerParameters: Array.Empty<string>());
    }

    /// <summary>Someone read / assigned / changed a lead - other operators' bells and lists refresh.</summary>
    public async Task LeadUpdatedAsync(string kind, long? id)
    {
        try
        {
            await _hub.Clients.Group(WebLeadsHub.Group).SendAsync(WebLeadsHub.UpdatedEvent, new { kind, id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebLeadNotifier: could not broadcast {Event}", WebLeadsHub.UpdatedEvent);
        }
    }

    private async Task PushAsync(WebLeadAlertDto alert)
    {
        try
        {
            await _hub.Clients.Group(WebLeadsHub.Group).SendAsync(WebLeadsHub.ReceivedEvent, alert);
        }
        catch (Exception ex)
        {
            // The lead is already saved - a failed push must never fail the website request.
            _logger.LogWarning(ex, "WebLeadNotifier: could not push {Reference}", alert.ReferenceNo);
        }
    }

    /// <summary>Department email. Returns the first recipient (used as Reply-To on the visitor's acknowledgement).</summary>
    private string? QueueEmail(string? formNotifyEmail, string subject, string body, List<(string, string)> details, string? replyTo)
    {
        var recipients = ParseRecipients(formNotifyEmail);
        if (recipients.Count == 0) recipients = ParseRecipients(_config["Website:NotifyEmail"]);
        if (recipients.Count == 0) return null;

        var smtp = SmtpMail.Read(_config);
        if (!smtp.IsConfigured)
        {
            _logger.LogInformation("WebLeadNotifier: Smtp is not configured - department email skipped for '{Subject}'", subject);
            return recipients[0];
        }

        var html = SmtpMail.ToHtml(body, details.Where(d => !string.IsNullOrWhiteSpace(d.Item2)));
        var safeReplyTo = IsEmail(replyTo) ? replyTo : null;

        RunInBackground($"department email '{subject}'", async ct =>
        {
            await SmtpMail.SendHtmlAsync(smtp, recipients, subject, html, safeReplyTo, ct);
            _logger.LogInformation("WebLeadNotifier: department email sent to {To} for '{Subject}'", string.Join(", ", recipients), subject);
        });
        return recipients[0];
    }

    /// <summary>"We received your enquiry" email to the website visitor.</summary>
    private void QueueAcknowledgement(WebEnquiryInsertResult r, string? departmentInbox)
    {
        if (bool.TryParse(_config["Website:SendAcknowledgement"], out var send) && !send) return;
        if (!IsEmail(r.Email)) return;

        var smtp = SmtpMail.Read(_config);
        if (!smtp.IsConfigured) return;

        var replyTo = _config["Website:AcknowledgementReplyTo"];
        if (!IsEmail(replyTo)) replyTo = IsEmail(departmentInbox) ? departmentInbox : smtp.FromEmail;

        var subject = $"We received your {RequestLabel(r.RequestType).ToLowerInvariant()} - {r.ReferenceNo} | {SmtpMail.CompanyName}";
        var html = AcknowledgementHtml(r);

        RunInBackground($"acknowledgement {r.ReferenceNo}", async ct =>
        {
            await SmtpMail.SendHtmlAsync(smtp, new[] { r.Email.Trim() }, subject, html, replyTo, ct);
            _logger.LogInformation("WebLeadNotifier: acknowledgement {Reference} emailed to the visitor", r.ReferenceNo);
        });
    }

    private void QueueWhatsApp(string reference, IReadOnlyList<string> staffParameters, string? customerPhone, IReadOnlyList<string> customerParameters)
    {
        var wa = _whatsApp.Read();
        if (!wa.IsConfigured) return;

        if (wa.StaffTemplate.IsSet)
            foreach (var number in wa.StaffNumbers)
                RunInBackground($"WhatsApp staff alert {reference}",
                    ct => _whatsApp.SendTemplateAsync(wa, number, wa.StaffTemplate, staffParameters, ct));

        if (wa.CustomerTemplate.IsSet && !string.IsNullOrWhiteSpace(customerPhone))
            RunInBackground($"WhatsApp confirmation {reference}",
                ct => _whatsApp.SendTemplateAsync(wa, customerPhone, wa.CustomerTemplate, customerParameters, ct));
    }

    private void RunInBackground(string what, Func<CancellationToken, Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await work(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebLeadNotifier: {What} failed", what);
            }
        });
    }

    private string AcknowledgementHtml(WebEnquiryInsertResult r)
    {
        string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        var phone = _config["Website:ContactPhone"] ?? "+971 4 454 0091";
        var whatsApp = new string((_config["Website:WhatsAppNumber"] ?? "971589970732").Where(char.IsDigit).ToArray());
        var site = (_config["Website:PublicUrl"] ?? "https://blisspointworld.com").TrimEnd('/');
        var interest = string.IsNullOrWhiteSpace(r.ServiceInterest) ? r.FormName : r.ServiceInterest;
        var waText = Uri.EscapeDataString($"Hello Bliss Point Group, my enquiry reference is {r.ReferenceNo}.");

        return $@"<div style=""font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.6;color:#1d2b3a;max-width:600px"">
<div style=""background:#005670;color:#fff;padding:18px 24px;border-radius:10px 10px 0 0;font-size:18px;font-weight:700"">{E(SmtpMail.CompanyName)}</div>
<div style=""border:1px solid #e3e8ee;border-top:0;padding:24px;border-radius:0 0 10px 10px"">
<p>Dear {E(FirstName(r.FullName))},</p>
<p>Thank you for contacting {E(SmtpMail.CompanyName)}. We have received your {E(RequestLabel(r.RequestType).ToLowerInvariant())} and one of our advisors will get back to you shortly, usually within one working day.</p>
<table style=""border-collapse:collapse;margin:16px 0;font-size:14px"">
<tr><td style=""padding:4px 16px 4px 0;color:#5d6b7e"">Reference</td><td style=""padding:4px 0""><b>{E(r.ReferenceNo)}</b></td></tr>
<tr><td style=""padding:4px 16px 4px 0;color:#5d6b7e"">Request</td><td style=""padding:4px 0""><b>{E(RequestLabel(r.RequestType))}</b></td></tr>
<tr><td style=""padding:4px 16px 4px 0;color:#5d6b7e"">Interested in</td><td style=""padding:4px 0""><b>{E(interest)}</b></td></tr>
</table>
<p>Need a faster answer? Call us on <a href=""tel:{E(new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray()))}"" style=""color:#005670"">{E(phone)}</a>
or <a href=""https://wa.me/{E(whatsApp)}?text={waText}"" style=""color:#005670"">message us on WhatsApp</a> and quote your reference.</p>
<p style=""margin-top:24px"">Kind regards,<br><b>{E(SmtpMail.CompanyName)}</b><br>
<span style=""color:#5d6b7e;font-size:13px"">Balooshi Building, Meena Bazar, Bur Dubai, Dubai - UAE<br><a href=""{E(site)}"" style=""color:#005670"">{E(site.Replace("https://", ""))}</a></span></p>
</div>
<p style=""color:#8a96a3;font-size:12px;margin-top:12px"">You are receiving this email because you submitted a form on {E(site.Replace("https://", ""))}. If this wasn't you, please ignore it.</p>
</div>";
    }

    /// <summary>Website request type (data-request-type) as visitor-friendly words.</summary>
    private static string RequestLabel(string? requestType) => (requestType ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "demo" => "Demo request",
        "access" => "Access request",
        "quote" => "Quote request",
        "" or "enquiry" or "inquiry" => "Enquiry",
        var other => char.ToUpperInvariant(other[0]) + other[1..] + " request",
    };

    private static string FirstName(string? fullName)
    {
        var name = (fullName ?? string.Empty).Trim();
        if (name.Length == 0) return "Customer";
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Length >= 2 ? first : name;
    }

    private static List<string> ParseRecipients(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
