using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Reports ▸ Sales &amp; Collections - one consolidated report for the sales team: pipeline (enquiries, leads,
/// quotations), billing (invoices, subscriptions), collections (payments) and what is still outstanding with its age,
/// per salesperson, source, plan and month.
///   GET api/platform/reports/sales-consolidated?from=2026-01-01&amp;to=2026-10-20&amp;employeeId=
/// Pipeline and billing figures are for the period; "outstanding" is as of today. With employeeId, everything is
/// limited to that salesperson's enquiries, the tenants converted from them and their payments.
/// A payment / tenant belongs to the salesperson of its enquiry (quotation payments) or of the lead the tenant was
/// converted from (subscription payments).
/// Menu: SALES_CONSOLIDATED_REPORT (DatabaseScripts/SalesReport_SuperAdminPanel.sql).
/// </summary>
[ApiController]
[Route("api/platform/reports")]
[RequirePlatformModule("SALES_CONSOLIDATED_REPORT")]
public class PlatformSalesReportController : ControllerBase
{
    private const string Unassigned = "Unassigned";
    private readonly PlatformDbContext _db;

    public PlatformSalesReportController(PlatformDbContext db) => _db = db;

    // ------------------------------------------------------------------ DTOs

    public sealed class Kpis
    {
        public int Enquiries { get; set; }
        public decimal PipelineValue { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public decimal WinRate { get; set; }
        public int Leads { get; set; }
        public int NewTenants { get; set; }
        public int Quotations { get; set; }
        public decimal QuotedValue { get; set; }
        public int AcceptedQuotations { get; set; }
        public int Invoices { get; set; }
        public decimal Invoiced { get; set; }
        public decimal SubscriptionBilled { get; set; }
        public decimal Collected { get; set; }
        public int Payments { get; set; }
        public decimal Outstanding { get; set; }
        public decimal Overdue { get; set; }
        public int ActiveSubscriptions { get; set; }
        public int NewSubscriptions { get; set; }
        public int Renewals { get; set; }
        public int Cancellations { get; set; }
        public decimal Mrr { get; set; }
        public decimal Arr { get; set; }
    }

    public sealed record MonthRow(string Month, int Enquiries, decimal PipelineValue, decimal Invoiced, decimal Collected, int NewSubscriptions);

    public sealed class PersonRow
    {
        public int? EmployeeId { get; set; }
        public string Name { get; set; } = Unassigned;
        public int Enquiries { get; set; }
        public decimal PipelineValue { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public int Leads { get; set; }
        public int Quotations { get; set; }
        public decimal QuotedValue { get; set; }
        public decimal Invoiced { get; set; }
        public decimal Collected { get; set; }
        public decimal Outstanding { get; set; }
        public decimal WinRate => Won + Lost == 0 ? 0 : Math.Round(Won * 100m / (Won + Lost), 1);
    }

    public sealed record SourceRow(string Source, int Enquiries, decimal Value, int Won);
    public sealed record PlanRow(string Plan, int Active, int Monthly, int Yearly, decimal Mrr);
    public sealed record MethodRow(string Method, int Count, decimal Amount);
    public sealed record CustomerRow(string Customer, int? TenantId, decimal Collected, decimal Outstanding);

    public sealed class OutstandingRow
    {
        public string Kind { get; set; } = "Invoice";   // Invoice / Subscription
        public string Reference { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public int? TenantId { get; set; }
        public string Salesperson { get; set; } = Unassigned;
        public decimal Billed { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        public DateTime DueDate { get; set; }
        /// <summary>Days past the due date (negative = not due yet).</summary>
        public int DaysOverdue { get; set; }
        /// <summary>Not due / 0-30 / 31-60 / 61-90 / 90+</summary>
        public string Bucket { get; set; } = "Not due";
    }

    public sealed record PaymentRow(int Id, DateTime Date, string Customer, string Reference, string Kind, string Method, decimal Amount, string Salesperson, string? InvoiceNo);
    public sealed record AgeingRow(string Bucket, int Count, decimal Amount);
    public sealed record Person(int? Id, string Name);

    public sealed class ConsolidatedReport
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public DateTime AsOf { get; set; }
        public string Currency { get; set; } = "AED";
        public int? EmployeeId { get; set; }
        public Kpis Kpis { get; set; } = new();
        public List<MonthRow> Monthly { get; set; } = new();
        public List<PersonRow> BySalesperson { get; set; } = new();
        public List<SourceRow> BySource { get; set; } = new();
        public List<PlanRow> ByPlan { get; set; } = new();
        public List<MethodRow> ByMethod { get; set; } = new();
        public List<AgeingRow> Ageing { get; set; } = new();
        public List<OutstandingRow> Outstanding { get; set; } = new();
        public List<CustomerRow> TopCustomers { get; set; } = new();
        public List<PaymentRow> Payments { get; set; } = new();
        /// <summary>Everyone with enquiries or leads - for the salesperson filter.</summary>
        public List<Person> Salespeople { get; set; } = new();
    }

    // ------------------------------------------------------------------ report

    [HttpGet("sales-consolidated")]
    public async Task<ActionResult<ConsolidatedReport>> Consolidated([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? employeeId, CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from ?? new DateTime(today.Year, 1, 1)).Date;
        var end = (to ?? today).Date;
        if (end < start) (start, end) = (end, start);
        var endExcl = end.AddDays(1);
        bool InPeriod(DateTime d) => d >= start && d < endExcl;

        // ---- load (the sales / billing tables are small; everything is joined in memory)
        var enquiries = await _db.Enquiries.AsNoTracking()
            .Select(e => new { e.SalesEnquiryId, e.EnquiryDate, e.CreatedDate, e.CustomerNameSnapshot, e.Source, e.EstimatedAmount, e.AssignedToEmployeeId, e.AssignedToEmployeeNameSnapshot, e.Status, e.IsActive })
            .ToListAsync(ct);
        var leads = await _db.Leads.AsNoTracking()
            .Select(l => new { l.LeadId, l.SalesEnquiryId, l.EmployeeId, l.EmployeeNameSnapshot, l.ConvertedDate, l.CreatedDate })
            .ToListAsync(ct);
        var quotations = await _db.Quotations.AsNoTracking()
            .Select(q => new { q.QuotationId, q.SalesEnquiryId, q.QuoteNo, q.QuoteDate, q.Status, q.GrandTotal })
            .ToListAsync(ct);
        var invoices = await _db.Invoices.AsNoTracking()
            .Select(i => new { i.InvoiceId, i.InvoiceNumber, i.QuotationId, i.InvoiceDate, i.DueDate, i.GrandTotal, i.Status })
            .ToListAsync(ct);
        var payments = await _db.Payments.AsNoTracking().ToListAsync(ct);
        var tenants = await _db.Tenants.AsNoTracking()
            .Select(t => new { t.TenantId, t.TenantName, t.LeadId, t.CreatedDate })
            .ToListAsync(ct);
        var licenses = await _db.Licenses.AsNoTracking()
            .Select(l => new { l.LicenseId, l.TenantId, l.LicenseKey, l.RenewalAmount, l.StartDate })
            .ToListAsync(ct);
        List<TenantSubscription> subs;
        try
        {
            subs = await _db.TenantSubscriptions.AsNoTracking().Include(s => s.Plan).ToListAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 208 or 207)
        {
            subs = new();   // Subscriptions not installed yet
        }

        // ---- who sells what
        var enquiryById = enquiries.ToDictionary(e => e.SalesEnquiryId);
        var leadById = leads.ToDictionary(l => l.LeadId);
        var quoteById = quotations.ToDictionary(q => q.QuotationId);
        var tenantById = tenants.ToDictionary(t => t.TenantId);
        var licenseById = licenses.ToDictionary(l => l.LicenseId);

        Person OfEnquiry(int? enquiryId) =>
            enquiryId is int id && enquiryById.TryGetValue(id, out var e) && (e.AssignedToEmployeeId != null || !string.IsNullOrWhiteSpace(e.AssignedToEmployeeNameSnapshot))
                ? new Person(e.AssignedToEmployeeId, Clean(e.AssignedToEmployeeNameSnapshot))
                : new Person(null, Unassigned);
        Person OfTenant(int? tenantId)
        {
            if (tenantId is not int tid || !tenantById.TryGetValue(tid, out var t) || t.LeadId is not int lid || !leadById.TryGetValue(lid, out var lead))
                return new Person(null, Unassigned);
            if (lead.EmployeeId != null || !string.IsNullOrWhiteSpace(lead.EmployeeNameSnapshot)) return new Person(lead.EmployeeId, Clean(lead.EmployeeNameSnapshot));
            return OfEnquiry(lead.SalesEnquiryId);
        }
        Person OfPayment(Payment p) =>
            p.QuotationId is int qid && quoteById.TryGetValue(qid, out var q) ? OfEnquiry(q.SalesEnquiryId) : OfTenant(p.TenantId ?? (p.LicenseId is int l && licenseById.TryGetValue(l, out var lic) ? lic.TenantId : null));
        bool Mine(Person p) => employeeId == null || p.Id == employeeId;

        // ---- pipeline
        var periodEnquiries = enquiries.Where(e => InPeriod(e.EnquiryDate) && Mine(OfEnquiry(e.SalesEnquiryId))).ToList();
        var periodLeads = leads.Where(l => InPeriod(l.ConvertedDate) && Mine(OfLead(l.EmployeeId, l.EmployeeNameSnapshot, l.SalesEnquiryId))).ToList();
        Person OfLead(int? emp, string? name, int enquiryId) => emp != null || !string.IsNullOrWhiteSpace(name) ? new Person(emp, Clean(name)) : OfEnquiry(enquiryId);
        var periodQuotes = quotations.Where(q => InPeriod(q.QuoteDate) && Mine(OfEnquiry(q.SalesEnquiryId))).ToList();
        var periodInvoices = invoices.Where(i => InPeriod(i.InvoiceDate) && quoteById.TryGetValue(i.QuotationId, out var q) && Mine(OfEnquiry(q.SalesEnquiryId))).ToList();
        var periodPayments = payments.Where(p => InPeriod(p.Date) && !IsVoid(p.Status) && Mine(OfPayment(p))).ToList();
        var periodTenants = tenants.Where(t => InPeriod(t.CreatedDate) && Mine(OfTenant(t.TenantId))).ToList();
        var mySubs = subs.Where(s => Mine(OfTenant(s.TenantId))).ToList();

        var report = new ConsolidatedReport { From = start, To = end, AsOf = today, EmployeeId = employeeId };
        report.Currency = mySubs.Select(s => s.Currency).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "AED";
        var k = report.Kpis;
        k.Enquiries = periodEnquiries.Count;
        k.PipelineValue = periodEnquiries.Sum(e => e.EstimatedAmount);
        k.Won = periodEnquiries.Count(e => IsWon(e.Status));
        k.Lost = periodEnquiries.Count(e => IsLost(e.Status));
        k.WinRate = k.Won + k.Lost == 0 ? 0 : Math.Round(k.Won * 100m / (k.Won + k.Lost), 1);
        k.Leads = periodLeads.Count;
        k.NewTenants = periodTenants.Count;
        k.Quotations = periodQuotes.Count;
        k.QuotedValue = periodQuotes.Sum(q => q.GrandTotal);
        k.AcceptedQuotations = periodQuotes.Count(q => string.Equals(q.Status, "Accepted", StringComparison.OrdinalIgnoreCase));
        k.Invoices = periodInvoices.Count;
        k.Invoiced = periodInvoices.Sum(i => i.GrandTotal);
        k.SubscriptionBilled = mySubs.Where(s => InPeriod(s.StartDate)).Sum(s => s.Price);
        k.Collected = periodPayments.Sum(p => p.Amount);
        k.Payments = periodPayments.Count;
        var active = mySubs.Where(s => s.Status == SubscriptionStatuses.Active).ToList();
        k.ActiveSubscriptions = active.Count;
        k.NewSubscriptions = mySubs.Count(s => s.RenewedFromId == null && InPeriod(s.StartDate) && s.Status != SubscriptionStatuses.Superseded);
        k.Renewals = mySubs.Count(s => s.RenewedFromId != null && InPeriod(s.StartDate));
        k.Cancellations = mySubs.Count(s => s.Status == SubscriptionStatuses.Cancelled && s.CancelledDate is DateTime c && InPeriod(c.ToLocalTime()));
        k.Mrr = Math.Round(active.Sum(MonthlyValue), 2);
        k.Arr = Math.Round(k.Mrr * 12, 2);

        // ---- outstanding (as of today)
        var outstanding = new List<OutstandingRow>();
        foreach (var inv in invoices)
        {
            if (!quoteById.TryGetValue(inv.QuotationId, out var q)) continue;
            if (string.Equals(inv.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) || string.Equals(inv.Status, "Void", StringComparison.OrdinalIgnoreCase)) continue;
            var person = OfEnquiry(q.SalesEnquiryId);
            if (!Mine(person)) continue;
            var paid = payments.Where(p => p.QuotationId == inv.QuotationId && !IsVoid(p.Status)).Sum(p => p.Amount);
            var balance = inv.GrandTotal - paid;
            if (balance <= 0.004m) continue;
            var tenantId = payments.Where(p => p.QuotationId == inv.QuotationId).Select(p => p.TenantId).FirstOrDefault(t => t != null);
            outstanding.Add(Row("Invoice", inv.InvoiceNumber, enquiryById.TryGetValue(q.SalesEnquiryId, out var e) ? e.CustomerNameSnapshot : "Customer",
                tenantId, person.Name, inv.GrandTotal, paid, inv.DueDate ?? inv.InvoiceDate, today));
        }
        foreach (var lic in licenses)
        {
            if (lic.RenewalAmount <= 0) continue;
            var person = OfTenant(lic.TenantId);
            if (!Mine(person)) continue;
            var paid = payments.Where(p => p.LicenseId == lic.LicenseId && !IsVoid(p.Status)).Sum(p => p.Amount);
            var balance = lic.RenewalAmount - paid;
            if (balance <= 0.004m) continue;
            // due from the start of the newest term billed on this license
            var due = subs.Where(s => s.LicenseId == lic.LicenseId && s.Price > 0).OrderByDescending(s => s.StartDate).Select(s => (DateTime?)s.StartDate).FirstOrDefault() ?? lic.StartDate;
            outstanding.Add(Row("Subscription", lic.LicenseKey, tenantById.TryGetValue(lic.TenantId, out var t) ? t.TenantName : "Tenant",
                lic.TenantId, person.Name, lic.RenewalAmount, paid, due, today));
        }
        report.Outstanding = outstanding.OrderByDescending(o => o.DaysOverdue).ThenByDescending(o => o.Balance).ToList();
        k.Outstanding = outstanding.Sum(o => o.Balance);
        k.Overdue = outstanding.Where(o => o.DaysOverdue > 0).Sum(o => o.Balance);
        report.Ageing = new[] { "Not due", "0-30", "31-60", "61-90", "90+" }
            .Select(b => new AgeingRow(b, outstanding.Count(o => o.Bucket == b), outstanding.Where(o => o.Bucket == b).Sum(o => o.Balance)))
            .ToList();

        // ---- by month (12 months up to the end of the period)
        var lastMonth = new DateTime(end.Year, end.Month, 1);
        for (var m = lastMonth.AddMonths(-11); m <= lastMonth; m = m.AddMonths(1))
        {
            var next = m.AddMonths(1);
            bool InMonth(DateTime d) => d >= m && d < next;
            report.Monthly.Add(new MonthRow(
                m.ToString("yyyy-MM"),
                enquiries.Count(e => InMonth(e.EnquiryDate) && Mine(OfEnquiry(e.SalesEnquiryId))),
                enquiries.Where(e => InMonth(e.EnquiryDate) && Mine(OfEnquiry(e.SalesEnquiryId))).Sum(e => e.EstimatedAmount),
                invoices.Where(i => InMonth(i.InvoiceDate) && quoteById.TryGetValue(i.QuotationId, out var q) && Mine(OfEnquiry(q.SalesEnquiryId))).Sum(i => i.GrandTotal)
                    + mySubs.Where(s => InMonth(s.StartDate)).Sum(s => s.Price),
                payments.Where(p => InMonth(p.Date) && !IsVoid(p.Status) && Mine(OfPayment(p))).Sum(p => p.Amount),
                mySubs.Count(s => s.RenewedFromId == null && InMonth(s.StartDate) && s.Status != SubscriptionStatuses.Superseded)));
        }

        // ---- by salesperson
        var people = new Dictionary<string, PersonRow>(StringComparer.OrdinalIgnoreCase);
        PersonRow P(Person p)
        {
            var key = p.Id?.ToString() ?? "n:" + p.Name;
            if (!people.TryGetValue(key, out var row)) people[key] = row = new PersonRow { EmployeeId = p.Id, Name = p.Name };
            return row;
        }
        foreach (var e in periodEnquiries)
        {
            var r = P(OfEnquiry(e.SalesEnquiryId));
            r.Enquiries++;
            r.PipelineValue += e.EstimatedAmount;
            if (IsWon(e.Status)) r.Won++;
            if (IsLost(e.Status)) r.Lost++;
        }
        foreach (var l in periodLeads) P(OfLead(l.EmployeeId, l.EmployeeNameSnapshot, l.SalesEnquiryId)).Leads++;
        foreach (var q in periodQuotes) { var r = P(OfEnquiry(q.SalesEnquiryId)); r.Quotations++; r.QuotedValue += q.GrandTotal; }
        foreach (var i in periodInvoices) P(OfEnquiry(quoteById[i.QuotationId].SalesEnquiryId)).Invoiced += i.GrandTotal;
        foreach (var s in mySubs.Where(s => InPeriod(s.StartDate))) P(OfTenant(s.TenantId)).Invoiced += s.Price;
        foreach (var p in periodPayments) P(OfPayment(p)).Collected += p.Amount;
        foreach (var o in outstanding)
        {
            var match = people.Values.FirstOrDefault(x => string.Equals(x.Name, o.Salesperson, StringComparison.OrdinalIgnoreCase));
            (match ?? P(new Person(null, o.Salesperson))).Outstanding += o.Balance;
        }
        report.BySalesperson = people.Values.OrderByDescending(x => x.Collected).ThenByDescending(x => x.PipelineValue).ToList();

        // ---- by source / plan / method / customer
        report.BySource = periodEnquiries
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Source) ? "Not set" : e.Source!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new SourceRow(g.Key, g.Count(), g.Sum(e => e.EstimatedAmount), g.Count(e => IsWon(e.Status))))
            .OrderByDescending(r => r.Enquiries).ToList();
        report.ByPlan = active
            .GroupBy(s => s.Plan?.PlanName ?? "Plan")
            .Select(g => new PlanRow(g.Key, g.Count(), g.Count(s => s.BillingCycle == SubscriptionCycles.Monthly), g.Count(s => s.BillingCycle == SubscriptionCycles.Yearly), Math.Round(g.Sum(MonthlyValue), 2)))
            .OrderByDescending(r => r.Mrr).ToList();
        report.ByMethod = periodPayments
            .GroupBy(p => p.Method)
            .Select(g => new MethodRow(g.Key, g.Count(), g.Sum(p => p.Amount)))
            .OrderByDescending(r => r.Amount).ToList();
        report.TopCustomers = periodPayments
            .GroupBy(p => p.TenantId?.ToString() ?? "c:" + p.CustomerNameSnapshot)
            .Select(g =>
            {
                var tid = g.First().TenantId;
                var name = tid is int t && tenantById.TryGetValue(t, out var tn) ? tn.TenantName : g.First().CustomerNameSnapshot;
                return new CustomerRow(name, tid, g.Sum(p => p.Amount), outstanding.Where(o => tid != null ? o.TenantId == tid : o.Customer == name).Sum(o => o.Balance));
            })
            .OrderByDescending(r => r.Collected).Take(10).ToList();

        report.Payments = periodPayments
            .OrderByDescending(p => p.Date)
            .Take(1000)
            .Select(p =>
            {
                var kind = p.QuotationId != null ? "Sale" : p.LicenseId != null ? "Subscription" : "Other";
                var reference = p.QuotationId is int qid && quoteById.TryGetValue(qid, out var q) ? q.QuoteNo ?? $"Q-{qid}"
                    : p.LicenseId is int lid && licenseById.TryGetValue(lid, out var lic) ? lic.LicenseKey : "-";
                var customer = p.TenantId is int t && tenantById.TryGetValue(t, out var tn) ? tn.TenantName : p.CustomerNameSnapshot;
                return new PaymentRow(p.PaymentId, p.Date, customer, reference, kind, p.Method, p.Amount, OfPayment(p).Name, p.InvoiceNo);
            })
            .ToList();

        // ---- salesperson filter list (everyone, not only this period)
        report.Salespeople = enquiries.Where(e => e.AssignedToEmployeeId != null)
            .Select(e => new Person(e.AssignedToEmployeeId, Clean(e.AssignedToEmployeeNameSnapshot)))
            .Concat(leads.Where(l => l.EmployeeId != null).Select(l => new Person(l.EmployeeId, Clean(l.EmployeeNameSnapshot))))
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .OrderBy(p => p.Name)
            .ToList();

        return Ok(report);
    }

    // ------------------------------------------------------------------ helpers

    private static OutstandingRow Row(string kind, string reference, string customer, int? tenantId, string person, decimal billed, decimal paid, DateTime due, DateTime today)
    {
        var days = (int)(today - due.Date).TotalDays;
        return new OutstandingRow
        {
            Kind = kind, Reference = reference, Customer = customer, TenantId = tenantId, Salesperson = person,
            Billed = billed, Paid = paid, Balance = billed - paid, DueDate = due.Date, DaysOverdue = days,
            Bucket = days < 0 ? "Not due" : days <= 30 ? "0-30" : days <= 60 ? "31-60" : days <= 90 ? "61-90" : "90+",
        };
    }

    private static decimal MonthlyValue(TenantSubscription s) => s.BillingCycle == SubscriptionCycles.Yearly ? s.Price / 12m : s.Price;

    private static bool IsWon(string? status) => status is not null &&
        (status.Equals("Won", StringComparison.OrdinalIgnoreCase) || status.Equals("Converted", StringComparison.OrdinalIgnoreCase));

    private static bool IsLost(string? status) => status is not null && status.Equals("Lost", StringComparison.OrdinalIgnoreCase);

    private static bool IsVoid(string? status) => status is not null &&
        (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || status.Equals("Refunded", StringComparison.OrdinalIgnoreCase)
         || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) || status.Equals("Void", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? name) => string.IsNullOrWhiteSpace(name) ? Unassigned : name.Trim();
}
