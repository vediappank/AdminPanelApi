using System.Threading.Channels;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Tenants waiting for their database to be built. Provision tenant puts the tenant id here and
/// returns at once; TenantDatabaseMigrationWorker picks it up within a second. Singleton.
/// </summary>
public class TenantDatabaseQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(int tenantId) => _channel.Writer.TryWrite(tenantId);

    public ValueTask<int> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
