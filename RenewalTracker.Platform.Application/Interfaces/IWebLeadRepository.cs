using RenewalTracker.Platform.Application.DTOs.WebLeads;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>
/// Website Leads data access - a thin Dapper wrapper over the dbo.usp_Web*
/// stored procedures (DatabaseScripts/WebsiteLeads_SuperAdminPanel.sql).
/// Business errors raised by the procedures (THROW 50001/50002/50010/50404)
/// surface as <see cref="WebLeadException"/> with a matching HTTP status.
/// </summary>
public interface IWebLeadRepository
{
    // website (anonymous)
    Task<WebEnquiryInsertResult> InsertEnquiryAsync(WebEnquiryInsert input, CancellationToken ct = default);
    Task<WebJobApplicationInsertResult> InsertJobApplicationAsync(WebJobApplicationInsert input, CancellationToken ct = default);

    // enquiries
    Task<PagedResult<WebEnquiryListItemDto>> ListEnquiriesAsync(WebEnquiryListQuery query, CancellationToken ct = default);
    Task<WebEnquiryDetailDto?> GetEnquiryAsync(long id, CancellationToken ct = default);
    Task UpdateEnquiryStatusAsync(long id, string status, string? note, string changedBy, CancellationToken ct = default);
    Task<WebEnquiryAssignResult> AssignEnquiryAsync(long id, int? assignedToUserId, string changedBy, CancellationToken ct = default);
    Task MarkEnquiryReadAsync(long id, string readBy, CancellationToken ct = default);
    Task MarkAllReadAsync(string readBy, CancellationToken ct = default);
    Task<WebEnquiryConvertResult> ConvertToSalesEnquiryAsync(long id, string changedBy, CancellationToken ct = default);

    // job applications
    Task<PagedResult<WebJobApplicationListItemDto>> ListJobApplicationsAsync(WebJobApplicationListQuery query, CancellationToken ct = default);
    Task<WebJobApplicationDetailDto?> GetJobApplicationAsync(long id, CancellationToken ct = default);
    Task<WebJobApplicationCvInfo?> GetJobApplicationCvAsync(long id, CancellationToken ct = default);
    Task UpdateJobApplicationStatusAsync(long id, string status, string? note, string changedBy, CancellationToken ct = default);
    Task MarkJobApplicationReadAsync(long id, string readBy, CancellationToken ct = default);

    // bell, assignees, forms
    Task<WebLeadSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<WebLeadDashboardDto> GetDashboardAsync(int days, CancellationToken ct = default);
    Task<List<WebLeadAssigneeDto>> GetAssigneesAsync(CancellationToken ct = default);
    Task<List<WebFormDto>> ListFormsAsync(CancellationToken ct = default);
    Task UpdateFormAsync(int webFormId, bool isActive, string? notifyEmail, string changedBy, CancellationToken ct = default);
}

/// <summary>A business rule refused by a Website Leads stored procedure - carries the HTTP status to return.</summary>
public class WebLeadException : Exception
{
    public int StatusCode { get; }

    public WebLeadException(int statusCode, string message, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
