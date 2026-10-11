using System.Globalization;
using System.Text;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Data for one Platform Panel quotation PDF. Same document as the Business
/// Panel's quotation PDF (BusinessPanelApi/Services/QuotationPdfBuilder.cs) -
/// only the customer fields and the Product column differ, because a
/// platform quotation is priced per Product (dbo.Applications) rather than
/// per service Category / Sub Category.
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
    IReadOnlyList<string>? Terms = null);

/// <summary>One service line. Amount and Vat are per unit; TaxableValue = Amount + Vat (per unit), so the line total is TaxableValue x Quantity - same convention as the Business Panel PDF.</summary>
public sealed record QuotationPdfItem(
    string ServiceName,
    string Product,
    int Quantity,
    decimal Amount,
    decimal Vat,
    decimal TaxableValue);

/// <summary>
/// Ported from the Business Panel (BusinessPanelApi/Services/QuotationPdfBuilder.cs)
/// so both panels hand customers the same quotation document.
/// Dependency-free quotation PDF renderer. The layout is intentionally designed to
/// match the customer-facing quotation/print view: branded header, quotation details,
/// item table, totals, terms and footer. All text is converted to PDF-safe ASCII so
/// the built-in Helvetica font remains portable.
/// </summary>
public static class QuotationPdfBuilder
{
    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double Margin = 40;
    private const double ContentWidth = PageWidth - (Margin * 2);

    public static byte[] Build(QuotationPdfData data)
    {
        var pages = BuildPages(data);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [ {string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + i * 2} 0 R"))} ] /Count {pages.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        for (var i = 0; i < pages.Count; i++)
        {
            var pageObject = 4 + i * 2;
            var contentObject = pageObject + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth.ToString(CultureInfo.InvariantCulture)} {PageHeight.ToString(CultureInfo.InvariantCulture)}] /Resources << /ProcSet [/PDF /Text] /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>");
            var stream = pages[i];
            var bytes = Encoding.ASCII.GetBytes(stream);
            objects.Add($"<< /Length {bytes.Length} >>\nstream\n{stream}\nendstream");
        }

        using var output = new MemoryStream();
        var offsets = new List<long> { 0 };
        WriteAscii(output, "%PDF-1.4\n%PDF-PlatformPanel-Quotation\n");

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteAscii(output, $"{offsets[i]:D10} 00000 n \n");

        WriteAscii(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static List<string> BuildPages(QuotationPdfData data)
    {
        var pages = new List<string>();
        // Lines stay in quotation order, grouped by Product (the order products were added).
        var items = (data.Items ?? Array.Empty<QuotationPdfItem>()).ToList();
        const int rowsPerPage = 11;

        if (items.Count == 0)
        {
            pages.Add(BuildPage(data, Array.Empty<QuotationPdfItem>(), 1, 1));
            return pages;
        }

        var pageCount = (int)Math.Ceiling(items.Count / (double)rowsPerPage);
        for (var page = 0; page < pageCount; page++)
        {
            var pageItems = items.Skip(page * rowsPerPage).Take(rowsPerPage).ToList();
            pages.Add(BuildPage(data, pageItems, page + 1, pageCount));
        }
        return pages;
    }

    private static string BuildPage(QuotationPdfData data, IReadOnlyList<QuotationPdfItem> items, int pageNumber, int pageCount)
    {
        var pdf = new StringBuilder();

        // Background and brand header.
        FillRect(pdf, 0, 0, PageWidth, PageHeight, 0.985, 0.99, 0.998);
        FillRect(pdf, 0, PageHeight - 92, PageWidth, 92, 0.075, 0.245, 0.42);
        FillRect(pdf, Margin, PageHeight - 73, 42, 42, 0.14, 0.39, 0.68);
        Text(pdf, "B", Margin + 13, PageHeight - 59, 18, 1, 1, 1, bold: true);
        Text(pdf, Safe(data.CompanyName), Margin + 52, PageHeight - 45, 17, 1, 1, 1, bold: true);
        Text(pdf, "Business & Professional Services", Margin + 52, PageHeight - 62, 8.5, 0.82, 0.9, 0.97);
        Text(pdf, Safe(data.DocumentTitle), 425, PageHeight - 47, 18, 1, 1, 1, bold: true);
        Text(pdf, $"Page {pageNumber} of {pageCount}", 478, PageHeight - 67, 7.5, 0.82, 0.9, 0.97);

        // Customer / quotation information card.
        var infoTop = PageHeight - 116;
        const double infoHeight = 96;
        StrokeRoundRect(pdf, Margin, infoTop - infoHeight, ContentWidth, infoHeight, 0.84, 0.89, 0.94);
        FillRoundRect(pdf, Margin + 1, infoTop - 77, ContentWidth - 2, 26, 0.94, 0.97, 0.995);
        Text(pdf, Safe(data.DocumentTitle) + " DETAILS", Margin + 12, infoTop - 17, 8, 0.13, 0.32, 0.52, bold: true);

        Text(pdf, Safe(data.NumberLabel), Margin + 12, infoTop - 42, 7, 0.48, 0.55, 0.62);
        Text(pdf, Safe(data.QuotationNumber), Margin + 12, infoTop - 55, 10, 0.08, 0.23, 0.38, bold: true);
        Text(pdf, "Date", 195, infoTop - 42, 7, 0.48, 0.55, 0.62);
        Text(pdf, data.CreatedDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), 195, infoTop - 55, 10, 0.08, 0.23, 0.38, bold: true);
        Text(pdf, "Customer", 300, infoTop - 42, 7, 0.48, 0.55, 0.62);
        Text(pdf, Shorten(data.CustomerName, 22), 300, infoTop - 55, 10, 0.08, 0.23, 0.38, bold: true);
        Text(pdf, Safe(data.ReferenceLabel), 425, infoTop - 42, 7, 0.48, 0.55, 0.62);
        Text(pdf, Safe(string.IsNullOrWhiteSpace(data.EnquiryNumber) ? "-" : data.EnquiryNumber), 425, infoTop - 55, 10, 0.08, 0.23, 0.38, bold: true);

        Text(pdf, Safe(data.ValidLabel), Margin + 12, infoTop - 70, 7, 0.48, 0.55, 0.62);
        Text(pdf, data.ValidTill is { } valid ? valid.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "-", Margin + 52, infoTop - 70, 8.5, 0.08, 0.23, 0.38, bold: true);
        Text(pdf, "Contact Person", 195, infoTop - 70, 7, 0.48, 0.55, 0.62);
        Text(pdf, Shorten(string.IsNullOrWhiteSpace(data.ContactPerson) ? "-" : data.ContactPerson, 40), 252, infoTop - 70, 8.5, 0.08, 0.23, 0.38, bold: true);
        Text(pdf, "Contact: " + Shorten(string.IsNullOrWhiteSpace(data.ContactLine) ? "-" : data.ContactLine, 110), Margin + 12, infoTop - 86, 7.5, 0.32, 0.42, 0.5);

        // Product label (shown once, above the table, instead of a Product column).
        var productNames = (data.Items ?? Array.Empty<QuotationPdfItem>())
            .Select(i => i.Product?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var productLabel = productNames.Count > 1 ? "PRODUCTS" : "PRODUCT";
        Text(pdf, productLabel, Margin, infoTop - 116, 7.5, 0.48, 0.55, 0.62, bold: true);
        Text(pdf, Shorten(productNames.Count == 0 ? "-" : string.Join(", ", productNames), 85), Margin + (productNames.Count > 1 ? 52 : 46), infoTop - 116, 10, 0.08, 0.23, 0.38, bold: true);

        // Item table.
        var tableTop = infoTop - 128;
        var headerHeight = 28;
        var rowHeight = 31;
        // Same table as the Business Panel PDF, minus its Category / Sub Category columns - the
        // product is named once in the label above the table instead.
        var cols = new[] { 24d, 288d, 36d, 58d, 45d, 64d };
        const int rightFrom = 2;   // first right-aligned column
        var x = Margin;
        var headers = new[] { "#", "SERVICE DESCRIPTION", "QTY", "AMOUNT", "VAT", "TOTAL" };
        var headerY = tableTop - headerHeight;
        FillRect(pdf, Margin, headerY, ContentWidth, headerHeight, 0.09, 0.30, 0.50);
        for (var c = 0; c < cols.Length; c++)
        {
            Text(pdf, headers[c], x + 5, headerY + 10, c == 0 ? 7 : 6.4, 1, 1, 1, bold: true);
            x += cols[c];
        }

        var rowY = headerY - rowHeight;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var fill = i % 2 == 0 ? new[] { 1d, 1d, 1d } : new[] { 0.965, 0.98, 0.992 };
            FillRect(pdf, Margin, rowY, ContentWidth, rowHeight, fill[0], fill[1], fill[2]);
            StrokeRect(pdf, Margin, rowY, ContentWidth, rowHeight, 0.84, 0.89, 0.94);

            var total = item.TaxableValue * item.Quantity;
            x = Margin;
            var values = new[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Safe(item.ServiceName),
                item.Quantity.ToString(CultureInfo.InvariantCulture),
                Money(item.Amount),
                Money(item.Vat),
                Money(total)
            };

            for (var c = 0; c < cols.Length; c++)
            {
                var alignRight = c >= rightFrom;
                var lines = Wrap(values[c], c switch { 1 => 64, _ => 12 });
                var font = c == 0 ? 7.5 : 6.8;
                var startY = rowY + rowHeight - 11;
                foreach (var line in lines.Take(2))
                {
                    var tx = alignRight ? x + cols[c] - 5 - TextWidth(line, font) : x + 5;
                    Text(pdf, line, tx, startY, font, 0.16, 0.25, 0.34, bold: c == 0 || c >= rightFrom);
                    startY -= 9;
                }
                x += cols[c];
            }
            rowY -= rowHeight;
        }

        // Fill unused table area for a clean print layout.
        var usedRows = Math.Max(items.Count, 1);
        var tableBottom = headerY - (usedRows * rowHeight);
        StrokeRect(pdf, Margin, tableBottom, ContentWidth, headerHeight + usedRows * rowHeight, 0.78, 0.85, 0.91);

        // Totals card only on final page.
        if (pageNumber == pageCount)
        {
            var totalsX = 355d;
            var totalsW = 200d;
            var totalsY = Math.Max(tableBottom - 18, 145);
            StrokeRoundRect(pdf, totalsX, totalsY - 93, totalsW, 93, 0.82, 0.87, 0.93);
            Text(pdf, "TOTAL SUMMARY", totalsX + 12, totalsY - 17, 8, 0.10, 0.31, 0.52, bold: true);
            Text(pdf, "Sub Total", totalsX + 12, totalsY - 39, 8, 0.36, 0.43, 0.5);
            Text(pdf, Money(data.Net), totalsX + 125, totalsY - 39, 9, 0.12, 0.2, 0.29, bold: true);
            Text(pdf, "VAT", totalsX + 12, totalsY - 58, 8, 0.36, 0.43, 0.5);
            Text(pdf, Money(data.Vat), totalsX + 125, totalsY - 58, 9, 0.12, 0.2, 0.29, bold: true);
            FillRoundRect(pdf, totalsX + 1, totalsY - 91, totalsW - 2, 28, 0.88, 0.95, 1);
            Text(pdf, "GRAND TOTAL", totalsX + 12, totalsY - 79, 9, 0.08, 0.28, 0.48, bold: true);
            Text(pdf, Safe(data.Currency) + " " + Money(data.GrandTotal), totalsX + 113, totalsY - 79, 11, 0.06, 0.28, 0.48, bold: true);

            // Terms / thank-you block.
            var termsY = Math.Max(tableBottom - 22, 145);
            Text(pdf, "TERMS & CONDITIONS", Margin, termsY - 16, 8.5, 0.10, 0.31, 0.52, bold: true);
            var terms = data.Terms is { Count: > 0 } custom
                ? custom.Take(2).ToList()
                : new List<string> { "This quotation is prepared for the customer named above.", "Please review the quotation and confirm the required services before payment." };
            Text(pdf, "- " + Shorten(terms[0], 105), Margin, termsY - 32, 7.5, 0.34, 0.41, 0.48);
            if (terms.Count > 1) Text(pdf, "- " + Shorten(terms[1], 105), Margin, termsY - 45, 7.5, 0.34, 0.41, 0.48);
            var noteOffset = 0d;
            if (!string.IsNullOrWhiteSpace(data.Notes))
            {
                foreach (var line in Wrap("Notes: " + data.Notes!, 62).Take(3))
                {
                    Text(pdf, line, Margin, termsY - 58 - noteOffset, 7.5, 0.34, 0.41, 0.48);
                    noteOffset += 11;
                }
            }
            Text(pdf, "Thank you for your business.", Margin, termsY - 70 - noteOffset, 9, 0.10, 0.31, 0.52, bold: true);
        }

        // Footer.
        StrokeLine(pdf, Margin, 46, PageWidth - Margin, 46, 0.82, 0.87, 0.92, 0.8);
        Text(pdf, Safe(data.CompanyName) + "  |  Business & Professional Services", Margin, 29, 7, 0.40, 0.49, 0.57);
        Text(pdf, Safe(data.QuotationNumber), PageWidth - Margin - 55, 29, 7, 0.40, 0.49, 0.57);

        return pdf.ToString();
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Safe(string? value) => ToAscii(value ?? string.Empty);
    private static string Shorten(string? value, int maxChars)
    {
        var text = Safe(value).Trim();
        return text.Length <= maxChars ? text : text[..Math.Max(1, maxChars - 3)].TrimEnd() + "...";
    }

    private static IEnumerable<string> Wrap(string value, int maxChars)
    {
        value = Safe(value).Trim();
        if (string.IsNullOrWhiteSpace(value)) return new[] { "" };
        if (value.Length <= maxChars) return new[] { value };

        var result = new List<string>();
        var remaining = value;
        while (remaining.Length > maxChars)
        {
            var cut = remaining.LastIndexOf(' ', maxChars - 1);
            if (cut < Math.Max(1, maxChars / 2)) cut = maxChars;
            result.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].Trim();
        }
        if (remaining.Length > 0) result.Add(remaining);
        return result;
    }

    private static void Text(StringBuilder pdf, string text, double x, double y, double size, double r, double g, double b, bool bold = false)
    {
        var fontSize = size.ToString(CultureInfo.InvariantCulture);
        pdf.Append("BT\n/F1 ").Append(fontSize).Append(" Tf\n")
           .Append(r.ToString(CultureInfo.InvariantCulture)).Append(' ')
           .Append(g.ToString(CultureInfo.InvariantCulture)).Append(' ')
           .Append(b.ToString(CultureInfo.InvariantCulture)).Append(" rg\n")
           .Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(" Td\n(")
           .Append(Escape(text)).Append(") Tj\nET\n");
    }

    private static double TextWidth(string text, double size) => Math.Min(240, Safe(text).Length * size * 0.52);

    private static void FillRect(StringBuilder pdf, double x, double y, double w, double h, double r, double g, double b)
    {
        pdf.Append(r.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(g.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(b.ToString(CultureInfo.InvariantCulture)).Append(" rg\n")
           .Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ').Append(Fmt(w)).Append(' ').Append(Fmt(h)).Append(" re f\n");
    }

    private static void FillRoundRect(StringBuilder pdf, double x, double y, double w, double h, double r, double g, double b)
    {
        // A rectangle with subtly rounded-looking corner accents is enough for the portable PDF renderer.
        FillRect(pdf, x + 2, y, w - 4, h, r, g, b);
        FillRect(pdf, x, y + 2, w, h - 4, r, g, b);
    }

    private static void StrokeRoundRect(StringBuilder pdf, double x, double y, double w, double h, double r, double g, double b)
        => StrokeRect(pdf, x, y, w, h, r, g, b);

    private static void StrokeRect(StringBuilder pdf, double x, double y, double w, double h, double r, double g, double b)
    {
        pdf.Append(r.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(g.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(b.ToString(CultureInfo.InvariantCulture)).Append(" RG\n0.65 w\n")
           .Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ').Append(Fmt(w)).Append(' ').Append(Fmt(h)).Append(" re S\n");
    }

    private static void StrokeLine(StringBuilder pdf, double x1, double y1, double x2, double y2, double r, double g, double b, double width)
    {
        pdf.Append(r.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(g.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(b.ToString(CultureInfo.InvariantCulture)).Append(" RG\n")
           .Append(width.ToString(CultureInfo.InvariantCulture)).Append(" w\n")
           .Append(Fmt(x1)).Append(' ').Append(Fmt(y1)).Append(" m ")
           .Append(Fmt(x2)).Append(' ').Append(Fmt(y2)).Append(" l S\n");
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ToAscii(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(ch is >= ' ' and <= '~' ? ch : '?');
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
