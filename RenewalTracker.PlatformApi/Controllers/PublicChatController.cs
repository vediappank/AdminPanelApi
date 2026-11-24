using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Actions of the website AI chat (n8n tools). Called by n8n, always with the visitor's website session in header X-BP-Session.
///
///   POST api/public/chat/questions            { question, matched }
///   POST api/public/chat/follow-up            { type: question|offer, hours: 24|48, note }
///   POST api/public/chat/priority             { service }
///   POST api/public/chat/draft-price-list     { service, summary }
///   POST api/public/chat/quotation            {}   needs a draft price list first
///   POST api/public/chat/decision             { decision: approved|rejected }   needs a quotation first
///
/// No session -> 401 (the agent then asks the visitor to log in). A session whose email or WhatsApp is not verified -> 403.
/// Each action works on the visitor's own open lead (a normal website lead, dbo.WebEnquiry); nothing here takes a lead id.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/chat")]
public class PublicChatController : ControllerBase
{
    private readonly SubscriberStore _subscribers;
    private readonly SubscriberMessenger _messenger;
    private readonly ChatLeadStore _leads;

    public PublicChatController(SubscriberStore subscribers, SubscriberMessenger messenger, ChatLeadStore leads)
    {
        _subscribers = subscribers; _messenger = messenger; _leads = leads;
    }

    public sealed record QuestionRequest(string? Question, bool Matched);
    public sealed record FollowUpRequest(string? Type, int Hours, string? Note);
    public sealed record PriorityRequest(string? Service);
    public sealed record DraftRequest(string? Service, string? Summary);
    public sealed record DecisionRequest(string? Decision);

    private static IActionResult Bad(string message) => new BadRequestObjectResult(new { ok = false, message });

    /// <summary>The verified visitor behind the session, or the error to return.</summary>
    private async Task<(SubscriberStore.Subscriber? Person, IActionResult? Error)> VisitorAsync(CancellationToken ct)
    {
        var me = await _subscribers.FromSessionAsync(Request.Headers[PublicSubscribersController.SessionHeader].ToString().Trim(), ct);
        if (me == null)
            return (null, new ObjectResult(new { ok = false, needsLogin = true, message = "The visitor is not logged in. Ask them to log in or create an account at https://blisspointworld.com/my-account.html." }) { StatusCode = 401 });
        if (me.EmailVerifiedAt == null || (_messenger.WhatsAppReady && me.WhatsAppVerifiedAt == null) || string.IsNullOrEmpty(me.WhatsApp))
            return (null, new ObjectResult(new { ok = false, needsVerification = true, message = "The visitor's email and WhatsApp number must both be verified. Ask them to verify them in My account." }) { StatusCode = 403 });
        return (me, null);
    }

    [HttpPost("questions")]
    public async Task<IActionResult> LogQuestion([FromBody] QuestionRequest req, CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        if (string.IsNullOrWhiteSpace(req.Question)) return Bad("Question is empty.");
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        return await _leads.LogQuestionAsync(lead, req.Question, req.Matched, ct)
            ? Ok(new { ok = true, message = "Question logged." })
            : StatusCode(429, new { ok = false, message = "Too many questions logged. Carry on without logging." });
    }

    [HttpPost("follow-up")]
    public async Task<IActionResult> FollowUp([FromBody] FollowUpRequest req, CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        var type = (req.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("question" or "offer")) return Bad("type must be question or offer.");
        var hours = type == "question" ? 24 : (req.Hours == 48 ? 48 : 24);   // question: always 24 hours; offer: 24 or 48
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        var due = await _leads.CreateTaskAsync(lead, type, hours, req.Note, ct);
        return due == null
            ? StatusCode(429, new { ok = false, message = "A follow-up is already booked. Tell the visitor the team will reply soon." })
            : Ok(new { ok = true, hours, message = $"Follow-up booked. The team replies within {hours} hours." });
    }

    [HttpPost("priority")]
    public async Task<IActionResult> Priority([FromBody] PriorityRequest req, CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        if (string.IsNullOrWhiteSpace(req.Service)) return Bad("Service is empty.");
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        await _leads.SetPriorityAsync(lead, req.Service, ct);
        return Ok(new { ok = true, message = "Priority service saved." });
    }

    [HttpPost("draft-price-list")]
    public async Task<IActionResult> Draft([FromBody] DraftRequest req, CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        if (string.IsNullOrWhiteSpace(req.Service) || string.IsNullOrWhiteSpace(req.Summary)) return Bad("Service and summary are required.");
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        await _leads.SaveDraftAsync(lead, req.Service, req.Summary, ct);
        return Ok(new { ok = true, message = "Draft price list saved on the lead." });
    }

    [HttpPost("quotation")]
    public async Task<IActionResult> Quotation(CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        if (lead.DraftAt == null) return Bad("Save a draft price list first, then ask for the quotation.");
        if (lead.QuotationRequestedAt != null) return Ok(new { ok = true, message = "The quotation was already requested. The team sends it shortly." });
        await _leads.MarkQuotationAsync(lead, ct);
        await _leads.CreateTaskAsync(lead, "quotation", 24, "Prepare and send the quotation for the accepted draft price list.", ct);
        return Ok(new { ok = true, message = "Quotation requested. The team sends the formal quotation within 24 hours. Ask the visitor to approve or reject it when received." });
    }

    [HttpPost("decision")]
    public async Task<IActionResult> Decision([FromBody] DecisionRequest req, CancellationToken ct)
    {
        var (me, err) = await VisitorAsync(ct); if (err != null) return err;
        var d = (req.Decision ?? string.Empty).Trim().ToLowerInvariant();
        if (d is not ("approved" or "rejected")) return Bad("decision must be approved or rejected.");
        var lead = await _leads.GetOrCreateLeadAsync(me!, ct);
        if (lead.QuotationRequestedAt == null) return Bad("There is no quotation yet. Request the quotation first.");
        await _leads.RecordDecisionAsync(lead, d == "approved", ct);
        return Ok(new { ok = true, message = d == "approved" ? "Approved. A sales record was created and the team will start the work." : "Rejected. The lead is closed as rejected." });
    }
}
