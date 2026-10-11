using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Sales;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Sales Pipeline ▸ Quotation ▸ Send - same idea as the Business Panel's
/// quotation Send: Email (PDF attached, editable subject / message / CC) or
/// WhatsApp (wa.me link with a ready message). Sending moves a Draft
/// quotation to Sent; a quotation already further along (Sent, Accepted,
/// Rejected) keeps its status.
///
/// Email needs the "Smtp" section in appsettings.json - without it
/// send-details returns EmailConfigured = false and POST email returns 400;
/// WhatsApp and the PDF keep working.
/// </summary>
[ApiController]
[Route("api/platform/quotations")]
[RequirePlatformAuth]
public class PlatformQuotationSendController : ControllerBase
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly PlatformDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformQuotationSendController> _logger;

    public PlatformQuotationSendController(PlatformDbContext db, IConfiguration config, ILogger<PlatformQuotationSendController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>Contact details and ready-to-edit texts for the Send quotation dialog.</summary>
    [HttpGet("{id:int}/send-details")]
    public async Task<ActionResult<QuotationSendDetailsDto>> GetSendDetails(int id)
    {
        var q = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound(new { message = "Quotation not found." });
        var enquiry = await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == q.SalesEnquiryId);

        var quoteNo = q.QuoteNo ?? $"Q-{q.QuotationId}";
        var name = enquiry?.CustomerNameSnapshot ?? "Customer";
        var greetingName = string.IsNullOrWhiteSpace(enquiry?.ContactPerson) ? name : enquiry!.ContactPerson!;
        var amount = $"AED {q.GrandTotal:N2}";
        var valid = q.ValidTill.HasValue ? $" This quotation is valid till {q.ValidTill.Value:dd MMM yyyy}." : string.Empty;
        var company = SmtpMail.CompanyName;

        return Ok(new QuotationSendDetailsDto
        {
            Quotation = await PlatformQuotationsController.LoadDtoAsync(_db, q),
            CustomerName = name,
            CustomerEmail = (enquiry?.Email ?? string.Empty).Trim(),
            CustomerMobile = FirstNonEmpty(enquiry?.WhatsApp, enquiry?.Mobile),
            DefaultSubject = $"Quotation {quoteNo} from {company}",
            DefaultBody =
                $"Dear {greetingName},\n\n" +
                $"Thank you for your enquiry. Please find attached our quotation {quoteNo} for {amount}.{valid}\n\n" +
                "Please review it and let us know if you would like to go ahead or need any changes.\n\n" +
                $"Regards,\n{company}",
            DefaultWhatsAppMessage =
                $"Dear {greetingName}, thank you for your enquiry. Our quotation {quoteNo} from {company} for {amount} is ready.{valid} " +
                "Please review it and let us know if you would like to go ahead.",
            EmailConfigured = SmtpMail.Read(_config).IsConfigured,
        });
    }

    /// <summary>Emails the quotation with its PDF attached; a Draft quotation becomes Sent.</summary>
    [HttpPost("{id:int}/email")]
    public async Task<ActionResult<QuotationSendResultDto>> SendEmail(int id, QuotationEmailRequest request)
    {
        var q = await _db.Quotations.FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound(new { message = "Quotation not found." });

        var to = (request.To ?? string.Empty).Trim();
        if (!EmailPattern.IsMatch(to)) return BadRequest(new { message = "Enter a valid customer email address." });
        if (string.IsNullOrWhiteSpace(request.Subject)) return BadRequest(new { message = "Email subject is required." });
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequest(new { message = "Email message is required." });
        var cc = (request.Cc ?? new List<string>()).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var address in cc)
        {
            if (!EmailPattern.IsMatch(address)) return BadRequest(new { message = $"Invalid CC email address: {address}" });
            if (string.Equals(address, to, StringComparison.OrdinalIgnoreCase)) return BadRequest(new { message = "The customer email must not be repeated in CC." });
        }

        var smtp = SmtpMail.Read(_config);
        if (!smtp.IsConfigured)
            return BadRequest(new { message = "Email is not set up on the server. Fill in the Smtp section of PlatformApi appsettings.json, or send the quotation by WhatsApp / PDF." });

        var quoteNo = q.QuoteNo ?? $"Q-{q.QuotationId}";
        var pdf = await PlatformQuotationsController.BuildPdfAsync(_db, q);
        var html = SmtpMail.ToHtml(request.Body, new[]
        {
            ("Quotation No", quoteNo),
            ("Date", q.QuoteDate.ToString("dd MMM yyyy")),
            ("Valid till", q.ValidTill.HasValue ? q.ValidTill.Value.ToString("dd MMM yyyy") : "-"),
            ("Amount", $"AED {q.GrandTotal:N2}"),
        });
        try
        {
            await SmtpMail.SendAsync(smtp, to, cc, request.Subject.Trim(), html, pdf, $"{quoteNo}.pdf", HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email quotation {QuoteNo}", quoteNo);
            return BadRequest(new { message = $"Email could not be sent: {ex.Message}" });
        }

        await MarkSentAsync(q);
        return Ok(new QuotationSendResultDto
        {
            Message = $"Quotation {quoteNo} emailed to {to}.",
            Quotation = await PlatformQuotationsController.LoadDtoAsync(_db, q),
        });
    }

    /// <summary>Builds the wa.me link for the browser to open; a Draft quotation becomes Sent.</summary>
    [HttpPost("{id:int}/whatsapp")]
    public async Task<ActionResult<QuotationSendResultDto>> SendWhatsApp(int id, QuotationWhatsAppRequest request)
    {
        var q = await _db.Quotations.FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound(new { message = "Quotation not found." });

        var mobile = new string((request.Mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        var text = (request.Message ?? string.Empty).Trim();
        if (mobile.Length < 8) return BadRequest(new { message = "Enter the customer's WhatsApp number with country code, e.g. 971501234567." });
        if (text.Length == 0) return BadRequest(new { message = "WhatsApp message is required." });

        await MarkSentAsync(q);
        var quoteNo = q.QuoteNo ?? $"Q-{q.QuotationId}";
        return Ok(new QuotationSendResultDto
        {
            Message = $"WhatsApp opened for quotation {quoteNo}. Attach the downloaded PDF in the chat.",
            WhatsAppUrl = $"https://wa.me/{mobile}?text={Uri.EscapeDataString(text)}",
            Quotation = await PlatformQuotationsController.LoadDtoAsync(_db, q),
        });
    }

    /// <summary>Only a Draft quotation moves to Sent - one already Sent / Accepted / Rejected is never pushed back.</summary>
    private async Task MarkSentAsync(Quotation q)
    {
        if (!string.Equals(q.Status, "Draft", StringComparison.OrdinalIgnoreCase)) return;
        q.Status = "Sent";
        q.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
