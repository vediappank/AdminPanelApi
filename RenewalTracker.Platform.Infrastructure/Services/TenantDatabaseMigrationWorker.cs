using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Builds and maintains every tenant database with no manual step - see TenantDatabaseService.
///   - Queued tenants (Provision tenant / Complete provisioning) are built straight away:
///     database, schema, master data, owner login.
///   - At API start-up and then every TenantDatabase:AutoMigrateMinutes (default 10;
///     0 = start-up only) every tenant database gets any new Schema / MasterData script.
/// </summary>
public class TenantDatabaseMigrationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<TenantDatabaseMigrationWorker> _logger;
    private readonly TenantDatabaseQueue _queue;

    public TenantDatabaseMigrationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<TenantDatabaseMigrationWorker> logger, TenantDatabaseQueue queue)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _queue = queue;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(ProcessQueueAsync(stoppingToken), PeriodicAsync(stoppingToken));

    /// <summary>Newly provisioned tenants - one at a time, as soon as they arrive.</summary>
    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int tenantId;
            try { tenantId = await _queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ITenantDatabaseService>().EnsureUpToDateAsync(tenantId, stoppingToken);
                _logger.LogInformation("Provisioning run for tenant {TenantId}: {Message}", tenantId, result.Message);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Provisioning run for tenant {TenantId} failed - the periodic run retries it.", tenantId);
            }
        }
    }

    private async Task PeriodicAsync(CancellationToken stoppingToken)
    {
        // Registry columns first (no delay), so the Control Center works right after start-up.
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITenantDatabaseService>().MigrateAllAsync(CancellationToken.None, registryOnly: true);
        }
        catch (Exception ex) { _logger.LogError(ex, "Could not prepare dbo.TenantDatabases."); }

        // Let the API finish starting first.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITenantDatabaseService>().MigrateAllAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Automatic tenant database migration failed - will retry on the next run.");
            }

            var minutes = _config.GetValue("TenantDatabase:AutoMigrateMinutes", 10);
            if (minutes <= 0) return;
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); } catch (TaskCanceledException) { return; }
        }
    }
}
