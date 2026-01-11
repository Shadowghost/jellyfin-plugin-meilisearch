using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ILogger<IncrementalReindexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalReindexTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="logger">The logger.</param>
    public IncrementalReindexTask(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        ILogger<IncrementalReindexTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
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

        var totalCount = 0;
        try
        {
            totalCount = _libraryManager.GetCount(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = ReindexTask.IndexableItemTypes,
                MinDateLastSaved = since,
            });
            _logger.LogInformation("Found {TotalCount} modified items to sync", totalCount);
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
                    IncludeItemTypes = ReindexTask.IndexableItemTypes,
                    MinDateLastSaved = since,
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

            // Pre-fetch all people for this page in a single DB query (avoids F2's N+1).
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
                progressPercent = fraction * 95d;
            }
            else
            {
                progressPercent = 95d - (90d / (1d + (processedCount / 1_000d)));
            }

            progressPercent = Math.Min(progressPercent, 95d);
            progress.Report(progressPercent);

            _logger.LogInformation(
                "Incremental batch {BatchNumber}: {IndexedCount} indexed, {SkippedCount} skipped, {ErrorCount} errors ({ProgressPercent:F1}%)",
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

        // Persist the run-start timestamp only after a successful sweep.
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.LastIncrementalReindexUtc = runStart;
            plugin.SaveConfiguration();
        }

        progress.Report(100);
        _logger.LogInformation(
            "Incremental Meilisearch sync complete. Indexed {IndexedCount} items, skipped {SkippedCount} items, {ErrorCount} errors in {BatchCount} batches ({TaskCount} Meilisearch tasks). Next run will pick up changes since {RunStart:O}",
            indexedCount,
            skippedCount,
            errorCount,
            batchNumber,
            taskUids.Count,
            runStart);
    }
}
