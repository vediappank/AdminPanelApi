using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Control Center ▸ Subscription: how much of its plan a tenant uses - Business Panel users against the plan's
/// user limit and the devices it signs in from against the device limit - and freeing a device.
/// Reads dbo.vw_TenantLogin and dbo.TenantDevices (DatabaseScripts/TenantLogin_SuperAdminPanel.sql; the Business
/// Panel API records a device at each login and refuses a new one over the limit).
///   GET    api/platform/tenants/{tenantId}/usage                     limits, counts and the device list
///   DELETE api/platform/tenants/{tenantId}/devices/{tenantDeviceId}  free that device's place
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:int}")]
[RequirePlatformModule("TENANTS_LIST", "SUBSCRIPTIONS")]
public class PlatformTenantDevicesController : ControllerBase
{
    private const int ActiveDays = 30;
    private readonly PlatformDbContext _db;
    private readonly ILogger<PlatformTenantDevicesController> _logger;

    public PlatformTenantDevicesController(PlatformDbContext db, ILogger<PlatformTenantDevicesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed class TenantDeviceDto
    {
        public int Id { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string? UserEmail { get; set; }
        public string? IpAddress { get; set; }
        public DateTime FirstSeenDate { get; set; }
        public DateTime LastSeenDate { get; set; }
        /// <summary>Used in the last 30 days - counts toward the device limit.</summary>
        public bool Counts { get; set; }
    }

    public sealed class TenantUsageDto
    {
        /// <summary>False until TenantLogin_SuperAdminPanel.sql (with dbo.TenantDevices) has been run.</summary>
        public bool Available { get; set; }
        public string? Message { get; set; }
        /// <summary>0 = no limit.</summary>
        public int UserLimit { get; set; }
        public int UserCount { get; set; }
        public int MaxDevices { get; set; }
        public int DeviceCount { get; set; }
        public int ActiveDays { get; set; } = PlatformTenantDevicesController.ActiveDays;
        public List<TenantDeviceDto> Devices { get; set; } = new();
    }

    [HttpGet("usage")]
    public async Task<ActionResult<TenantUsageDto>> Usage(int tenantId, CancellationToken ct)
    {
        if (!await _db.Tenants.AnyAsync(t => t.TenantId == tenantId, ct)) return NotFound(new { message = "Tenant not found." });
        var result = new TenantUsageDto();
        try
        {
            var cn = _db.Database.GetDbConnection();
            if (cn.State != ConnectionState.Open) await cn.OpenAsync(ct);

            await using (var cmd = Command(cn, "SELECT UserLimit, UserCount, MaxDevices, DeviceCount FROM dbo.vw_TenantLogin WHERE TenantId = @id", tenantId))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct))
                {
                    result.UserLimit = Convert.ToInt32(r["UserLimit"]);
                    result.UserCount = Convert.ToInt32(r["UserCount"]);
                    result.MaxDevices = Convert.ToInt32(r["MaxDevices"]);
                    result.DeviceCount = Convert.ToInt32(r["DeviceCount"]);
                }
            }

            await using (var cmd = Command(cn, @"SELECT TenantDeviceId, DeviceName, UserEmail, IpAddress, FirstSeenDate, LastSeenDate
FROM dbo.TenantDevices WHERE TenantId = @id ORDER BY LastSeenDate DESC", tenantId))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                var since = DateTime.UtcNow.AddDays(-ActiveDays);
                while (await r.ReadAsync(ct))
                {
                    var last = Convert.ToDateTime(r["LastSeenDate"]);
                    result.Devices.Add(new TenantDeviceDto
                    {
                        Id = Convert.ToInt32(r["TenantDeviceId"]),
                        DeviceName = r["DeviceName"] as string ?? "Browser",
                        UserEmail = r["UserEmail"] as string,
                        IpAddress = r["IpAddress"] as string,
                        FirstSeenDate = DateTime.SpecifyKind(Convert.ToDateTime(r["FirstSeenDate"]), DateTimeKind.Utc),
                        LastSeenDate = DateTime.SpecifyKind(last, DateTimeKind.Utc),
                        Counts = last >= since,
                    });
                }
            }
            result.Available = true;
        }
        catch (SqlException ex) when (ex.Number is 207 or 208)   // column / table missing
        {
            result.Message = "Device tracking needs DatabaseScripts/TenantLogin_SuperAdminPanel.sql to be run on the SuperAdminPanel database.";
        }
        return Ok(result);
    }

    [HttpDelete("devices/{tenantDeviceId:int}")]
    public async Task<IActionResult> RemoveDevice(int tenantId, int tenantDeviceId, CancellationToken ct)
    {
        var removed = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE dbo.TenantDevices WHERE TenantId = {tenantId} AND TenantDeviceId = {tenantDeviceId}", ct);
        if (removed == 0) return NotFound(new { message = "Device not found." });
        _logger.LogInformation("Tenant {TenantId}: device {DeviceId} removed from the device list", tenantId, tenantDeviceId);
        return Ok(new { message = "Device removed - its place is free for a new device." });
    }

    private static DbCommand Command(DbConnection cn, string sql, int tenantId)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.DbType = DbType.Int32;
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
