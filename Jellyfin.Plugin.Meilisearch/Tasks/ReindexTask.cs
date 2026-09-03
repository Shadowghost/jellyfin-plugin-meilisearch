using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Meilisearch.Embeddings;
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
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<ReindexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="indexService">The index service used to pause real-time sync during reindex.</param>
    /// <param name="embeddings">The embedding service used to attach vectors to indexed documents.</param>
    /// <param name="logger">The logger.</param>
    public ReindexTask(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        MeilisearchIndexService indexService,
        EmbeddingService embeddings,
        ILogger<ReindexTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _indexService = indexService;
        _embeddings = embeddings;
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

    /// <summary>
    /// Copies one page of ids out of the snapshot.
    /// </summary>
    /// <param name="itemIds">The full id snapshot.</param>
    /// <param name="startIndex">Index of the first id in this page.</param>
    /// <param name="batchSize">Maximum number of ids in this page.</param>
    /// <returns>The ids for this page.</returns>
    internal static Guid[] BuildChunk(IReadOnlyList<Guid> itemIds, int startIndex, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var length = Math.Min(batchSize, itemIds.Count - startIndex);
        var chunk = new Guid[length];
        for (var i = 0; i < length; i++)
        {
            chunk[i] = itemIds[startIndex + i];
        }

        return chunk;
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

        // Load the model (downloading it if allowed) before the index is reset, so a full rebuild
        // either embeds every document or none of them - never a confusing half-vectorized index.
        if (_embeddings.IsEnabled
            && !await _embeddings.EnsureReadyAsync(null, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Semantic search is enabled but the embedding model is not available ({Error}); reindexing without vectors",
                _embeddings.Error);
        }

        var completedCleanly = false;

        _logger.LogInformation("Pausing real-time sync");
        await _indexService.PauseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _embeddings.BeginCacheRetention();

            // Built beside the live index, which keeps answering searches until the swap at the end.
            // The name is logged so the Meilisearch side of a long run is followable.
            var target = await _client.BeginRebuildAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Rebuilding into Meilisearch index {IndexName}; searches keep using {LiveIndexName} until it is finished",
                target,
                configuration?.IndexName);
            progress.Report(2);

            // Snapshot the id set up front and page over that rather than over StartIndex/Limit.
            IReadOnlyList<Guid> itemIds;
            try
            {
                itemIds = _libraryManager.GetItemIds(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = IndexableItemTypes,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate library items; aborting reindex - re-run this task");
                return;
            }

            var totalCount = itemIds.Count;
            _logger.LogInformation("Found {TotalCount} items to index", totalCount);

            var reporter = new ReindexEmbeddingReporter(
                _embeddings,
                _logger,
                progress,
                fraction => Math.Min(2d + (fraction * 93d), 95d),
                totalCount);

            if (_embeddings.IsReady && totalCount > 0)
            {
                _logger.LogInformation(
                    "Semantic search is on, so this rebuild needs a vector for each of {TotalCount} items. "
                    + "Ones already in the cache are reused; the rest run through the embedding model, and "
                    + "progress is logged as they do",
                    totalCount);
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
            var consecutiveFetchFailures = 0;
            var abortedEarly = false;
            const int MaxConsecutiveFetchFailures = 5;

            while (startIndex < totalCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var idChunk = BuildChunk(itemIds, startIndex, batchSize);

                IReadOnlyList<BaseItem> items;
                try
                {
                    // Items deleted since the snapshot simply don't come back, which is correct:
                    // their removal is handled by the real-time sync queue.
                    items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = idChunk });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Error fetching items at offset {StartIndex}, skipping batch",
                        startIndex);
                    errorCount += idChunk.Length;
                    startIndex += batchSize;

                    if (++consecutiveFetchFailures >= MaxConsecutiveFetchFailures)
                    {
                        _logger.LogError(
                            "Aborting reindex after {FailureCount} consecutive fetch failures (last at offset {StartIndex})",
                            consecutiveFetchFailures,
                            startIndex);
                        abortedEarly = true;
                        break;
                    }

                    continue;
                }

                consecutiveFetchFailures = 0;

                // Pre-fetch all people for this page in a single DB query (eliminates F2's N+1).
                var peopleEligibleIds = items
                    .Where(MeilisearchIndexService.ShouldIndexItem)
                    .Where(i => i.SupportsPeople)
                    .Select(i => i.Id)
                    .Distinct()
                    .ToArray();

                var peopleLookup = peopleEligibleIds.Length > 0
                    ? _libraryManager.GetPeopleNamesByItems(peopleEligibleIds, [])
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
                    // Embed on this thread rather than inside the parallel push below: inference is
                    // already internally parallel, and overlapping batches would oversubscribe the
                    // CPU - or, on a GPU provider, the device.
                    batchNumber++;
                    reporter.AttachVectors(batch, batchNumber, startIndex, idChunk.Length, cancellationToken);

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

                startIndex += batchSize;

                // Measured against the snapshot position rather than the item count, so items
                // deleted since the snapshot don't stall progress short of the end.
                var position = Math.Min(startIndex, totalCount);
                var progressPercent = reporter.ReportProgress(position);

                _logger.LogInformation(
                    "Indexed batch {BatchNumber}: {IndexedCount} indexed, {SkippedCount} skipped, "
                    + "{ErrorCount} errors ({ProgressPercent}%){Embedding}{Remaining}",
                    batchNumber,
                    indexedCount,
                    skippedCount,
                    errorCount,
                    progressPercent.ToString("F1", CultureInfo.InvariantCulture),
                    reporter.DescribeLastBatch(),
                    reporter.DescribeRemaining(position));
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

            _logger.LogInformation(
                "Meilisearch reindex finished. Indexed {IndexedCount} items, skipped {SkippedCount} items, "
                + "{ErrorCount} errors in {BatchCount} batches ({TaskCount} Meilisearch tasks){Embedding}",
                indexedCount,
                skippedCount,
                errorCount,
                batchNumber,
                taskUids.Count,
                reporter.DescribeRun());

            if (abortedEarly || taskFailures > 0)
            {
                _logger.LogError(
                    "Meilisearch reindex did not complete cleanly - re-run this task. The half-built index was "
                    + "discarded, searches keep using the previous one, and the incremental sync watermark was "
                    + "left unchanged");
            }
            else
            {
                // Only now, with every document accepted, does the rebuild become the live index.
                await _client.CommitRebuildAsync(cancellationToken).ConfigureAwait(false);
                completedCleanly = true;
            }

            progress.Report(100);

            // Anchor the incremental task's watermark so it doesn't re-index everything (or fall
            // back to the 24h heuristic) on its next run. We use the pre-work timestamp so any
            // items modified during the reindex still get picked up.
            var plugin = Plugin.Instance;
            if (completedCleanly && plugin is not null)
            {
                plugin.Configuration.LastIncrementalReindexUtc = runStart;
                plugin.Configuration.IndexSchemaVersion = MeilisearchDocument.SchemaVersion;
                plugin.Configuration.IndexedEmbeddingModelId = _embeddings.IsReady
                    ? EmbeddingService.ActiveModel.IndexIdentity
                    : string.Empty;
                plugin.SaveConfiguration();
                _logger.LogInformation("Updated incremental sync watermark to {RunStart:O}", runStart);
            }
        }
        finally
        {
            // A run that was cancelled or threw leaves a half-built staging index behind; the live
            // one has been serving the whole time and stays as it is.
            if (!completedCleanly)
            {
                await _client.AbandonRebuildAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await _indexService.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Resumed real-time sync");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resuming real-time sync after reindex");
            }

            // Only a clean run saw the whole library, so only a clean run may prune what it missed.
            _embeddings.EndCacheRetention(completedCleanly);
        }
    }
}
