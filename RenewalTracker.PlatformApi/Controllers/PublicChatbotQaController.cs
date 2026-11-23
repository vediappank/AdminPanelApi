using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.ChatbotQa;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// The website chatbot (js/bp-chat.js) reads its published questions here:
///   GET api/public/chatbot-qa
/// Anonymous and read-only, "Website" CORS policy. Not cached, so a change in the Admin Panel shows at once.
/// </summary>
[ApiController]
[Route("api/public/chatbot-qa")]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
public class PublicChatbotQaController : ControllerBase
{
    private readonly IChatbotQaRepository _repo;

    public PublicChatbotQaController(IChatbotQaRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<ActionResult<List<PublicChatbotQaDto>>> List(CancellationToken ct) =>
        Ok(await _repo.ListPublishedAsync(ct));
}
