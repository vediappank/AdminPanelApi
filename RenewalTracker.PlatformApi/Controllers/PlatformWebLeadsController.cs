using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.WebLeads;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Hubs;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel ▸ Website Leads - enquiries, CRM requests and CVs submitted on
/// blisspointgroup.com (see PublicWebFormsController). Every call goes through
/// the dbo.usp_Web* stored procedures (IWebLeadRepository).
///
/// Access: any of the WEBSITE / WEBSITE_DASHBOARD / WEBSITE_ENQUIRIES / WEBSITE_JOBS / WEBSITE_FORMS
/// modules (granted to every role that has SALES by the install script).
/// Changes are broadcast over SignalR ("WebLeadUpdated") so every operator's
/// bell and list stay in step.
/// </summary>
[ApiController]
[Route("api/platform/web-leads")]
[RequirePlatformModule("WEBSITE", "WEBSITE_DASHBOARD", "WEBSITE_ENQUIRIES", "WEBSITE_JOBS", "WEBSITE_FORMS")]
[WebLeadExceptionFilter]
public class PlatformWebLeadsController : ControllerBase
{
    private readonly IWebLeadRepository _repo;
    private readonly ICurrentPlatformUserService _currentUser;
    private readonly WebLeadNotifier _notifier;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public PlatformWebLeadsController(IWebLeadRepository repo, ICurrentPlatformUserService currentUser, WebLeadNotifier notifier,
        IConfiguration config, IWebHostEnvironment env)
    {
        _repo = repo;
        _currentUser = currentUser;
        _notifier = notifier;
        _config = config;
        _env = env;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    // ----- bell / dashboard -----

    /// <summary>Unread counters, New-by-category and the latest 10 unread items (bell dropdown).</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<WebLeadSummaryDto>> Summary(CancellationToken ct) =>
        Ok(await _repo.GetSummaryAsync(ct));

    /// <summary>Website Leads dashboard for the last N days (7 / 30 / 90): KPIs, daily trend, business, pipeline, forms, sources, team.</summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<WebLeadDashboardDto>> Dashboard([FromQuery] int days = 30, CancellationToken ct = default) =>
        Ok(await _repo.GetDashboardAsync(days, ct));

    [HttpGet("assignees")]
    public async Task<ActionResult<List<WebLeadAssigneeDto>>> Assignees(CancellationToken ct) =>
        Ok(await _repo.GetAssigneesAsync(ct));

    /// <summary>Marks every enquiry and job application as read (bell ▸ "Mark all read").</summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await _repo.MarkAllReadAsync(Me, ct);
        await _notifier.LeadUpdatedAsync("All", null);
        return NoContent();
    }

    // ----- enquiries -----

    [HttpGet("enquiries")]
    public async Task<ActionResult<PagedResult<WebEnquiryListItemDto>>> Enquiries([FromQuery] WebEnquiryListQuery query, CancellationToken ct) =>
        Ok(await _repo.ListEnquiriesAsync(query, ct));

    [HttpGet("enquiries/{id:long}")]
    public async Task<ActionResult<WebEnquiryDetailDto>> Enquiry(long id, CancellationToken ct)
    {
        var detail = await _repo.GetEnquiryAsync(id, ct);
        return detail == null ? NotFound(new { message = "Enquiry not found." }) : Ok(detail);
    }

    [HttpPost("enquiries/{id:long}/read")]
    public async Task<IActionResult> ReadEnquiry(long id, CancellationToken ct)
    {
        await _repo.MarkEnquiryReadAsync(id, Me, ct);
        await _notifier.LeadUpdatedAsync("Enquiry", id);
        return NoContent();
    }

    [HttpPut("enquiries/{id:long}/status")]
    public async Task<ActionResult<WebEnquiryDetailDto>> EnquiryStatus(long id, [FromBody] WebStatusUpdateRequest request, CancellationToken ct)
    {
        await _repo.UpdateEnquiryStatusAsync(id, request.Status?.Trim() ?? string.Empty, request.Note, Me, ct);
        await _notifier.LeadUpdatedAsync("Enquiry", id);
        return Ok(await _repo.GetEnquiryAsync(id, ct));
    }

    [HttpPut("enquiries/{id:long}/assign")]
    public async Task<ActionResult<WebEnquiryDetailDto>> AssignEnquiry(long id, [FromBody] WebEnquiryAssignRequest request, CancellationToken ct)
    {
        await _repo.AssignEnquiryAsync(id, request.AssignedToUserId, Me, ct);
        await _notifier.LeadUpdatedAsync("Enquiry", id);
        return Ok(await _repo.GetEnquiryAsync(id, ct));
    }

    /// <summary>Copies the enquiry into Sales ▸ Enquiries (dbo.SalesEnquiries, Source = Website). Safe to call twice.</summary>
    [HttpPost("enquiries/{id:long}/convert-to-sales")]
    public async Task<ActionResult<WebEnquiryConvertResult>> ConvertToSales(long id, CancellationToken ct)
    {
        var result = await _repo.ConvertToSalesEnquiryAsync(id, Me, ct);
        await _notifier.LeadUpdatedAsync("Enquiry", id);
        return Ok(result);
    }

    // ----- job applications -----

    [HttpGet("job-applications")]
    public async Task<ActionResult<PagedResult<WebJobApplicationListItemDto>>> JobApplications([FromQuery] WebJobApplicationListQuery query, CancellationToken ct) =>
        Ok(await _repo.ListJobApplicationsAsync(query, ct));

    [HttpGet("job-applications/{id:long}")]
    public async Task<ActionResult<WebJobApplicationDetailDto>> JobApplication(long id, CancellationToken ct)
    {
        var detail = await _repo.GetJobApplicationAsync(id, ct);
        return detail == null ? NotFound(new { message = "Job application not found." }) : Ok(detail);
    }

    [HttpPost("job-applications/{id:long}/read")]
    public async Task<IActionResult> ReadJobApplication(long id, CancellationToken ct)
    {
        await _repo.MarkJobApplicationReadAsync(id, Me, ct);
        await _notifier.LeadUpdatedAsync("JobApplication", id);
        return NoContent();
    }

    [HttpPut("job-applications/{id:long}/status")]
    public async Task<ActionResult<WebJobApplicationDetailDto>> JobApplicationStatus(long id, [FromBody] WebStatusUpdateRequest request, CancellationToken ct)
    {
        await _repo.UpdateJobApplicationStatusAsync(id, request.Status?.Trim() ?? string.Empty, request.Note, Me, ct);
        await _notifier.LeadUpdatedAsync("JobApplication", id);
        return Ok(await _repo.GetJobApplicationAsync(id, ct));
    }

    /// <summary>Streams the uploaded CV (stored outside wwwroot - never publicly reachable).</summary>
    [HttpGet("job-applications/{id:long}/cv")]
    public async Task<IActionResult> DownloadCv(long id, CancellationToken ct)
    {
        var cv = await _repo.GetJobApplicationCvAsync(id, ct);
        if (cv == null) return NotFound(new { message = "Job application not found." });

        var folder = Path.GetFullPath(PublicWebFormsController.CvFolder(_config, _env));
        var fullPath = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(cv.CvFilePath)));
        if (!fullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(fullPath))
            return NotFound(new { message = "The CV file is no longer on the server." });

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return File(stream, cv.CvContentType ?? "application/octet-stream", cv.CvFileName);
    }

    // ----- website forms -----

    [HttpGet("forms")]
    public async Task<ActionResult<List<WebFormDto>>> Forms(CancellationToken ct) =>
        Ok(await _repo.ListFormsAsync(ct));

    [HttpPut("forms/{id:int}")]
    [RequirePlatformModule("WEBSITE", "WEBSITE_FORMS")]
    public async Task<IActionResult> UpdateForm(int id, [FromBody] WebFormUpdateRequest request, CancellationToken ct)
    {
        await _repo.UpdateFormAsync(id, request.IsActive, request.NotifyEmail, Me, ct);
        return NoContent();
    }
}
