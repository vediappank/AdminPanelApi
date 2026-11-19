using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Share page for any published article, built from the database on every request:
///   GET api/public/blog/share/{slug}
/// Social crawlers (LinkedIn, Facebook, X, WhatsApp) read the Open Graph tags in the HTML and do not run scripts,
/// so the tags must come from the server. People who click are redirected to the article on the website.
/// Works for every post, including ones published later - nothing to generate per post.
/// </summary>
[ApiController]
[Route("api/public/blog/share")]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicBlogShareController : ControllerBase
{
    private readonly IBlogRepository _repo;
    private readonly IConfiguration _config;
    private readonly ILogger<PublicBlogShareController> _logger;

    public PublicBlogShareController(IBlogRepository repo, IConfiguration config, ILogger<PublicBlogShareController> logger)
    {
        _repo = repo;
        _config = config;
        _logger = logger;
    }

    [HttpGet("{slug}")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Share(string slug, CancellationToken ct)
    {
        slug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!BlogContent.IsValidSlug(slug)) return NotFound();

        var row = await _repo.GetPublicPostAsync(slug, ct);
        if (row == null) return NotFound();

        var site = (_config["Website:PublicUrl"] ?? "https://blisspointworld.com").TrimEnd('/');
        var articleUrl = $"{site}/blog/post.html?slug={Uri.EscapeDataString(row.Slug)}";
        var shareUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/public/blog/share/{Uri.EscapeDataString(row.Slug)}";
        var image = PublicBlogController.ImageUrl(Request, _config, row.CoverImage) ?? $"{site}/images/home/hero-poster.jpg";
        var description = row.MetaDescription ?? row.Summary ?? string.Empty;
        var title = row.MetaTitle ?? row.Title;
        var published = DateTime.SpecifyKind(row.PublishedAt, DateTimeKind.Utc).ToString("o");

        string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);

        var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{E(title)} | Bliss Point Group</title>
<meta name=""description"" content=""{E(description)}"">
<link rel=""canonical"" href=""{E(shareUrl)}"">
<meta property=""og:type"" content=""article"">
<meta property=""og:site_name"" content=""Bliss Point Group"">
<meta property=""og:locale"" content=""en_AE"">
<meta property=""og:title"" content=""{E(title)}"">
<meta property=""og:description"" content=""{E(description)}"">
<meta property=""og:url"" content=""{E(shareUrl)}"">
<meta property=""og:image"" content=""{E(image)}"">
<meta property=""article:published_time"" content=""{E(published)}"">
<meta property=""article:author"" content=""{E(row.AuthorName ?? "Bliss Point Group")}"">
<meta name=""twitter:card"" content=""summary_large_image"">
<meta name=""twitter:title"" content=""{E(title)}"">
<meta name=""twitter:description"" content=""{E(description)}"">
<meta name=""twitter:image"" content=""{E(image)}"">
<script>location.replace({System.Text.Json.JsonSerializer.Serialize(articleUrl)});</script>
</head>
<body>
<p><a href=""{E(articleUrl)}"">Read the full article: {E(row.Title)}</a></p>
</body>
</html>";

        return Content(html, "text/html; charset=utf-8");
    }
}
