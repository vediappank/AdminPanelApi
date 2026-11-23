using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.ChatbotQa;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel ▸ Website Leads ▸ Chatbot Q&amp;A - add, edit, publish and delete the questions the website
/// chatbot answers. The website reads published items through PublicChatbotQaController.
/// Access: WEBSITE_CHATBOT module (DatabaseScripts/WebsiteChatbotQa_SuperAdminPanel.sql).
/// </summary>
[ApiController]
[Route("api/platform/chatbot-qa")]
[RequirePlatformModule("WEBSITE_CHATBOT", "WEBSITE")]
public class PlatformChatbotQaController : ControllerBase
{
    private static readonly string[] Statuses = { "Draft", "Published" };

    private readonly IChatbotQaRepository _repo;
    private readonly ICurrentPlatformUserService _currentUser;
    private readonly ILogger<PlatformChatbotQaController> _logger;

    public PlatformChatbotQaController(IChatbotQaRepository repo, ICurrentPlatformUserService currentUser,
        ILogger<PlatformChatbotQaController> logger)
    {
        _repo = repo;
        _currentUser = currentUser;
        _logger = logger;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email;

    [HttpGet]
    public async Task<ActionResult<List<ChatbotQaDto>>> List(CancellationToken ct) =>
        Ok(await _repo.ListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ChatbotQaDto>> Get(int id, CancellationToken ct)
    {
        var item = await _repo.GetAsync(id, ct);
        return item == null ? NotFound(new { message = "Question not found." }) : Ok(item);
    }

    [HttpPost]
    public Task<ActionResult<ChatbotQaDto>> Create([FromBody] ChatbotQaSaveRequest request, CancellationToken ct) =>
        Save(null, request, ct);

    [HttpPut("{id:int}")]
    public Task<ActionResult<ChatbotQaDto>> Update(int id, [FromBody] ChatbotQaSaveRequest request, CancellationToken ct) =>
        Save(id, request, ct);

    private async Task<ActionResult<ChatbotQaDto>> Save(int? id, ChatbotQaSaveRequest? r, CancellationToken ct)
    {
        if (r == null) return BadRequest(new { message = "Nothing to save." });

        r.Category = (r.Category ?? string.Empty).Trim();
        r.Question = (r.Question ?? string.Empty).Trim();
        r.Answer = (r.Answer ?? string.Empty).Trim();
        r.Status = Statuses.FirstOrDefault(s => s.Equals(r.Status?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Draft";

        var errors = new List<string>();
        if (r.Category.Length is < 2 or > 60) errors.Add("Topic must be 2 - 60 characters.");
        if (r.Question.Length is < 5 or > 300) errors.Add("Question must be 5 - 300 characters.");
        if (r.Answer.Length is < 5 or > 1000) errors.Add("Answer must be 5 - 1000 characters.");
        if (r.DisplayOrder is < 0 or > 9999) errors.Add("Order must be between 0 and 9999.");
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors), errors });

        var saved = await _repo.SaveAsync(id, r, Me, ct);
        if (saved == null) return NotFound(new { message = "Question not found." });
        _logger.LogInformation("Chatbot Q&A {Id} saved as {Status} by {User}", saved.ChatbotQaId, saved.Status, Me);
        return Ok(saved);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _repo.DeleteAsync(id, Me, ct);
        if (!deleted) return NotFound(new { message = "Question not found." });
        _logger.LogInformation("Chatbot Q&A {Id} deleted by {User}", id, Me);
        return NoContent();
    }
}
