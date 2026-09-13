using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>Lets a signed-in platform operator add/manage further Bliss Point Group staff accounts, and assign each one a Role.
/// Gated by its own PLATFORM_USERS code again (back from the brief SETTINGS-combined gate) - "User" still lives nested
/// under the Settings nav group in the sidebar alongside "Role" and "Module" (see settings-menu-merge.sql), but each of
/// the three is once again individually grantable per role - see PlatformRolesController.GetModules(), which now lists
/// Settings expanded into its three children instead of one combined checkbox, and settings-tenant-submodule-split.sql
/// for the PlatformRoleModules migration that preserves access for any role that was only granted the combined
/// Settings row.</summary>
[ApiController]
[Route("api/platform/users")]
[RequirePlatformModule("PLATFORM_USERS")]
public class PlatformUsersController : ControllerBase
{
    private readonly IPlatformUserAdminService _service;

    public PlatformUsersController(IPlatformUserAdminService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<PlatformUserDto>>> Search([FromQuery] string? search)
    {
        return Ok(await _service.SearchAsync(search));
    }

    [HttpPost]
    public async Task<ActionResult<PlatformUserDto>> Create(CreatePlatformUserDto dto)
    {
        try
        {
            return Ok(await _service.CreateAsync(dto));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PlatformUserDto>> Update(int id, UpdatePlatformUserDto dto)
    {
        try
        {
            var result = await _service.UpdateAsync(id, dto);
            return result == null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:int}/role")]
    public async Task<ActionResult<PlatformUserDto>> UpdateRole(int id, UpdatePlatformUserRoleDto dto)
    {
        try
        {
            var result = await _service.UpdateRoleAsync(id, dto.PlatformRoleId);
            return result == null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPlatformUserPasswordDto dto)
    {
        var ok = await _service.ResetPasswordAsync(id, dto.NewPassword);
        return ok ? NoContent() : NotFound();
    }
}
