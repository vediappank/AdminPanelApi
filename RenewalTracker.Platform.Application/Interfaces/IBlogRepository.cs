using RenewalTracker.Platform.Application.DTOs.Blog;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Website Blog (dbo.BlogPost / dbo.BlogCategory - DatabaseScripts/WebsiteBlog_SuperAdminPanel.sql).
/// Business-rule failures (duplicate slug, category in use, not found) throw WebLeadException
/// with the HTTP status to return.
/// </summary>
public interface IBlogRepository
{
    // categories
    Task<List<BlogCategoryDto>> ListCategoriesAsync(bool activeOnly, CancellationToken ct = default);
    Task<BlogCategoryDto> SaveCategoryAsync(int? id, BlogCategorySaveRequest request, CancellationToken ct = default);
    Task DeleteCategoryAsync(int id, CancellationToken ct = default);

    // posts (Admin Panel)
    Task<List<BlogPostListItemDto>> ListPostsAsync(BlogPostQuery query, CancellationToken ct = default);
    Task<BlogPostDto?> GetPostAsync(int id, CancellationToken ct = default);
    Task<BlogPostDto> SavePostAsync(int? id, BlogPostSaveRequest request, string changedBy, CancellationToken ct = default);
    Task DeletePostAsync(int id, string changedBy, CancellationToken ct = default);

    // public website - only Published posts whose PublishedAt has passed
    Task<List<PublicBlogCategoryDto>> ListPublicCategoriesAsync(CancellationToken ct = default);
    Task<(List<BlogPublicRow> Rows, int Total)> ListPublicPostsAsync(string? categorySlug, string? tag, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<BlogPublicRow?> GetPublicPostAsync(string slug, CancellationToken ct = default);
    Task<List<BlogPublicRow>> ListRelatedAsync(int blogPostId, int? categoryId, int take, CancellationToken ct = default);
    Task AddViewAsync(int blogPostId, CancellationToken ct = default);
}
