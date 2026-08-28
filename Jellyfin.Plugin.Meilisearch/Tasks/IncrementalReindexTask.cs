using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Meilisearch.Embeddings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Scheduled task that performs an incremental reindex of items modified since the previous incremental run.
/// </summary>
public class IncrementalReindexTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly MeilisearchClientWrapper _client;
    private readonly MeilisearchIndexService _indexService;
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<IncrementalReindexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalReindexTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="indexService">The index service used to pause real-time sync during the sweep.</param>
    /// <param name="embeddings">The embedding service used to attach vectors to indexed documents.</param>
    /// <param name="logger">The logger.</param>
    public IncrementalReindexTask(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        MeilisearchIndexService indexService,
        EmbeddingService embeddings,
        ILogger<IncrementalReindexTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _indexService = indexService;
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Incremental Meilisearch Sync";

    /// <inheritdoc />
    public string Key => "MeilisearchIncrementalReindex";

    /// <inheritdoc />
    public string Description => "Indexes library items modified since the last incremental sync.";

    /// <inheritdoc />
    public string Category => "Search";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            _logger.LogWarning("Meilisearch is not configured. Skipping incremental reindex task");
            return;
        }

        if (!await ReindexCoordinator.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("A full reindex is in progress; skipping this incremental sync");
            return;
        }

        try
        {
            // Hold real-time writes for the duration of the sweep.
            await _indexService.PauseAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await ExecuteCoreAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _indexService.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error resuming real-time sync after incremental sync");
                }
            }
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

        DateTime since;
        if (configuration?.LastIncrementalReindexUtc is { } previous)
        {
            since = previous;
        }
        else
        {
            since = DateTime.UtcNow - TimeSpan.FromDays(1);
            _logger.LogInformation(
                "No previous incremental sync timestamp; using {Since:O} (last 24h). Run the full reindex task for a complete rebuild",
                since);
        }

        // Capture the run-start instant before querying so items modified during
        // the run aren't lost on the next pass.
        var runStart = DateTime.UtcNow;

        progress.Report(0);
        _logger.LogInformation(
            "Starting incremental Meilisearch sync for items modified since {Since:O} (batch size {BatchSize}, parallelism {Parallelism})",
            since,
            batchSize,
            parallelism);

        // Snapshot the id set and page over that. See ReindexTask for why offset pagination over a
        // live library is unsafe here.
        IReadOnlyList<Guid> itemIds;
        try
        {
            itemIds = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = ReindexTask.IndexableItemTypes,
                MinDateLastSaved = since,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate modified items; skipping this incremental sync");
            return;
        }

        var totalCount = itemIds.Count;
        _logger.LogInformation("Found {TotalCount} modified items to sync", totalCount);

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

            var idChunk = ReindexTask.BuildChunk(itemIds, startIndex, batchSize);

            IReadOnlyList<BaseItem> items;
            try
            {
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
                        "Aborting incremental sync after {FailureCount} consecutive fetch failures (last at offset {StartIndex})",
                        consecutiveFetchFailures,
                        startIndex);
                    abortedEarly = true;
                    break;
                }

                continue;
            }

            consecutiveFetchFailures = 0;

            // Pre-fetch all people for this page in a single DB query (avoids F2's N+1).
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
                _embeddings.AttachVectors(batch, cancellationToken);

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

            startIndex += batchSize;

            // Measured against the snapshot position rather than the item count, so items deleted
            // since the snapshot don't stall progress short of the end.
            var fraction = Math.Min(1d, (double)Math.Min(startIndex, totalCount) / totalCount);
            var progressPercent = Math.Min(fraction * 95d, 95d);
            progress.Report(progressPercent);

            _logger.LogInformation(
                "Incremental batch {BatchNumber}: {IndexedCount} indexed, {SkippedCount} skipped, {ErrorCount} errors ({ProgressPercent:F1}%)",
                batchNumber,
                indexedCount,
                skippedCount,
                errorCount,
                progressPercent.ToString("F1", CultureInfo.InvariantCulture));
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);
        progress.Report(97);

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
                "{FailureCount} of {TotalTasks} Meilisearch incremental indexing tasks did not complete successfully",
                taskFailures,
                taskUids.Count);
        }

        // Advance the watermark only when the sweep actually covered everything it set out to.
        var plugin = Plugin.Instance;
        if (abortedEarly || taskFailures > 0)
        {
            _logger.LogWarning(
                "Incremental sync did not complete cleanly; leaving the watermark at {Since:O} so the next run retries the same window",
                since);
        }
        else if (plugin is not null)
        {
            plugin.Configuration.LastIncrementalReindexUtc = runStart;
            plugin.SaveConfiguration();
        }

        progress.Report(100);
        _logger.LogInformation(
            "Incremental Meilisearch sync complete. Indexed {IndexedCount} items, skipped {SkippedCount} items, {ErrorCount} errors in {BatchCount} batches ({TaskCount} Meilisearch tasks). Next run will pick up changes since {NextSince:O}",
            indexedCount,
            skippedCount,
            errorCount,
            batchNumber,
            taskUids.Count,
            abortedEarly || taskFailures > 0 ? since : runStart);
    }
}
