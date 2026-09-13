using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Auth;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

public class PlatformAuthService : IPlatformAuthService
{
    private readonly PlatformDbContext _db;
    private readonly IPlatformJwtTokenService _tokenService;

    public PlatformAuthService(PlatformDbContext db, IPlatformJwtTokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
    }

    public async Task<PlatformLoginResponseDto?> LoginAsync(PlatformLoginRequestDto request)
    {
        var user = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.Email == request.Email && u.Status == "Active");
        if (user == null) return null;
        if (!PasswordHasher.Verify(request.Password, user.PasswordHash)) return null;

        var current = await ToDtoAsync(user);
        var (token, expiresAtUtc) = _tokenService.GenerateToken(current);
        return new PlatformLoginResponseDto { Token = token, ExpiresAtUtc = expiresAtUtc, User = current };
    }

    public async Task<CurrentPlatformUserDto?> GetCurrentAsync(int platformUserId)
    {
        var user = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.PlatformUserId == platformUserId);
        return user == null ? null : await ToDtoAsync(user);
    }

    public async Task<bool> ChangePasswordAsync(int platformUserId, string currentPassword, string newPassword)
    {
        var user = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.PlatformUserId == platformUserId);
        if (user == null) return false;
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash)) return false;

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    // Computed fresh on every login/me call - see CurrentPlatformUserDto.ModuleCodes
    // for why this isn't baked into the JWT.
    private async Task<CurrentPlatformUserDto> ToDtoAsync(Domain.Entities.PlatformUser user)
    {
        string? roleName = null;
        var moduleCodes = new List<string>();

        if (user.PlatformRoleId.HasValue)
        {
            roleName = await _db.PlatformRoles.AsNoTracking()
                .Where(r => r.PlatformRoleId == user.PlatformRoleId.Value)
                .Select(r => r.RoleName)
                .FirstOrDefaultAsync();

            moduleCodes = await _db.PlatformRoleModules.AsNoTracking()
                .Where(rm => rm.PlatformRoleId == user.PlatformRoleId.Value && rm.Module.IsActive)
                .Select(rm => rm.Module.ModuleCode)
                .ToListAsync();
        }

        return new CurrentPlatformUserDto
        {
            PlatformUserId = user.PlatformUserId,
            Email = user.Email,
            FullName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            PlatformRoleId = user.PlatformRoleId,
            RoleName = roleName,
            ModuleCodes = moduleCodes,
        };
    }
}
