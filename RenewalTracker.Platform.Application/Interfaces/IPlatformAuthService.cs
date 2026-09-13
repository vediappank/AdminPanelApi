using RenewalTracker.Platform.Application.DTOs.Platform;

namespace RenewalTracker.Platform.Application.Interfaces;

public interface IPlatformAuthService
{
    /// <summary>Returns null when the credentials are invalid.</summary>
    Task<PlatformLoginResponseDto?> LoginAsync(PlatformLoginRequestDto request);

    Task<CurrentPlatformUserDto?> GetCurrentAsync(int platformUserId);

    Task<bool> ChangePasswordAsync(int platformUserId, string currentPassword, string newPassword);
}
