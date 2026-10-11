using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Billing;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Sales Pipeline ▸ Invoice - one invoice per Accepted quotation (dbo.SalesInvoices).
/// Flow: quotation marked Accepted -> Create invoice (amounts copied from the
/// quotation, which is then locked) -> PDF view / Email / WhatsApp (marks it
/// Sent) -> payments recorded against it (PlatformBillingController's
/// RecordQuotationPayment, which requires the invoice). Paid / balance are
/// always computed from dbo.Payments.
///
/// Email needs the "Smtp" section in appsettings.json (Host, Port, EnableSsl,
/// UserName, Password, FromEmail, FromName). Without it, GET send-details
/// returns EmailConfigured = false and POST email returns 400 with a clear
/// message; PDF and WhatsApp keep working.
/// </summary>
[ApiController]
[Route("api/platform/invoices")]
[RequirePlatformAuth]
public class PlatformInvoicesController : ControllerBase
{
    private const string CompanyName = "Bliss Point Group";
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly PlatformDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformInvoicesController> _logger;

    public PlatformInvoicesController(PlatformDbContext db, IConfiguration config, ILogger<PlatformInvoicesController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>Every invoice (optionally for one enquiry), newest first, with paid / balance.</summary>
    [HttpGet]
    public async Task<ActionResult<List<InvoiceDto>>> GetInvoices([FromQuery] int? enquiryId)
    {
        var query = _db.Invoices.AsNoTracking().AsQueryable();
        if (enquiryId.HasValue)
        {
            var quotationIds = _db.Quotations.Where(q => q.SalesEnquiryId == enquiryId.Value).Select(q => q.QuotationId);
            query = query.Where(i => quotationIds.Contains(i.QuotationId));
        }
        var rows = await query.OrderByDescending(i => i.InvoiceId).ToListAsync();
        return Ok(await ToDtosAsync(rows));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> GetInvoice(int id)
    {
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });
        return Ok((await ToDtosAsync(new List<Invoice> { invoice }))[0]);
    }

    /// <summary>Creates the invoice for an Accepted quotation. One per quotation.</summary>
    [HttpPost("quotation/{quotationId:int}")]
    public async Task<ActionResult<InvoiceDto>> CreateInvoice(int quotationId, CreateInvoiceDto? dto)
    {
        var quotation = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(q => q.QuotationId == quotationId);
        if (quotation == null) return NotFound(new { message = "Quotation not found." });
        if (quotation.Status != "Accepted")
            return BadRequest(new { message = "Mark the quotation as Accepted before creating the invoice." });
        if (quotation.GrandTotal <= 0)
            return BadRequest(new { message = "The quotation total is zero - nothing to invoice." });
        if (await _db.Invoices.AnyAsync(i => i.QuotationId == quotationId))
            return Conflict(new { message = "This quotation already has an invoice." });

        var invoiceDate = (dto?.InvoiceDate ?? DateTime.UtcNow).Date;
        var dueDate = (dto?.DueDate ?? invoiceDate.AddDays(7)).Date;
        if (dueDate < invoiceDate) return BadRequest(new { message = "Due date cannot be before the invoice date." });

        var invoice = new Invoice
        {
            QuotationId = quotationId,
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            NetAmount = quotation.Subtotal,
            VatAmount = quotation.VatAmount,
            GrandTotal = quotation.GrandTotal,
            Status = "Created",
            Notes = string.IsNullOrWhiteSpace(dto?.Notes) ? null : dto!.Notes!.Trim(),
            CreatedDate = DateTime.UtcNow,
        };

        // INV-000001 numbering; the unique index is the real guard, retry once on a race.
        for (var attempt = 0; ; attempt++)
        {
            var maxId = await _db.Invoices.MaxAsync(i => (int?)i.InvoiceId) ?? 0;
            invoice.InvoiceNumber = $"INV-{maxId + 1 + attempt:D6}";
            _db.Invoices.Add(invoice);
            try
            {
                await _db.SaveChangesAsync();
                break;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                _db.Entry(invoice).State = EntityState.Detached;
                if (await _db.Invoices.AnyAsync(i => i.QuotationId == quotationId))
                    return Conflict(new { message = "This quotation already has an invoice." });
            }
        }

        return Ok((await ToDtosAsync(new List<Invoice> { invoice }))[0]);
    }

    /// <summary>Changes invoice date / due date / notes - only while nothing has been paid against it.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> UpdateInvoice(int id, CreateInvoiceDto dto)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });
        if (await PaidAsync(invoice.QuotationId) > 0)
            return BadRequest(new { message = "Payments are recorded against this invoice - it can no longer be changed." });

        var invoiceDate = (dto.InvoiceDate ?? invoice.InvoiceDate).Date;
        var dueDate = (dto.DueDate ?? invoice.DueDate ?? invoiceDate.AddDays(7)).Date;
        if (dueDate < invoiceDate) return BadRequest(new { message = "Due date cannot be before the invoice date." });

        invoice.InvoiceDate = invoiceDate;
        invoice.DueDate = dueDate;
        invoice.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        invoice.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok((await ToDtosAsync(new List<Invoice> { invoice }))[0]);
    }

    /// <summary>Cancels (deletes) an invoice that has no payments - unlocks the quotation again.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteInvoice(int id)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });
        if (await PaidAsync(invoice.QuotationId) > 0)
            return BadRequest(new { message = "Payments are recorded against this invoice - it cannot be deleted." });
        _db.Invoices.Remove(invoice);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The invoice PDF - same layout as the quotation PDF, titled INVOICE.</summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> GetInvoicePdf(int id)
    {
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound();
        var bytes = await BuildPdfAsync(invoice);
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{invoice.InvoiceNumber}.pdf\"";
        return File(bytes, "application/pdf");
    }

    /// <summary>Contact details and default texts for the Send invoice dialog.</summary>
    [HttpGet("{id:int}/send-details")]
    public async Task<ActionResult<InvoiceSendDetailsDto>> GetSendDetails(int id)
    {
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });
        var dto = (await ToDtosAsync(new List<Invoice> { invoice }))[0];
        var (enquiry, lead) = await LoadCustomerAsync(invoice.QuotationId);

        var name = enquiry?.CustomerNameSnapshot ?? dto.Customer ?? "Customer";
        var greetingName = string.IsNullOrWhiteSpace(enquiry?.ContactPerson) ? name : enquiry!.ContactPerson!;
        var due = invoice.DueDate.HasValue ? invoice.DueDate.Value.ToString("dd MMM yyyy") : "-";
        var amount = $"AED {invoice.GrandTotal:N2}";

        return Ok(new InvoiceSendDetailsDto
        {
            Invoice = dto,
            CustomerName = name,
            CustomerEmail = FirstNonEmpty(enquiry?.Email, lead?.Email),
            CustomerMobile = FirstNonEmpty(enquiry?.Mobile, lead?.Mobile),
            DefaultSubject = $"Invoice {invoice.InvoiceNumber} from {CompanyName}",
            DefaultBody =
                $"Dear {greetingName},\n\n" +
                $"Thank you for choosing {CompanyName}. Please find attached invoice {invoice.InvoiceNumber} for {amount}" +
                (dto.QuoteNo != null ? $" against quotation {dto.QuoteNo}" : string.Empty) + ".\n\n" +
                $"Payment is due by {due}. Please quote the invoice number with your payment.\n\n" +
                $"Regards,\n{CompanyName}",
            DefaultWhatsAppMessage =
                $"Dear {greetingName}, your invoice {invoice.InvoiceNumber} from {CompanyName} for {amount} is ready. " +
                $"Payment is due by {due}. Please quote the invoice number with your payment. Thank you.",
            EmailConfigured = Smtp().IsConfigured,
        });
    }

    /// <summary>Emails the invoice with the PDF attached, then marks it Sent.</summary>
    [HttpPost("{id:int}/email")]
    public async Task<ActionResult<InvoiceSendResultDto>> SendEmail(int id, InvoiceEmailRequest request)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });

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

        var smtp = Smtp();
        if (!smtp.IsConfigured)
            return BadRequest(new { message = "Email is not set up on the server. Fill in the Smtp section of PlatformApi appsettings.json, or send the invoice by WhatsApp / PDF." });

        var pdf = await BuildPdfAsync(invoice);
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(smtp.FromEmail, smtp.FromName),
                Subject = request.Subject.Trim(),
                Body = BuildEmailHtml(request.Body, invoice),
                IsBodyHtml = true,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
            };
            message.To.Add(new MailAddress(to));
            foreach (var address in cc) message.CC.Add(new MailAddress(address));
            using var pdfStream = new MemoryStream(pdf, writable: false);
            message.Attachments.Add(new Attachment(pdfStream, $"{invoice.InvoiceNumber}.pdf", System.Net.Mime.MediaTypeNames.Application.Pdf));

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                Credentials = new NetworkCredential(smtp.UserName, smtp.Password),
                EnableSsl = smtp.EnableSsl,
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 60000,
            };
            await client.SendMailAsync(message, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email invoice {InvoiceNumber}", invoice.InvoiceNumber);
            return BadRequest(new { message = $"Email could not be sent: {ex.Message}" });
        }

        await MarkSentAsync(invoice, "Email");
        return Ok(new InvoiceSendResultDto
        {
            Message = $"Invoice {invoice.InvoiceNumber} emailed to {to}.",
            Invoice = (await ToDtosAsync(new List<Invoice> { invoice }))[0],
        });
    }

    /// <summary>Builds the wa.me link for the browser to open, then marks the invoice Sent (via WhatsApp).</summary>
    [HttpPost("{id:int}/whatsapp")]
    public async Task<ActionResult<InvoiceSendResultDto>> SendWhatsApp(int id, InvoiceWhatsAppRequest request)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });

        var mobile = new string((request.Mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        var text = (request.Message ?? string.Empty).Trim();
        if (mobile.Length < 8) return BadRequest(new { message = "Enter the customer's WhatsApp number with country code, e.g. 971501234567." });
        if (text.Length == 0) return BadRequest(new { message = "WhatsApp message is required." });

        await MarkSentAsync(invoice, "WhatsApp");
        return Ok(new InvoiceSendResultDto
        {
            Message = $"WhatsApp opened for invoice {invoice.InvoiceNumber}. Attach the downloaded PDF in the chat.",
            WhatsAppUrl = $"https://wa.me/{mobile}?text={Uri.EscapeDataString(text)}",
            Invoice = (await ToDtosAsync(new List<Invoice> { invoice }))[0],
        });
    }

    // ------------------------------------------------------------------ helpers

    private async Task MarkSentAsync(Invoice invoice, string via)
    {
        invoice.Status = "Sent";
        invoice.SentVia = via;
        invoice.SentDate = DateTime.UtcNow;
        invoice.SentCount += 1;
        invoice.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<decimal> PaidAsync(int quotationId) =>
        await _db.Payments.AsNoTracking().Where(p => p.QuotationId == quotationId).SumAsync(p => (decimal?)p.Amount) ?? 0m;

    private async Task<List<InvoiceDto>> ToDtosAsync(List<Invoice> rows)
    {
        if (rows.Count == 0) return new List<InvoiceDto>();
        var quotationIds = rows.Select(r => r.QuotationId).Distinct().ToList();
        var quotations = await _db.Quotations.AsNoTracking()
            .Where(q => quotationIds.Contains(q.QuotationId))
            .Select(q => new { q.QuotationId, q.QuoteNo, q.SalesEnquiryId })
            .ToDictionaryAsync(q => q.QuotationId);
        var enquiryIds = quotations.Values.Select(q => q.SalesEnquiryId).Distinct().ToList();
        var customers = await _db.Enquiries.AsNoTracking()
            .Where(e => enquiryIds.Contains(e.SalesEnquiryId))
            .Select(e => new { e.SalesEnquiryId, e.CustomerNameSnapshot })
            .ToDictionaryAsync(e => e.SalesEnquiryId, e => e.CustomerNameSnapshot);
        var paid = await _db.Payments.AsNoTracking()
            .Where(p => p.QuotationId != null && quotationIds.Contains(p.QuotationId.Value))
            .GroupBy(p => p.QuotationId!.Value)
            .Select(g => new { QuotationId = g.Key, Paid = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.QuotationId, x => x.Paid);

        return rows.Select(i =>
        {
            quotations.TryGetValue(i.QuotationId, out var q);
            var enquiryId = q?.SalesEnquiryId ?? 0;
            customers.TryGetValue(enquiryId, out var customer);
            paid.TryGetValue(i.QuotationId, out var paidAmount);
            var balance = Math.Max(i.GrandTotal - paidAmount, 0m);
            return new InvoiceDto
            {
                Id = i.InvoiceId,
                InvoiceNumber = i.InvoiceNumber,
                QuotationId = i.QuotationId,
                QuoteNo = q?.QuoteNo,
                EnquiryId = enquiryId,
                Customer = customer,
                InvoiceDate = i.InvoiceDate,
                DueDate = i.DueDate,
                NetAmount = i.NetAmount,
                VatAmount = i.VatAmount,
                GrandTotal = i.GrandTotal,
                Paid = paidAmount,
                Balance = balance,
                PaymentStatus = paidAmount <= 0 ? "Unpaid" : balance <= 0 ? "Paid" : "Partially paid",
                Status = i.Status,
                SentDate = i.SentDate,
                SentVia = i.SentVia,
                SentCount = i.SentCount,
                Notes = i.Notes,
                CreatedDate = i.CreatedDate,
            };
        }).ToList();
    }

    private async Task<(Enquiry? Enquiry, Lead? Lead)> LoadCustomerAsync(int quotationId)
    {
        var enquiryId = await _db.Quotations.AsNoTracking().Where(q => q.QuotationId == quotationId).Select(q => (int?)q.SalesEnquiryId).FirstOrDefaultAsync();
        if (enquiryId == null) return (null, null);
        var enquiry = await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == enquiryId.Value);
        var lead = await _db.Leads.AsNoTracking().Where(l => l.SalesEnquiryId == enquiryId.Value).OrderByDescending(l => l.LeadId).FirstOrDefaultAsync();
        return (enquiry, lead);
    }

    private async Task<byte[]> BuildPdfAsync(Invoice invoice)
    {
        var quotation = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(q => q.QuotationId == invoice.QuotationId);
        var (enquiry, _) = await LoadCustomerAsync(invoice.QuotationId);
        var products = await _db.QuotationProducts.AsNoTracking().Where(p => p.QuotationId == invoice.QuotationId).OrderBy(p => p.QuotationProductId).ToListAsync();
        var productIds = products.Select(p => p.QuotationProductId).ToList();
        var lines = await _db.QuotationServiceLines.AsNoTracking()
            .Where(s => productIds.Contains(s.QuotationProductId))
            .OrderBy(s => s.QuotationServiceLineId)
            .ToListAsync();

        var items = new List<QuotationPdfItem>();
        foreach (var p in products)
        {
            foreach (var s in lines.Where(l => l.QuotationProductId == p.QuotationProductId))
            {
                var unitVat = Math.Round(s.Amount * s.VatPercent / 100m, 2);
                items.Add(new QuotationPdfItem(s.ServiceName, p.ApplicationNameSnapshot, s.Qty, s.Amount, unitVat, s.Amount + unitVat));
            }
        }

        var contact = string.Join(" / ", new[] { enquiry?.Mobile, enquiry?.Email }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return QuotationPdfBuilder.Build(new QuotationPdfData(
            CompanyName: CompanyName,
            QuotationNumber: invoice.InvoiceNumber,
            CustomerName: enquiry?.CustomerNameSnapshot ?? "-",
            EnquiryNumber: quotation?.QuoteNo ?? $"Q-{invoice.QuotationId}",
            ContactLine: contact,
            ContactPerson: enquiry?.ContactPerson ?? string.Empty,
            CreatedDate: invoice.InvoiceDate,
            ValidTill: invoice.DueDate,
            Items: items,
            Net: invoice.NetAmount,
            Vat: invoice.VatAmount,
            GrandTotal: invoice.GrandTotal,
            DocumentTitle: "INVOICE",
            Notes: invoice.Notes,
            NumberLabel: "Invoice No",
            ValidLabel: "Due Date",
            ReferenceLabel: "Quotation No",
            Terms: new List<string>
            {
                "Payment is due by the due date shown above.",
                "Please quote the invoice number with your payment.",
            }));
    }

    private static string BuildEmailHtml(string body, Invoice invoice)
    {
        var text = WebUtility.HtmlEncode(body.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");
        return $@"<div style=""font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1d2b3a"">
<p>{text}</p>
<table style=""border-collapse:collapse;margin-top:12px;font-size:13px"">
<tr><td style=""padding:4px 12px 4px 0;color:#5d6b7e"">Invoice No</td><td style=""padding:4px 0""><b>{WebUtility.HtmlEncode(invoice.InvoiceNumber)}</b></td></tr>
<tr><td style=""padding:4px 12px 4px 0;color:#5d6b7e"">Invoice date</td><td style=""padding:4px 0"">{invoice.InvoiceDate:dd MMM yyyy}</td></tr>
<tr><td style=""padding:4px 12px 4px 0;color:#5d6b7e"">Due date</td><td style=""padding:4px 0"">{(invoice.DueDate.HasValue ? invoice.DueDate.Value.ToString("dd MMM yyyy") : "-")}</td></tr>
<tr><td style=""padding:4px 12px 4px 0;color:#5d6b7e"">Amount</td><td style=""padding:4px 0""><b>AED {invoice.GrandTotal:N2}</b></td></tr>
</table>
</div>";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private SmtpSettings Smtp()
    {
        var s = _config.GetSection("Smtp");
        var userName = (s["UserName"] ?? string.Empty).Trim();
        var fromEmail = (s["FromEmail"] ?? string.Empty).Trim();
        return new SmtpSettings(
            Host: (s["Host"] ?? string.Empty).Trim(),
            Port: int.TryParse(s["Port"], out var port) ? port : 587,
            EnableSsl: !bool.TryParse(s["EnableSsl"], out var ssl) || ssl,
            UserName: userName,
            Password: (s["Password"] ?? string.Empty).Trim(),
            FromEmail: fromEmail.Length > 0 ? fromEmail : userName,
            FromName: string.IsNullOrWhiteSpace(s["FromName"]) ? CompanyName : s["FromName"]!.Trim());
    }

    private sealed record SmtpSettings(string Host, int Port, bool EnableSsl, string UserName, string Password, string FromEmail, string FromName)
    {
        public bool IsConfigured => Host.Length > 0 && UserName.Length > 0 && Password.Length > 0 && FromEmail.Length > 0;
    }
}
