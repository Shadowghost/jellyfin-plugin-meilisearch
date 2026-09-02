using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Queues an incremental Meilisearch sync as soon as a library scan finishes.
/// </summary>
public class LibraryScanSyncTask : ILibraryPostScanTask
{
    private readonly MeilisearchClientWrapper _client;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<LibraryScanSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanSyncTask"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="taskManager">The task manager the sync is queued through.</param>
    /// <param name="logger">The logger.</param>
    public LibraryScanSyncTask(
        MeilisearchClientWrapper client,
        ITaskManager taskManager,
        ILogger<LibraryScanSyncTask> logger)
    {
        _client = client;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            _logger.LogDebug("Meilisearch is not configured; skipping the post-scan sync");
            return Task.CompletedTask;
        }

        try
        {
            // Skips rather than stacks when a sweep is already under way.
            _taskManager.QueueIfNotRunning<IncrementalReindexTask>();

            _logger.LogInformation(
                "Library scan finished; queued the \"Incremental Meilisearch Sync\" task. "
                + "It runs in the background and the scan does not wait for it");
        }
#pragma warning disable CA1031 // Failing to queue the sync must not fail the library scan it is attached to.
        catch (Exception ex)
        {
            // QueueIfNotRunning throws when there is no worker for the task, which would mean the
            // scheduled task never got registered. The hourly trigger is the fallback either way.
            _logger.LogError(
                ex,
                "Could not queue the post-scan Meilisearch sync. The hourly incremental sync will cover the same window");
        }
#pragma warning restore CA1031

        return Task.CompletedTask;
    }
}
