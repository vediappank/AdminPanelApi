using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Generic;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.Platform.Infrastructure.Data;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// See IPlatformContactUniquenessService. E-mails ignore case and spaces, mobiles ignore formatting and the
/// country code (ContactKeys - the same rules as the Business Panel API). Tenants and platform users are
/// few, so they are compared in memory; the Business Panel login directory (dbo.TenantUsers) is queried.
/// </summary>
public class PlatformContactUniquenessService : IPlatformContactUniquenessService
{
    private readonly PlatformDbContext _db;

    public PlatformContactUniquenessService(PlatformDbContext db)
    {
        _db = db;
    }

    // ------------------------------------------------------------------ tenants

    public async Task<UniqueCheckDto> CheckTenantContactAsync(string field, string? value, int? tenantId, CancellationToken ct = default)
    {
        var isEmail = IsEmail(field);
        if (!isEmail && !IsMobile(field)) return UniqueCheckDto.Ok();
        var key = isEmail ? ContactKeys.Email(value) : ContactKeys.Mobile(value);
        if (key is null) return UniqueCheckDto.Ok();

        var tenants = await _db.Tenants.AsNoTracking()
            .Select(t => new { t.TenantId, t.TenantCode, t.TenantName, t.ContactEmail, t.ContactMobile })
            .ToListAsync(ct);

        if (tenantId is int id)
        {
            var self = tenants.FirstOrDefault(t => t.TenantId == id);
            if (self != null && key == (isEmail ? ContactKeys.Email(self.ContactEmail) : ContactKeys.Mobile(self.ContactMobile)))
                return UniqueCheckDto.Ok();   // unchanged value of this tenant
        }

        var other = tenants.FirstOrDefault(t => t.TenantId != tenantId &&
            key == (isEmail ? ContactKeys.Email(t.ContactEmail) : ContactKeys.Mobile(t.ContactMobile)));
        return other == null
            ? UniqueCheckDto.Ok()
            : UniqueCheckDto.Taken($"{(value ?? string.Empty).Trim()} is already the contact {(isEmail ? "e-mail" : "mobile")} of tenant {other.TenantName} ({other.TenantCode}).");
    }

    public async Task<string?> TenantContactProblemAsync(string? email, string? mobile, int? tenantId, CancellationToken ct = default)
    {
        var e = await CheckTenantContactAsync("email", email, tenantId, ct);
        if (!e.Available) return e.Message;
        var m = await CheckTenantContactAsync("mobile", mobile, tenantId, ct);
        return m.Available ? null : m.Message;
    }

    // ------------------------------------------------------------------ platform users

    public async Task<UniqueCheckDto> CheckPlatformUserAsync(string field, string? value, int? platformUserId, CancellationToken ct = default)
    {
        var isEmail = IsEmail(field);
        if (!isEmail && !IsMobile(field)) return UniqueCheckDto.Ok();
        var key = isEmail ? ContactKeys.Email(value) : ContactKeys.Mobile(value);
        if (key is null) return UniqueCheckDto.Ok();

        var users = await _db.PlatformUsers.AsNoTracking()
            .Select(u => new { u.PlatformUserId, u.FirstName, u.LastName, u.Email, u.Mobile })
            .ToListAsync(ct);

        if (platformUserId is int id)
        {
            var self = users.FirstOrDefault(u => u.PlatformUserId == id);
            if (self != null && key == (isEmail ? ContactKeys.Email(self.Email) : ContactKeys.Mobile(self.Mobile)))
                return UniqueCheckDto.Ok();   // unchanged value of this user
        }

        var other = users.FirstOrDefault(u => u.PlatformUserId != platformUserId &&
            key == (isEmail ? ContactKeys.Email(u.Email) : ContactKeys.Mobile(u.Mobile)));
        if (other != null)
            return UniqueCheckDto.Taken($"{(value ?? string.Empty).Trim()} is already used by platform user {$"{other.FirstName} {other.LastName}".Trim()}.");

        // one e-mail = one login: not also a Business Panel login of a tenant
        if (isEmail && await BusinessPanelLoginOfAsync(key, ct) is string tenant)
            return UniqueCheckDto.Taken($"{key} is already the Business Panel login of tenant {tenant}. Use a different e-mail address.");

        return UniqueCheckDto.Ok();
    }

    public async Task<string?> PlatformUserProblemAsync(string? email, string? mobile, int? platformUserId, CancellationToken ct = default)
    {
        var e = await CheckPlatformUserAsync("email", email, platformUserId, ct);
        if (!e.Available) return e.Message;
        var m = await CheckPlatformUserAsync("mobile", mobile, platformUserId, ct);
        return m.Available ? null : m.Message;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>"Name (CODE)" of the tenant whose Business Panel login this e-mail is, or null.</summary>
    private async Task<string?> BusinessPanelLoginOfAsync(string email, CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.TenantUsers', N'U') IS NULL BEGIN SELECT CAST(NULL AS NVARCHAR(400)); RETURN; END;
SELECT TOP (1) t.TenantName + N' (' + t.TenantCode + N')'
FROM dbo.TenantUsers u JOIN dbo.Tenants t ON t.TenantId = u.TenantId
WHERE u.Email = @e;";

        DbConnection conn = _db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p = cmd.CreateParameter();
            p.ParameterName = "@e"; p.DbType = DbType.String; p.Size = 200; p.Value = email;
            cmd.Parameters.Add(p);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private static bool IsEmail(string? field) => string.Equals(field, "email", StringComparison.OrdinalIgnoreCase);
    private static bool IsMobile(string? field) => string.Equals(field, "mobile", StringComparison.OrdinalIgnoreCase);
}
