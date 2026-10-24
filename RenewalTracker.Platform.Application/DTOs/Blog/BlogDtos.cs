namespace RenewalTracker.Platform.Application.DTOs.Blog;

/// <summary>dbo.BlogCategory row (+ how many posts use it).</summary>
public class BlogCategoryDto
{
    public int BlogCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public int PostCount { get; set; }
    public int PublishedCount { get; set; }
}

public class BlogCategorySaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A post in the Admin Panel list (no body).</summary>
public class BlogPostListItemDto
{
    public int BlogPostId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImage { get; set; }
    public int? BlogCategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategorySlug { get; set; }
    public string? Tags { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime? PublishedAt { get; set; }
    public string? AuthorName { get; set; }
    public bool IsFeatured { get; set; }
    public int ViewCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>A full post (Admin Panel editor).</summary>
public class BlogPostDto : BlogPostListItemDto
{
    public string ContentHtml { get; set; } = string.Empty;
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? CreatedBy { get; set; }
}

public class BlogPostSaveRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? Summary { get; set; }
    public string ContentHtml { get; set; } = string.Empty;
    public string? CoverImage { get; set; }
    public int? BlogCategoryId { get; set; }
    public string? Tags { get; set; }
    /// <summary>Draft | Published</summary>
    public string Status { get; set; } = "Draft";
    /// <summary>UTC. Empty + Published = now. A future date schedules the post.</summary>
    public DateTime? PublishedAt { get; set; }
    public string? AuthorName { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public bool IsFeatured { get; set; }
}

public class BlogPostQuery
{
    public string? Status { get; set; }
    public int? CategoryId { get; set; }
    public string? Search { get; set; }
}

public class BlogImageUploadResult
{
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

// ----------------------------------------------------------------- public (website)

public class PublicBlogCategoryDto
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PostCount { get; set; }
}

public class PublicBlogPostSummaryDto
{
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? CategoryName { get; set; }
    public string? CategorySlug { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public string? AuthorName { get; set; }
    public bool IsFeatured { get; set; }
    public int ReadMinutes { get; set; }
}

public class PublicBlogPostDto : PublicBlogPostSummaryDto
{
    public string ContentHtml { get; set; } = string.Empty;
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<PublicBlogPostSummaryDto> Related { get; set; } = new();

    /// <summary>Internal id used to count views - never sent to the website.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ViewId { get; set; }
}

public class PublicBlogPage
{
    public List<PublicBlogPostSummaryDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>Row shape the repository returns for the public endpoints (image URL is built by the controller).</summary>
public class BlogPublicRow
{
    public int BlogPostId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImage { get; set; }
    public string? CategoryName { get; set; }
    public string? CategorySlug { get; set; }
    public string? Tags { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? AuthorName { get; set; }
    public bool IsFeatured { get; set; }
    public int ContentLength { get; set; }
    public string? ContentHtml { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? BlogCategoryId { get; set; }
}
