using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.Blog;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// The website's blog (blog.html, blog/post.html and the government-updates bell in js/bp-extras.js):
///   GET api/public/blog/categories
///   GET api/public/blog/posts?category=visas&amp;tag=&amp;q=&amp;page=1&amp;pageSize=9
///   GET api/public/blog/posts/{slug}
///   GET api/public/blog/images/{file}
/// Anonymous, "Website" CORS policy. Lists are cached in memory for 60 seconds and cleared the
/// moment someone saves in the Admin Panel, so new posts show at once without DB load per visit.
/// </summary>
[ApiController]
[Route("api/public/blog")]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicBlogController : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (DateTime At, object Value)> Cache = new();

    private readonly IBlogRepository _repo;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PublicBlogController> _logger;

    public PublicBlogController(IBlogRepository repo, IConfiguration config, IWebHostEnvironment env, ILogger<PublicBlogController> logger)
    {
        _repo = repo;
        _config = config;
        _env = env;
        _logger = logger;
    }

    /// <summary>Called after every Admin Panel save/delete.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>Public address of a stored blog image (Website:ApiPublicUrl, else this request's host).</summary>
    public static string? ImageUrl(HttpRequest request, IConfiguration config, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var root = config["Website:ApiPublicUrl"];
        if (string.IsNullOrWhiteSpace(root)) root = $"{request.Scheme}://{request.Host}{request.PathBase}";
        return $"{root.TrimEnd('/')}/api/public/blog/images/{Uri.EscapeDataString(fileName)}";
    }

    private async Task<T> Cached<T>(string key, Func<Task<T>> load) where T : class
    {
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor && hit.Value is T value) return value;
        var fresh = await load();
        if (Cache.Count > 500) Cache.Clear();      // many different searches - keep memory bounded
        Cache[key] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    private PublicBlogPostSummaryDto Summary(BlogPublicRow r) => new()
    {
        Slug = r.Slug,
        Title = r.Title,
        Summary = r.Summary,
        CoverImageUrl = ImageUrl(Request, _config, r.CoverImage),
        CategoryName = r.CategoryName,
        CategorySlug = r.CategorySlug,
        Tags = BlogContent.SplitTags(r.Tags),
        PublishedAt = DateTime.SpecifyKind(r.PublishedAt, DateTimeKind.Utc),
        AuthorName = r.AuthorName,
        IsFeatured = r.IsFeatured,
        ReadMinutes = BlogContent.ReadMinutes(r.ContentLength),
    };

    [HttpGet("categories")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<List<PublicBlogCategoryDto>>> Categories(CancellationToken ct) =>
        Ok(await Cached("categories", () => _repo.ListPublicCategoriesAsync(ct)));

    [HttpGet("posts")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]   // the query string is part of the URL, so browsers/CDNs cache each list separately anyway
    public async Task<ActionResult<PublicBlogPage>> Posts([FromQuery] string? category, [FromQuery] string? tag, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 9, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 30);
        category = category?.Trim().ToLowerInvariant();
        tag = tag?.Trim();
        q = q?.Trim();
        if (q?.Length > 80) q = q[..80];

        var key = $"posts|{category}|{tag}|{q}|{page}|{pageSize}";
        var result = await Cached(key, async () =>
        {
            var (rows, total) = await _repo.ListPublicPostsAsync(category, tag, q, page, pageSize, ct);
            return new PublicBlogPage { Items = rows.Select(Summary).ToList(), Total = total, Page = page, PageSize = pageSize };
        });
        return Ok(result);
    }

    /// <summary>Sitemap of every published article - listed in https://blisspointworld.com/robots.txt.</summary>
    [HttpGet("sitemap.xml")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        var xml = await Cached("sitemap", async () =>
        {
            var site = (_config["Website:PublicUrl"] ?? "https://blisspointworld.com").TrimEnd('/');
            var (rows, _) = await _repo.ListPublicPostsAsync(null, null, null, 1, 5000, ct);
            var sb = new System.Text.StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            foreach (var r in rows)
                sb.Append("  <url><loc>").Append(System.Security.SecurityElement.Escape($"{site}/blog/post.html?slug={Uri.EscapeDataString(r.Slug)}"))
                  .Append("</loc><lastmod>").Append(r.PublishedAt.ToString("yyyy-MM-dd")).Append("</lastmod></url>\n");
            sb.Append("</urlset>\n");
            return sb.ToString();
        });
        return Content(xml, "application/xml; charset=utf-8");
    }

    [HttpGet("posts/{slug}")]
    public async Task<ActionResult<PublicBlogPostDto>> Post(string slug, CancellationToken ct)
    {
        slug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!BlogContent.IsValidSlug(slug)) return NotFound(new { message = "Article not found." });

        var post = await Cached("post|" + slug, async () =>
        {
            var row = await _repo.GetPublicPostAsync(slug, ct);
            if (row == null) return new PublicBlogPostDto();     // cached "not found" (empty slug)
            var related = await _repo.ListRelatedAsync(row.BlogPostId, row.BlogCategoryId, 3, ct);
            var s = Summary(row);
            var plain = BlogContent.PlainText(row.ContentHtml);
            return new PublicBlogPostDto
            {
                Slug = s.Slug, Title = s.Title, Summary = s.Summary, CoverImageUrl = s.CoverImageUrl, CategoryName = s.CategoryName,
                CategorySlug = s.CategorySlug, Tags = s.Tags, PublishedAt = s.PublishedAt, AuthorName = s.AuthorName, IsFeatured = s.IsFeatured,
                ReadMinutes = Math.Max(1, (int)Math.Round(plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 200.0)),
                ContentHtml = BlogContent.CleanHtml(row.ContentHtml),
                MetaTitle = row.MetaTitle,
                MetaDescription = row.MetaDescription ?? row.Summary,
                UpdatedAt = row.UpdatedAt.HasValue ? DateTime.SpecifyKind(row.UpdatedAt.Value, DateTimeKind.Utc) : null,
                Related = related.Select(Summary).ToList(),
                ViewId = row.BlogPostId,
            };
        });
        if (string.IsNullOrEmpty(post.Slug)) return NotFound(new { message = "Article not found." });

        try { await _repo.AddViewAsync(post.ViewId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Blog view count failed for {Slug}", slug); }
        return Ok(post);
    }

    [HttpGet("images/{file}")]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Any)]
    public IActionResult Image(string file)
    {
        if (!BlogContent.IsStoredImageName(file)) return NotFound();
        var folder = Path.GetFullPath(BlogContent.ImageFolder(_config, _env));
        var path = Path.GetFullPath(Path.Combine(folder, file));
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)) return NotFound();
        var type = BlogContent.ImageTypes.TryGetValue(Path.GetExtension(file), out var t) ? t : "application/octet-stream";
        return PhysicalFile(path, type);
    }
}
