using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// The website chat window talks to the AI assistant here (js/bp-chat.js):
///   POST api/public/chat/message   { sessionId, message }   optional header X-BP-Session (the signed-in visitor)
///   -> { output }   or 503 { message } when no AI provider could answer (the widget then falls back to the FAQ answers).
/// Limits (in memory, per network and per chat session) keep AI usage and cost under control.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors(PublicWebFormsController.WebsiteCorsPolicy)]
[Route("api/public/chat")]
public class PublicChatAgentController : ControllerBase
{
    private const int MaxMessageLength = 600;
    private const int PerSessionPer10Min = 25;
    private const int PerIpPer10Min = 60;
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Start)> Hits = new();

    private readonly ChatAgentService _agent;
    public PublicChatAgentController(ChatAgentService agent) => _agent = agent;

    public sealed record MessageRequest(string? SessionId, string? Message);

    private static bool Allowed(string key, int limit)
    {
        var now = DateTime.UtcNow;
        var e = Hits.AddOrUpdate(key, _ => (1, now), (_, old) => now - old.Start > TimeSpan.FromMinutes(10) ? (1, now) : (old.Count + 1, old.Start));
        if (Hits.Count > 5000) foreach (var k in Hits.Where(x => now - x.Value.Start > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList()) Hits.TryRemove(k, out _);
        return e.Count <= limit;
    }

    [HttpPost("message")]
    public async Task<IActionResult> Message([FromBody] MessageRequest req, CancellationToken ct)
    {
        if (!_agent.Ready) return StatusCode(503, new { message = "The assistant is not available right now." });
        var text = (req.Message ?? string.Empty).Trim();
        if (text.Length == 0) return BadRequest(new { message = "Please type your question." });
        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];
        if (!ChatMemoryStore.ValidSessionId(req.SessionId)) return BadRequest(new { message = "Invalid chat session." });

        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var ip = !string.IsNullOrWhiteSpace(forwarded) ? forwarded.Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!Allowed("s:" + req.SessionId, PerSessionPer10Min) || !Allowed("i:" + ip, PerIpPer10Min))
            return StatusCode(429, new { message = "You are sending messages very quickly. Please wait a few minutes, or WhatsApp us on +971 58 997 0732." });

        var reply = await _agent.AskAsync(req.SessionId!, text, Request.Headers[PublicSubscribersController.SessionHeader].ToString(), ct);
        return reply.Output != null ? Ok(new { output = reply.Output }) : StatusCode(503, new { message = reply.Error });
    }
}
