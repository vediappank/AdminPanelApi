using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// A Bliss Point Group platform operator - completely separate from the
/// per-tenant Users table (RenewalTracker.Api). No TenantId, no relation to
/// any Tenant at all: this is the identity for the people who run the
/// platform (provisioning tenants, maintaining the global Modules/
/// Permissions catalog), not a tenant's own staff. Never mirrored to the
/// Business database - see PlatformAuthService and
/// RenewalTracker.PlatformApi.Controllers.Platform*.
///
/// PlatformRoleId is nullable and treated as fail-closed: a user with no
/// role granted can sign in but sees/reaches no gated screen (see
/// RequirePlatformModuleAttribute) - Dashboard is the one exception, always
/// reachable regardless of role. See PlatformRole.
/// </summary>
public class PlatformUser
{
    [Key]
    public int PlatformUserId { get; set; }

    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? LastName { get; set; }

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? PasswordHash { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "Active";

    public int? PlatformRoleId { get; set; }

    public PlatformRole? PlatformRole { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
