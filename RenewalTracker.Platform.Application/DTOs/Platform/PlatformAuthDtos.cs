namespace RenewalTracker.Platform.Application.DTOs.Platform;

public class PlatformLoginRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class CurrentPlatformUserDto
{
    public int PlatformUserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    public int? PlatformRoleId { get; set; }
    public string? RoleName { get; set; }

    /// <summary>ModuleCode of every top-level Module this user's role currently
    /// grants (Dashboard aside - that one's always reachable). Computed fresh
    /// at login/me, not a JWT claim - for the Angular app to filter its own
    /// nav/routes with; the API still checks live against the database on
    /// every gated request (see RequirePlatformModuleAttribute), so this list
    /// going stale client-side is a UX-only concern, never a security one.</summary>
    public List<string> ModuleCodes { get; set; } = new();
}

public class PlatformLoginResponseDto
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public CurrentPlatformUserDto User { get; set; } = new();
}

public class PlatformChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
