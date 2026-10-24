using System.Globalization;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>What one payment receipt shows - see PaymentReceiptPdf.</summary>
public sealed record PaymentReceiptData(
    string CompanyName,
    string? LogoDataUrl,
    string Template,
    string ReceiptNumber,
    DateTime PaymentDate,
    string ReceivedFrom,
    IReadOnlyList<string> CustomerLines,
    string Method,
    string? Reference,
    /// <summary>What it pays for, e.g. "Invoice INV-0042" / "Subscription Pro (Yearly)".</summary>
    string Against,
    string Description,
    decimal Amount,
    string Currency,
    decimal? Total,
    decimal? PaidToDate,
    string Status = "Completed",
    string? Notes = null);

/// <summary>Payment receipt PDF - one per payment, drawn with the chosen template (DocumentPdfRenderer).</summary>
public static class PaymentReceiptPdf
{
    public static byte[] Build(PaymentReceiptData d)
    {
        var meta = new List<(string, string)>
        {
            ("Payment Date", d.PaymentDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            ("Payment Method", d.Method),
        };
        if (!string.IsNullOrWhiteSpace(d.Reference)) meta.Add(("Reference", d.Reference!));
        if (!string.IsNullOrWhiteSpace(d.Against)) meta.Add(("Against", d.Against));

        var totals = new List<(string, string)>();
        if (d.Total is decimal total)
        {
            totals.Add(("Total Amount", M(d.Currency, total)));
            if (d.PaidToDate is decimal paid)
            {
                totals.Add(("Paid To Date", M(d.Currency, paid)));
                totals.Add(("Balance Due", M(d.Currency, Math.Max(0, total - paid))));
            }
        }

        // money received unless the payment is marked failed / refunded / pending (Business: Paid / Partial; Admin: Completed)
        var received = !new[] { "Failed", "Refunded", "Cancelled", "Pending" }.Contains(d.Status?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var fullyPaid = d.Total is decimal t && d.PaidToDate is decimal pd && pd >= t;
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Notes)) notes.Add(d.Notes!.Trim());
        if (!received) notes.Add($"Payment status: {d.Status}.");
        notes.Add("This is a computer generated receipt and needs no signature.");

        return DocumentPdfRenderer.Render(new PdfDocument
        {
            Template = DocumentTemplates.Normalize(d.Template),
            CompanyName = d.CompanyName,
            LogoDataUrl = d.LogoDataUrl,
            Title = "PAYMENT RECEIPT",
            NumberLabel = "Receipt No",
            Number = d.ReceiptNumber,
            Meta = meta,
            PartyHeading = "Received From",
            PartyLines = new[] { d.ReceivedFrom }.Concat(d.CustomerLines.Where(l => !string.IsNullOrWhiteSpace(l))).ToList(),
            Columns = new[] { new PdfColumn("#", 0.35), new PdfColumn("Description", 5), new PdfColumn("Amount", 1.4, true) },
            Rows = new[] { new[] { "1", d.Description, M(d.Currency, d.Amount) } },
            Totals = totals,
            Highlight = ("Amount Received", M(d.Currency, d.Amount)),
            NotesHeading = "Notes",
            Notes = notes,
            ThankYou = "Thank you for your payment.",
            Stamp = !received ? null : fullyPaid ? "PAID" : "RECEIVED",
        });
    }

    private static string M(string currency, decimal value) => $"{currency} {value.ToString("#,0.00", CultureInfo.InvariantCulture)}";
}
