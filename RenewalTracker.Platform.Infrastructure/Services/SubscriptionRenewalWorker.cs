using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Ends or renews subscriptions whose term is over - see SubscriptionService.ProcessDueAsync - and puts
/// tenants without any subscription on the default plan (SubscriptionService.EnsureDefaultSubscriptionsAsync).
/// Runs a minute after start-up and then every Subscriptions:CheckHours (default 6; 0 = start-up only).
/// Does nothing until DatabaseScripts/Subscriptions_SuperAdminPanel.sql has created the tables.
/// </summary>
public class SubscriptionRenewalWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionRenewalWorker> _logger;

    public SubscriptionRenewalWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SubscriptionRenewalWorker> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                var handled = await subscriptions.ProcessDueAsync(stoppingToken);
                if (handled > 0) _logger.LogInformation("Subscriptions: {Count} finished term(s) renewed or expired.", handled);
                var started = await subscriptions.EnsureDefaultSubscriptionsAsync(null, stoppingToken);
                if (started > 0) _logger.LogInformation("Subscriptions: {Count} tenant(s) put on the default plan.", started);
            }
            catch (SqlException ex) when (ex.Number == 208)   // invalid object name - tables not created yet
            {
                _logger.LogWarning("Subscriptions: dbo.TenantSubscriptions does not exist yet - run DatabaseScripts/Subscriptions_SuperAdminPanel.sql.");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Subscriptions: renewal run failed - will retry on the next run.");
            }

            var hours = _config.GetValue("Subscriptions:CheckHours", 6);
            if (hours <= 0) return;
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); } catch (TaskCanceledException) { return; }
        }
    }
}
