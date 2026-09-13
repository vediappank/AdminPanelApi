using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.Common;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Auth;

public class PlatformCurrentUserService : ICurrentPlatformUserService
{
    private readonly IHttpContextAccessor _accessor;
    private readonly PlatformDbContext _db;

    public PlatformCurrentUserService(IHttpContextAccessor accessor, PlatformDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    private System.Security.Claims.ClaimsPrincipal? User => _accessor.HttpContext?.User;

    // Requires the platform_user_id claim specifically - a tenant-user
    // token (issued and validated exclusively by RenewalTracker.Api, which
    // this API shares no signing key or code with) never carries it.
    public bool IsAuthenticated =>
        (User?.Identity?.IsAuthenticated ?? false) && User?.FindFirst(AppClaimTypes.PlatformUserId) != null;

    public int PlatformUserId
    {
        get
        {
            var value = User?.FindFirst(AppClaimTypes.PlatformUserId)?.Value;
            return int.TryParse(value, out var id) ? id : 0;
        }
    }

    public string Email => User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;

    // Deliberately a live DB check rather than a JWT claim - the token's
    // claim set stays minimal (see PlatformJwtTokenService), and a role or
    // its module grants can be changed and take effect immediately, without
    // waiting for the user's token to expire and them to log in again.
    public async Task<bool> HasModuleAccessAsync(string moduleCode)
    {
        if (!IsAuthenticated) return false;

        var roleId = await _db.PlatformUsers.AsNoTracking()
            .Where(u => u.PlatformUserId == PlatformUserId)
            .Select(u => u.PlatformRoleId)
            .FirstOrDefaultAsync();
        if (roleId == null) return false;

        return await _db.PlatformRoleModules.AsNoTracking().AnyAsync(rm =>
            rm.PlatformRoleId == roleId.Value &&
            rm.Module.ModuleCode == moduleCode &&
            rm.Module.IsActive);
    }
}
