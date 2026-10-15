using System.Net.Mail;
using Microsoft.AspNetCore.SignalR;
using RenewalTracker.Platform.Application.DTOs.WebLeads;
using RenewalTracker.PlatformApi.Hubs;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Tells the Admin Panel about website submissions the moment they are saved:
///   1. SignalR "WebLeadReceived" to every connected operator with Website Leads access
///      (bell badge + toast + sound + browser notification - see web-lead-bell.component.ts);
///   2. optionally an email to the form's department inbox (dbo.WebForm.NotifyEmail),
///      falling back to "Website:NotifyEmail" in appsettings - sent in the background so the
///      website visitor never waits on SMTP.
/// Spam (honeypot filled) and duplicate resubmissions are never alerted.
/// </summary>
public class WebLeadNotifier
{
    private readonly IHubContext<WebLeadsHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<WebLeadNotifier> _logger;

    public WebLeadNotifier(IHubContext<WebLeadsHub> hub, IConfiguration config, ILogger<WebLeadNotifier> logger)
    {
        _hub = hub;
        _config = config;
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
        QueueEmail(r.NotifyEmail,
            $"New website {r.RequestType.ToLowerInvariant()} {r.ReferenceNo} - {alert.Title}",
            request.Message ?? "", details, replyTo: r.Email);
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

    private void QueueEmail(string? formNotifyEmail, string subject, string body, List<(string, string)> details, string? replyTo)
    {
        var recipients = ParseRecipients(formNotifyEmail);
        if (recipients.Count == 0) recipients = ParseRecipients(_config["Website:NotifyEmail"]);
        if (recipients.Count == 0) return;

        var smtp = SmtpMail.Read(_config);
        if (!smtp.IsConfigured)
        {
            _logger.LogInformation("WebLeadNotifier: Smtp is not configured - department email skipped for '{Subject}'", subject);
            return;
        }

        var html = SmtpMail.ToHtml(body, details.Where(d => !string.IsNullOrWhiteSpace(d.Item2)));
        var safeReplyTo = IsEmail(replyTo) ? replyTo : null;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await SmtpMail.SendHtmlAsync(smtp, recipients, subject, html, safeReplyTo, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebLeadNotifier: department email failed for '{Subject}'", subject);
            }
        });
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
