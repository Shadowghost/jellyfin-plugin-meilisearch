using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Runs an incremental Meilisearch sync as soon as a library scan finishes.
/// </summary>
/// <remarks>
/// Real-time sync already indexes the items a scan discovers, but only while it is enabled and while
/// Meilisearch is reachable: anything added during a server outage, a paused queue or a dropped event
/// is otherwise not picked up until the hourly incremental task comes round. A scan is exactly the
/// point where the library and the index are most likely to have drifted, so this closes the gap
/// immediately. The sweep only covers items modified since the last incremental run, so on an
/// unchanged library it costs one query.
/// </remarks>
public class LibraryScanSyncTask : ILibraryPostScanTask
{
    private readonly MeilisearchClientWrapper _client;
    private readonly IncrementalReindexTask _incrementalReindex;
    private readonly ILogger<LibraryScanSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanSyncTask"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="incrementalReindex">The incremental sync task this delegates to.</param>
    /// <param name="logger">The logger.</param>
    public LibraryScanSyncTask(
        MeilisearchClientWrapper client,
        IncrementalReindexTask incrementalReindex,
        ILogger<LibraryScanSyncTask> logger)
    {
        _client = client;
        _incrementalReindex = incrementalReindex;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            _logger.LogDebug("Meilisearch is not configured; skipping the post-scan sync");
            return;
        }

        _logger.LogInformation("Library scan finished; running an incremental Meilisearch sync");

        try
        {
            // The task takes care of the rest: it yields to a running full reindex and pauses
            // real-time sync for the duration of the sweep.
            await _incrementalReindex.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed sync must not fail the library scan it is attached to.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-scan Meilisearch sync failed. The next incremental sync will retry the same window");
        }
#pragma warning restore CA1031
    }
}
