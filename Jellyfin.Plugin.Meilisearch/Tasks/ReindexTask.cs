using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Scheduled task to rebuild the Meilisearch index.
/// </summary>
public class ReindexTask : IScheduledTask
{
    private const int BatchSize = 10000;
    private readonly ILibraryManager _libraryManager;
    private readonly MeilisearchClientWrapper _client;
    private readonly ILogger<ReindexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="logger">The logger.</param>
    public ReindexTask(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        ILogger<ReindexTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
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

        progress.Report(0);
        _logger.LogInformation("Starting Meilisearch reindex task");

        _logger.LogInformation("Resetting Meilisearch index");
        await _client.ResetIndexAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(5);

        _logger.LogInformation("Counting library items");
        var countQuery = new InternalItemsQuery
        {
            Recursive = true
        };

        var totalItems = _libraryManager.GetCount(countQuery);
        _logger.LogInformation("Found {TotalCount} items to process", totalItems);
        progress.Report(10);

        if (totalItems == 0)
        {
            _logger.LogInformation("No items to index");
            progress.Report(100);
            return;
        }

        const double IndexingStartPercent = 10;
        const double IndexingEndPercent = 95;
        var indexingRange = IndexingEndPercent - IndexingStartPercent;

        var batch = new List<MeilisearchDocument>(BatchSize);
        var processedCount = 0;
        var indexedCount = 0;
        var skippedCount = 0;
        var errorCount = 0;
        var batchNumber = 0;
        var startIndex = 0;

        while (startIndex < totalItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<BaseItem> items;
            try
            {
                var pageQuery = new InternalItemsQuery
                {
                    Recursive = true,
                    StartIndex = startIndex,
                    Limit = BatchSize
                };

                items = _libraryManager.GetItemList(pageQuery);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Error fetching items at offset {StartIndex}, skipping batch",
                    startIndex);
                errorCount += BatchSize;
                startIndex += BatchSize;
                continue;
            }

            if (items.Count == 0)
            {
                break;
            }

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

                    batch.Add(MeilisearchIndexService.CreateDocument(item));
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
                await _client.IndexDocumentsAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            var progressPercent = IndexingStartPercent + (indexingRange * processedCount / totalItems);
            progress.Report(progressPercent);

            _logger.LogDebug(
                "Indexed batch {BatchNumber}: {IndexedCount} indexed, {SkippedCount} skipped, {ErrorCount} errors ({ProgressPercent:F1}%)",
                batchNumber,
                indexedCount,
                skippedCount,
                errorCount,
                progressPercent);

            startIndex += BatchSize;
        }

        progress.Report(100);
        _logger.LogInformation(
            "Meilisearch reindex complete. Indexed {IndexedCount} items, skipped {SkippedCount} items, {ErrorCount} errors in {BatchCount} batches",
            indexedCount,
            skippedCount,
            errorCount,
            batchNumber);
    }
}
