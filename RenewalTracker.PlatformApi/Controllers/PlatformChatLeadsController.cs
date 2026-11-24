using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel ▸ Website Leads ▸ AI chat follow-ups. What the website AI chat recorded (see PublicChatController) for the team to act on.
///
///   GET  api/platform/web-leads/chat/summary               { open, overdue, dueToday }
///   GET  api/platform/web-leads/chat/tasks?status=Open&amp;type=question|offer|quotation&amp;take=200
///   POST api/platform/web-leads/chat/tasks/{id}/done       the team replied / sent the offer or quotation
///   GET  api/platform/web-leads/chat/enquiries/{id}        questions asked, draft price list, quotation and decision of one lead
///
/// Same access as the other Website Leads calls.
/// </summary>
[ApiController]
[Route("api/platform/web-leads/chat")]
[RequirePlatformModule("WEBSITE", "WEBSITE_DASHBOARD", "WEBSITE_ENQUIRIES", "WEBSITE_JOBS", "WEBSITE_FORMS")]
[WebLeadExceptionFilter]
public class PlatformChatLeadsController : ControllerBase
{
    private readonly ChatLeadStore _store;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformChatLeadsController(ChatLeadStore store, ICurrentPlatformUserService currentUser)
    {
        _store = store; _currentUser = currentUser;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var s = await _store.TaskStatsAsync(ct);
        return Ok(new { open = s.Open, overdue = s.Overdue, dueToday = s.DueToday });
    }

    [HttpGet("tasks")]
    public async Task<IActionResult> Tasks([FromQuery] string? status = "Open", [FromQuery] string? type = null, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        status = status is "Open" or "Done" ? status : null;
        type = type is "question" or "offer" or "quotation" ? type : null;
        return Ok(await _store.ListTasksAsync(status, type, take, ct));
    }

    [HttpPost("tasks/{id:long}/done")]
    public async Task<IActionResult> Done(long id, CancellationToken ct) =>
        await _store.CompleteTaskAsync(id, Me, ct) ? NoContent() : NotFound(new { message = "Task not found or already done." });

    [HttpGet("enquiries/{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken ct)
    {
        var d = await _store.ChatDetailAsync(id, ct);
        return d == null ? NotFound(new { message = "This lead has no chat journey." }) : Ok(d);
    }
}
