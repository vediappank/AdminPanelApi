using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using RenewalTracker.Platform.Application.DTOs.WebLeads;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Repositories;

/// <summary>
/// Dapper over the dbo.usp_Web* stored procedures - see IWebLeadRepository
/// and DatabaseScripts/WebsiteLeads_SuperAdminPanel.sql. Uses the same
/// "DefaultConnection" (SuperAdminPanel) as PlatformDbContext.
/// </summary>
public class WebLeadRepository : IWebLeadRepository
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string _connectionString;

    public WebLeadRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    }

    private SqlConnection Open() => new(_connectionString);

    private static CommandDefinition Proc(string name, object? args, CancellationToken ct) =>
        new(name, args, commandType: CommandType.StoredProcedure, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct);

    /// <summary>Maps THROW 5xxxx from the procedures to a WebLeadException with an HTTP status.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            var status = ex.Number switch
            {
                50404 => 404,
                50010 => 403,   // form switched off
                _ => 400,       // 50001 invalid status, 50002 user not found, ...
            };
            throw new WebLeadException(status, ex.Message, ex);
        }
    }

    private static Task Run(Func<Task> action) => Run(async () => { await action(); return true; });

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v[..max] : v;
    }

    /// <summary>Travellers / pilgrims: 1-500 (dbo.WebEnquiryTravel CHECK), anything else is stored as NULL.</summary>
    private static short? Small(int? value) =>
        value is >= 1 and <= 500 ? (short)value.Value : null;

    // =================================================================
    // website
    // =================================================================

    public Task<WebEnquiryInsertResult> InsertEnquiryAsync(WebEnquiryInsert input, CancellationToken ct = default) => Run(async () =>
    {
        var r = input.Request;
        var p = new DynamicParameters();
        p.Add("@FormCode", Clean(r.FormCode, 60), DbType.AnsiString);
        p.Add("@PagePath", Clean(r.PagePath, 300));
        p.Add("@PageTitle", Clean(r.PageTitle, 150));
        p.Add("@RequestType", Clean(r.RequestType, 20) ?? "Enquiry", DbType.AnsiString);
        p.Add("@ServiceInterest", Clean(r.ServiceInterest, 200));
        p.Add("@FullName", Clean(r.FullName, 120));
        p.Add("@CompanyName", Clean(r.CompanyName, 200));
        p.Add("@Email", Clean(r.Email, 200));
        p.Add("@Phone", Clean(r.Phone, 30));
        p.Add("@Message", Clean(r.Message, 2000));
        p.Add("@ConsentGiven", r.ConsentGiven);
        p.Add("@SourcePage", Clean(input.SourcePage, 300));
        p.Add("@Referrer", Clean(r.Referrer, 500));
        p.Add("@UtmSource", Clean(r.UtmSource, 100));
        p.Add("@UtmMedium", Clean(r.UtmMedium, 100));
        p.Add("@UtmCampaign", Clean(r.UtmCampaign, 150));
        p.Add("@IpAddress", Clean(input.IpAddress, 45), DbType.AnsiString);
        p.Add("@UserAgent", Clean(input.UserAgent, 400));
        p.Add("@IsSpam", input.IsSpam);
        p.Add("@Destination", Clean(r.Destination, 150));
        p.Add("@TravelDatesText", Clean(r.TravelDatesText, 100));
        p.Add("@DepartureDate", r.DepartureDate?.Date, DbType.Date);
        p.Add("@ArrivalDate", r.ArrivalDate?.Date, DbType.Date);
        p.Add("@Travellers", Small(r.Travellers), DbType.Int16);
        p.Add("@Pilgrims", Small(r.Pilgrims), DbType.Int16);
        p.Add("@RoomType", Clean(r.RoomType, 60));
        p.Add("@VisaType", Clean(r.VisaType, 60));
        p.Add("@TeamSize", Clean(r.TeamSize, 20), DbType.AnsiString);
        p.Add("@BusinessType", Clean(r.BusinessType, 60));
        var modules = r.Modules?
            .Select(m => Clean(m?.Replace(",", " "), 60))
            .Where(m => m != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        p.Add("@ModulesCsv", modules is { Count: > 0 } ? Clean(string.Join(",", modules), 600) : null);

        await using var cn = Open();
        return await cn.QuerySingleAsync<WebEnquiryInsertResult>(Proc("dbo.usp_WebEnquiry_Insert", p, ct));
    });

    public Task<WebJobApplicationInsertResult> InsertJobApplicationAsync(WebJobApplicationInsert input, CancellationToken ct = default) => Run(async () =>
    {
        var p = new DynamicParameters();
        p.Add("@FullName", Clean(input.FullName, 120));
        p.Add("@Email", Clean(input.Email, 200));
        p.Add("@Phone", Clean(input.Phone, 30));
        p.Add("@RoleApplied", Clean(input.RoleApplied, 80));
        p.Add("@Experience", Clean(input.Experience, 20), DbType.AnsiString);
        p.Add("@CoverNote", Clean(input.CoverNote, 2000));
        p.Add("@CvFileName", Clean(input.CvFileName, 260));
        p.Add("@CvFilePath", Clean(input.CvFilePath, 500));
        p.Add("@CvContentType", Clean(input.CvContentType, 100), DbType.AnsiString);
        p.Add("@CvSizeBytes", input.CvSizeBytes);
        p.Add("@SourcePage", Clean(input.SourcePage, 300));
        p.Add("@IpAddress", Clean(input.IpAddress, 45), DbType.AnsiString);
        p.Add("@UserAgent", Clean(input.UserAgent, 400));
        p.Add("@IsSpam", input.IsSpam);

        await using var cn = Open();
        return await cn.QuerySingleAsync<WebJobApplicationInsertResult>(Proc("dbo.usp_WebJobApplication_Insert", p, ct));
    });

    // =================================================================
    // enquiries
    // =================================================================

    public Task<PagedResult<WebEnquiryListItemDto>> ListEnquiriesAsync(WebEnquiryListQuery q, CancellationToken ct = default) => Run(async () =>
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 5, 200);
        var p = new DynamicParameters();
        p.Add("@Status", Clean(q.Status, 20), DbType.AnsiString);
        p.Add("@Category", Clean(q.Category, 40), DbType.AnsiString);
        p.Add("@RequestType", Clean(q.RequestType, 20), DbType.AnsiString);
        p.Add("@WebFormId", q.WebFormId);
        p.Add("@AssignedToUserId", q.AssignedToUserId);
        p.Add("@Search", Clean(q.Search, 100));
        p.Add("@FromDate", q.FromDate?.Date, DbType.Date);
        p.Add("@ToDate", q.ToDate?.Date, DbType.Date);
        p.Add("@UnreadOnly", q.UnreadOnly);
        p.Add("@IncludeSpam", q.IncludeSpam);
        p.Add("@PageNumber", page);
        p.Add("@PageSize", size);

        await using var cn = Open();
        var rows = (await cn.QueryAsync<WebEnquiryListRow>(Proc("dbo.usp_WebEnquiry_List", p, ct))).ToList();
        return new PagedResult<WebEnquiryListItemDto>
        {
            Items = rows.Cast<WebEnquiryListItemDto>().ToList(),
            Total = rows.Count > 0 ? rows[0].TotalCount : 0,
            Page = page,
            PageSize = size,
        };
    });

    public Task<WebEnquiryDetailDto?> GetEnquiryAsync(long id, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        using var grid = await cn.QueryMultipleAsync(Proc("dbo.usp_WebEnquiry_Get", new { WebEnquiryId = id }, ct));
        var detail = await grid.ReadSingleOrDefaultAsync<WebEnquiryDetailDto>();
        if (detail == null) return null;
        detail.Modules = (await grid.ReadAsync<string>()).ToList();
        detail.History = (await grid.ReadAsync<WebStatusHistoryDto>()).ToList();
        return detail;
    });

    public Task UpdateEnquiryStatusAsync(long id, string status, string? note, string changedBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebEnquiry_UpdateStatus", new
        {
            WebEnquiryId = id,
            Status = new DbString { Value = status, IsAnsi = true, Length = 20 },
            Note = Clean(note, 500),
            ChangedBy = Clean(changedBy, 100) ?? "admin",
        }, ct));
    });

    public Task<WebEnquiryAssignResult> AssignEnquiryAsync(long id, int? assignedToUserId, string changedBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        return await cn.QuerySingleAsync<WebEnquiryAssignResult>(Proc("dbo.usp_WebEnquiry_Assign", new
        {
            WebEnquiryId = id,
            AssignedToUserId = assignedToUserId,
            ChangedBy = Clean(changedBy, 100) ?? "admin",
        }, ct));
    });

    public Task MarkEnquiryReadAsync(long id, string readBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebEnquiry_MarkRead", new { WebEnquiryId = id, ReadBy = Clean(readBy, 100) ?? "admin" }, ct));
    });

    public Task MarkAllReadAsync(string readBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebEnquiry_MarkAllRead", new { ReadBy = Clean(readBy, 100) ?? "admin" }, ct));
    });

    public Task<WebEnquiryConvertResult> ConvertToSalesEnquiryAsync(long id, string changedBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        return await cn.QuerySingleAsync<WebEnquiryConvertResult>(Proc("dbo.usp_WebEnquiry_ConvertToSalesEnquiry", new
        {
            WebEnquiryId = id,
            ChangedBy = Clean(changedBy, 100) ?? "admin",
        }, ct));
    });

    // =================================================================
    // job applications
    // =================================================================

    public Task<PagedResult<WebJobApplicationListItemDto>> ListJobApplicationsAsync(WebJobApplicationListQuery q, CancellationToken ct = default) => Run(async () =>
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 5, 200);
        var p = new DynamicParameters();
        p.Add("@Status", Clean(q.Status, 20), DbType.AnsiString);
        p.Add("@RoleApplied", Clean(q.RoleApplied, 80));
        p.Add("@Search", Clean(q.Search, 100));
        p.Add("@FromDate", q.FromDate?.Date, DbType.Date);
        p.Add("@ToDate", q.ToDate?.Date, DbType.Date);
        p.Add("@UnreadOnly", q.UnreadOnly);
        p.Add("@IncludeSpam", q.IncludeSpam);
        p.Add("@PageNumber", page);
        p.Add("@PageSize", size);

        await using var cn = Open();
        var rows = (await cn.QueryAsync<WebJobApplicationListRow>(Proc("dbo.usp_WebJobApplication_List", p, ct))).ToList();
        return new PagedResult<WebJobApplicationListItemDto>
        {
            Items = rows.Cast<WebJobApplicationListItemDto>().ToList(),
            Total = rows.Count > 0 ? rows[0].TotalCount : 0,
            Page = page,
            PageSize = size,
        };
    });

    public Task<WebJobApplicationDetailDto?> GetJobApplicationAsync(long id, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        using var grid = await cn.QueryMultipleAsync(Proc("dbo.usp_WebJobApplication_Get", new { WebJobApplicationId = id }, ct));
        var detail = await grid.ReadSingleOrDefaultAsync<WebJobApplicationDetailDto>();
        if (detail == null) return null;
        detail.History = (await grid.ReadAsync<WebStatusHistoryDto>()).ToList();
        return detail;
    });

    public Task<WebJobApplicationCvInfo?> GetJobApplicationCvAsync(long id, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        using var grid = await cn.QueryMultipleAsync(Proc("dbo.usp_WebJobApplication_Get", new { WebJobApplicationId = id }, ct));
        return await grid.ReadSingleOrDefaultAsync<WebJobApplicationCvInfo>();
    });

    public Task UpdateJobApplicationStatusAsync(long id, string status, string? note, string changedBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebJobApplication_UpdateStatus", new
        {
            WebJobApplicationId = id,
            Status = new DbString { Value = status, IsAnsi = true, Length = 20 },
            Note = Clean(note, 500),
            ChangedBy = Clean(changedBy, 100) ?? "admin",
        }, ct));
    });

    public Task MarkJobApplicationReadAsync(long id, string readBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebJobApplication_MarkRead", new { WebJobApplicationId = id, ReadBy = Clean(readBy, 100) ?? "admin" }, ct));
    });

    // =================================================================
    // bell, assignees, forms
    // =================================================================

    public Task<WebLeadSummaryDto> GetSummaryAsync(CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        using var grid = await cn.QueryMultipleAsync(Proc("dbo.usp_WebLead_Summary", null, ct));
        return new WebLeadSummaryDto
        {
            Counters = await grid.ReadSingleAsync<WebLeadCountersDto>(),
            NewByCategory = (await grid.ReadAsync<WebLeadCategoryCountDto>()).ToList(),
            Latest = (await grid.ReadAsync<WebLeadLatestDto>()).ToList(),
        };
    });

    public Task<WebLeadDashboardDto> GetDashboardAsync(int days, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        using var grid = await cn.QueryMultipleAsync(Proc("dbo.usp_WebLead_Dashboard", new { Days = Math.Clamp(days, 1, 366) }, ct));
        return new WebLeadDashboardDto
        {
            Kpis = await grid.ReadSingleAsync<WebLeadDashboardKpisDto>(),
            Trend = (await grid.ReadAsync<WebLeadTrendPointDto>()).ToList(),
            ByCategory = (await grid.ReadAsync<WebLeadByCategoryDto>()).ToList(),
            ByStatus = (await grid.ReadAsync<(string Status, int Total)>()).Select(x => new WebLeadCountDto { Name = x.Status, Total = x.Total }).ToList(),
            ByRequestType = (await grid.ReadAsync<(string RequestType, int Total)>()).Select(x => new WebLeadCountDto { Name = x.RequestType, Total = x.Total }).ToList(),
            TopForms = (await grid.ReadAsync<WebLeadTopFormDto>()).ToList(),
            Sources = (await grid.ReadAsync<(string Source, int Total)>()).Select(x => new WebLeadCountDto { Name = x.Source, Total = x.Total }).ToList(),
            Workload = (await grid.ReadAsync<WebLeadWorkloadDto>()).ToList(),
            Recent = (await grid.ReadAsync<WebLeadRecentDto>()).ToList(),
        };
    });

    public Task<List<WebLeadAssigneeDto>> GetAssigneesAsync(CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        return (await cn.QueryAsync<WebLeadAssigneeDto>(Proc("dbo.usp_WebLead_Assignees", null, ct))).ToList();
    });

    public Task<List<WebFormDto>> ListFormsAsync(CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        return (await cn.QueryAsync<WebFormDto>(Proc("dbo.usp_WebForm_List", null, ct))).ToList();
    });

    public Task UpdateFormAsync(int webFormId, bool isActive, string? notifyEmail, string changedBy, CancellationToken ct = default) => Run(async () =>
    {
        await using var cn = Open();
        await cn.ExecuteAsync(Proc("dbo.usp_WebForm_Update", new
        {
            WebFormId = webFormId,
            IsActive = isActive,
            NotifyEmail = Clean(notifyEmail, 400),
            ChangedBy = Clean(changedBy, 100) ?? "admin",
        }, ct));
    });
}
