using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using RenewalTracker.Platform.Application.DTOs.Blog;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Repositories;

/// <summary>Dapper over dbo.BlogPost / dbo.BlogCategory - same "DefaultConnection" (SuperAdminPanel) as WebSettingRepository.</summary>
public class BlogRepository : IBlogRepository
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string _connectionString;

    public BlogRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    }

    private SqlConnection Open() => new(_connectionString);

    private static CommandDefinition Cmd(string sql, object? args, CancellationToken ct) =>
        new(sql, args, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct);

    /// <summary>A post is live once it is Published and its publish time has passed.</summary>
    private const string LiveFilter = "p.IsDeleted = 0 AND p.Status = 'Published' AND p.PublishedAt <= SYSUTCDATETIME()";

    // =============================================================== categories

    public async Task<List<BlogCategoryDto>> ListCategoriesAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<BlogCategoryDto>(Cmd($@"
SELECT c.BlogCategoryId, c.Name, c.Slug, c.Description, c.DisplayOrder, c.IsActive,
       (SELECT COUNT(*) FROM dbo.BlogPost p WHERE p.BlogCategoryId = c.BlogCategoryId AND p.IsDeleted = 0) AS PostCount,
       (SELECT COUNT(*) FROM dbo.BlogPost p WHERE p.BlogCategoryId = c.BlogCategoryId AND {LiveFilter}) AS PublishedCount
FROM dbo.BlogCategory c
WHERE @ActiveOnly = 0 OR c.IsActive = 1
ORDER BY c.DisplayOrder, c.Name;", new { ActiveOnly = activeOnly }, ct));
        return rows.ToList();
    }

    public async Task<BlogCategoryDto> SaveCategoryAsync(int? id, BlogCategorySaveRequest r, CancellationToken ct = default)
    {
        await using var cn = Open();
        try
        {
            int savedId;
            if (id is null)
            {
                savedId = await cn.ExecuteScalarAsync<int>(Cmd(@"
INSERT dbo.BlogCategory (Name, Slug, Description, DisplayOrder, IsActive)
VALUES (@Name, @Slug, @Description, @DisplayOrder, @IsActive);
SELECT CAST(SCOPE_IDENTITY() AS INT);", r, ct));
            }
            else
            {
                var n = await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.BlogCategory
   SET Name = @Name, Slug = @Slug, Description = @Description, DisplayOrder = @DisplayOrder,
       IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME()
 WHERE BlogCategoryId = @Id;", new { r.Name, r.Slug, r.Description, r.DisplayOrder, r.IsActive, Id = id.Value }, ct));
                if (n == 0) throw new WebLeadException(404, "Category not found.");
                savedId = id.Value;
            }
            return (await ListCategoriesAsync(false, ct)).First(c => c.BlogCategoryId == savedId);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new WebLeadException(409, $"Another category already uses the web address '{r.Slug}'.", ex);
        }
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        await using var cn = Open();
        var used = await cn.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM dbo.BlogPost WHERE BlogCategoryId = @Id AND IsDeleted = 0", new { Id = id }, ct));
        if (used > 0)
            throw new WebLeadException(409, $"This category is used by {used} post(s). Move them to another category, or switch the category off instead.");
        await cn.ExecuteAsync(Cmd("UPDATE dbo.BlogPost SET BlogCategoryId = NULL WHERE BlogCategoryId = @Id; DELETE dbo.BlogCategory WHERE BlogCategoryId = @Id;", new { Id = id }, ct));
    }

    // =============================================================== posts (Admin Panel)

    private const string PostColumns = @"
p.BlogPostId, p.Title, p.Slug, p.Summary, p.CoverImage, p.BlogCategoryId, c.Name AS CategoryName, c.Slug AS CategorySlug,
p.Tags, p.Status, p.PublishedAt, p.AuthorName, p.IsFeatured, p.ViewCount, p.CreatedAt, p.UpdatedAt, p.UpdatedBy";

    public async Task<List<BlogPostListItemDto>> ListPostsAsync(BlogPostQuery q, CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<BlogPostListItemDto>(Cmd($@"
SELECT {PostColumns}
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE p.IsDeleted = 0
  AND (@Status IS NULL OR p.Status = @Status)
  AND (@CategoryId IS NULL OR p.BlogCategoryId = @CategoryId)
  AND (@Search IS NULL OR p.Title LIKE @Search OR p.Summary LIKE @Search OR p.Tags LIKE @Search)
ORDER BY COALESCE(p.PublishedAt, p.CreatedAt) DESC, p.BlogPostId DESC;",
            new
            {
                Status = string.IsNullOrWhiteSpace(q.Status) ? null : q.Status,
                q.CategoryId,
                Search = string.IsNullOrWhiteSpace(q.Search) ? null : "%" + q.Search.Trim() + "%",
            }, ct));
        return rows.ToList();
    }

    public async Task<BlogPostDto?> GetPostAsync(int id, CancellationToken ct = default)
    {
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<BlogPostDto>(Cmd($@"
SELECT {PostColumns}, p.ContentHtml, p.MetaTitle, p.MetaDescription, p.CreatedBy
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE p.BlogPostId = @Id AND p.IsDeleted = 0;", new { Id = id }, ct));
    }

    public async Task<BlogPostDto> SavePostAsync(int? id, BlogPostSaveRequest r, string changedBy, CancellationToken ct = default)
    {
        await using var cn = Open();
        var args = new
        {
            Id = id,
            r.Title, r.Slug, r.Summary, r.ContentHtml, r.CoverImage, r.BlogCategoryId, r.Tags, r.Status, r.PublishedAt,
            r.AuthorName, r.MetaTitle, r.MetaDescription, r.IsFeatured,
            By = changedBy.Length > 100 ? changedBy[..100] : changedBy,
        };
        try
        {
            int savedId;
            if (id is null)
            {
                savedId = await cn.ExecuteScalarAsync<int>(Cmd(@"
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt,
                     AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (@Title, @Slug, @Summary, @ContentHtml, @CoverImage, @BlogCategoryId, @Tags, @Status, @PublishedAt,
        @AuthorName, @MetaTitle, @MetaDescription, @IsFeatured, @By);
SELECT CAST(SCOPE_IDENTITY() AS INT);", args, ct));
            }
            else
            {
                var n = await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.BlogPost
   SET Title = @Title, Slug = @Slug, Summary = @Summary, ContentHtml = @ContentHtml, CoverImage = @CoverImage,
       BlogCategoryId = @BlogCategoryId, Tags = @Tags, Status = @Status, PublishedAt = @PublishedAt,
       AuthorName = @AuthorName, MetaTitle = @MetaTitle, MetaDescription = @MetaDescription, IsFeatured = @IsFeatured,
       UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By
 WHERE BlogPostId = @Id AND IsDeleted = 0;", args, ct));
                if (n == 0) throw new WebLeadException(404, "Post not found.");
                savedId = id.Value;
            }
            return (await GetPostAsync(savedId, ct))!;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new WebLeadException(409, $"Another post already uses the web address '{r.Slug}'. Change the title or the web address.", ex);
        }
    }

    public async Task DeletePostAsync(int id, string changedBy, CancellationToken ct = default)
    {
        await using var cn = Open();
        // soft delete; the slug gets a suffix so a new post can reuse the address
        var n = await cn.ExecuteAsync(Cmd(@"
UPDATE dbo.BlogPost
   SET IsDeleted = 1, Slug = LEFT(Slug, 200) + '-deleted-' + CAST(BlogPostId AS VARCHAR(10)),
       UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By
 WHERE BlogPostId = @Id AND IsDeleted = 0;", new { Id = id, By = changedBy.Length > 100 ? changedBy[..100] : changedBy }, ct));
        if (n == 0) throw new WebLeadException(404, "Post not found.");
    }

    // =============================================================== public website

    public async Task<List<PublicBlogCategoryDto>> ListPublicCategoriesAsync(CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<PublicBlogCategoryDto>(Cmd($@"
SELECT c.Name, c.Slug, c.Description,
       (SELECT COUNT(*) FROM dbo.BlogPost p WHERE p.BlogCategoryId = c.BlogCategoryId AND {LiveFilter}) AS PostCount
FROM dbo.BlogCategory c
WHERE c.IsActive = 1
ORDER BY c.DisplayOrder, c.Name;", null, ct));
        return rows.ToList();
    }

    private const string PublicColumns = @"
p.BlogPostId, p.Slug, p.Title, p.Summary, p.CoverImage, c.Name AS CategoryName, c.Slug AS CategorySlug, p.Tags,
p.PublishedAt, p.AuthorName, p.IsFeatured, LEN(p.ContentHtml) AS ContentLength, p.BlogCategoryId";

    public async Task<(List<BlogPublicRow> Rows, int Total)> ListPublicPostsAsync(string? categorySlug, string? tag, string? search,
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var cn = Open();
        var where = $@"
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE {LiveFilter}
  AND (c.BlogCategoryId IS NULL OR c.IsActive = 1)
  AND (@Category IS NULL OR c.Slug = @Category)
  AND (@Tag IS NULL OR ',' + REPLACE(p.Tags, ', ', ',') + ',' LIKE '%,' + @Tag + ',%')
  AND (@Search IS NULL OR p.Title LIKE @Search OR p.Summary LIKE @Search OR p.Tags LIKE @Search)";
        var args = new
        {
            Category = string.IsNullOrWhiteSpace(categorySlug) ? null : categorySlug.Trim(),
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            Search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%",
            Skip = (page - 1) * pageSize,
            Take = pageSize,
        };
        using var multi = await cn.QueryMultipleAsync(Cmd($@"
SELECT COUNT(*) {where};
SELECT {PublicColumns} {where}
ORDER BY p.IsFeatured DESC, p.PublishedAt DESC, p.BlogPostId DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", args, ct));
        var total = await multi.ReadSingleAsync<int>();
        var rows = (await multi.ReadAsync<BlogPublicRow>()).ToList();
        return (rows, total);
    }

    public async Task<BlogPublicRow?> GetPublicPostAsync(string slug, CancellationToken ct = default)
    {
        await using var cn = Open();
        return await cn.QuerySingleOrDefaultAsync<BlogPublicRow>(Cmd($@"
SELECT {PublicColumns}, p.ContentHtml, p.MetaTitle, p.MetaDescription, p.UpdatedAt
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE {LiveFilter} AND p.Slug = @Slug;", new { Slug = slug }, ct));
    }

    public async Task<List<BlogPublicRow>> ListRelatedAsync(int blogPostId, int? categoryId, int take, CancellationToken ct = default)
    {
        await using var cn = Open();
        var rows = await cn.QueryAsync<BlogPublicRow>(Cmd($@"
SELECT TOP (@Take) {PublicColumns}
FROM dbo.BlogPost p
LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId
WHERE {LiveFilter} AND p.BlogPostId <> @Id
ORDER BY CASE WHEN p.BlogCategoryId = @CategoryId THEN 0 ELSE 1 END, p.PublishedAt DESC;",
            new { Id = blogPostId, CategoryId = categoryId, Take = take }, ct));
        return rows.ToList();
    }

    public async Task AddViewAsync(int blogPostId, CancellationToken ct = default)
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Cmd("UPDATE dbo.BlogPost SET ViewCount = ViewCount + 1 WHERE BlogPostId = @Id", new { Id = blogPostId }, ct));
    }
}
