using RenewalTracker.Platform.Application.DTOs.Platform;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>Lets an existing platform operator add/manage further Bliss Point Group staff accounts.</summary>
public interface IPlatformUserAdminService
{
    Task<List<PlatformUserDto>> SearchAsync(string? search);
    Task<PlatformUserDto> CreateAsync(CreatePlatformUserDto dto);
    Task<PlatformUserDto?> UpdateRoleAsync(int platformUserId, int platformRoleId);
    Task<PlatformUserDto?> UpdateAsync(int platformUserId, UpdatePlatformUserDto dto);
    Task<bool> ResetPasswordAsync(int platformUserId, string newPassword);
}
