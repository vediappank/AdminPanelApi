namespace RenewalTracker.Platform.Application.DTOs.Platform;

public class PlatformUserDto
{
    public int PlatformUserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }

    public int? PlatformRoleId { get; set; }
    public string? RoleName { get; set; }
}

public class CreatePlatformUserDto
{
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string InitialPassword { get; set; } = string.Empty;
    public int PlatformRoleId { get; set; }
}

public class UpdatePlatformUserRoleDto
{
    public int PlatformRoleId { get; set; }
}

/// <summary>Full-record edit (Edit screen) - every editable field except password, which stays on its own Reset password action.</summary>
public class UpdatePlatformUserDto
{
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public int PlatformRoleId { get; set; }
    public string Status { get; set; } = "Active";
}

public class ResetPlatformUserPasswordDto
{
    public string NewPassword { get; set; } = string.Empty;
}
