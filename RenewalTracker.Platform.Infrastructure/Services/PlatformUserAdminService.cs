using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Auth;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

public class PlatformUserAdminService : IPlatformUserAdminService
{
    private readonly PlatformDbContext _db;

    public PlatformUserAdminService(PlatformDbContext db)
    {
        _db = db;
    }

    public async Task<List<PlatformUserDto>> SearchAsync(string? search)
    {
        var query = _db.PlatformUsers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(u => u.Email.Contains(s) || u.FirstName.Contains(s) || (u.LastName != null && u.LastName.Contains(s)));
        }

        var users = await query.OrderBy(u => u.FirstName).ToListAsync();
        var roleNameById = await _db.PlatformRoles.AsNoTracking().ToDictionaryAsync(r => r.PlatformRoleId, r => r.RoleName);
        return users.Select(u => ToDto(u, roleNameById)).ToList();
    }

    public async Task<PlatformUserDto> CreateAsync(CreatePlatformUserDto dto)
    {
        if (await _db.PlatformUsers.AnyAsync(u => u.Email == dto.Email))
        {
            throw new InvalidOperationException($"A platform user with email '{dto.Email}' already exists.");
        }
        if (!await _db.PlatformRoles.AnyAsync(r => r.PlatformRoleId == dto.PlatformRoleId))
        {
            throw new InvalidOperationException("Select a valid role.");
        }

        var entity = new PlatformUser
        {
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Email = dto.Email,
            Status = "Active",
            CreatedDate = DateTime.UtcNow,
            PasswordHash = PasswordHasher.Hash(dto.InitialPassword),
            PlatformRoleId = dto.PlatformRoleId,
        };
        _db.PlatformUsers.Add(entity);
        await _db.SaveChangesAsync();

        var roleName = await _db.PlatformRoles.Where(r => r.PlatformRoleId == dto.PlatformRoleId).Select(r => r.RoleName).FirstOrDefaultAsync();
        return ToDto(entity, roleName);
    }

    public async Task<PlatformUserDto?> UpdateRoleAsync(int platformUserId, int platformRoleId)
    {
        var entity = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.PlatformUserId == platformUserId);
        if (entity == null) return null;
        if (!await _db.PlatformRoles.AnyAsync(r => r.PlatformRoleId == platformRoleId))
        {
            throw new InvalidOperationException("Select a valid role.");
        }

        entity.PlatformRoleId = platformRoleId;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var roleName = await _db.PlatformRoles.Where(r => r.PlatformRoleId == platformRoleId).Select(r => r.RoleName).FirstOrDefaultAsync();
        return ToDto(entity, roleName);
    }

    public async Task<PlatformUserDto?> UpdateAsync(int platformUserId, UpdatePlatformUserDto dto)
    {
        var entity = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.PlatformUserId == platformUserId);
        if (entity == null) return null;

        if (await _db.PlatformUsers.AnyAsync(u => u.PlatformUserId != platformUserId && u.Email == dto.Email))
        {
            throw new InvalidOperationException($"A platform user with email '{dto.Email}' already exists.");
        }
        if (!await _db.PlatformRoles.AnyAsync(r => r.PlatformRoleId == dto.PlatformRoleId))
        {
            throw new InvalidOperationException("Select a valid role.");
        }

        entity.FirstName = dto.FirstName;
        entity.LastName = dto.LastName;
        entity.Email = dto.Email;
        entity.PlatformRoleId = dto.PlatformRoleId;
        entity.Status = dto.Status;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var roleName = await _db.PlatformRoles.Where(r => r.PlatformRoleId == dto.PlatformRoleId).Select(r => r.RoleName).FirstOrDefaultAsync();
        return ToDto(entity, roleName);
    }

    public async Task<bool> ResetPasswordAsync(int platformUserId, string newPassword)
    {
        var entity = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.PlatformUserId == platformUserId);
        if (entity == null) return false;

        entity.PasswordHash = PasswordHasher.Hash(newPassword);
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private static PlatformUserDto ToDto(PlatformUser u, IReadOnlyDictionary<int, string> roleNameById) =>
        ToDto(u, u.PlatformRoleId.HasValue && roleNameById.TryGetValue(u.PlatformRoleId.Value, out var name) ? name : null);

    private static PlatformUserDto ToDto(PlatformUser u, string? roleName) => new()
    {
        PlatformUserId = u.PlatformUserId,
        FirstName = u.FirstName,
        LastName = u.LastName,
        Email = u.Email,
        Status = u.Status,
        CreatedDate = u.CreatedDate,
        PlatformRoleId = u.PlatformRoleId,
        RoleName = roleName,
    };
}
