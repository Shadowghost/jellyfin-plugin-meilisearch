using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Meilisearch.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Hosted service that periodically pings Meilisearch and coordinates pause/resume of the real-time index service based on observed health transitions.
/// </summary>
public class MeilisearchHealthMonitor : IHostedService, IDisposable
{
    private readonly MeilisearchClientWrapper _client;
    private readonly MeilisearchIndexService _indexService;
    private readonly ILogger<MeilisearchHealthMonitor> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool? _lastSeenHealthy;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchHealthMonitor"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="indexService">The Meilisearch index service to pause/resume.</param>
    /// <param name="logger">The logger.</param>
    public MeilisearchHealthMonitor(
        MeilisearchClientWrapper client,
        MeilisearchIndexService indexService,
        ILogger<MeilisearchHealthMonitor> logger)
    {
        _client = client;
        _indexService = indexService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        _logger.LogInformation("Meilisearch health monitor started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed; nothing to cancel.
            }
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown is happening faster than the loop can exit cleanly.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Meilisearch health monitor terminated with an error");
            }
        }

        _logger.LogInformation("Meilisearch health monitor stopped");
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var intervalSeconds = Math.Max(1, Configuration.HealthCheckIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

                if (!Configuration.EnableHealthMonitor)
                {
                    continue;
                }

                if (!_client.IsConfigured)
                {
                    continue;
                }

                MeilisearchHealth health;
                try
                {
                    health = await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // Catch all exceptions from the health probe so the monitor loop survives transient faults.
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Meilisearch health check threw unexpectedly");
                    continue;
                }
#pragma warning restore CA1031

                await HandleHealthTransitionAsync(health, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
#pragma warning disable CA1031 // Don't let an unexpected error tear down the host.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meilisearch health monitor loop crashed");
        }
#pragma warning restore CA1031
    }

    private async Task HandleHealthTransitionAsync(MeilisearchHealth health, CancellationToken cancellationToken)
    {
        if (_lastSeenHealthy is null)
        {
            _lastSeenHealthy = health.IsHealthy;
            if (!health.IsHealthy)
            {
                _logger.LogWarning("Meilisearch unhealthy: {Error}, pausing real-time sync", health.Error);
                await _indexService.PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var wasHealthy = _lastSeenHealthy.Value;
        if (wasHealthy && !health.IsHealthy)
        {
            _logger.LogWarning("Meilisearch unhealthy: {Error}, pausing real-time sync", health.Error);
            await _indexService.PauseAsync(cancellationToken).ConfigureAwait(false);
            _lastSeenHealthy = false;
        }
        else if (!wasHealthy && health.IsHealthy)
        {
            _logger.LogInformation("Meilisearch recovered, resuming real-time sync");
            await _indexService.ResumeAsync(cancellationToken).ConfigureAwait(false);
            _lastSeenHealthy = true;
        }
    }

    /// <summary>
    /// Releases the resources used by the <see cref="MeilisearchHealthMonitor"/> instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="MeilisearchHealthMonitor"/> and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cts?.Dispose();
            _cts = null;
        }

        _disposed = true;
    }
}
