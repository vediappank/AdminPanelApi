using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// The four document templates a company can choose for its quotations, invoices and payment receipts
/// (Admin Panel ▸ Tenants ▸ Control Center ▸ Business Panel branding). Stored per document type in the
/// tenant database settings "Tenant.Template.Quotation" / "Tenant.Template.Invoice" / "Tenant.Template.Receipt".
/// </summary>
public static class DocumentTemplates
{
    public const string Classic = "classic";
    public const string Modern = "modern";
    public const string Elegant = "elegant";
    public const string Minimal = "minimal";

    public static readonly IReadOnlyList<string> All = new[] { Classic, Modern, Elegant, Minimal };

    /// <summary>A known template code (any case), otherwise Classic.</summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(v) ? v : Classic;
    }

    public static bool IsKnown(string? value) => All.Contains((value ?? string.Empty).Trim().ToLowerInvariant());
}

/// <summary>One column of the item table. Width is relative - the columns share the page width in proportion.</summary>
public sealed record PdfColumn(string Header, double Width, bool AlignRight = false);

/// <summary>
/// Everything a quotation, invoice or receipt PDF shows, independent of the template that draws it.
/// Built by QuotationPdfBuilder (quotation / invoice) and the payment receipt code, drawn by DocumentPdfRenderer.
/// </summary>
public sealed class PdfDocument
{
    public string Template { get; init; } = DocumentTemplates.Classic;
    public string CompanyName { get; init; } = string.Empty;
    public string? CompanyTagline { get; init; } = "Business & Professional Services";
    /// <summary>data:image/png;base64,... or data:image/jpeg;base64,... - drawn in the header; other formats fall back to the initial.</summary>
    public string? LogoDataUrl { get; init; }

    public string Title { get; init; } = "QUOTATION";
    public string NumberLabel { get; init; } = "No";
    public string Number { get; init; } = string.Empty;
    /// <summary>Date, due date, reference ... shown next to the number.</summary>
    public IReadOnlyList<(string Label, string Value)> Meta { get; init; } = Array.Empty<(string, string)>();

    public string PartyHeading { get; init; } = "Bill To";
    /// <summary>First line is the name (bold), then code / address / contact lines.</summary>
    public IReadOnlyList<string> PartyLines { get; init; } = Array.Empty<string>();

    /// <summary>Optional line above the table (e.g. "PRODUCT: Renewal Tracker").</summary>
    public string? TableCaption { get; init; }
    public IReadOnlyList<PdfColumn> Columns { get; init; } = Array.Empty<PdfColumn>();
    public IReadOnlyList<string[]> Rows { get; init; } = Array.Empty<string[]>();

    public IReadOnlyList<(string Label, string Value)> Totals { get; init; } = Array.Empty<(string, string)>();
    public (string Label, string Value)? GrandTotal { get; init; }
    /// <summary>Big highlighted figure (receipt: "AMOUNT RECEIVED").</summary>
    public (string Label, string Value)? Highlight { get; init; }

    public string? NotesHeading { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string? ThankYou { get; init; } = "Thank you for your business.";
    /// <summary>Diagonal-free stamp word in the header area, e.g. "PAID".</summary>
    public string? Stamp { get; init; }
    public string? FooterText { get; init; }
}

/// <summary>
/// Dependency-free PDF writer for the four document templates (A4, built-in Helvetica / Times fonts, ASCII text).
///   classic  dark blue banner across the top, logo tile, blue table header (the original quotation look)
///   modern   white page, coloured bar down the left edge, teal accents, large title
///   elegant  centred logo and serif type, burgundy and gold rules, framed table
///   minimal  black and grey only, hairline rules, no fills - prints well on any printer
/// Logos: PNG (8-bit, any colour type, transparency kept) and JPEG are embedded; SVG / GIF / WEBP fall back to the initial.
/// </summary>
public static class DocumentPdfRenderer
{
    private const double W = 595, H = 842, M = 40, CW = W - 2 * M;

    private sealed record Rgb(double R, double G, double B);

    private sealed record Theme(
        string Code, bool Serif, Rgb Primary, Rgb Accent, Rgb Text, Rgb Muted, Rgb Line, Rgb Soft, Rgb Page, Rgb TableHeadText);

    private static Theme ThemeFor(string code) => DocumentTemplates.Normalize(code) switch
    {
        DocumentTemplates.Modern => new("modern", false, new(0.05, 0.42, 0.45), new(0.98, 0.62, 0.20), new(0.13, 0.17, 0.22), new(0.45, 0.50, 0.56), new(0.86, 0.89, 0.92), new(0.93, 0.97, 0.97), new(1, 1, 1), new(1, 1, 1)),
        DocumentTemplates.Elegant => new("elegant", true, new(0.42, 0.10, 0.18), new(0.72, 0.58, 0.30), new(0.18, 0.13, 0.12), new(0.48, 0.43, 0.40), new(0.80, 0.72, 0.60), new(0.985, 0.965, 0.94), new(1, 0.995, 0.985), new(0.42, 0.10, 0.18)),
        DocumentTemplates.Minimal => new("minimal", false, new(0.08, 0.08, 0.08), new(0.08, 0.08, 0.08), new(0.10, 0.10, 0.10), new(0.42, 0.42, 0.42), new(0.75, 0.75, 0.75), new(0.96, 0.96, 0.96), new(1, 1, 1), new(0.08, 0.08, 0.08)),
        _ => new("classic", false, new(0.075, 0.245, 0.42), new(0.14, 0.39, 0.68), new(0.12, 0.20, 0.29), new(0.48, 0.55, 0.62), new(0.84, 0.89, 0.94), new(0.94, 0.97, 0.995), new(0.985, 0.99, 0.998), new(1, 1, 1)),
    };

    // ------------------------------------------------------------------ public entry

    public static byte[] Render(PdfDocument doc)
    {
        var theme = ThemeFor(doc.Template);
        var logo = PdfImage.FromDataUrl(doc.LogoDataUrl);
        var pages = Layout(doc, theme, logo);
        return Write(pages, theme, logo);
    }

    // ------------------------------------------------------------------ layout

    private sealed class Page
    {
        public readonly StringBuilder S = new();
    }

    private static List<Page> Layout(PdfDocument doc, Theme t, PdfImage? logo)
    {
        var cols = doc.Columns.Count > 0 ? doc.Columns : new[] { new PdfColumn("DESCRIPTION", 1) };
        var totalWeight = cols.Sum(c => c.Width <= 0 ? 1 : c.Width);
        var widths = cols.Select(c => CW * (c.Width <= 0 ? 1 : c.Width) / totalWeight).ToArray();
        const double cellFont = 7.6, lineH = 9.6, padY = 7;

        // pre-wrap every row
        var wrapped = doc.Rows.Select(r => cols.Select((c, i) => WrapToWidth(i < r.Length ? r[i] : string.Empty, widths[i] - 10, cellFont, t, false).Take(4).ToList()).ToList()).ToList();
        var rowHeights = wrapped.Select(r => Math.Max(1, r.Max(l => l.Count)) * lineH + padY * 2 - 2).ToList();

        var pages = new List<Page>();
        var rowIndex = 0;
        var pageNo = 0;
        var summaryHeight = SummaryHeight(doc);
        do
        {
            pageNo++;
            var page = new Page();
            pages.Add(page);
            var y = DrawHeader(page.S, doc, t, logo, pageNo == 1);
            if (pageNo == 1) y = DrawInfo(page.S, doc, t, y);
            else y -= 6;

            if (pageNo == 1 && !string.IsNullOrWhiteSpace(doc.TableCaption))
            {
                Text(page.S, t, Shorten(doc.TableCaption!, 95, t), M, y - 12, 8.5, t.Primary, bold: true);
                y -= 22;
            }

            if (doc.Rows.Count > 0 || pageNo == 1)
            {
                y = DrawTableHead(page.S, cols, widths, t, y);
                var footerLimit = 70d;
                var any = false;
                while (rowIndex < wrapped.Count)
                {
                    var rh = rowHeights[rowIndex];
                    var isLast = rowIndex == wrapped.Count - 1;
                    var need = rh + (isLast ? summaryHeight : 0);
                    if (y - need < footerLimit && any) break;
                    DrawRow(page.S, cols, widths, wrapped[rowIndex], t, y, rh, rowIndex, cellFont, lineH, padY);
                    y -= rh;
                    rowIndex++;
                    any = true;
                }
                if (doc.Rows.Count == 0)
                {
                    Text(page.S, t, "No items", M + 8, y - 16, 8, t.Muted);
                    y -= 26;
                }
                DrawTableClose(page.S, t, y);
            }

            if (rowIndex >= wrapped.Count)
            {
                if (y - summaryHeight < 60)
                {
                    // summary does not fit - give it its own page
                    DrawFooter(page.S, doc, t, pageNo);
                    pageNo++;
                    page = new Page();
                    pages.Add(page);
                    y = DrawHeader(page.S, doc, t, logo, false) - 6;
                }
                DrawSummary(page.S, doc, t, y - 16);
            }
            DrawFooter(page.S, doc, t, pageNo);
        } while (rowIndex < wrapped.Count);

        // "Page x of y" now that the count is known
        var count = pages.Count;
        for (var i = 0; i < pages.Count; i++)
            pages[i].S.Replace("{{PAGES}}", count.ToString(CultureInfo.InvariantCulture));
        return pages;
    }

    private static double SummaryHeight(PdfDocument doc)
    {
        var totals = doc.Totals.Count * 18 + (doc.GrandTotal != null ? 34 : 0) + (doc.Highlight != null ? 52 : 0) + 30;
        var notes = (doc.Notes.Count > 0 ? 20 + doc.Notes.Count * 12 : 0) + (string.IsNullOrWhiteSpace(doc.ThankYou) ? 0 : 22);
        return Math.Max(totals, notes) + 10;
    }

    // ------------------------------------------------------------------ header (one per template)

    /// <summary>Draws the template's header and returns the y below it.</summary>
    private static double DrawHeader(StringBuilder s, PdfDocument doc, Theme t, PdfImage? logo, bool first)
    {
        Fill(s, 0, 0, W, H, t.Page);
        var company = string.IsNullOrWhiteSpace(doc.CompanyName) ? "Company" : doc.CompanyName;
        switch (t.Code)
        {
            case "modern":
            {
                Fill(s, 0, 0, 14, H, t.Primary);
                Fill(s, 14, 0, 4, H, t.Accent);
                var x = M + 4;
                var logoW = LogoOrInitial(s, t, logo, company, x, H - 92, 120, 52, filledTile: false);
                var titleSize = FitSize(doc.Title, 24, 190, t, true);
                var nameX = x + logoW + (logoW > 0 ? 12 : 0);
                var nameRoom = W - M - Width(doc.Title, titleSize, t, true) - 18 - nameX;
                Text(s, t, ShortenToWidth(company, nameRoom, 15, t, true), nameX, H - 58, 15, t.Text, bold: true);
                if (!string.IsNullOrWhiteSpace(doc.CompanyTagline))
                    Text(s, t, ShortenToWidth(doc.CompanyTagline, nameRoom, 8, t, false), nameX, H - 74, 8, t.Muted);
                RightText(s, t, doc.Title, W - M, H - 62, titleSize, t.Primary, bold: true);
                RightText(s, t, $"Page {{{{PAGE}}}} of {{{{PAGES}}}}", W - M, H - 80, 7.5, t.Muted);
                Line(s, x, H - 104, W - M, H - 104, t.Line, 0.8);
                Fill(s, x, H - 106, 70, 3, t.Accent);
                return H - 118;
            }
            case "elegant":
            {
                Stroke(s, 22, 22, W - 44, H - 44, t.Accent, 0.8);
                Stroke(s, 26, 26, W - 52, H - 52, t.Accent, 0.3);
                var top = H - 48;
                double hh = 0;
                if (logo != null)
                {
                    var (lw, lh) = Fit(logo, 150, 46);
                    DrawImage(s, (W - lw) / 2, top - lh, lw, lh);
                    hh = lh + 8;
                }
                CenterText(s, t, Shorten(company, 48, t).ToUpperInvariant(), W / 2, top - hh - 14, 15, t.Primary, bold: true, spacing: 1.2);
                if (!string.IsNullOrWhiteSpace(doc.CompanyTagline))
                    CenterText(s, t, Shorten(doc.CompanyTagline!, 70, t), W / 2, top - hh - 28, 8.5, t.Muted, italic: true);
                var ruleY = top - hh - 40;
                Line(s, M + 60, ruleY, W - M - 60, ruleY, t.Accent, 0.9);
                Line(s, M + 60, ruleY - 3, W - M - 60, ruleY - 3, t.Accent, 0.35);
                CenterText(s, t, Spaced(doc.Title), W / 2, ruleY - 22, 13, t.Primary, bold: true);
                RightText(s, t, $"Page {{{{PAGE}}}} of {{{{PAGES}}}}", W - M, ruleY - 22, 7, t.Muted);
                return ruleY - 34;
            }
            case "minimal":
            {
                var logoW = LogoOrInitial(s, t, logo, company, M, H - 82, 110, 44, filledTile: false);
                var tx = M + logoW + (logoW > 0 ? 12 : 0);
                var titleSize = FitSize(doc.Title.ToUpperInvariant(), 16, 200, t, false, 2);
                var room = W - M - Width(doc.Title.ToUpperInvariant(), titleSize, t, false, 2) - 18 - tx;
                Text(s, t, ShortenToWidth(company, room, 13, t, true), tx, H - 56, 13, t.Text, bold: true);
                if (!string.IsNullOrWhiteSpace(doc.CompanyTagline))
                    Text(s, t, ShortenToWidth(doc.CompanyTagline, room, 7.5, t, false), tx, H - 70, 7.5, t.Muted);
                RightText(s, t, doc.Title.ToUpperInvariant(), W - M, H - 58, titleSize, t.Text, bold: false, spacing: 2);
                RightText(s, t, $"Page {{{{PAGE}}}} of {{{{PAGES}}}}", W - M, H - 72, 7, t.Muted);
                Line(s, M, H - 92, W - M, H - 92, t.Text, 1.2);
                return H - 104;
            }
            default: // classic
            {
                Fill(s, 0, H - 92, W, 92, t.Primary);
                double tx;
                if (logo != null)
                {
                    Fill(s, M - 4, H - 80, 132, 60, new Rgb(1, 1, 1));
                    var (lw, lh) = Fit(logo, 124, 52);
                    DrawImage(s, M, H - 76 + (52 - lh) / 2, lw, lh);
                    tx = M + 140;
                }
                else
                {
                    Fill(s, M, H - 73, 42, 42, t.Accent);
                    CenterText(s, t, Initial(company), M + 21, H - 59, 18, new Rgb(1, 1, 1), bold: true);
                    tx = M + 52;
                }
                var titleSize = FitSize(doc.Title, 17, 170, t, true);
                var room = W - M - Width(doc.Title, titleSize, t, true) - 18 - tx;
                Text(s, t, ShortenToWidth(company, room, 16, t, true), tx, H - 45, 16, new Rgb(1, 1, 1), bold: true);
                if (!string.IsNullOrWhiteSpace(doc.CompanyTagline))
                    Text(s, t, ShortenToWidth(doc.CompanyTagline, room, 8.5, t, false), tx, H - 62, 8.5, new Rgb(0.82, 0.9, 0.97));
                RightText(s, t, doc.Title, W - M, H - 47, titleSize, new Rgb(1, 1, 1), bold: true);
                RightText(s, t, $"Page {{{{PAGE}}}} of {{{{PAGES}}}}", W - M, H - 67, 7.5, new Rgb(0.82, 0.9, 0.97));
                return H - 108;
            }
        }
    }

    /// <summary>Number / dates / party block. Returns the y below it.</summary>
    private static double DrawInfo(StringBuilder s, PdfDocument doc, Theme t, double top)
    {
        var meta = new List<(string Label, string Value)> { (doc.NumberLabel, doc.Number) };
        meta.AddRange(doc.Meta.Where(m => !string.IsNullOrWhiteSpace(m.Value)));
        var party = doc.PartyLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(5).ToList();

        var leftW = CW * 0.52;
        var rightX = M + leftW + 16;
        var rightW = CW - leftW - 16;
        var metaRows = meta.Count;
        var height = Math.Max(26 + party.Count * 12, 18 + metaRows * 15) + 14;

        switch (t.Code)
        {
            case "classic":
                Stroke(s, M, top - height, CW, height, t.Line, 0.65);
                Fill(s, M + 0.5, top - 22, CW - 1, 21.5, t.Soft);
                break;
            case "modern":
                Fill(s, M + 4, top - height, leftW - 4, height, t.Soft);
                Fill(s, M + 4, top - height, 3, height, t.Primary);
                break;
            case "elegant":
                Line(s, M, top - height, W - M, top - height, t.Line, 0.5);
                break;
            default:
                Line(s, M, top - height, W - M, top - height, t.Line, 0.4);
                break;
        }

        // party (left)
        var px = M + (t.Code == "modern" ? 16 : 10);
        Text(s, t, Upper(doc.PartyHeading, t), px, top - 15, 7.5, t.Primary, bold: true, spacing: t.Code == "elegant" ? 1 : 0.4);
        var py = top - (t.Code == "classic" ? 36 : 30);
        for (var i = 0; i < party.Count; i++)
        {
            Text(s, t, Shorten(party[i], i == 0 ? 42 : 70, t), px, py, i == 0 ? 10.5 : 8, i == 0 ? t.Text : t.Muted, bold: i == 0);
            py -= i == 0 ? 14 : 11.5;
        }

        // meta (right) - label left, value right
        var my = top - 15;
        foreach (var (label, value) in meta)
        {
            Text(s, t, Shorten(label, 22, t), rightX, my, 7.5, t.Muted);
            RightText(s, t, Shorten(string.IsNullOrWhiteSpace(value) ? "-" : value, 30, t), M + CW - 10, my, 8.6, t.Text, bold: true);
            my -= 15;
        }
        return top - height - 16;
    }

    // ------------------------------------------------------------------ table

    private static double DrawTableHead(StringBuilder s, IReadOnlyList<PdfColumn> cols, double[] widths, Theme t, double y)
    {
        const double hh = 24;
        switch (t.Code)
        {
            case "minimal":
                Line(s, M, y, W - M, y, t.Text, 0.9);
                Line(s, M, y - hh, W - M, y - hh, t.Text, 0.5);
                break;
            case "elegant":
                Fill(s, M, y - hh, CW, hh, t.Soft);
                Line(s, M, y, W - M, y, t.Primary, 1);
                Line(s, M, y - hh, W - M, y - hh, t.Accent, 0.6);
                break;
            default:
                Fill(s, M, y - hh, CW, hh, t.Primary);
                break;
        }
        var x = M;
        for (var i = 0; i < cols.Count; i++)
        {
            var label = Shorten(cols[i].Header.ToUpperInvariant(), 26, t);
            if (cols[i].AlignRight) RightText(s, t, label, x + widths[i] - 5, y - 15, 6.6, t.TableHeadText, bold: true);
            else Text(s, t, label, x + 5, y - 15, 6.6, t.TableHeadText, bold: true);
            x += widths[i];
        }
        return y - hh;
    }

    private static void DrawRow(StringBuilder s, IReadOnlyList<PdfColumn> cols, double[] widths, List<List<string>> cells, Theme t, double y, double rh, int index, double font, double lineH, double padY)
    {
        switch (t.Code)
        {
            case "classic":
                if (index % 2 == 1) Fill(s, M, y - rh, CW, rh, t.Soft);
                Line(s, M, y - rh, W - M, y - rh, t.Line, 0.5);
                break;
            case "modern":
                if (index % 2 == 1) Fill(s, M, y - rh, CW, rh, t.Soft);
                break;
            case "elegant":
                Line(s, M, y - rh, W - M, y - rh, t.Line, 0.35);
                break;
            default:
                Line(s, M, y - rh, W - M, y - rh, t.Line, 0.3);
                break;
        }
        var x = M;
        for (var c = 0; c < cols.Count; c++)
        {
            var ty = y - padY - font + 1;
            foreach (var line in cells[c])
            {
                if (cols[c].AlignRight) RightText(s, t, line, x + widths[c] - 5, ty, font, t.Text, bold: c == cols.Count - 1);
                else Text(s, t, line, x + 5, ty, font, t.Text, bold: c == 0 && cols[c].Width < 0.6);
                ty -= lineH;
            }
            x += widths[c];
        }
    }

    private static void DrawTableClose(StringBuilder s, Theme t, double y)
    {
        if (t.Code is "minimal") Line(s, M, y, W - M, y, t.Text, 0.9);
        else if (t.Code is "elegant") Line(s, M, y, W - M, y, t.Primary, 0.8);
        else if (t.Code is "modern") Line(s, M, y, W - M, y, t.Primary, 1.2);
    }

    // ------------------------------------------------------------------ totals, notes, footer

    private static void DrawSummary(StringBuilder s, PdfDocument doc, Theme t, double top)
    {
        var bx = M + CW * 0.56;
        var bw = CW - CW * 0.56;
        var y = top;

        if (doc.Totals.Count > 0 || doc.GrandTotal != null)
        {
            foreach (var (label, value) in doc.Totals)
            {
                Text(s, t, Shorten(label, 26, t), bx + 10, y - 4, 8.2, t.Muted);
                RightText(s, t, Shorten(value, 24, t), bx + bw - 10, y - 4, 9, t.Text, bold: true);
                y -= 18;
            }
            if (doc.GrandTotal is { } gt)
            {
                y -= 4;
                switch (t.Code)
                {
                    case "minimal":
                        Line(s, bx, y + 6, bx + bw, y + 6, t.Text, 0.9);
                        Text(s, t, Upper(gt.Label, t), bx + 10, y - 12, 9, t.Text, bold: true);
                        RightText(s, t, gt.Value, bx + bw - 10, y - 12, 11, t.Text, bold: true);
                        Line(s, bx, y - 22, bx + bw, y - 22, t.Text, 0.9);
                        break;
                    case "elegant":
                        Line(s, bx, y + 6, bx + bw, y + 6, t.Accent, 0.8);
                        Text(s, t, Upper(gt.Label, t), bx + 10, y - 12, 9, t.Primary, bold: true);
                        RightText(s, t, gt.Value, bx + bw - 10, y - 12, 11.5, t.Primary, bold: true);
                        Line(s, bx, y - 22, bx + bw, y - 22, t.Accent, 0.8);
                        Line(s, bx, y - 25, bx + bw, y - 25, t.Accent, 0.3);
                        break;
                    default:
                        Fill(s, bx, y - 24, bw, 28, t.Code == "modern" ? t.Primary : t.Soft);
                        var c = t.Code == "modern" ? new Rgb(1, 1, 1) : t.Primary;
                        Text(s, t, Upper(gt.Label, t), bx + 10, y - 13, 9, c, bold: true);
                        RightText(s, t, gt.Value, bx + bw - 10, y - 13, 11.5, c, bold: true);
                        break;
                }
                y -= 34;
            }
        }

        if (doc.Highlight is { } hl)
        {
            y -= 4;
            var hc = t.Code == "minimal" ? t.Text : t.Primary;
            if (t.Code == "minimal") Stroke(s, bx, y - 40, bw, 44, t.Text, 1);
            else if (t.Code == "elegant") { Stroke(s, bx, y - 40, bw, 44, t.Accent, 0.9); Stroke(s, bx + 3, y - 37, bw - 6, 38, t.Accent, 0.3); }
            else Fill(s, bx, y - 40, bw, 44, t.Soft);
            if (t.Code == "modern") Fill(s, bx, y - 40, 4, 44, t.Accent);
            Text(s, t, Upper(hl.Label, t), bx + 12, y - 11, 7.5, t.Muted, bold: true, spacing: 0.6);
            RightText(s, t, hl.Value, bx + bw - 12, y - 31, 15, hc, bold: true);
            y -= 52;
        }

        // notes / terms on the left
        var ly = top;
        var lw = CW * 0.52;
        if (doc.Notes.Count > 0)
        {
            Text(s, t, Upper(doc.NotesHeading ?? "Notes", t), M, ly - 4, 8, t.Primary, bold: true, spacing: 0.5);
            ly -= 18;
            foreach (var note in doc.Notes.Take(6))
            {
                foreach (var line in WrapToWidth("- " + note, lw, 7.5, t, false).Take(2))
                {
                    Text(s, t, line, M, ly, 7.5, t.Muted);
                    ly -= 10.5;
                }
            }
            ly -= 6;
        }
        if (!string.IsNullOrWhiteSpace(doc.ThankYou))
            Text(s, t, Shorten(doc.ThankYou!, 60, t), M, ly - 8, 9.5, t.Primary, bold: true, italic: t.Serif);

        if (!string.IsNullOrWhiteSpace(doc.Stamp))
        {
            var sx = M + lw - 110;
            var sy = Math.Min(ly, y) - 6;
            if (sy > 90)
            {
                var sc = t.Code == "minimal" ? t.Text : new Rgb(0.10, 0.55, 0.30);
                var stamp = doc.Stamp!.ToUpperInvariant();
                var sw = Math.Max(100, Width(stamp, 14, t, true, 2) + 26);
                Stroke(s, sx, sy - 34, sw, 30, sc, 1.6);
                CenterText(s, t, stamp, sx + sw / 2, sy - 24, 14, sc, bold: true, spacing: 2);
            }
        }
    }

    private static void DrawFooter(StringBuilder s, PdfDocument doc, Theme t, int pageNo)
    {
        s.Replace("{{PAGE}}", pageNo.ToString(CultureInfo.InvariantCulture));
        var footer = string.IsNullOrWhiteSpace(doc.FooterText)
            ? doc.CompanyName + (string.IsNullOrWhiteSpace(doc.CompanyTagline) ? string.Empty : "  |  " + doc.CompanyTagline)
            : doc.FooterText!;
        var fy = t.Code == "elegant" ? 40d : 30d;
        Line(s, M, fy + 16, W - M, fy + 16, t.Code == "elegant" ? t.Accent : t.Line, 0.6);
        if (t.Code == "elegant")
            CenterText(s, t, Shorten(footer, 110, t), W / 2, fy, 7, t.Muted, italic: true);
        else
        {
            Text(s, t, Shorten(footer, 95, t), M, fy, 7, t.Muted);
            RightText(s, t, Shorten(doc.Number, 30, t), W - M, fy, 7, t.Muted);
        }
    }

    // ------------------------------------------------------------------ logo

    /// <summary>Logo inside (x, y, maxW, maxH) - or, without one, a lettered tile. Returns the width used.</summary>
    private static double LogoOrInitial(StringBuilder s, Theme t, PdfImage? logo, string company, double x, double y, double maxW, double maxH, bool filledTile)
    {
        if (logo != null)
        {
            var (lw, lh) = Fit(logo, maxW, maxH);
            DrawImage(s, x, y + (maxH - lh) / 2, lw, lh);
            return lw;
        }
        var size = Math.Min(maxH, 40);
        var ty = y + (maxH - size) / 2;
        if (t.Code == "minimal") Stroke(s, x, ty, size, size, t.Text, 1.2);
        else Fill(s, x, ty, size, size, t.Primary);
        CenterText(s, t, Initial(company), x + size / 2, ty + size / 2 - 6, 17, t.Code == "minimal" ? t.Text : new Rgb(1, 1, 1), bold: true);
        return size;
    }

    private static (double W, double H) Fit(PdfImage img, double maxW, double maxH)
    {
        var scale = Math.Min(maxW / img.Width, maxH / img.Height);
        return (img.Width * scale, img.Height * scale);
    }

    private static void DrawImage(StringBuilder s, double x, double y, double w, double h) =>
        s.Append("q ").Append(F(w)).Append(" 0 0 ").Append(F(h)).Append(' ').Append(F(x)).Append(' ').Append(F(y)).Append(" cm /Im1 Do Q\n");

    private static string Initial(string company)
    {
        var c = (company ?? string.Empty).Trim().FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "B" : char.ToUpperInvariant(c).ToString();
    }

    // ------------------------------------------------------------------ drawing primitives

    private static void Text(StringBuilder s, Theme t, string text, double x, double y, double size, Rgb c, bool bold = false, bool italic = false, double spacing = 0)
    {
        var font = FontKey(t, bold, italic);
        s.Append("BT\n/").Append(font).Append(' ').Append(F(size)).Append(" Tf\n");
        if (spacing > 0) s.Append(F(spacing)).Append(" Tc\n");
        s.Append(F(c.R)).Append(' ').Append(F(c.G)).Append(' ').Append(F(c.B)).Append(" rg\n")
         .Append(F(x)).Append(' ').Append(F(y)).Append(" Td\n(").Append(Escape(Ascii(text))).Append(") Tj\n");
        if (spacing > 0) s.Append("0 Tc\n");
        s.Append("ET\n");
    }

    private static void RightText(StringBuilder s, Theme t, string text, double right, double y, double size, Rgb c, bool bold = false, double spacing = 0) =>
        Text(s, t, text, right - Width(text, size, t, bold, spacing), y, size, c, bold, false, spacing);

    private static void CenterText(StringBuilder s, Theme t, string text, double center, double y, double size, Rgb c, bool bold = false, bool italic = false, double spacing = 0) =>
        Text(s, t, text, center - Width(text, size, t, bold, spacing) / 2, y, size, c, bold, italic, spacing);

    private static void Fill(StringBuilder s, double x, double y, double w, double h, Rgb c) =>
        s.Append(F(c.R)).Append(' ').Append(F(c.G)).Append(' ').Append(F(c.B)).Append(" rg\n")
         .Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re f\n");

    private static void Stroke(StringBuilder s, double x, double y, double w, double h, Rgb c, double width) =>
        s.Append(F(c.R)).Append(' ').Append(F(c.G)).Append(' ').Append(F(c.B)).Append(" RG\n").Append(F(width)).Append(" w\n")
         .Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re S\n");

    private static void Line(StringBuilder s, double x1, double y1, double x2, double y2, Rgb c, double width) =>
        s.Append(F(c.R)).Append(' ').Append(F(c.G)).Append(' ').Append(F(c.B)).Append(" RG\n").Append(F(width)).Append(" w\n")
         .Append(F(x1)).Append(' ').Append(F(y1)).Append(" m ").Append(F(x2)).Append(' ').Append(F(y2)).Append(" l S\n");

    // ------------------------------------------------------------------ text helpers

    private static string FontKey(Theme t, bool bold, bool italic) =>
        t.Serif ? (bold ? "F4" : italic ? "F5" : "F3") : (bold ? "F2" : italic ? "F6" : "F1");

    // Helvetica advance widths (1/1000 em) for ASCII 32..126.
    private static readonly int[] Helv =
    {
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
        1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
        333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584
    };

    private static double Width(string text, double size, Theme t, bool bold, double spacing = 0)
    {
        var sum = 0d;
        foreach (var ch in Ascii(text)) sum += ch is >= ' ' and <= '~' ? Helv[ch - 32] : 556;
        var factor = (bold ? 1.06 : 1.0) * (t.Serif ? 0.93 : 1.0);
        return sum * size / 1000 * factor + spacing * Ascii(text).Length;
    }

    private static List<string> WrapToWidth(string value, double maxWidth, double size, Theme t, bool bold)
    {
        var text = Ascii(value ?? string.Empty).Trim();
        var result = new List<string>();
        if (text.Length == 0) { result.Add(string.Empty); return result; }
        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, t, bold) <= maxWidth) { line = candidate; continue; }
                if (line.Length > 0) result.Add(line);
                // a single word longer than the column: hard-break it
                var w = word;
                while (Width(w, size, t, bold) > maxWidth && w.Length > 1)
                {
                    var cut = w.Length - 1;
                    while (cut > 1 && Width(w[..cut], size, t, bold) > maxWidth) cut--;
                    result.Add(w[..cut]);
                    w = w[cut..];
                }
                line = w;
            }
            result.Add(line);
        }
        return result;
    }

    private static string Shorten(string? value, int maxChars, Theme t)
    {
        var text = Ascii(value ?? string.Empty).Trim();
        return text.Length <= maxChars ? text : text[..Math.Max(1, maxChars - 3)].TrimEnd() + "...";
    }

    /// <summary>Largest size up to <paramref name="max"/> at which <paramref name="text"/> fits in <paramref name="width"/>.</summary>
    private static double FitSize(string text, double max, double width, Theme t, bool bold, double spacing = 0)
    {
        var size = max;
        while (size > 7 && Width(text, size, t, bold, spacing) > width) size -= 0.5;
        return size;
    }

    private static string ShortenToWidth(string? value, double width, double size, Theme t, bool bold)
    {
        var text = Ascii(value ?? string.Empty).Trim();
        if (Width(text, size, t, bold) <= width) return text;
        while (text.Length > 1 && Width(text + "...", size, t, bold) > width) text = text[..^1];
        return text.TrimEnd() + "...";
    }

    private static string Upper(string value, Theme t) => (value ?? string.Empty).ToUpperInvariant();

    private static string Spaced(string value) => string.Join(" ", (value ?? string.Empty).ToUpperInvariant().ToCharArray()).Replace("   ", "  ");

    private static string Ascii(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                >= ' ' and <= '~' => ch,
                '\n' => '\n',
                '\u2013' or '\u2014' => '-',
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u20B9' => 'R',      // rupee sign - Helvetica has none
                '\u00A0' => ' ',
                _ => '?'
            });
        }
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\n", " ");

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ file writer

    private static byte[] Write(List<Page> pages, Theme t, PdfImage? logo)
    {
        // objects: 1 catalog, 2 pages, 3-8 fonts, [9 image, 10 smask], then page/content pairs
        var objects = new List<byte[]>();
        void Add(string body) => objects.Add(Encoding.ASCII.GetBytes(body));

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("PAGES-PLACEHOLDER");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>");

        var xobject = string.Empty;
        if (logo != null)
        {
            var imageObj = objects.Count + 1;
            var smaskRef = string.Empty;
            if (logo.Alpha != null)
            {
                var smaskObj = imageObj + 1;
                smaskRef = $" /SMask {smaskObj} 0 R";
            }
            objects.Add(Stream($"<< /Type /XObject /Subtype /Image /Width {logo.Width} /Height {logo.Height} /ColorSpace /{logo.ColorSpace} /BitsPerComponent 8 /Filter /{logo.Filter}{smaskRef}", logo.Data));
            if (logo.Alpha != null)
                objects.Add(Stream($"<< /Type /XObject /Subtype /Image /Width {logo.Width} /Height {logo.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", logo.Alpha));
            xobject = $" /XObject << /Im1 {imageObj} 0 R >>";
        }

        var kids = new List<string>();
        foreach (var page in pages)
        {
            var pageObj = objects.Count + 1;
            var contentObj = pageObj + 1;
            kids.Add($"{pageObj} 0 R");
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(W)} {F(H)}] /Resources << /ProcSet [/PDF /Text /ImageB /ImageC] /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R /F5 7 0 R /F6 8 0 R >>{xobject} >> /Contents {contentObj} 0 R >>");
            objects.Add(Stream("<<", Encoding.ASCII.GetBytes(page.S.ToString())));
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [ {string.Join(" ", kids)} ] /Count {pages.Count} >>");

        using var output = new MemoryStream();
        var offsets = new List<long>();
        WriteAscii(output, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            WriteAscii(output, "\nendobj\n");
        }
        var xref = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) WriteAscii(output, $"{off:D10} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] Stream(string dictStart, byte[] data)
    {
        using var ms = new MemoryStream();
        var head = Encoding.ASCII.GetBytes($"{dictStart} /Length {data.Length} >>\nstream\n");
        ms.Write(head);
        ms.Write(data);
        ms.Write(Encoding.ASCII.GetBytes("\nendstream"));
        return ms.ToArray();
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>A logo ready to embed: JPEG as-is (DCTDecode), PNG decoded to raw pixels and re-compressed (FlateDecode) with its alpha as a soft mask.</summary>
internal sealed class PdfImage
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public string ColorSpace { get; private init; } = "DeviceRGB";
    public string Filter { get; private init; } = "FlateDecode";
    public byte[] Data { get; private init; } = Array.Empty<byte>();
    public byte[]? Alpha { get; private init; }

    private static readonly Regex DataUrl = new(@"^data:image/(?<type>png|jpeg|jpg);base64,(?<data>[A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Null when there is no logo or it is not a readable PNG / JPEG.</summary>
    public static PdfImage? FromDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var m = DataUrl.Match(dataUrl.Trim());
        if (!m.Success) return null;
        try
        {
            var bytes = Convert.FromBase64String(m.Groups["data"].Value);
            return m.Groups["type"].Value.ToLowerInvariant() == "png" ? FromPng(bytes) : FromJpeg(bytes);
        }
        catch
        {
            return null;   // a broken logo never breaks the document
        }
    }

    private static PdfImage? FromJpeg(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var len = (b[i + 2] << 8) | b[i + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var height = (b[i + 5] << 8) | b[i + 6];
                var width = (b[i + 7] << 8) | b[i + 8];
                var components = b[i + 9];
                if (width == 0 || height == 0) return null;
                return new PdfImage
                {
                    Width = width, Height = height, Filter = "DCTDecode", Data = b,
                    ColorSpace = components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" },
                };
            }
            i += 2 + len;
        }
        return null;
    }

    private static PdfImage? FromPng(byte[] b)
    {
        if (b.Length < 33 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47) return null;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= b.Length)
        {
            var len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
            var type = Encoding.ASCII.GetString(b, pos + 4, 4);
            var data = pos + 8;
            if (len < 0 || data + len > b.Length) return null;
            switch (type)
            {
                case "IHDR":
                    width = (b[data] << 24) | (b[data + 1] << 16) | (b[data + 2] << 8) | b[data + 3];
                    height = (b[data + 4] << 24) | (b[data + 5] << 16) | (b[data + 6] << 8) | b[data + 7];
                    bitDepth = b[data + 8]; colorType = b[data + 9]; interlace = b[data + 12];
                    break;
                case "PLTE": palette = b[data..(data + len)]; break;
                case "tRNS": paletteAlpha = b[data..(data + len)]; break;
                case "IDAT": idat.Write(b, data, len); break;
                case "IEND": pos = b.Length; continue;
            }
            pos = data + len + 4;
        }
        if (width <= 0 || height <= 0 || width > 4000 || height > 4000 || interlace != 0) return null;
        if (bitDepth != 8 && !(colorType == 3 && bitDepth is 1 or 2 or 4)) return null;

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return null;
        var bitsPerPixel = channels * bitDepth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);

        byte[] raw;
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            z.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        if (raw.Length < (stride + 1) * height) return null;

        // undo the per-row filters
        var pixels = new byte[stride * height];
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var row = new byte[stride];
            for (var x = 0; x < stride; x++)
            {
                var a = x >= bpp ? row[x - bpp] : 0;
                var up = prev[x];
                var c = x >= bpp ? prev[x - bpp] : 0;
                var v = raw[src + x];
                row[x] = filter switch
                {
                    1 => (byte)(v + a),
                    2 => (byte)(v + up),
                    3 => (byte)(v + ((a + up) >> 1)),
                    4 => (byte)(v + Paeth(a, up, c)),
                    _ => v,
                };
            }
            Buffer.BlockCopy(row, 0, pixels, y * stride, stride);
            prev = row;
        }

        // split into colour + alpha
        var gray = colorType is 0 or 4;
        var colour = new byte[width * height * (gray ? 1 : 3)];
        byte[]? alpha = colorType is 4 or 6 || (colorType == 3 && paletteAlpha != null) ? new byte[width * height] : null;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                switch (colorType)
                {
                    case 0: colour[i] = pixels[y * stride + x]; break;
                    case 4: colour[i] = pixels[y * stride + x * 2]; alpha![i] = pixels[y * stride + x * 2 + 1]; break;
                    case 2:
                        Buffer.BlockCopy(pixels, y * stride + x * 3, colour, i * 3, 3);
                        break;
                    case 6:
                        Buffer.BlockCopy(pixels, y * stride + x * 4, colour, i * 3, 3);
                        alpha![i] = pixels[y * stride + x * 4 + 3];
                        break;
                    case 3:
                    {
                        int index;
                        if (bitDepth == 8) index = pixels[y * stride + x];
                        else
                        {
                            var bitPos = x * bitDepth;
                            var by = pixels[y * stride + bitPos / 8];
                            index = (by >> (8 - bitDepth - bitPos % 8)) & ((1 << bitDepth) - 1);
                        }
                        if (palette != null && index * 3 + 2 < palette.Length)
                            Buffer.BlockCopy(palette, index * 3, colour, i * 3, 3);
                        if (alpha != null) alpha[i] = index < paletteAlpha!.Length ? paletteAlpha[index] : (byte)255;
                        break;
                    }
                }
            }
        }
        if (alpha != null && alpha.All(v => v == 255)) alpha = null;

        return new PdfImage
        {
            Width = width, Height = height,
            ColorSpace = gray ? "DeviceGray" : "DeviceRGB",
            Filter = "FlateDecode",
            Data = Deflate(colour),
            Alpha = alpha == null ? null : Deflate(alpha),
        };
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
