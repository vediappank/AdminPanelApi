using System.Globalization;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Data for one Platform Panel quotation PDF. Same document as the Business
/// Panel's quotation PDF (BusinessPanelApi/Services/QuotationPdfBuilder.cs) -
/// only the customer fields and the Product column differ, because a
/// platform quotation is priced per Product (dbo.Applications) rather than
/// per service Category / Sub Category.
/// Template / LogoDataUrl come from Settings ▸ Branding (PlatformDocumentBranding).
/// </summary>
public sealed record QuotationPdfData(
    string CompanyName,
    string QuotationNumber,
    string CustomerName,
    string EnquiryNumber,
    string ContactLine,
    string ContactPerson,
    DateTime CreatedDate,
    DateTime? ValidTill,
    IReadOnlyList<QuotationPdfItem> Items,
    decimal Net,
    decimal Vat,
    decimal GrandTotal,
    string Currency = "AED",
    string DocumentTitle = "QUOTATION",
    string? Notes = null,
    // Labels so the same layout serves the invoice PDF (PlatformInvoicesController).
    string NumberLabel = "Quotation No",
    string ValidLabel = "Valid Till",
    string ReferenceLabel = "Enquiry No",
    IReadOnlyList<string>? Terms = null,
    string? Template = null,
    string? LogoDataUrl = null);

/// <summary>One service line. Amount and Vat are per unit; TaxableValue = Amount + Vat (per unit), so the line total is TaxableValue x Quantity - same convention as the Business Panel PDF.</summary>
public sealed record QuotationPdfItem(
    string ServiceName,
    string Product,
    int Quantity,
    decimal Amount,
    decimal Vat,
    decimal TaxableValue);

/// <summary>
/// Quotation / invoice PDF - turns QuotationPdfData into a PdfDocument and draws it with the chosen template
/// (Classic / Modern / Elegant / Minimal - see DocumentPdfRenderer).
/// </summary>
public static class QuotationPdfBuilder
{
    public static byte[] Build(QuotationPdfData data) => DocumentPdfRenderer.Render(ToDocument(data));

    public static PdfDocument ToDocument(QuotationPdfData data)
    {
        var isInvoice = string.Equals(data.DocumentTitle, "INVOICE", StringComparison.OrdinalIgnoreCase);
        var items = data.Items ?? Array.Empty<QuotationPdfItem>();
        var products = items.Select(i => i.Product?.Trim() ?? string.Empty).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var party = new List<string> { string.IsNullOrWhiteSpace(data.CustomerName) ? "-" : data.CustomerName };
        if (!string.IsNullOrWhiteSpace(data.ContactPerson)) party.Add("Contact Person: " + data.ContactPerson);
        if (!string.IsNullOrWhiteSpace(data.ContactLine)) party.Add(data.ContactLine);

        var terms = data.Terms is { Count: > 0 } custom
            ? custom.ToList()
            : isInvoice
                ? new List<string> { data.ValidTill is { } due ? $"Payment is due by {D(due)}." : "Payment is due on receipt of this invoice." }
                : new List<string> { "This quotation is prepared for the customer named above.", "Please review the quotation and confirm the required services before payment." };
        if (!string.IsNullOrWhiteSpace(data.Notes)) terms.Add("Notes: " + data.Notes!.Trim());

        return new PdfDocument
        {
            Template = DocumentTemplates.Normalize(data.Template),
            CompanyName = data.CompanyName,
            LogoDataUrl = data.LogoDataUrl,
            Title = data.DocumentTitle,
            NumberLabel = data.NumberLabel,
            Number = data.QuotationNumber,
            Meta = new[]
            {
                ("Date", D(data.CreatedDate)),
                (data.ValidLabel, data.ValidTill is { } v ? D(v) : "-"),
                (data.ReferenceLabel, string.IsNullOrWhiteSpace(data.EnquiryNumber) ? "-" : data.EnquiryNumber),
            },
            PartyHeading = isInvoice ? "Bill To" : "Quotation For",
            PartyLines = party,
            TableCaption = products.Count == 0 ? null : (products.Count > 1 ? "Products: " : "Product: ") + string.Join(", ", products),
            Columns = new[]
            {
                new PdfColumn("#", 0.35),
                new PdfColumn("Service Description", 4.2),
                new PdfColumn("Qty", 0.55, true),
                new PdfColumn("Amount", 1.0, true),
                new PdfColumn("VAT", 0.85, true),
                new PdfColumn("Total", 1.15, true),
            },
            Rows = items.Select((item, i) => new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                item.ServiceName,
                item.Quantity.ToString(CultureInfo.InvariantCulture),
                Money(item.Amount),
                Money(item.Vat),
                Money(item.TaxableValue * item.Quantity),
            }).ToList(),
            Totals = new[] { ("Sub Total", Money(data.Net)), ("VAT", Money(data.Vat)) },
            GrandTotal = ("Grand Total", $"{data.Currency} {Money(data.GrandTotal)}"),
            NotesHeading = isInvoice ? "Payment Notes" : "Terms & Conditions",
            Notes = terms,
        };
    }

    private static string D(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string Money(decimal value) => value.ToString("#,0.00", CultureInfo.InvariantCulture);
}
