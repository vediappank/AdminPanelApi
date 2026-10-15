using System;
using System.Collections.Generic;

namespace RenewalTracker.Platform.Application.DTOs.WebLeads;

/// <summary>
/// Website Leads - everything submitted by a form on blisspointgroup.com.
/// Stored by the dbo.usp_Web* stored procedures (see
/// DatabaseScripts/WebsiteLeads_SuperAdminPanel.sql), read by the Admin Panel's
/// Website Leads screens, and pushed live to every signed-in operator over
/// SignalR (/hubs/web-leads, event "WebLeadReceived").
///
/// All *At columns are stored as UTC (SYSUTCDATETIME()).
/// </summary>

// =====================================================================
// Public (website) - anonymous
// =====================================================================

/// <summary>JSON body the website posts to api/public/web-forms/enquiry (js/bp-leads.js).</summary>
public class PublicEnquiryRequest
{
    public string FormCode { get; set; } = string.Empty;
    public string? PagePath { get; set; }
    public string? PageTitle { get; set; }
    /// <summary>Enquiry | Quote | Demo | Access</summary>
    public string? RequestType { get; set; }
    public string? ServiceInterest { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool ConsentGiven { get; set; }

    public string? Referrer { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }

    // Travel & Tourism forms
    public string? Destination { get; set; }
    public string? TravelDatesText { get; set; }
    public DateTime? DepartureDate { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public int? Travellers { get; set; }
    public int? Pilgrims { get; set; }
    public string? RoomType { get; set; }
    public string? VisaType { get; set; }

    // CRM demo / Get Access forms
    public string? TeamSize { get; set; }
    public string? BusinessType { get; set; }
    public List<string>? Modules { get; set; }

    /// <summary>Hidden honeypot field - real visitors never fill it. Filled = spam (stored, never alerted).</summary>
    public string? Website { get; set; }
}

/// <summary>Everything usp_WebEnquiry_Insert needs (request + server-side values).</summary>
public class WebEnquiryInsert
{
    public PublicEnquiryRequest Request { get; set; } = new();
    public string? SourcePage { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool IsSpam { get; set; }
}

/// <summary>Row returned by usp_WebEnquiry_Insert.</summary>
public class WebEnquiryInsertResult
{
    public long WebEnquiryId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public bool IsDuplicate { get; set; }
    public string FormName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string? ServiceInterest { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsSpam { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? NotifyEmail { get; set; }
}

/// <summary>Everything usp_WebJobApplication_Insert needs (multipart careers form + saved CV).</summary>
public class WebJobApplicationInsert
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoleApplied { get; set; } = string.Empty;
    public string? Experience { get; set; }
    public string? CoverNote { get; set; }
    public string CvFileName { get; set; } = string.Empty;
    public string CvFilePath { get; set; } = string.Empty;
    public string? CvContentType { get; set; }
    public int? CvSizeBytes { get; set; }
    public string? SourcePage { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool IsSpam { get; set; }
}

/// <summary>Row returned by usp_WebJobApplication_Insert.</summary>
public class WebJobApplicationInsertResult
{
    public long WebJobApplicationId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoleApplied { get; set; } = string.Empty;
    public string? Experience { get; set; }
    public bool IsSpam { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>What the website gets back - just enough to show "Thank you, your reference is WE-2610-000123".</summary>
public class PublicSubmitResponse
{
    public string ReferenceNo { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

// =====================================================================
// Real-time alert (SignalR "WebLeadReceived")
// =====================================================================

public class WebLeadAlertDto
{
    /// <summary>Enquiry | JobApplication</summary>
    public string Kind { get; set; } = "Enquiry";
    public long Id { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SubTitle { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? FormName { get; set; }
    public string? RequestType { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }
}

// =====================================================================
// Admin Panel - common
// =====================================================================

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public class WebStatusHistoryDto
{
    public long WebStatusHistoryId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
}

public class WebStatusUpdateRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
}

// =====================================================================
// Admin Panel - enquiries
// =====================================================================

public class WebEnquiryListQuery
{
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? RequestType { get; set; }
    public int? WebFormId { get; set; }
    public int? AssignedToUserId { get; set; }
    public string? Search { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public bool UnreadOnly { get; set; }
    public bool IncludeSpam { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class WebEnquiryListItemDto
{
    public long WebEnquiryId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string PagePath { get; set; } = string.Empty;
    public string? ServiceInterest { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? MessagePreview { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public bool IsRead { get; set; }
    public bool IsSpam { get; set; }
    public int? SalesEnquiryId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Internal: list row + COUNT(*) OVER () from usp_WebEnquiry_List.</summary>
public class WebEnquiryListRow : WebEnquiryListItemDto
{
    public int TotalCount { get; set; }
}

public class WebEnquiryDetailDto
{
    public long WebEnquiryId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public int WebFormId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string PagePath { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? ServiceInterest { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool ConsentGiven { get; set; }
    public string? SourcePage { get; set; }
    public string? Referrer { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool IsSpam { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public string? StatusNote { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? ReadBy { get; set; }
    public int? SalesEnquiryId { get; set; }
    public int? ConvertedTenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    // travel
    public string? Destination { get; set; }
    public string? TravelDatesText { get; set; }
    public DateTime? DepartureDate { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public short? Travellers { get; set; }
    public short? Pilgrims { get; set; }
    public string? RoomType { get; set; }
    public string? VisaType { get; set; }

    // CRM
    public string? TeamSize { get; set; }
    public string? BusinessType { get; set; }
    public DateTime? ProvisionedAt { get; set; }

    public List<string> Modules { get; set; } = new();
    public List<WebStatusHistoryDto> History { get; set; } = new();
}

public class WebEnquiryAssignRequest
{
    /// <summary>null = unassign</summary>
    public int? AssignedToUserId { get; set; }
}

public class WebEnquiryAssignResult
{
    public int? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
}

public class WebEnquiryConvertResult
{
    public int SalesEnquiryId { get; set; }
    public bool AlreadyConverted { get; set; }
}

// =====================================================================
// Admin Panel - job applications
// =====================================================================

public class WebJobApplicationListQuery
{
    public string? Status { get; set; }
    public string? RoleApplied { get; set; }
    public string? Search { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public bool UnreadOnly { get; set; }
    public bool IncludeSpam { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class WebJobApplicationListItemDto
{
    public long WebJobApplicationId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoleApplied { get; set; } = string.Empty;
    public string? Experience { get; set; }
    public string CvFileName { get; set; } = string.Empty;
    public int? CvSizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public bool IsSpam { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Internal: list row + COUNT(*) OVER () from usp_WebJobApplication_List.</summary>
public class WebJobApplicationListRow : WebJobApplicationListItemDto
{
    public int TotalCount { get; set; }
}

public class WebJobApplicationDetailDto
{
    public long WebJobApplicationId { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoleApplied { get; set; } = string.Empty;
    public string? Experience { get; set; }
    public string? CoverNote { get; set; }
    public string CvFileName { get; set; } = string.Empty;
    public string? CvContentType { get; set; }
    public int? CvSizeBytes { get; set; }
    public string? SourcePage { get; set; }
    public string? IpAddress { get; set; }
    public bool IsSpam { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StatusNote { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? ReadBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public List<WebStatusHistoryDto> History { get; set; } = new();
}

/// <summary>Internal only - the stored CV file name is never sent to the browser.</summary>
public class WebJobApplicationCvInfo
{
    public string CvFileName { get; set; } = string.Empty;
    public string CvFilePath { get; set; } = string.Empty;
    public string? CvContentType { get; set; }
}

// =====================================================================
// Admin Panel - bell / summary, assignees, forms
// =====================================================================

public class WebLeadCountersDto
{
    public int UnreadEnquiries { get; set; }
    public int UnreadJobApplications { get; set; }
    public int NewEnquiries { get; set; }
    public int EnquiriesToday { get; set; }
    public int JobApplicationsToday { get; set; }
    public int EnquiriesLast30Days { get; set; }
}

public class WebLeadCategoryCountDto
{
    public string Category { get; set; } = string.Empty;
    public int NewCount { get; set; }
}

public class WebLeadLatestDto
{
    public string Kind { get; set; } = "Enquiry";
    public long Id { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SubTitle { get; set; }
    public string Category { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class WebLeadSummaryDto
{
    public WebLeadCountersDto Counters { get; set; } = new();
    public int UnreadTotal => Counters.UnreadEnquiries + Counters.UnreadJobApplications;
    public List<WebLeadCategoryCountDto> NewByCategory { get; set; } = new();
    public List<WebLeadLatestDto> Latest { get; set; } = new();
}

// =====================================================================
// Admin Panel - Website Leads dashboard (usp_WebLead_Dashboard)
// =====================================================================

public class WebLeadDashboardKpisDto
{
    public int Enquiries { get; set; }
    public int EnquiriesPrevious { get; set; }
    public int EnquiriesToday { get; set; }
    public int OpenNew { get; set; }
    public int UnreadEnquiries { get; set; }
    public int Converted { get; set; }
    public int Responded { get; set; }
    public double? AvgFirstResponseMinutes { get; set; }
    public int JobApplications { get; set; }
    public int UnreadJobApplications { get; set; }
    public int SpamBlocked { get; set; }
    public int Days { get; set; }
}

public class WebLeadTrendPointDto
{
    public DateTime Day { get; set; }
    public int Enquiries { get; set; }
    public int JobApplications { get; set; }
}

public class WebLeadByCategoryDto
{
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Converted { get; set; }
    public int OpenNew { get; set; }
}

public class WebLeadCountDto
{
    /// <summary>Status / RequestType / Source - whichever the list is grouped by.</summary>
    public string Name { get; set; } = string.Empty;
    public int Total { get; set; }
}

public class WebLeadTopFormDto
{
    public int WebFormId { get; set; }
    public string FormName { get; set; } = string.Empty;
    public string PagePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Converted { get; set; }
}

public class WebLeadWorkloadDto
{
    public string AssignedTo { get; set; } = string.Empty;
    public int? AssignedToUserId { get; set; }
    public int OpenCount { get; set; }
    public int ConvertedInPeriod { get; set; }
    public int Total { get; set; }
}

public class WebLeadRecentDto
{
    public string Kind { get; set; } = "Enquiry";
    public long Id { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SubTitle { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class WebLeadDashboardDto
{
    public WebLeadDashboardKpisDto Kpis { get; set; } = new();
    public List<WebLeadTrendPointDto> Trend { get; set; } = new();
    public List<WebLeadByCategoryDto> ByCategory { get; set; } = new();
    public List<WebLeadCountDto> ByStatus { get; set; } = new();
    public List<WebLeadCountDto> ByRequestType { get; set; } = new();
    public List<WebLeadTopFormDto> TopForms { get; set; } = new();
    public List<WebLeadCountDto> Sources { get; set; } = new();
    public List<WebLeadWorkloadDto> Workload { get; set; } = new();
    public List<WebLeadRecentDto> Recent { get; set; } = new();
}

public class WebLeadAssigneeDto
{
    public int PlatformUserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class WebFormDto
{
    public int WebFormId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string PagePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public string? NotifyEmail { get; set; }
    public bool IsActive { get; set; }
    public int TotalSubmissions { get; set; }
    public int Last30Days { get; set; }
    public DateTime? LastSubmittedAt { get; set; }
}

public class WebFormUpdateRequest
{
    public bool IsActive { get; set; }
    /// <summary>Comma-separated department inboxes that also get an email for every submission (optional).</summary>
    public string? NotifyEmail { get; set; }
}
