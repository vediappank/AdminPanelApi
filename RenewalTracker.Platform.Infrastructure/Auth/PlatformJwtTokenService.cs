using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RenewalTracker.Platform.Application.Common;
using RenewalTracker.Platform.Application.DTOs.Platform;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Auth;

/// <summary>
/// Signs platform-operator tokens with this project's own Jwt:Key/Issuer/
/// Audience (configured via JwtOptions in RenewalTracker.PlatformApi's
/// appsettings) - a different signing key from RenewalTracker.Api's tenant
/// tokens, which this project shares no code with at all. The claim set
/// stays deliberately minimal - only AppClaimTypes.PlatformUserId, email and
/// name, no tenant_id, no role/permission claims.
/// </summary>
public class PlatformJwtTokenService : IPlatformJwtTokenService
{
    private readonly JwtOptions _options;

    public PlatformJwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateToken(CurrentPlatformUserDto user)
    {
        var expires = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.PlatformUserId.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new(AppClaimTypes.PlatformUserId, user.PlatformUserId.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return (tokenString, expires);
    }
}
