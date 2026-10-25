using System.Text;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel > Website Leads > Subscribers (module WEBSITE_SUBSCRIBERS):
///   GET    api/platform/subscribers?search=&amp;status=&amp;page=&amp;pageSize=   list + counts
///   GET    api/platform/subscribers/overview                         setup status, next automatic message, recent sends
///   GET    api/platform/subscribers/export                           CSV of everyone
///   POST   api/platform/subscribers/{id}/unsubscribe                 stop all messages to this person
///   DELETE api/platform/subscribers/{id}                             remove the person and their data
///   POST   api/platform/subscribers/test                             { email, whatsapp } latest posts as a test message
///   POST   api/platform/subscribers/send-now                         run the automatic check now (still once a day, in hours)
///   GET/PUT api/platform/subscribers/posts/{blogPostId}              "Send to subscribers" switch of one blog post
/// </summary>
[ApiController]
[Route("api/platform/subscribers")]
[RequirePlatformModule("WEBSITE_SUBSCRIBERS", "WEBSITE_BLOG", "WEBSITE")]
public class PlatformSubscribersController : ControllerBase
{
    private readonly SubscriberStore _store;
    private readonly SubscriberMessenger _messenger;
    private readonly NewsletterWorker _worker;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformSubscribersController(SubscriberStore store, SubscriberMessenger messenger, NewsletterWorker worker, ICurrentPlatformUserService currentUser)
    {
        _store = store;
        _messenger = messenger;
        _worker = worker;
        _currentUser = currentUser;
    }

    private string Me => string.IsNullOrWhiteSpace(_currentUser.Email) ? $"user#{_currentUser.PlatformUserId}" : _currentUser.Email!;

    public sealed record TestRequest(string? Email, string? WhatsApp);
    public sealed record PostSettingRequest(bool SendToSubscribers);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default) => Ok(await _store.ListAsync(search, status, page, pageSize, ct));

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var due = await _store.DuePostsAsync(ct);
        return Ok(new
        {
            emailReady = _messenger.EmailReady,
            whatsappReady = _messenger.WhatsAppReady,
            autoSend = _messenger.AutoSendEnabled,
            sendFromHour = _messenger.SendFromHour,
            sendToHour = _messenger.SendToHour,
            uaeNow = NewsletterWorker.UaeNow,
            waitingReason = await _worker.WaitingReasonAsync(ct),
            duePosts = due.Select(p => new { p.BlogPostId, p.Title, p.PublishedAt }),
            sends = await _store.SendsAsync(ct),
        });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var sb = new StringBuilder("Name,Email,WhatsApp,Language,Status,EmailVerified,WhatsAppVerified,EmailOn,WhatsAppOn,SignedUpUtc,ConsentUtc\r\n");
        foreach (var s in await _store.AllAsync(ct))
            sb.Append(string.Join(",", Csv(s.Name), Csv(s.Email), Csv(s.WhatsApp == null ? "" : "+" + s.WhatsApp), s.Language, s.Status,
                s.EmailVerifiedAt != null ? "yes" : "no", s.WhatsAppVerifiedAt != null ? "yes" : "no",
                s.EmailOptIn ? "yes" : "no", s.WhatsAppOptIn ? "yes" : "no",
                s.CreatedAt.ToString("yyyy-MM-dd HH:mm"), s.ConsentAt?.ToString("yyyy-MM-dd HH:mm") ?? "")).Append("\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"website-subscribers-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;          // no formulas in Excel
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    [HttpPost("{id:int}/unsubscribe")]
    public async Task<IActionResult> Unsubscribe(int id, CancellationToken ct)
    {
        await _store.UnsubscribeAsync(id, "all", ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>The latest posts as a "new posts" message to the given email / WhatsApp number - nothing is marked as sent.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestRequest req, CancellationToken ct)
    {
        var email = SubscriberStore.CleanEmail(req.Email);
        var phone = string.IsNullOrWhiteSpace(req.WhatsApp) ? null : WhatsAppCloud.NormalizePhone(req.WhatsApp, "971");
        if (email == null && phone == null) return BadRequest(new { message = "Enter an email address and / or a WhatsApp number." });
        var posts = await _store.DuePostsAsync(ct);
        if (posts.Count == 0) posts = await _store.LatestPostsAsync(2, ct);
        if (posts.Count == 0) return BadRequest(new { message = "There is no published post to send yet." });

        var fake = new SubscriberStore.Subscriber
        {
            Name = "Test", Email = email ?? "test@example.com", WhatsApp = phone, Language = "en", Status = "Active",
            EmailVerifiedAt = email != null ? DateTime.UtcNow : null, WhatsAppVerifiedAt = phone != null ? DateTime.UtcNow : null,
            EmailOptIn = email != null, WhatsAppOptIn = phone != null, UnsubscribeToken = new string('0', 32),
        };
        var sendId = await _store.StartSendAsync("test", posts, 1, Me, ct);
        var r = await _messenger.SendPostsAsync(fake, posts, ct);
        await _store.FinishSendAsync(sendId, r.Email == true ? 1 : 0, r.Email == false ? 1 : 0, r.WhatsApp == true ? 1 : 0, r.WhatsApp == false ? 1 : 0, ct);
        return Ok(new
        {
            email = r.Email, whatsapp = r.WhatsApp,
            message = (email == null ? "" : r.Email == true ? "Test email sent. " : "The test email could not be sent - check the SMTP settings. ")
                    + (phone == null ? "" : r.WhatsApp == true ? "Test WhatsApp sent." : "WhatsApp is not set up yet, or the message failed."),
        });
    }

    [HttpPost("send-now")]
    public async Task<IActionResult> SendNow(CancellationToken ct)
    {
        var reason = await _worker.WaitingReasonAsync(ct);
        if (reason != null) return BadRequest(new { message = reason });
        await _worker.RunOnceAsync(ct);
        return Ok(new { message = "Checked - any new posts have been sent." });
    }

    [HttpGet("posts/{blogPostId:int}")]
    public async Task<IActionResult> GetPost(int blogPostId, CancellationToken ct) => Ok(await _store.GetPostSettingAsync(blogPostId, ct));

    [HttpPut("posts/{blogPostId:int}")]
    public async Task<IActionResult> SetPost(int blogPostId, [FromBody] PostSettingRequest req, CancellationToken ct)
    {
        await _store.SetPostSettingAsync(blogPostId, req.SendToSubscribers, Me, ct);
        return Ok(await _store.GetPostSettingAsync(blogPostId, ct));
    }
}
