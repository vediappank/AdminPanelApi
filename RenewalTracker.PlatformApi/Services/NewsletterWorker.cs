namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Sends new blog / news posts to website subscribers automatically.
///
///   * checks every 5 minutes;
///   * only between Newsletter:SendFromHour and SendToHour, UAE time (default 9:00-20:00) - a post published
///     at night goes out the next morning;
///   * at most ONE automatic message a day: posts published the same day go out together;
///   * a post goes out once: it is marked as sent before sending, so an edit or a restart never sends it again;
///   * posts with "Send to subscribers" switched off (Admin Panel blog editor) are skipped;
///   * an email address that fails 3 times in a row is switched off.
/// Switch off with "Newsletter": { "Enabled": false }.
/// </summary>
public sealed class NewsletterWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UaeOffset = TimeSpan.FromHours(4);   // UAE has no daylight saving

    private readonly SubscriberStore _store;
    private readonly SubscriberMessenger _messenger;
    private readonly ILogger<NewsletterWorker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NewsletterWorker(SubscriberStore store, SubscriberMessenger messenger, ILogger<NewsletterWorker> logger)
    {
        _store = store;
        _messenger = messenger;
        _logger = logger;
    }

    public static DateTime UaeNow => DateTime.UtcNow + UaeOffset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Newsletter: check failed - will try again"); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Why nothing goes out right now (Admin Panel), or null when the next check may send.</summary>
    public async Task<string?> WaitingReasonAsync(CancellationToken ct)
    {
        if (!_messenger.AutoSendEnabled) return "Automatic sending is switched off (Newsletter:Enabled).";
        if (!_messenger.EmailReady && !_messenger.WhatsAppReady) return "No email (SMTP) or WhatsApp account is set up.";
        var now = UaeNow;
        if (now.Hour < _messenger.SendFromHour || now.Hour >= _messenger.SendToHour)
            return $"Outside sending hours ({_messenger.SendFromHour:00}:00-{_messenger.SendToHour:00}:00 UAE time).";
        var last = await _store.LastAutoSendAsync(ct);
        if (last != null && (last.Value + UaeOffset).Date == now.Date) return "Today's message has already gone out - the next one goes tomorrow.";
        return null;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (await WaitingReasonAsync(ct) != null) return;
            var posts = await _store.DuePostsAsync(ct);
            if (posts.Count == 0) return;
            var people = await _store.RecipientsAsync(ct);
            var sendId = await _store.StartSendAsync("posts", posts, people.Count, "automatic", ct);
            await _store.MarkPostsSentAsync(posts.Select(p => p.BlogPostId), sendId, ct);
            _logger.LogInformation("Newsletter: sending {Posts} post(s) to {People} subscriber(s) (send #{SendId})", posts.Count, people.Count, sendId);

            int emailSent = 0, emailFailed = 0, waSent = 0, waFailed = 0;
            foreach (var person in people)
            {
                ct.ThrowIfCancellationRequested();
                var r = await _messenger.SendPostsAsync(person, posts, ct);
                if (r.Email == true) emailSent++; else if (r.Email == false) emailFailed++;
                if (r.WhatsApp == true) waSent++; else if (r.WhatsApp == false) waFailed++;
                if (r.Email == true || r.EmailBounced) await _store.RecordEmailResultAsync(person.SubscriberId, r.Email == true, ct);
                await Task.Delay(150, ct);   // gentle on the SMTP service's rate limit
            }
            await _store.FinishSendAsync(sendId, emailSent, emailFailed, waSent, waFailed, ct);
            _logger.LogInformation("Newsletter: send #{SendId} done - email {EmailSent} sent / {EmailFailed} failed, WhatsApp {WaSent} / {WaFailed}",
                sendId, emailSent, emailFailed, waSent, waFailed);
        }
        finally
        {
            _gate.Release();
        }
    }
}
