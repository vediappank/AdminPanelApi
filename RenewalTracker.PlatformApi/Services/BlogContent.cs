using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Helpers for the Website Blog: web addresses (slugs), a defensive HTML clean-up for post
/// bodies (staff write them in the Admin Panel editor, but the website shows them to everyone),
/// and where blog images are kept (Website:BlogImagePath, default App_Data/blog-images).
/// </summary>
public static partial class BlogContent
{
    public const long MaxImageBytes = 5 * 1024 * 1024;

    public static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
    };

    /// <summary>"UAE VAT Deadline: July 2026 Update!" -> "uae-vat-deadline-july-2026-update"</summary>
    public static string Slugify(string? value, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == '&') sb.Append("-and-");
            else sb.Append('-');
        }
        var slug = MultiDash().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].Trim('-');
        return slug;
    }

    public static bool IsValidSlug(string? slug) => !string.IsNullOrEmpty(slug) && SlugRegex().IsMatch(slug);

    /// <summary>
    /// Removes anything that can run code: script/style/iframe(other than YouTube)/object/embed/form
    /// elements, on* event attributes and javascript:/data: links. Ordinary formatting, links,
    /// images, tables and YouTube embeds are kept.
    /// </summary>
    public static string CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = html;
        s = DangerousBlock().Replace(s, string.Empty);
        s = DangerousSingle().Replace(s, string.Empty);
        s = IframeTag().Replace(s, m => YouTubeSrc().IsMatch(m.Value) ? m.Value : string.Empty);
        s = EventAttr().Replace(s, string.Empty);
        s = ScriptUrl().Replace(s, "$1=\"#\"");
        s = StyleExpression().Replace(s, string.Empty);
        return s.Trim();
    }

    /// <summary>Plain text of a post body - for the automatic summary and reading time.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = Tags().Replace(html, " ");
        return Spaces().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }

    public static string AutoSummary(string? html, int max = 300)
    {
        var text = PlainText(html);
        if (text.Length <= max) return text;
        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':') + "...";
    }

    /// <summary>About 200 words per minute; the HTML length is used when only that is known (list views).</summary>
    public static int ReadMinutes(int htmlLength) => Math.Max(1, (int)Math.Round(htmlLength / 6.0 / 1.6 / 200.0));

    public static List<string> SplitTags(string? tags) =>
        (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();

    public static string ImageFolder(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Website:BlogImagePath"];
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(env.ContentRootPath, "App_Data", "blog-images");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    /// <summary>Only names this API created (yyyyMM_guid.ext) are ever served - no paths, no surprises.</summary>
    public static bool IsStoredImageName(string? name) => !string.IsNullOrEmpty(name) && StoredName().IsMatch(name);

    /// <summary>Checks the first bytes really are an image of the claimed type.</summary>
    public static bool HasImageSignature(ReadOnlySpan<byte> h, string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        ".png" => h.Length >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47,
        ".gif" => h.Length >= 4 && h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38,
        ".webp" => h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50,
        _ => false,
    };

    [GeneratedRegex("-{2,}")] private static partial Regex MultiDash();
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex SlugRegex();
    [GeneratedRegex("^[0-9]{6}_[0-9a-f]{32}\\.(jpg|jpeg|png|webp|gif)$")] private static partial Regex StoredName();
    [GeneratedRegex(@"<\s*(script|style|object|embed|form|textarea|select|button|svg|math|template|noscript)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousBlock();
    [GeneratedRegex(@"<\s*/?\s*(script|style|object|embed|form|input|textarea|select|button|link|meta|base|svg|math|template|noscript|frameset|frame)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousSingle();
    [GeneratedRegex(@"<\s*iframe\b[^>]*>(.*?<\s*/\s*iframe\s*>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IframeTag();
    [GeneratedRegex(@"src\s*=\s*[""']https://(www\.)?(youtube\.com|youtube-nocookie\.com)/embed/[A-Za-z0-9_\-?=&;]+[""']", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeSrc();
    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttr();
    [GeneratedRegex(@"\b(href|src|action|formaction|xlink:href)\s*=\s*[""']?\s*(javascript|vbscript|data)\s*:[^""'\s>]*[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrl();
    [GeneratedRegex(@"expression\s*\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleExpression();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
