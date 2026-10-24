using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Billing;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Billing ▸ Payments - partial-payment ledgers for two independent totals:
///   - Sale-side: installments against a Quotation.GrandTotal, recorded via
///     RecordQuotationPayment. Can happen before the originating Lead has
///     been converted to a Tenant (a deposit collected pre-conversion), so
///     TenantId on the resulting Payment may come back null until then.
///   - Renewal-side: installments against a License.RenewalAmount, recorded
///     via RecordLicensePayment. The License always has a Tenant already,
///     so TenantId is set immediately.
///
/// A Payment references exactly one of QuotationId/LicenseId, never both -
/// enforced both here (two separate endpoints, each only ever sets one) and
/// in the database (CK_Payments_ExactlyOneTarget - see
/// PlatformDbContext.Platform.cs). There is deliberately no generic
/// "POST payments" endpoint anymore - the old one accepted a free-text
/// Customer with no FK at all; every payment now has to say which Quotation
/// or License it's paying down. Platform-owned, hand-written against
/// PlatformDbContext.
/// </summary>
[ApiController]
[Route("api/platform/billing")]
[RequirePlatformAuth]
public class PlatformBillingController : ControllerBase
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly PlatformDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformBillingController> _logger;

    public PlatformBillingController(PlatformDbContext db, IConfiguration config, ILogger<PlatformBillingController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ------------------------------------------------------------------ payment receipts

    public class EmailReceiptRequest
    {
        /// <summary>Default: the tenant's contact e-mail (license payments) or the enquiry's e-mail (quotation payments).</summary>
        public string? To { get; set; }
        public List<string>? Cc { get; set; }
    }

    public class ReceiptInfoDto
    {
        public string ReceiptNumber { get; set; } = string.Empty;
        public string? DefaultEmail { get; set; }
        public bool EmailConfigured { get; set; }
    }

    /// <summary>Receipt PDF for one payment - inline (preview) or ?download=true. Uses Settings > Branding (name, logo, receipt template).</summary>
    [HttpGet("payments/{id:int}/receipt")]
    public async Task<IActionResult> GetReceipt(int id, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var receipt = await BuildReceiptAsync(id, ct);
        if (receipt == null) return NotFound(new { message = "Payment not found." });
        if (download) return File(receipt.Pdf, "application/pdf", receipt.FileName);
        Response.Headers.ContentDisposition = $"inline; filename=\"{receipt.FileName}\"";
        return File(receipt.Pdf, "application/pdf");
    }

    /// <summary>Receipt number and default recipient for the Email receipt dialog.</summary>
    [HttpGet("payments/{id:int}/receipt/info")]
    public async Task<ActionResult<ReceiptInfoDto>> GetReceiptInfo(int id, CancellationToken ct)
    {
        var receipt = await BuildReceiptAsync(id, ct, pdf: false);
        if (receipt == null) return NotFound(new { message = "Payment not found." });
        return Ok(new ReceiptInfoDto { ReceiptNumber = receipt.Number, DefaultEmail = receipt.Email, EmailConfigured = SmtpMail.Read(_config).IsConfigured });
    }

    /// <summary>E-mails the receipt PDF to the payer.</summary>
    [HttpPost("payments/{id:int}/receipt/email")]
    public async Task<IActionResult> EmailReceipt(int id, EmailReceiptRequest request, CancellationToken ct)
    {
        var receipt = await BuildReceiptAsync(id, ct);
        if (receipt == null) return NotFound(new { message = "Payment not found." });

        var to = (string.IsNullOrWhiteSpace(request?.To) ? receipt.Email : request!.To)?.Trim();
        if (string.IsNullOrWhiteSpace(to)) return BadRequest(new { message = "There is no e-mail address for this customer - enter one." });
        if (!EmailPattern.IsMatch(to)) return BadRequest(new { message = "Enter a valid e-mail address." });
        var cc = (request?.Cc ?? new List<string>()).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var address in cc)
            if (!EmailPattern.IsMatch(address)) return BadRequest(new { message = $"Invalid CC e-mail address: {address}" });

        var smtp = SmtpMail.Read(_config);
        if (!smtp.IsConfigured) return BadRequest(new { message = "Email is not set up on the server (Smtp section of appsettings.json). Download the receipt instead." });

        var html = SmtpMail.ToHtml($"Dear {receipt.Payer},\n\nThank you for your payment. Please find your receipt {receipt.Number} attached.\n\nRegards,\n{receipt.Company}", new[]
        {
            ("Receipt No", receipt.Number),
            ("Date", receipt.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
            ("Amount", receipt.AmountText),
            ("Against", receipt.Against),
        });
        try
        {
            await SmtpMail.SendAsync(smtp, to, cc, $"Payment receipt {receipt.Number} - {receipt.Company}", html, receipt.Pdf, receipt.FileName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email receipt {Receipt}", receipt.Number);
            return BadRequest(new { message = $"Email could not be sent: {ex.Message}" });
        }
        return Ok(new { message = $"Receipt {receipt.Number} emailed to {to}." });
    }

    private sealed record BuiltReceipt(byte[] Pdf, string FileName, string Number, string? Email, string Payer, string Company, DateTime Date, string AmountText, string Against);

    private async Task<BuiltReceipt?> BuildReceiptAsync(int paymentId, CancellationToken ct, bool pdf = true)
    {
        var p = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.PaymentId == paymentId, ct);
        if (p == null) return null;
        var branding = await PlatformDocumentBranding.ReadAsync(_db, ct);
        var tenant = p.TenantId is int tid ? await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tid, ct) : null;

        var currency = "AED";
        string against, description;
        decimal? total = null;
        string? email = tenant?.ContactEmail;
        var lines = new List<string>();
        if (tenant != null) lines.Add($"Customer Code: {tenant.TenantCode}");
        if (!string.IsNullOrWhiteSpace(tenant?.ContactName)) lines.Add($"Contact: {tenant!.ContactName}");

        if (p.QuotationId is int qid)
        {
            var q = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.QuotationId == qid, ct);
            var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.QuotationId == qid, ct);
            var enquiry = q == null ? null : await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == q.SalesEnquiryId, ct);
            var quoteNo = q?.QuoteNo ?? $"Q-{qid}";
            against = invoice != null ? $"Invoice {invoice.InvoiceNumber}" : $"Quotation {quoteNo}";
            description = invoice != null ? $"Payment towards invoice {invoice.InvoiceNumber} (quotation {quoteNo})" : $"Payment towards quotation {quoteNo}";
            total = q?.GrandTotal;
            email = string.IsNullOrWhiteSpace(enquiry?.Email) ? email : enquiry!.Email;
            if (enquiry != null && tenant == null)
            {
                if (!string.IsNullOrWhiteSpace(enquiry.ContactPerson)) lines.Add($"Contact: {enquiry.ContactPerson}");
                lines.Add(string.Join(" / ", new[] { enquiry.Mobile, enquiry.Email }.Where(v => !string.IsNullOrWhiteSpace(v))));
            }
        }
        else
        {
            var license = p.LicenseId is int lid ? await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.LicenseId == lid, ct) : null;
            // the subscription term running on the payment date (or the latest one) names the plan
            var sub = p.LicenseId == null ? null : await _db.TenantSubscriptions.AsNoTracking().Include(s => s.Plan)
                .Where(s => s.LicenseId == p.LicenseId && s.StartDate <= p.Date)
                .OrderByDescending(s => s.StartDate).FirstOrDefaultAsync(ct);
            if (sub != null) currency = sub.Currency;
            against = sub != null ? $"Subscription {sub.Plan.PlanName} ({sub.BillingCycle})" : $"License {license?.LicenseKey}";
            description = sub != null
                ? $"Subscription {sub.Plan.PlanName} - {sub.BillingCycle}, {sub.StartDate:dd MMM yyyy} to {sub.EndDate:dd MMM yyyy} (license {license?.LicenseKey})"
                : $"License renewal - {license?.LicenseKey}";
            total = license?.RenewalAmount;
        }
        if (!string.IsNullOrWhiteSpace(tenant?.ContactMobile)) lines.Add(tenant!.ContactMobile!);
        if (!string.IsNullOrWhiteSpace(email) && !lines.Any(l => l.Contains(email!, StringComparison.OrdinalIgnoreCase))) lines.Add(email!);

        // paid up to and including this payment (same quotation / license)
        var sameTarget = _db.Payments.AsNoTracking().Where(x => p.QuotationId != null ? x.QuotationId == p.QuotationId : x.LicenseId == p.LicenseId);
        var paidToDate = await sameTarget.Where(x => x.Date < p.Date || (x.Date == p.Date && x.PaymentId <= p.PaymentId)).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

        var number = string.IsNullOrWhiteSpace(p.InvoiceNo) ? $"RCPT-{p.PaymentId:D6}" : p.InvoiceNo!;
        var payer = string.IsNullOrWhiteSpace(p.CustomerNameSnapshot) ? tenant?.TenantName ?? "Customer" : p.CustomerNameSnapshot;
        var bytes = !pdf ? Array.Empty<byte>() : PaymentReceiptPdf.Build(new PaymentReceiptData(
            branding.CompanyName, branding.LogoDataUrl, branding.ReceiptTemplate, number, p.Date, payer, lines, p.Method, null,
            against, description, p.Amount, currency, total is > 0 ? total : null, total is > 0 ? paidToDate : null, p.Status));
        var safeName = Regex.Replace(number, @"[^A-Za-z0-9_-]", "_");
        return new BuiltReceipt(bytes, $"Receipt-{safeName}.pdf", number, email, payer, branding.CompanyName, p.Date, $"{currency} {p.Amount:N2}", against);
    }

    /// <summary>Optionally filtered by tenant, quotation, or license - e.g. a tenant's full payment history, or one quotation's installments.</summary>
    [HttpGet("payments")]
    public async Task<ActionResult<List<PaymentDto>>> GetPayments([FromQuery] int? tenantId, [FromQuery] int? quotationId, [FromQuery] int? licenseId)
    {
        var query = _db.Payments.AsNoTracking().AsQueryable();
        if (tenantId.HasValue) query = query.Where(p => p.TenantId == tenantId);
        if (quotationId.HasValue) query = query.Where(p => p.QuotationId == quotationId);
        if (licenseId.HasValue) query = query.Where(p => p.LicenseId == licenseId);

        var rows = await query.OrderByDescending(p => p.Date).ToListAsync();
        return Ok(await ToDtosAsync(rows));
    }

    /// <summary>Records one sale-side installment. Rejects an amount that is <= 0 or that would overshoot Quotation.GrandTotal - use Complete provisioning's error style, not silent clamping, so an over-collection is caught immediately.</summary>
    [HttpPost("payments/quotation/{quotationId:int}")]
    public async Task<ActionResult<PaymentDto>> RecordQuotationPayment(int quotationId, RecordQuotationPaymentDto dto)
    {
        var quotation = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(q => q.QuotationId == quotationId);
        if (quotation == null) return NotFound(new { message = "Quotation not found." });

        // Sale-side payments are taken against the quotation's invoice (Sales Pipeline ▸ Create invoice).
        if (!await _db.Invoices.AsNoTracking().AnyAsync(i => i.QuotationId == quotationId))
            return BadRequest(new { message = "Create the invoice for this quotation before recording a payment." });

        if (dto.Amount <= 0) return BadRequest(new { message = "Payment amount must be positive." });

        var paidSoFar = await _db.Payments.AsNoTracking().Where(p => p.QuotationId == quotationId).SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var balance = quotation.GrandTotal - paidSoFar;
        if (dto.Amount > balance)
        {
            return BadRequest(new { message = $"Amount exceeds the outstanding balance ({balance:N2})." });
        }

        var tenantId = await ResolveTenantIdForQuotationAsync(quotation.SalesEnquiryId);
        var entity = new Payment
        {
            TenantId = tenantId,
            QuotationId = quotationId,
            Amount = dto.Amount,
            Method = dto.Method,
            Status = "Completed",
            Date = DateTime.UtcNow,
            CustomerNameSnapshot = await ResolveCustomerNameAsync(tenantId, quotation.SalesEnquiryId),
        };
        _db.Payments.Add(entity);
        await SaveWithUniqueInvoiceNoAsync(entity, tenantId, quotationId: quotationId, licenseId: null);

        return Ok((await ToDtosAsync(new List<Payment> { entity }))[0]);
    }

    /// <summary>Records one renewal-side installment. Same over-collection guard as RecordQuotationPayment, against License.RenewalAmount instead.</summary>
    [HttpPost("payments/license/{licenseId:int}")]
    public async Task<ActionResult<PaymentDto>> RecordLicensePayment(int licenseId, RecordLicensePaymentDto dto)
    {
        var license = await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.LicenseId == licenseId);
        if (license == null) return NotFound(new { message = "License not found." });

        if (dto.Amount <= 0) return BadRequest(new { message = "Payment amount must be positive." });

        var paidSoFar = await _db.Payments.AsNoTracking().Where(p => p.LicenseId == licenseId).SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var balance = license.RenewalAmount - paidSoFar;
        if (dto.Amount > balance)
        {
            return BadRequest(new { message = $"Amount exceeds the outstanding balance ({balance:N2})." });
        }

        var licenseCustomerName = await _db.Tenants.AsNoTracking()
            .Where(t => t.TenantId == license.TenantId)
            .Select(t => t.TenantName)
            .FirstOrDefaultAsync();
        var entity = new Payment
        {
            TenantId = license.TenantId,
            LicenseId = licenseId,
            Amount = dto.Amount,
            Method = dto.Method,
            Status = "Completed",
            Date = DateTime.UtcNow,
            CustomerNameSnapshot = string.IsNullOrWhiteSpace(licenseCustomerName) ? "Unknown" : licenseCustomerName,
        };
        _db.Payments.Add(entity);
        await SaveWithUniqueInvoiceNoAsync(entity, license.TenantId, quotationId: null, licenseId: licenseId);

        return Ok((await ToDtosAsync(new List<Payment> { entity }))[0]);
    }

    /// <summary>Total / paid / balance for one Quotation - drives the sale-side "fully paid, safe to Convert to Tenant" check.</summary>
    [HttpGet("quotations/{id:int}/balance")]
    public async Task<ActionResult<BalanceDto>> GetQuotationBalance(int id)
    {
        var quotation = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(q => q.QuotationId == id);
        if (quotation == null) return NotFound();

        var paid = await _db.Payments.AsNoTracking().Where(p => p.QuotationId == id).SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var balance = quotation.GrandTotal - paid;
        return Ok(new BalanceDto { Total = quotation.GrandTotal, Paid = paid, Balance = balance, IsFullyPaid = balance <= 0 && paid > 0 });
    }

    /// <summary>Total / paid / balance for one License - drives the renewal-side "fully paid, safe to extend ExpiryDate" check.</summary>
    [HttpGet("licenses/{id:int}/balance")]
    public async Task<ActionResult<BalanceDto>> GetLicenseBalance(int id)
    {
        var license = await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.LicenseId == id);
        if (license == null) return NotFound();

        var paid = await _db.Payments.AsNoTracking().Where(p => p.LicenseId == id).SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var balance = license.RenewalAmount - paid;
        return Ok(new BalanceDto { Total = license.RenewalAmount, Paid = paid, Balance = balance, IsFullyPaid = balance <= 0 && paid > 0 });
    }

    /// <summary>Admin correction only (e.g. a mis-keyed amount) - not part of the normal flow, which never edits a recorded installment, only adds new ones.</summary>
    [HttpDelete("payments/{id:int}")]
    public async Task<IActionResult> DeletePayment(int id)
    {
        var entity = await _db.Payments.FirstOrDefaultAsync(p => p.PaymentId == id);
        if (entity == null) return NotFound();

        _db.Payments.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Chases Quotation -> SalesEnquiryId -> any SalesLeads row from that
    /// Enquiry -> a Tenant whose LeadId points at that Lead. Returns null
    /// until conversion happens - there is deliberately no fallback here,
    /// a Payment with TenantId still null just means "paid pre-conversion,
    /// not yet linked" rather than an error.
    /// </summary>
    private async Task<int?> ResolveTenantIdForQuotationAsync(int salesEnquiryId)
    {
        var leadIds = await _db.Leads.AsNoTracking()
            .Where(l => l.SalesEnquiryId == salesEnquiryId)
            .Select(l => l.LeadId)
            .ToListAsync();
        if (leadIds.Count == 0) return null;

        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.LeadId != null && leadIds.Contains(t.LeadId.Value));
        return tenant?.TenantId;
    }

    /// <summary>
    /// Billed-to name for a sale-side payment (Payment.CustomerNameSnapshot -
    /// a real NOT NULL column on the legacy dbo.Payments table). Prefers the
    /// resolved Tenant's name; falls back to the originating Enquiry's own
    /// CustomerNameSnapshot for a deposit collected before Lead-to-Tenant
    /// conversion (tenantId still null at that point). "Unknown" is a last
    /// resort only - it should never actually happen since every Quotation
    /// has an Enquiry.
    /// </summary>
    private async Task<string> ResolveCustomerNameAsync(int? tenantId, int salesEnquiryId)
    {
        if (tenantId.HasValue)
        {
            var tenantName = await _db.Tenants.AsNoTracking()
                .Where(t => t.TenantId == tenantId.Value)
                .Select(t => t.TenantName)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(tenantName)) return tenantName;
        }

        var enquiryName = await _db.Enquiries.AsNoTracking()
            .Where(e => e.SalesEnquiryId == salesEnquiryId)
            .Select(e => e.CustomerNameSnapshot)
            .FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(enquiryName) ? "Unknown" : enquiryName;
    }

    /// <summary>
    /// Payment.InvoiceNo = TenantCode + the Quotation/License's own numeric
    /// id (4-digit) + a 4-digit sequence - that SPECIFIC quotation/license's
    /// own running invoice count, not the tenant's overall count. A tenant
    /// with several quotations gets a fresh 0001 on each one instead of one
    /// shared number space (e.g. Ksv Tech's QuotationId 1 -> KSVTECH0001####;
    /// Praveen Softwares' QuotationId 3 -> PRAVEENSOFTW0003####, QuotationId
    /// 2 -> PRAVEENSOFTW0002####). Every payment still gets its own unique
    /// number - two payments against the same quotation still each mint a
    /// fresh one, never reused. Returns null when tenantId is null (a
    /// sale-side deposit collected before Lead-to-Tenant conversion has
    /// nothing to number against yet).
    ///
    /// The "count existing, use count+1" read here is NOT atomic under
    /// concurrent requests - two payments recorded close together for the
    /// same quotation/license can both read the same count before either
    /// commits, computing the same number (this is exactly what produced
    /// duplicate InvoiceNo values before this fix). The actual guarantee
    /// against that is the unique index on Payments.InvoiceNo (see
    /// alter-payments-invoiceno-unique-index.sql) plus
    /// SaveWithUniqueInvoiceNoAsync's retry-on-violation below - this method
    /// only picks the next LIKELY number, it never guarantees it alone.
    /// </summary>
    private async Task<string?> ResolveInvoiceNoAsync(int? tenantId, int? quotationId, int? licenseId)
    {
        if (!tenantId.HasValue) return null;

        var tenantCode = await _db.Tenants.AsNoTracking()
            .Where(t => t.TenantId == tenantId.Value)
            .Select(t => t.TenantCode)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(tenantCode)) tenantCode = "TEN";

        var refNo = (quotationId ?? licenseId ?? 0).ToString("D4");

        var query = _db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId.Value && p.InvoiceNo != null);
        query = quotationId.HasValue ? query.Where(p => p.QuotationId == quotationId) : query.Where(p => p.LicenseId == licenseId);
        var issuedSoFar = await query.CountAsync();

        return tenantCode + refNo + (issuedSoFar + 1).ToString("D4");
    }

    /// <summary>
    /// Assigns entity.InvoiceNo and saves it, retrying with a freshly
    /// resolved number if a concurrent request already claimed the one we
    /// picked. ResolveInvoiceNoAsync's read-then-use-count+1 isn't atomic on
    /// its own; this retry loop plus the unique index on Payments.InvoiceNo
    /// (alter-payments-invoiceno-unique-index.sql) is what actually makes a
    /// collision impossible instead of merely unlikely. entity must already
    /// be tracked (_db.Payments.Add(entity)) with everything else set.
    /// </summary>
    private async Task SaveWithUniqueInvoiceNoAsync(Payment entity, int? tenantId, int? quotationId, int? licenseId)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            entity.InvoiceNo = await ResolveInvoiceNoAsync(tenantId, quotationId, licenseId);
            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && IsInvoiceNoUniqueViolation(ex))
            {
                // Another request's payment grabbed this exact InvoiceNo
                // first - the database's unique index caught it before a
                // duplicate could be saved. Loop around and try the next one.
            }
        }
    }

    private static bool IsInvoiceNoUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627);

    /// <summary>Batches the TenantName/QuoteNo/LicenseKey lookups instead of querying per row - same pattern as PlatformQuotationsController's enquiryNames dictionary.</summary>
    private async Task<List<PaymentDto>> ToDtosAsync(List<Payment> rows)
    {
        var tenantIds = rows.Where(p => p.TenantId.HasValue).Select(p => p.TenantId!.Value).Distinct().ToList();
        var quotationIds = rows.Where(p => p.QuotationId.HasValue).Select(p => p.QuotationId!.Value).Distinct().ToList();
        var licenseIds = rows.Where(p => p.LicenseId.HasValue).Select(p => p.LicenseId!.Value).Distinct().ToList();

        var tenantNames = await _db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.TenantId)).ToDictionaryAsync(t => t.TenantId, t => t.TenantName);
        var quoteNos = await _db.Quotations.AsNoTracking().Where(q => quotationIds.Contains(q.QuotationId)).ToDictionaryAsync(q => q.QuotationId, q => q.QuoteNo);
        var licenseKeys = await _db.Licenses.AsNoTracking().Where(l => licenseIds.Contains(l.LicenseId)).ToDictionaryAsync(l => l.LicenseId, l => l.LicenseKey);

        return rows.Select(p => new PaymentDto
        {
            Id = p.PaymentId,
            TenantId = p.TenantId,
            TenantName = p.TenantId.HasValue ? tenantNames.GetValueOrDefault(p.TenantId.Value) : null,
            QuotationId = p.QuotationId,
            QuoteNo = p.QuotationId.HasValue ? quoteNos.GetValueOrDefault(p.QuotationId.Value) : null,
            LicenseId = p.LicenseId,
            LicenseKey = p.LicenseId.HasValue ? licenseKeys.GetValueOrDefault(p.LicenseId.Value) : null,
            Amount = p.Amount,
            Method = p.Method,
            Status = p.Status,
            Date = p.Date,
            InvoiceNo = p.InvoiceNo,
        }).ToList();
    }
}
