using Microsoft.AspNetCore.Mvc;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Admin Panel > Settings > Deployments - the build that is live on each site right now, and every build
/// seen before (dbo.DeploymentHistory). See DeploymentTracker for where the versions come from.
/// Access: DEPLOYMENTS module (DatabaseScripts/Deployments_SuperAdminPanel.sql).
/// </summary>
[ApiController]
[Route("api/platform/deployments")]
[RequirePlatformModule("DEPLOYMENTS")]
public class PlatformDeploymentsController : ControllerBase
{
    private readonly DeploymentTracker _tracker;
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<PlatformDeploymentsController> _logger;

    public PlatformDeploymentsController(DeploymentTracker tracker, SchemaMigrator migrator, ILogger<PlatformDeploymentsController> logger)
    {
        _tracker = tracker;
        _migrator = migrator;
        _logger = logger;
    }

    public sealed class DeploymentsResponse
    {
        public DateTime CheckedAt { get; set; }
        public List<DeploymentTracker.AppStatus> Apps { get; set; } = new();
        public List<DeploymentTracker.HistoryRow> History { get; set; } = new();
        public string? HistoryError { get; set; }
        /// <summary>Database scripts of this Admin API build (DatabaseScripts\Migrations) - result of the run at startup.</summary>
        public SchemaMigrator.Report Migrations { get; set; } = new();
    }

    /// <summary>Checks all five sites now (records any new build) and returns the latest history.</summary>
    [HttpGet]
    public async Task<ActionResult<DeploymentsResponse>> Get([FromQuery] string? app, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var result = new DeploymentsResponse
        {
            CheckedAt = DateTime.UtcNow,
            Apps = await _tracker.CheckAsync(record: true, ct),
            Migrations = _migrator.Last,
        };
        try
        {
            result.History = await _tracker.HistoryAsync(app, take, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deployments: history could not be read.");
            result.HistoryError = "History could not be read from the database: " + ex.Message;
        }
        return Ok(result);
    }
}
