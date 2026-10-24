using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.Blog;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel ▸ Website Leads ▸ Blog - write, publish and delete posts, manage categories,
/// upload images. The website reads published posts through PublicBlogController.
/// Access: WEBSITE_BLOG module (DatabaseScripts/WebsiteBlog_SuperAdminPanel.sql).
/// </summary>
[ApiController]
[Route("api/platform/blog")]
[RequirePlatformModule("WEBSITE_BLOG", "WEBSITE")]
[WebLeadExceptionFilter]
public class PlatformBlogController : ControllerBase
{
    private static readonly string[] Statuses = { "Draft", "Published" };

    private readonly IBlogRepository _repo;
    private readonly ICurrentPlatformUserService _currentUser;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PlatformBlogController> _logger;

    public PlatformBlogController(IBlogRepository repo, ICurrentPlatformUserService currentUser, IConfiguration config,
        IWebHostEnvironment env, ILogger<PlatformBlogController> logger)
    {
        _repo = repo;
        _currentUser = currentUser;
        _config = config;
        _env = env;
        _logger = logger;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    // ----------------------------------------------------------------- categories

    [HttpGet("categories")]
    public async Task<ActionResult<List<BlogCategoryDto>>> Categories(CancellationToken ct) =>
        Ok(await _repo.ListCategoriesAsync(activeOnly: false, ct));

    [HttpPost("categories")]
    public Task<ActionResult<BlogCategoryDto>> CreateCategory([FromBody] BlogCategorySaveRequest request, CancellationToken ct) =>
        SaveCategory(null, request, ct);

    [HttpPut("categories/{id:int}")]
    public Task<ActionResult<BlogCategoryDto>> UpdateCategory(int id, [FromBody] BlogCategorySaveRequest request, CancellationToken ct) =>
        SaveCategory(id, request, ct);

    private async Task<ActionResult<BlogCategoryDto>> SaveCategory(int? id, BlogCategorySaveRequest? r, CancellationToken ct)
    {
        if (r == null) return BadRequest(new { message = "Nothing to save." });
        r.Name = (r.Name ?? string.Empty).Trim();
        r.Slug = BlogContent.Slugify(string.IsNullOrWhiteSpace(r.Slug) ? r.Name : r.Slug, 90);
        r.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (r.Name.Length is < 2 or > 80) return BadRequest(new { message = "Category name must be 2 - 80 characters." });
        if (!BlogContent.IsValidSlug(r.Slug)) return BadRequest(new { message = "Enter a category name with letters or numbers." });
        if (r.Description?.Length > 300) return BadRequest(new { message = "Description is too long (300 characters max)." });

        var saved = await _repo.SaveCategoryAsync(id, r, ct);
        PublicBlogController.ClearCache();
        return Ok(saved);
    }

    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken ct)
    {
        await _repo.DeleteCategoryAsync(id, ct);
        PublicBlogController.ClearCache();
        return NoContent();
    }

    // ----------------------------------------------------------------- posts

    [HttpGet("posts")]
    public async Task<ActionResult<List<BlogPostListItemDto>>> Posts([FromQuery] BlogPostQuery query, CancellationToken ct) =>
        Ok(await _repo.ListPostsAsync(query, ct));

    [HttpGet("posts/{id:int}")]
    public async Task<ActionResult<BlogPostDto>> Post(int id, CancellationToken ct)
    {
        var post = await _repo.GetPostAsync(id, ct);
        return post == null ? NotFound(new { message = "Post not found." }) : Ok(post);
    }

    [HttpPost("posts")]
    public Task<ActionResult<BlogPostDto>> CreatePost([FromBody] BlogPostSaveRequest request, CancellationToken ct) =>
        SavePost(null, request, ct);

    [HttpPut("posts/{id:int}")]
    public Task<ActionResult<BlogPostDto>> UpdatePost(int id, [FromBody] BlogPostSaveRequest request, CancellationToken ct) =>
        SavePost(id, request, ct);

    private async Task<ActionResult<BlogPostDto>> SavePost(int? id, BlogPostSaveRequest? r, CancellationToken ct)
    {
        if (r == null) return BadRequest(new { message = "Nothing to save." });

        r.Title = (r.Title ?? string.Empty).Trim();
        r.Slug = BlogContent.Slugify(string.IsNullOrWhiteSpace(r.Slug) ? r.Title : r.Slug, 200);
        r.ContentHtml = BlogContent.CleanHtml(r.ContentHtml);
        r.Summary = string.IsNullOrWhiteSpace(r.Summary) ? BlogContent.AutoSummary(r.ContentHtml) : r.Summary.Trim();
        r.Status = Statuses.FirstOrDefault(s => s.Equals(r.Status?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Draft";
        r.Tags = string.Join(", ", BlogContent.SplitTags(r.Tags));
        if (r.Tags.Length == 0) r.Tags = null;
        r.AuthorName = string.IsNullOrWhiteSpace(r.AuthorName) ? null : r.AuthorName.Trim();
        r.MetaTitle = string.IsNullOrWhiteSpace(r.MetaTitle) ? null : r.MetaTitle.Trim();
        r.MetaDescription = string.IsNullOrWhiteSpace(r.MetaDescription) ? null : r.MetaDescription.Trim();
        r.CoverImage = string.IsNullOrWhiteSpace(r.CoverImage) ? null : r.CoverImage.Trim();
        if (r.Status == "Published" && r.PublishedAt == null) r.PublishedAt = DateTime.UtcNow;
        if (r.PublishedAt is { Kind: DateTimeKind.Local } local) r.PublishedAt = local.ToUniversalTime();

        var errors = new List<string>();
        if (r.Title.Length is < 5 or > 200) errors.Add("Title must be 5 - 200 characters.");
        if (!BlogContent.IsValidSlug(r.Slug)) errors.Add("Web address can only use letters, numbers and dashes.");
        if (r.Status == "Published" && BlogContent.PlainText(r.ContentHtml).Length < 50) errors.Add("Write at least a few sentences before publishing.");
        if (r.Summary?.Length > 400) errors.Add("Summary is too long (400 characters max).");
        if (r.Tags?.Length > 300) errors.Add("Too many tags.");
        if (r.MetaTitle?.Length > 200) errors.Add("SEO title is too long (200 characters max).");
        if (r.MetaDescription?.Length > 320) errors.Add("SEO description is too long (320 characters max).");
        if (r.CoverImage != null && !BlogContent.IsStoredImageName(r.CoverImage)) errors.Add("Upload the cover image again.");
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors), errors });

        var saved = await _repo.SavePostAsync(id, r, Me, ct);
        PublicBlogController.ClearCache();
        _logger.LogInformation("Blog post {Id} '{Title}' saved as {Status} by {User}", saved.BlogPostId, saved.Title, saved.Status, Me);
        return Ok(saved);
    }

    [HttpDelete("posts/{id:int}")]
    public async Task<IActionResult> DeletePost(int id, CancellationToken ct)
    {
        await _repo.DeletePostAsync(id, Me, ct);
        PublicBlogController.ClearCache();
        _logger.LogInformation("Blog post {Id} deleted by {User}", id, Me);
        return NoContent();
    }

    // ----------------------------------------------------------------- images (cover + in-article)

    [HttpPost("images")]
    [RequestSizeLimit(BlogContent.MaxImageBytes + 256 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = BlogContent.MaxImageBytes + 256 * 1024)]
    public async Task<ActionResult<BlogImageUploadResult>> UploadImage(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "Choose an image to upload." });
        var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        if (!BlogContent.ImageTypes.ContainsKey(ext)) return BadRequest(new { message = "Use a JPG, PNG, WEBP or GIF image." });
        if (file.Length > BlogContent.MaxImageBytes) return BadRequest(new { message = "Images must be 5 MB or smaller." });

        byte[] bytes;
        await using (var ms = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        if (!BlogContent.HasImageSignature(bytes, ext)) return BadRequest(new { message = "That file is not a real image." });

        var folder = BlogContent.ImageFolder(_config, _env);
        Directory.CreateDirectory(folder);
        var name = $"{DateTime.UtcNow:yyyyMM}_{Guid.NewGuid():N}{(ext == ".jpeg" ? ".jpg" : ext)}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, ct);

        return Ok(new BlogImageUploadResult { FileName = name, Url = PublicBlogController.ImageUrl(Request, _config, name)! });
    }
}
