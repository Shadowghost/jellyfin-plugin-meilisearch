using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Scheduled task to rebuild the Meilisearch index from scratch.
/// </summary>
public class ReindexTask : IScheduledTask
{
    /// <summary>
    /// The set of <see cref="BaseItemKind"/> values that the plugin indexes.
    /// Shared with <see cref="IncrementalReindexTask"/> so both tasks query
    /// the library using the same server-side type filter.
    /// </summary>
    internal static readonly BaseItemKind[] IndexableItemTypes =
    [
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Series,
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.MusicArtist,
        BaseItemKind.MusicVideo,
        BaseItemKind.Book,
        BaseItemKind.AudioBook,
        BaseItemKind.BoxSet,
        BaseItemKind.Person,
        BaseItemKind.Trailer,
        BaseItemKind.LiveTvChannel,
        BaseItemKind.LiveTvProgram,
        BaseItemKind.Playlist,
        BaseItemKind.Genre,
        BaseItemKind.MusicGenre,
        BaseItemKind.Studio,
        BaseItemKind.Video
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly MeilisearchClientWrapper _client;
    private readonly MeilisearchIndexService _indexService;
    private readonly ILogger<ReindexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="indexService">The index service used to pause real-time sync during reindex.</param>
    /// <param name="logger">The logger.</param>
    public ReindexTask(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        MeilisearchIndexService indexService,
        ILogger<ReindexTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _indexService = indexService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Rebuild Meilisearch Index";

    /// <inheritdoc />
    public string Key => "MeilisearchReindex";

    /// <inheritdoc />
    public string Description => "Clears and rebuilds the Meilisearch search index from all library items.";

    /// <inheritdoc />
    public string Category => "Search";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default triggers - manual execution only
        yield break;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            _logger.LogWarning("Meilisearch is not configured. Skipping reindex task");
            return;
        }

        if (!await ReindexCoordinator.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Another reindex (full or incremental) is already running; skipping this run");
            return;
        }

        try
        {
            await ExecuteCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReindexCoordinator.Gate.Release();
        }
    }

    private async Task ExecuteCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        var batchSize = configuration?.ReindexBatchSize ?? 2000;
        if (batchSize <= 0)
        {
            batchSize = 2000;
        }

        var parallelism = configuration?.ReindexParallelism ?? 2;
        if (parallelism <= 0)
        {
            parallelism = 1;
        }

        progress.Report(0);
        _logger.LogInformation(
            "Starting Meilisearch reindex task (batch size {BatchSize}, parallelism {Parallelism})",
            batchSize,
            parallelism);

        // Capture before any work so the next incremental run picks up anything
        // modified during this reindex.
        var runStart = DateTime.UtcNow;

        _logger.LogInformation("Pausing real-time sync");
        await _indexService.PauseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _logger.LogInformation("Resetting Meilisearch index");
            await _client.ResetIndexAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(2);

            // Get an upfront count for monotonic progress. Concurrent edits may shift this slightly
            // (we cap the reported percentage at 90 anyway), so an approximate total is fine.
            var totalCount = 0;
            try
            {
                totalCount = _libraryManager.GetCount(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = IndexableItemTypes,
                });
                _logger.LogInformation("Found {TotalCount} items to index", totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get item count; progress will be reported as a fraction of work observed so far");
            }

            var taskUids = new ConcurrentBag<int>();
            using var semaphore = new SemaphoreSlim(parallelism, parallelism);
            var inFlight = new List<Task>();

            var processedCount = 0;
            var indexedCount = 0;
            var skippedCount = 0;
            var errorCount = 0;
            var batchNumber = 0;
            var startIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<BaseItem> items;
                try
                {
                    var pageQuery = new InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = IndexableItemTypes,
                        StartIndex = startIndex,
                        Limit = batchSize
                    };

                    items = _libraryManager.GetItemList(pageQuery);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Error fetching items at offset {StartIndex}, skipping batch",
                        startIndex);
                    errorCount += batchSize;
                    startIndex += batchSize;
                    continue;
                }

                if (items.Count == 0)
                {
                    break;
                }

                // Pre-fetch all people for this page in a single DB query (eliminates F2's N+1).
                var peopleEligibleIds = items
                    .Where(MeilisearchIndexService.ShouldIndexItem)
                    .Where(i => i.SupportsPeople)
                    .Select(i => i.Id)
                    .Distinct()
                    .ToArray();

                IReadOnlyDictionary<Guid, IReadOnlyList<string>> peopleLookup = peopleEligibleIds.Length > 0
                    ? _libraryManager.GetPeopleNamesByItem(peopleEligibleIds, Array.Empty<string>())
                    : new Dictionary<Guid, IReadOnlyList<string>>();

                var batch = new List<MeilisearchDocument>(items.Count);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedCount++;

                    try
                    {
                        if (!MeilisearchIndexService.ShouldIndexItem(item))
                        {
                            skippedCount++;
                            continue;
                        }

                        batch.Add(MeilisearchIndexService.CreateDocument(item, peopleLookup: peopleLookup));
                        indexedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error processing item {ItemId}, skipping", item.Id);
                        errorCount++;
                    }
                }

                if (batch.Count > 0)
                {
                    batchNumber++;
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var batchToSubmit = batch;
                    inFlight.Add(Task.Run(
                        async () =>
                        {
                            try
                            {
                                var uid = await _client.IndexDocumentsAsync(batchToSubmit, cancellationToken).ConfigureAwait(false);
                                if (uid.HasValue)
                                {
                                    taskUids.Add(uid.Value);
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        },
                        cancellationToken));
                }

                double progressPercent;
                if (totalCount > 0)
                {
                    var fraction = Math.Min(1d, (double)processedCount / totalCount);
                    progressPercent = 2d + (fraction * 93d);
                }
                else
                {
                    // No upfront count: report a monotonic asymptote that creeps toward 95.
                    progressPercent = 95d - (90d / (1d + (processedCount / 10_000d)));
                }

                progressPercent = Math.Min(progressPercent, 95d);
                progress.Report(progressPercent);

                _logger.LogInformation(
                    "Indexed batch {BatchNumber}: {IndexedCount} indexed, {SkippedCount} skipped, {ErrorCount} errors ({ProgressPercent:F1}%)",
                    batchNumber,
                    indexedCount,
                    skippedCount,
                    errorCount,
                    progressPercent.ToString("F1", CultureInfo.InvariantCulture));

                if (items.Count < batchSize)
                {
                    break;
                }

                startIndex += batchSize;
            }

            // Wait for all in-flight indexing requests to be accepted by Meilisearch.
            await Task.WhenAll(inFlight).ConfigureAwait(false);
            progress.Report(97);

            // At-least-once: await every task UID and surface any failures.
            var taskFailures = 0;
            foreach (var uid in taskUids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var succeeded = await _client.AwaitTaskAsync(uid, cancellationToken).ConfigureAwait(false);
                if (!succeeded)
                {
                    taskFailures++;
                }
            }

            if (taskFailures > 0)
            {
                _logger.LogWarning(
                    "{FailureCount} of {TotalTasks} Meilisearch indexing tasks did not complete successfully",
                    taskFailures,
                    taskUids.Count);
            }

            progress.Report(100);
            _logger.LogInformation(
                "Meilisearch reindex complete. Indexed {IndexedCount} items, skipped {SkippedCount} items, {ErrorCount} errors in {BatchCount} batches ({TaskCount} Meilisearch tasks)",
                indexedCount,
                skippedCount,
                errorCount,
                batchNumber,
                taskUids.Count);

            // Anchor the incremental task's watermark so it doesn't re-index everything (or fall
            // back to the 24h heuristic) on its next run. We use the pre-work timestamp so any
            // items modified during the reindex still get picked up.
            var plugin = Plugin.Instance;
            if (plugin is not null)
            {
                plugin.Configuration.LastIncrementalReindexUtc = runStart;
                plugin.SaveConfiguration();
                _logger.LogInformation("Updated incremental sync watermark to {RunStart:O}", runStart);
            }
        }
        finally
        {
            try
            {
                await _indexService.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Resumed real-time sync");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resuming real-time sync after reindex");
            }
        }
    }
}
