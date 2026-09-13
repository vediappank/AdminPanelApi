using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Login for the Platform Panel (platform-admin-ui) - entirely separate
/// from RenewalTracker.Api's tenant-user AuthController: different
/// process, different database table (PlatformUsers, in
/// RenewalTrackerPlatform), different signing key, no tenant/role/
/// permission concept at all. See IPlatformAuthService.
/// </summary>
[ApiController]
[Route("api/platform-auth")]
public class PlatformAuthController : ControllerBase
{
    private readonly IPlatformAuthService _authService;
    private readonly ICurrentPlatformUserService _currentUser;

    public PlatformAuthController(IPlatformAuthService authService, ICurrentPlatformUserService currentUser)
    {
        _authService = authService;
        _currentUser = currentUser;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<PlatformLoginResponseDto>> Login(PlatformLoginRequestDto request)
    {
        var result = await _authService.LoginAsync(request);
        if (result == null) return Unauthorized(new { message = "Invalid email or password." });
        return Ok(result);
    }

    [HttpGet("me")]
    [RequirePlatformAuth]
    public async Task<ActionResult<CurrentPlatformUserDto>> Me()
    {
        var user = await _authService.GetCurrentAsync(_currentUser.PlatformUserId);
        if (user == null) return NotFound();
        return Ok(user);
    }

    [HttpPost("change-password")]
    [RequirePlatformAuth]
    public async Task<IActionResult> ChangePassword(PlatformChangePasswordDto request)
    {
        var ok = await _authService.ChangePasswordAsync(_currentUser.PlatformUserId, request.CurrentPassword, request.NewPassword);
        if (!ok) return BadRequest(new { message = "Current password is incorrect." });
        return NoContent();
    }
}
