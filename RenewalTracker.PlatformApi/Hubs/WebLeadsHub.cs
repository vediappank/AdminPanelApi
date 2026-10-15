using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Common;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.PlatformApi.Hubs;

/// <summary>
/// Real-time Website Leads alerts for the Admin Panel - /hubs/web-leads.
///
/// The Angular app connects once after login (WebLeadsRealtimeService) with
/// its platform JWT (sent as ?access_token=..., see Program.cs
/// JwtBearerEvents.OnMessageReceived). Only platform operators whose role
/// grants one of the WEBSITE modules join the "web-leads" group; everyone
/// else is disconnected straight away.
///
/// Server → client events:
///   "WebLeadReceived" (WebLeadAlertDto)  - a new enquiry / CV arrived from the website
///   "WebLeadUpdated"  ({ kind, id })     - someone read / assigned / changed status, so bells refresh
/// </summary>
[Authorize]
public class WebLeadsHub : Hub
{
    public const string Path = "/hubs/web-leads";
    public const string Group = "web-leads";
    public const string ReceivedEvent = "WebLeadReceived";
    public const string UpdatedEvent = "WebLeadUpdated";

    public static readonly string[] ModuleCodes = { "WEBSITE", "WEBSITE_DASHBOARD", "WEBSITE_ENQUIRIES", "WEBSITE_JOBS", "WEBSITE_FORMS" };

    private readonly PlatformDbContext _db;
    private readonly ILogger<WebLeadsHub> _logger;

    public WebLeadsHub(PlatformDbContext db, ILogger<WebLeadsHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Same rule as [RequirePlatformModule]: a genuine platform-operator token
        // (platform_user_id claim) whose role currently grants a WEBSITE module.
        var userIdClaim = Context.User?.FindFirst(AppClaimTypes.PlatformUserId)?.Value;
        if (!int.TryParse(userIdClaim, out var userId) || !await HasWebsiteAccessAsync(userId))
        {
            _logger.LogInformation("WebLeadsHub: connection {ConnectionId} refused (user {UserId} has no Website Leads access)",
                Context.ConnectionId, userIdClaim ?? "-");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        await base.OnConnectedAsync();
    }

    private async Task<bool> HasWebsiteAccessAsync(int platformUserId)
    {
        var roleId = await _db.PlatformUsers.AsNoTracking()
            .Where(u => u.PlatformUserId == platformUserId)
            .Select(u => u.PlatformRoleId)
            .FirstOrDefaultAsync();
        if (roleId == null) return false;

        return await _db.PlatformRoleModules.AsNoTracking().AnyAsync(rm =>
            rm.PlatformRoleId == roleId.Value &&
            ModuleCodes.Contains(rm.Module.ModuleCode) &&
            rm.Module.IsActive);
    }
}
