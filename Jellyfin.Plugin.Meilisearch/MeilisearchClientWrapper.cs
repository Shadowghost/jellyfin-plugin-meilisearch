using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Meilisearch.Configuration;
using Jellyfin.Plugin.Meilisearch.Embeddings;
using Meilisearch;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Wrapper around the Meilisearch client for Jellyfin integration.
/// </summary>
public class MeilisearchClientWrapper : IDisposable
{
    private const double TaskWaitTimeoutMs = 3 * 60 * 1000;
    private const int TaskWaitIntervalMs = 250;

    // Budget for a single AddDocuments request. Meilisearch rejects payloads over 100 MB by default;
    // the gap absorbs the error in the size estimate below and any lower limit imposed by a proxy.
    private const long MaxIndexPayloadBytes = 64L * 1024 * 1024;

    // Enclosing brackets of the JSON array a batch is serialised into.
    private const int JsonArrayOverheadBytes = 2;

    // Allowance per document for the scalar fields and the property names around the values weighed
    // individually in EstimateDocumentBytes.
    private const int DocumentOverheadBytes = 1024;

    // Quotes, separator, and room for escaping in a JSON string value.
    private const int TextFieldOverheadBytes = 8;

    // "_vectors" key plus the "embeddings"/"regenerate" scaffolding around one embedding.
    private const int VectorOverheadBytes = 64;

    // One serialised float and its separator, e.g. "-0.052734375,".
    private const int VectorComponentBytes = 16;

    // Weight given to the newest sample in the rolling search-latency average. Low enough that one
    // slow query does not dominate the figure shown on the config page.
    private const double SearchTimeSmoothingFactor = 0.25;

    private static readonly string[] SupportedMatchingStrategies = ["frequency", "last", "all"];
    private static readonly string[] IdOnly = ["id"];
    private static readonly string[] IdAndType = ["id", "itemType"];

    private readonly ILogger<MeilisearchClientWrapper> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly object _metricsLock = new();
    private MeilisearchClient? _client;
    private string? _currentUrl;
    private string? _currentApiKey;
    private double _averageSearchMilliseconds;
    private long _searchCount;

    // Set while a rebuild is building into a second index, which is where writes go until the swap.
    private volatile string? _rebuildIndexName;

    private volatile global::Meilisearch.Index? _cachedIndex;
    private volatile string? _cachedIndexKey;
    private volatile string? _settingsAppliedKey;

    // Set once the server rejects the configured matching strategy, so the fallback is used for the
    // rest of this connection instead of retrying a query that is known to fail.
    private volatile bool _matchingStrategyRejected;

    // The last unrecognized matching strategy warned about, so a mistyped configuration value is
    // logged once instead of once per query.
    private volatile string? _warnedMatchingStrategy;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchClientWrapper"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public MeilisearchClientWrapper(ILogger<MeilisearchClientWrapper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether the client is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Configuration.MeilisearchUrl);

    /// <summary>
    /// Gets the rolling average time a search takes end to end, in milliseconds, or null when no
    /// search has run yet.
    /// </summary>
    /// <remarks>
    /// An exponential moving average weighted by <see cref="SearchTimeSmoothingFactor"/>, so it
    /// tracks current behaviour rather than accumulating history. Covers embedding the query and the
    /// Meilisearch call, but not the time Jellyfin then spends loading and access-filtering items.
    /// </remarks>
    public double? AverageSearchTimeMilliseconds
    {
        get
        {
            lock (_metricsLock)
            {
                return _searchCount == 0 ? null : _averageSearchMilliseconds;
            }
        }
    }

    /// <summary>
    /// Gets the number of Meilisearch search requests issued since startup.
    /// </summary>
    public long SearchCount
    {
        get
        {
            lock (_metricsLock)
            {
                return _searchCount;
            }
        }
    }

    /// <summary>
    /// Gets the matching strategy in effect, which is the configured one unless the server has
    /// rejected it and the fallback took over.
    /// </summary>
    public string EffectiveMatchingStrategy => ResolveMatchingStrategy();

    /// <summary>
    /// Discards the cached client, index handle and applied-settings marker so that the next request
    /// reconnects from scratch. Exposed for the config page's reconnect action; recovery from a
    /// transient failure happens automatically and does not need this.
    /// </summary>
    public void Reconnect()
    {
        ResetClient();
    }

    /// <summary>
    /// Searches for documents matching the query.
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="filter">Optional Meilisearch filter expression.</param>
    /// <param name="queryVector">Optional query embedding. When supplied the query runs as a hybrid keyword/vector search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results with IDs and scores.</returns>
    public async Task<IReadOnlyList<(string Id, double Score)>> SearchAsync(
        string searchTerm,
        int limit,
        string? filter,
        double[]? queryVector,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        try
        {
            return await ExecuteSearchAsync<IReadOnlyList<(string Id, double Score)>>(
                async (matchingStrategy, ct) =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    var searchParams = new SearchQuery
                    {
                        Limit = limit,
                        ShowRankingScore = true,
                        MatchingStrategy = matchingStrategy,
                        Filter = filter,
                        AttributesToRetrieve = IdOnly
                    };

                    ApplyScoreThreshold(searchParams, queryVector);
                    ApplyHybrid(searchParams, queryVector);

                    var results = await index.SearchAsync<MeilisearchDocument>(searchTerm, searchParams, ct).ConfigureAwait(false);

                    return results.Hits
                        .Select(hit => (hit.Id, hit.RankingScore ?? 0.0))
                        .ToList();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Meilisearch for term '{SearchTerm}'", searchTerm);
            return [];
        }
    }

    /// <summary>
    /// Searches every requested item type in one query and applies the per-type quota to the hits it
    /// returns, so that strongly-matching documents of one type (e.g. songs by an artist) cannot
    /// drown out weaker but relevant matches of other types (e.g. movies, episodes).
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="types">The item types to search.</param>
    /// <param name="totalLimit">Maximum total number of results across all types.</param>
    /// <param name="extraFilter">Optional Meilisearch filter expression applied on top of the type filter.</param>
    /// <param name="queryVector">Optional query embedding. When supplied the query runs as a hybrid keyword/vector search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of (id, score) tuples in descending score order, capped at <paramref name="totalLimit"/>.</returns>
    public async Task<IReadOnlyList<(string Id, double Score)>> MultiTypeSearchAsync(
        string searchTerm,
        IReadOnlyList<BaseItemKind> types,
        int totalLimit,
        string? extraFilter,
        double[]? queryVector,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured || types.Count == 0)
        {
            return [];
        }

        try
        {
            return await ExecuteSearchAsync<IReadOnlyList<(string Id, double Score)>>(
                async (matchingStrategy, ct) =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);

                    var distinctTypes = types.Distinct().ToArray();
                    var typeFilter = $"itemType IN [{string.Join(", ", distinctTypes.Select(t => $"\"{t}\""))}]";
                    var filter = string.IsNullOrEmpty(extraFilter)
                        ? typeFilter
                        : $"{typeFilter} AND {extraFilter}";

                    var searchParams = new SearchQuery
                    {
                        Limit = totalLimit,
                        ShowRankingScore = true,
                        MatchingStrategy = matchingStrategy,
                        Filter = filter,

                        // itemType on top of the id: the quota below buckets by it, and it is one
                        // short string per hit against the whole document this would otherwise return.
                        AttributesToRetrieve = IdAndType
                    };

                    ApplyScoreThreshold(searchParams, queryVector);
                    ApplyHybrid(searchParams, queryVector);

                    var results = await index.SearchAsync<MeilisearchDocument>(searchTerm, searchParams, ct).ConfigureAwait(false);

                    return ApplyPerTypeQuota(results.Hits, distinctTypes.Length, totalLimit);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing multi-type Meilisearch for term '{SearchTerm}'", searchTerm);
            return [];
        }
    }

    private static IReadOnlyList<(string Id, double Score)> ApplyPerTypeQuota(
        IEnumerable<MeilisearchDocument> hits,
        int typeCount,
        int totalLimit)
    {
        // The same share the per-type sub-queries used as their individual limits, so a query that
        // would have saturated every one of them selects the same documents it did before.
        var perTypeLimit = Math.Min(Math.Max(20, totalLimit / Math.Max(1, typeCount)), totalLimit);

        var selected = new List<(string Id, double Score)>(Math.Min(totalLimit, 512));
        var overflow = new List<(string Id, double Score)>();
        var takenPerType = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            if (string.IsNullOrEmpty(hit.Id))
            {
                continue;
            }

            var entry = (hit.Id, hit.RankingScore ?? 0.0);
            var itemType = hit.ItemType ?? string.Empty;

            takenPerType.TryGetValue(itemType, out var taken);
            if (taken < perTypeLimit)
            {
                takenPerType[itemType] = taken + 1;
                selected.Add(entry);
                continue;
            }

            // Over its share. Held back rather than dropped, in case the budget is not spent.
            overflow.Add(entry);
        }

        // Both lists are already in descending score order, so the refill only has to take a prefix.
        if (selected.Count < totalLimit && overflow.Count > 0)
        {
            selected.AddRange(overflow.Take(totalLimit - selected.Count));
            selected.Sort(static (a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : string.CompareOrdinal(a.Id, b.Id);
            });
        }

        if (selected.Count > totalLimit)
        {
            selected.RemoveRange(totalLimit, selected.Count - totalLimit);
        }

        return selected;
    }

    /// <summary>
    /// Indexes a single document.
    /// </summary>
    /// <param name="document">The document to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Meilisearch task UID, or null when not configured or on failure.</returns>
    public async Task<int?> IndexDocumentAsync(MeilisearchDocument document, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            return await ExecuteWithReconnectRetryAsync<int?>(
                async ct =>
                {
                    var index = await GetWriteIndexAsync(ct).ConfigureAwait(false);
                    var task = await index.AddDocumentsAsync([document], cancellationToken: ct).ConfigureAwait(false);
                    _logger.LogDebug("Indexed document {Id} ({Name})", document.Id, document.Name);

                    return task.TaskUid;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing document {Id}", document.Id);
            return null;
        }
    }

    /// <summary>
    /// Indexes multiple documents in a batch.
    /// </summary>
    /// <param name="documents">The documents to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Meilisearch task UID, or null when not configured or on failure.</returns>
    public async Task<int?> IndexDocumentsAsync(IEnumerable<MeilisearchDocument> documents, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            var docList = documents.ToList();
            int? lastTaskUid = null;
            foreach (var chunk in SplitForPayloadLimit(docList))
            {
                lastTaskUid = await ExecuteWithReconnectRetryAsync<int?>(
                    async ct =>
                    {
                        var index = await GetWriteIndexAsync(ct).ConfigureAwait(false);
                        var task = await index.AddDocumentsAsync(chunk, cancellationToken: ct).ConfigureAwait(false);
                        _logger.LogDebug("Indexed {Count} documents", chunk.Count);

                        return task.TaskUid;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            // Meilisearch processes an index's tasks in the order they were enqueued, so awaiting the
            // last uid covers every chunk of this batch.
            return lastTaskUid;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing documents batch");
            return null;
        }
    }

    /// <summary>
    /// Splits a batch into requests that stay under the server's payload limit. A document carrying a
    /// 1024-dimension embedding serialises to roughly 12 KB, so a few thousand of them are enough to
    /// blow past the 100 MB Meilisearch accepts by default and have the whole batch rejected.
    /// </summary>
    private static IEnumerable<List<MeilisearchDocument>> SplitForPayloadLimit(List<MeilisearchDocument> documents)
    {
        var chunk = new List<MeilisearchDocument>();
        var chunkBytes = (long)JsonArrayOverheadBytes;

        foreach (var document in documents)
        {
            var documentBytes = EstimateDocumentBytes(document);

            if (chunk.Count > 0 && chunkBytes + documentBytes > MaxIndexPayloadBytes)
            {
                yield return chunk;
                chunk = [];
                chunkBytes = JsonArrayOverheadBytes;
            }

            chunk.Add(document);
            chunkBytes += documentBytes;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Approximates the serialised size of a document. Deliberately an estimate rather than a
    /// measurement: serialising every batch twice just to weigh it would double the work for the
    /// common case that fits in a single request. The budget it is compared against leaves enough
    /// headroom to absorb the error.
    /// </summary>
    private static long EstimateDocumentBytes(MeilisearchDocument document)
    {
        var bytes = (long)DocumentOverheadBytes;

        bytes += TextBytes(document.Id);
        bytes += TextBytes(document.Name);
        bytes += TextBytes(document.OriginalTitle);
        bytes += TextBytes(document.SortName);
        bytes += TextBytes(document.Overview);
        bytes += TextBytes(document.Tagline);
        bytes += TextBytes(document.ItemType);
        bytes += TextBytes(document.MediaType);
        bytes += TextBytes(document.OfficialRating);
        bytes += TextBytes(document.SeriesName);
        bytes += TextBytes(document.SeriesId);
        bytes += TextBytes(document.SeasonName);
        bytes += TextBytes(document.SeasonId);
        bytes += TextBytes(document.AlbumName);
        bytes += TextBytes(document.AlbumId);
        bytes += TextBytes(document.ParentId);
        bytes += TextBytes(document.Container);
        bytes += TextBytes(document.Path);
        bytes += TextBytes(document.TopParentId);
        bytes += TextBytes(document.Genres);
        bytes += TextBytes(document.Tags);
        bytes += TextBytes(document.Studios);
        bytes += TextBytes(document.ProductionLocations);
        bytes += TextBytes(document.Artists);
        bytes += TextBytes(document.AlbumArtists);
        bytes += TextBytes(document.People);
        bytes += TextBytes(document.AncestorIds);

        if (document.ProviderIds is not null)
        {
            foreach (var (key, value) in document.ProviderIds)
            {
                bytes += TextBytes(key) + TextBytes(value);
            }
        }

        if (document.Vectors is not null)
        {
            foreach (var (name, vector) in document.Vectors)
            {
                bytes += TextBytes(name) + VectorOverheadBytes + ((long)vector.Embeddings.Count * VectorComponentBytes);
            }
        }

        return bytes;
    }

    private static long TextBytes(string? value)
        => value is null ? 0 : Encoding.UTF8.GetByteCount(value) + TextFieldOverheadBytes;

    private static long TextBytes(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return 0;
        }

        var bytes = 0L;
        foreach (var value in values)
        {
            bytes += TextBytes(value);
        }

        return bytes;
    }

    /// <summary>
    /// Removes a document from the index.
    /// </summary>
    /// <param name="documentId">The document ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            await ExecuteWithReconnectRetryAsync(
                async ct =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    await index.DeleteOneDocumentAsync(documentId, ct).ConfigureAwait(false);
                    _logger.LogDebug("Removed document {Id}", documentId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing document {Id}", documentId);
        }
    }

    /// <summary>
    /// Removes multiple documents from the index in a single bulk operation.
    /// </summary>
    /// <param name="documentIds">The document IDs to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the removal was accepted by Meilisearch; false if it failed.</returns>
    public async Task<bool> RemoveDocumentsAsync(IEnumerable<string> documentIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var idList = documentIds.ToList();
            await ExecuteWithReconnectRetryAsync(
                async ct =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    await index.DeleteDocumentsAsync(idList, ct).ConfigureAwait(false);
                    _logger.LogDebug("Removed {Count} documents", idList.Count);
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing documents batch");
            return false;
        }
    }

    /// <summary>
    /// Prepares the index a full rebuild writes into, and directs writes there.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name of the index the rebuild will populate.</returns>
    /// <remarks>
    /// A rebuild is never written into the live index. Emptying it first would mean hours of no
    /// search results on a large library, so the replacement is built beside it and swapped in by
    /// <see cref="CommitRebuildAsync"/> once every document has been accepted. The staging index is
    /// created empty and fully configured, so the swap puts a complete index live in one step, and
    /// anything left over from an interrupted rebuild is dropped first: it holds a partial library,
    /// and continuing into it would leave documents no longer in the library behind.
    /// </remarks>
    public async Task<string> BeginRebuildAsync(CancellationToken cancellationToken)
    {
        _rebuildIndexName = null;

        // Named after the live index so it is obvious what it belongs to, and so a leftover from
        // an interrupted run is recognisable rather than mysterious.
        var staging = Configuration.IndexName + "_rebuild";

        await ExecuteWithReconnectRetryAsync(
            async ct =>
            {
                var client = GetClient();

                await DeleteIndexIfPresentAsync(client, staging, ct).ConfigureAwait(false);

                _logger.LogInformation("Building the rebuild into {IndexName}; the live index keeps serving searches", staging);
                var createTask = await client.CreateIndexAsync(staging, "id", ct).ConfigureAwait(false);
                await client.WaitForTaskAsync(createTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, ct).ConfigureAwait(false);

                await ConfigureIndexSettingsAsync(client.Index(staging), isNewIndex: true, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        _rebuildIndexName = staging;
        return staging;
    }

    /// <summary>
    /// Puts a finished rebuild live, swapping the staging index with the one searches use and
    /// dropping what it replaced.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>
    /// A swap is atomic from a searcher's point of view: queries answered before it see the old
    /// index, queries after it the new one, and none of them see an empty one.
    /// </remarks>
    public async Task CommitRebuildAsync(CancellationToken cancellationToken)
    {
        var staging = _rebuildIndexName;
        if (staging is null)
        {
            return;
        }

        var live = Configuration.IndexName;

        await ExecuteWithReconnectRetryAsync(
            async ct =>
            {
                var client = GetClient();

                // A swap needs both sides to exist; on a first run there is no live index yet.
                await GetOrCreateIndexAsync(ct).ConfigureAwait(false);

                var swapTask = await client.SwapIndexesAsync([new IndexSwap(live, staging, false)], ct).ConfigureAwait(false);
                await client.WaitForTaskAsync(swapTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, ct).ConfigureAwait(false);

                _logger.LogInformation("Swapped the rebuilt index into {IndexName}", live);

                // Now holding what used to be live.
                await DeleteIndexIfPresentAsync(client, staging, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        _rebuildIndexName = null;

        // The handle names an index whose contents were replaced underneath it; drop it so the next
        // access re-reads the settings that came across with the swap.
        InvalidateIndexCache();
    }

    /// <summary>
    /// Discards a rebuild that did not finish, leaving the live index untouched.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task AbandonRebuildAsync(CancellationToken cancellationToken)
    {
        var staging = _rebuildIndexName;
        _rebuildIndexName = null;

        if (staging is null)
        {
            return;
        }

        try
        {
            await ExecuteWithReconnectRetryAsync(
                async ct =>
                {
                    _logger.LogInformation("Discarding the unfinished rebuild in {IndexName}", staging);
                    await DeleteIndexIfPresentAsync(GetClient(), staging, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Worth a line but not worth failing on: the next rebuild deletes it before it starts.
            _logger.LogWarning(ex, "Could not remove the unfinished rebuild index {IndexName}", staging);
        }
    }

    /// <summary>
    /// Tests the connection to Meilisearch including authentication.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the server is reachable and the API key (when configured) authenticates.</returns>
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var health = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return health.IsAuthenticated;
    }

    /// <summary>
    /// Performs a connection and authentication health check against the Meilisearch server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="MeilisearchHealth"/> describing reachability, authentication and any error message.</returns>
    public async Task<MeilisearchHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new MeilisearchHealth(false, false, "Not configured");
        }

        try
        {
            await ExecuteWithReconnectRetryAsync(
                async ct =>
                {
                    // GetClient() inside the operation ensures the retry runs against the recreated client.
                    await GetClient().HealthAsync(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch health check failed");
            return new MeilisearchHealth(false, false, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(Configuration.ApiKey))
        {
            return new MeilisearchHealth(true, true, null);
        }

        try
        {
            await ExecuteWithReconnectRetryAsync(
                async ct =>
                {
                    await GetClient().GetStatsAsync(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return new MeilisearchHealth(true, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch authentication check failed");
            return new MeilisearchHealth(true, false, ex.Message);
        }
    }

    /// <summary>
    /// Gets the index statistics for the configured Meilisearch index.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Meilisearch <see cref="IndexStats"/>, or null on error or when not configured.</returns>
    public async Task<IndexStats?> GetIndexStatsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            return await ExecuteWithReconnectRetryAsync<IndexStats?>(
                async ct =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    return await index.GetStatsAsync(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving Meilisearch index stats");
            return null;
        }
    }

    /// <summary>
    /// Awaits the completion of a Meilisearch task by its UID.
    /// </summary>
    /// <param name="taskUid">The task UID to await.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the task finished with the <c>Succeeded</c> status, false otherwise.</returns>
    public async Task<bool> AwaitTaskAsync(int taskUid, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            return await ExecuteWithReconnectRetryAsync<bool>(
                async ct =>
                {
                    var client = GetClient();
                    var resource = await client.WaitForTaskAsync(taskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, ct).ConfigureAwait(false);
                    return resource.Status == TaskInfoStatus.Succeeded;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error awaiting Meilisearch task {TaskUid}", taskUid);
            return false;
        }
    }

    /// <summary>
    /// Gets the Meilisearch client, creating or recreating it if configuration changed.
    /// </summary>
    /// <returns>The Meilisearch client.</returns>
    private MeilisearchClient GetClient()
    {
        var config = Configuration;

        _clientLock.Wait();
        try
        {
            if (_client is null || _currentUrl != config.MeilisearchUrl || _currentApiKey != config.ApiKey)
            {
                _currentUrl = config.MeilisearchUrl;
                _currentApiKey = config.ApiKey;
                _client = string.IsNullOrWhiteSpace(config.ApiKey)
                    ? new MeilisearchClient(config.MeilisearchUrl)
                    : new MeilisearchClient(config.MeilisearchUrl, config.ApiKey);

                // Configuration changed; invalidate cached index/settings. The matching-strategy
                // probe is per-server, so a new URL means asking again.
                _cachedIndex = null;
                _cachedIndexKey = null;
                _settingsAppliedKey = null;
                _matchingStrategyRejected = false;

                _logger.LogInformation("Created Meilisearch client for {Url}", config.MeilisearchUrl);
            }

            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Discards the cached client and index handles so the next access recreates them. Recreating the
    /// <see cref="MeilisearchClient"/> rebuilds its underlying <see cref="HttpClient"/>, which clears the pooled
    /// TCP connection and cached DNS entry. This is required to recover after the Meilisearch server is restarted
    /// or its container is recreated with a new address, without having to restart Jellyfin.
    /// </summary>
    private void ResetClient()
    {
        _clientLock.Wait();
        try
        {
            _client = null;
            _currentUrl = null;
            _currentApiKey = null;
            _cachedIndex = null;
            _cachedIndexKey = null;
            _settingsAppliedKey = null;
            _matchingStrategyRejected = false;
            _logger.LogInformation("Reset Meilisearch client; it will be recreated on next use");
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Executes a Meilisearch operation, transparently recreating the client and retrying once when a transient
    /// communication failure is detected. The operation must (re-)acquire the client/index internally so that the
    /// retry runs against the freshly created client.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    private async Task<T> ExecuteWithReconnectRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReconnectable(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Meilisearch communication failure; recreating client and retrying once");
            ResetClient();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a Meilisearch operation that returns no value, applying the same reconnect-and-retry behaviour as
    /// <see cref="ExecuteWithReconnectRetryAsync{T}"/>.
    /// </summary>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    private Task ExecuteWithReconnectRetryAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        => ExecuteWithReconnectRetryAsync<object?>(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return null;
            },
            cancellationToken);

    /// <summary>
    /// Determines whether an exception represents a transient communication failure that warrants recreating the
    /// client and retrying. A cancellation requested by the caller is deliberately not treated as reconnectable;
    /// only an <see cref="HttpClient"/>-originated timeout (a <see cref="TaskCanceledException"/> wrapping a
    /// <see cref="TimeoutException"/>) is.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> if the operation should be retried against a fresh client.</returns>
    private static bool IsReconnectable(Exception ex)
        => ex switch
        {
            MeilisearchCommunicationError => true,
            MeilisearchTimeoutError => true,
            HttpRequestException => true,
            TimeoutException => true,
            TaskCanceledException tce => tce.InnerException is TimeoutException,
            _ => false
        };

    /// <summary>
    /// Runs a search operation with the configured matching strategy, applying the same
    /// reconnect-and-retry behaviour as <see cref="ExecuteWithReconnectRetryAsync{T}"/> and, on top of
    /// it, falling back to <see cref="PluginConfiguration.FallbackMatchingStrategy"/> if the server
    /// rejects the strategy it was given.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The operation to execute; receives the matching strategy to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <remarks>
    /// Probing the server version needs the <c>version</c> action, which a restricted API key lacks,
    /// so support is inferred from the first rejection and then remembered for the connection.
    /// </remarks>
    private async Task<T> ExecuteSearchAsync<T>(Func<string, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var strategy = ResolveMatchingStrategy();

        try
        {
            return await ExecuteWithReconnectRetryAsync(ct => operation(strategy, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (IsUnsupportedMatchingStrategy(ex, strategy))
        {
            _matchingStrategyRejected = true;
            _logger.LogWarning(
                "Meilisearch rejected the '{Strategy}' matching strategy; using '{Fallback}' instead for this connection. The 'frequency' strategy needs Meilisearch 1.11 or newer",
                strategy,
                PluginConfiguration.FallbackMatchingStrategy);

            return await ExecuteWithReconnectRetryAsync(
                ct => operation(PluginConfiguration.FallbackMatchingStrategy, ct),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the matching strategy to send with a query: the configured one, corrected to
    /// <see cref="PluginConfiguration.FallbackMatchingStrategy"/> when it is unrecognized or when the
    /// server has already rejected it.
    /// </summary>
    /// <returns>A matching strategy Meilisearch accepts.</returns>
    private string ResolveMatchingStrategy()
    {
        var configured = Configuration.MatchingStrategy;
        var requested = string.IsNullOrWhiteSpace(configured)
            ? PluginConfiguration.DefaultMatchingStrategy
            : configured.Trim();

        string? canonical = null;
        foreach (var supported in SupportedMatchingStrategies)
        {
            if (string.Equals(supported, requested, StringComparison.OrdinalIgnoreCase))
            {
                canonical = supported;
                break;
            }
        }

        if (canonical is null)
        {
            // Only reachable by hand-editing the configuration file. Warn once per distinct value
            // rather than on every query.
            if (!string.Equals(_warnedMatchingStrategy, requested, StringComparison.Ordinal))
            {
                _warnedMatchingStrategy = requested;
                _logger.LogWarning(
                    "Unknown Meilisearch matching strategy '{Strategy}' configured; using '{Fallback}'",
                    requested,
                    PluginConfiguration.FallbackMatchingStrategy);
            }

            return PluginConfiguration.FallbackMatchingStrategy;
        }

        return _matchingStrategyRejected ? PluginConfiguration.FallbackMatchingStrategy : canonical;
    }

    /// <summary>
    /// Determines whether an API error is Meilisearch refusing the matching strategy that was sent,
    /// rather than a problem with the query itself.
    /// </summary>
    /// <param name="error">The error returned by Meilisearch.</param>
    /// <param name="strategy">The strategy that was sent.</param>
    /// <returns><c>true</c> if the query is worth retrying with the fallback strategy.</returns>
    private static bool IsUnsupportedMatchingStrategy(MeilisearchApiError error, string strategy)
    {
        if (string.Equals(strategy, PluginConfiguration.FallbackMatchingStrategy, StringComparison.Ordinal))
        {
            // The fallback is universally supported; a rejection means something else is wrong.
            return false;
        }

        return error.Code?.Contains("matching_strategy", StringComparison.OrdinalIgnoreCase) == true
            || error.Message?.Contains("matchingStrategy", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Records how long a search took end to end, for the rolling average the status endpoint
    /// reports.
    /// </summary>
    /// <param name="elapsedMilliseconds">Elapsed time, including embedding the query.</param>
    public void RecordSearchDuration(double elapsedMilliseconds)
    {
        lock (_metricsLock)
        {
            _averageSearchMilliseconds = _searchCount == 0
                ? elapsedMilliseconds
                : (SearchTimeSmoothingFactor * elapsedMilliseconds) + ((1 - SearchTimeSmoothingFactor) * _averageSearchMilliseconds);
            _searchCount++;
        }
    }

    /// <summary>
    /// Sets the score a hit has to reach to be returned at all.
    /// </summary>
    /// <param name="query">The query to augment.</param>
    /// <param name="queryVector">The query embedding, or null for a pure keyword search.</param>
    private static void ApplyScoreThreshold(SearchQuery query, double[]? queryVector)
    {
        var threshold = Math.Max(0, Configuration.MinimumMatchScore ?? 0);

        if (queryVector is { Length: > 0 })
        {
            threshold = Math.Max(threshold, Math.Clamp(Configuration.MinimumSemanticScore, 0, 100));
        }

        if (threshold > 0)
        {
            query.RankingScoreThreshold = threshold / 100m;
        }
    }

    /// <summary>
    /// Turns a query into a hybrid keyword/vector search when a query embedding is available.
    /// </summary>
    /// <param name="query">The query to augment.</param>
    /// <param name="queryVector">The query embedding, or null to leave the query as pure keyword search.</param>
    /// <remarks>
    /// The embedder is registered as <c>userProvided</c>, so Meilisearch cannot embed the query
    /// itself - the vector has to travel with the request.
    /// </remarks>
    private static void ApplyHybrid(SearchQuery query, double[]? queryVector)
    {
        if (queryVector is null || queryVector.Length == 0)
        {
            return;
        }

        query.Vector = queryVector;
        query.Hybrid = new HybridSearch
        {
            Embedder = EmbeddingService.EmbedderName,
            SemanticRatio = Math.Clamp(Configuration.SemanticRatio, 0, 100) / 100d
        };
    }

    /// <summary>
    /// The attributes keyword search looks at, ordered by priority, high to low.
    /// </summary>
    /// <remarks>
    /// Order matters: the <c>attribute</c> ranking rule is what keeps a title match above one buried
    /// in the plot, so the long free-text fields come last.
    /// </remarks>
    private static List<string> BuildSearchableAttributes(PluginConfiguration config)
    {
        var attributes = new List<string>(20)
        {
            "name",
            "originalTitle",
            "sortName",
            "seriesName",
            "seasonName",
            "albumName",
            "artists",
            "albumArtists",
            "people",
            "genres",
            "tags",
            "studios",
            "providerIds.Imdb",
            "providerIds.Tmdb",
            "providerIds.Tvdb",
            "productionLocations",
            "tagline"
        };

        if (config.SearchOverviews)
        {
            attributes.Add("overview");
        }

        // Lowest priority on purpose: a file-name match should never outrank a title or a plot
        // match, it only has to make the item findable by its release name.
        if (config.SearchFilePaths)
        {
            attributes.Add("path");
        }

        return attributes;
    }

    private static string BuildCacheKey(PluginConfiguration config)
        => string.Concat(
            config.MeilisearchUrl ?? string.Empty,
            "|",
            config.ApiKey ?? string.Empty,
            "|",
            config.IndexName ?? string.Empty,
            "|",
            config.EnableSemanticSearch ? "vec:" + EmbeddingModels.Resolve(config.EmbeddingModelId).Id : "novec",
            "|",
            config.BinaryQuantizeVectors ? "bq" : "f32",
            "|",
            // In the key so toggling either reapplies the index settings; otherwise the change would
            // never reach Meilisearch.
            config.SearchOverviews ? "ov" : "noov",
            config.SearchFilePaths ? "path" : "nopath");

    /// <summary>
    /// Invalidates the cached index handle and the applied-settings marker.
    /// </summary>
    private void InvalidateIndexCache()
    {
        _clientLock.Wait();
        try
        {
            _cachedIndex = null;
            _cachedIndexKey = null;
            _settingsAppliedKey = null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Gets the index documents are written to: the staging index while a zero-downtime rebuild is
    /// in progress, otherwise the live one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The index to write to.</returns>
    /// <remarks>
    /// Only writes are routed. Searches stay on the live index throughout, which is the whole point
    /// of building the replacement beside it.
    /// </remarks>
    private async Task<global::Meilisearch.Index> GetWriteIndexAsync(CancellationToken cancellationToken)
    {
        // Index(uid) only names the index rather than fetching it, so this costs no round trip per
        // batch. The staging index was created and configured by BeginRebuildAsync.
        if (_rebuildIndexName is { } staging)
        {
            return GetClient().Index(staging);
        }

        return await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteIndexIfPresentAsync(MeilisearchClient client, string indexName, CancellationToken cancellationToken)
    {
        try
        {
            var task = await client.DeleteIndexAsync(indexName, cancellationToken).ConfigureAwait(false);
            await client.WaitForTaskAsync(task.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            _logger.LogDebug("Index {IndexName} does not exist, nothing to delete", indexName);
        }
    }

    /// <summary>
    /// Gets the index, creating it if it doesn't exist and ensuring settings are up to date.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Meilisearch index.</returns>
    private async Task<global::Meilisearch.Index> GetOrCreateIndexAsync(CancellationToken cancellationToken)
    {
        var config = Configuration;
        var cacheKey = BuildCacheKey(config);
        var client = GetClient();

        // Fast path: index handle already cached for this configuration.
        var cached = _cachedIndex;
        if (cached is not null && _cachedIndexKey == cacheKey)
        {
            // Settings may still need to be applied if config (e.g. synonyms) changed without invalidating the handle.
            if (_settingsAppliedKey != cacheKey)
            {
                await ConfigureIndexSettingsIfNeededAsync(cached, cacheKey, false, cancellationToken).ConfigureAwait(false);
            }

            return cached;
        }

        var indexName = config.IndexName;
        var isNewIndex = false;

        global::Meilisearch.Index index;
        try
        {
            index = await client.GetIndexAsync(indexName, cancellationToken).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            _logger.LogInformation("Creating Meilisearch index {IndexName}", indexName);
            var task = await client.CreateIndexAsync(indexName, "id", cancellationToken).ConfigureAwait(false);
            await client.WaitForTaskAsync(task.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken).ConfigureAwait(false);
            index = await client.GetIndexAsync(indexName, cancellationToken).ConfigureAwait(false);
            isNewIndex = true;
        }

        _cachedIndex = index;
        _cachedIndexKey = cacheKey;

        await ConfigureIndexSettingsIfNeededAsync(index, cacheKey, isNewIndex, cancellationToken).ConfigureAwait(false);

        return index;
    }

    /// <summary>
    /// Applies index settings if they have not yet been applied for the current configuration key.
    /// </summary>
    private async Task ConfigureIndexSettingsIfNeededAsync(global::Meilisearch.Index index, string cacheKey, bool isNewIndex, CancellationToken cancellationToken)
    {
        // Quick check outside the lock.
        if (!isNewIndex && _settingsAppliedKey == cacheKey)
        {
            return;
        }

        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!isNewIndex && _settingsAppliedKey == cacheKey)
            {
                return;
            }

            if (await ConfigureIndexSettingsAsync(index, isNewIndex, cancellationToken).ConfigureAwait(false))
            {
                _settingsAppliedKey = cacheKey;
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Configures index settings. These operations are idempotent.
    /// </summary>
    /// <returns><c>false</c> when the embedder could not be brought in line with the configuration,
    /// which leaves the settings uncached so the next index access tries again.</returns>
    private async Task<bool> ConfigureIndexSettingsAsync(global::Meilisearch.Index index, bool isNewIndex, CancellationToken cancellationToken)
    {
        if (isNewIndex)
        {
            _logger.LogInformation("Configuring Meilisearch index settings");
        }
        else
        {
            _logger.LogDebug("Applying Meilisearch index settings");
        }

        await index.UpdateSearchableAttributesAsync(
            BuildSearchableAttributes(Configuration),
            cancellationToken).ConfigureAwait(false);

        // Configure filterable attributes.
        await index.UpdateFilterableAttributesAsync(
            [
                "itemType",
                "mediaType",
                "ancestorIds",
                "productionYear",
                "genres",
                "tags",
                "studios",
                "officialRating",
                "communityRating",
                "criticRating",
                "seriesId",
                "seasonId",
                "albumId",
                "parentId",
                "topParentId",
                "container",
                "productionLocations",
                "people",
                "providerIds.Imdb",
                "providerIds.Tmdb",
                "providerIds.Tvdb"
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure sortable attributes.
        await index.UpdateSortableAttributesAsync(
            [
                "name",
                "sortName",
                "productionYear",
                "premiereDate",
                "communityRating",
                "criticRating",
                "runTimeTicks",
                "indexNumber",
                "parentIndexNumber",
                "typeRank"
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure custom ranking rules.
        await index.UpdateRankingRulesAsync(
            [
                "words",
                "typo",
                "proximity",
                "attribute",
                "exactness",
                "typeRank:desc",
                "sort",
                "productionYear:desc",

                // Final tie-breakers for equally relevant items of the same type and year: prefer
                // the better-rated one, and treat a missing rating as neutral rather than worst.
                "communityRating:desc",
                "criticRating:desc",
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure typo tolerance for fuzzy matching.
        await index.UpdateTypoToleranceAsync(
            new TypoTolerance
            {
                Enabled = true,
                MinWordSizeForTypos = new TypoTolerance.TypoSize
                {
                    OneTypo = 4,
                    TwoTypos = 8
                }
            },
            cancellationToken).ConfigureAwait(false);

        // Configure distinct attribute to deduplicate results by document id.
        await index.UpdateDistinctAttributeAsync("id", cancellationToken).ConfigureAwait(false);

        // Restrict displayed attributes to what a search actually consumes.
        await index.UpdateDisplayedAttributesAsync(["id", "itemType"], cancellationToken).ConfigureAwait(false);

        var embeddersConfigured = await ConfigureEmbeddersAsync(index, cancellationToken).ConfigureAwait(false);

        // Apply synonyms from configuration.
        var lastSettingsTask = await index.UpdateSynonymsAsync(ParseSynonyms(Configuration.Synonyms), cancellationToken).ConfigureAwait(false);
        if (isNewIndex)
        {
            await GetClient()
                .WaitForTaskAsync(lastSettingsTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken)
                .ConfigureAwait(false);
        }

        return embeddersConfigured;
    }

    /// <summary>
    /// Registers or removes the vector field depending on whether semantic search is enabled.
    /// </summary>
    /// <returns><c>false</c> when a change was attempted and failed, so the caller leaves the
    /// settings uncached and the next index access tries again.</returns>
    /// <remarks>
    /// Registered as <c>userProvided</c>: the plugin embeds locally and ships vectors with each
    /// document, so Meilisearch needs no embedding service or network access of its own. Removing the
    /// embedder also drops the stored vectors, which is what reclaims the space.
    /// </remarks>
    private async Task<bool> ConfigureEmbeddersAsync(global::Meilisearch.Index index, CancellationToken cancellationToken)
    {
        Dictionary<string, Embedder> existing;
        try
        {
            existing = await index.GetEmbeddersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Vector support is optional; keyword search must survive its absence.
        catch (Exception ex)
        {
            // Reported as configured on purpose: a server without the setting has nothing to retry,
            // and saying otherwise would reapply every index setting on every single access.
            _logger.LogWarning(
                ex,
                "Could not read the Meilisearch embedder settings. Vector search needs Meilisearch 1.10 or newer; keyword search is unaffected");
            return true;
        }
#pragma warning restore CA1031

        try
        {
            if (Configuration.EnableSemanticSearch)
            {
                await RemoveStaleEmbeddersAsync(index, existing, cancellationToken).ConfigureAwait(false);

                await index.UpdateEmbeddersAsync(
                    new Dictionary<string, Embedder>(StringComparer.Ordinal)
                    {
                        [EmbeddingService.EmbedderName] = new Embedder
                        {
                            Source = EmbedderSource.UserProvided,
                            Dimensions = EmbeddingService.Dimensions,
                            BinaryQuantized = Configuration.BinaryQuantizeVectors
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Registered Meilisearch embedder {EmbedderName} ({Dimensions} dimensions, {Storage})",
                    EmbeddingService.EmbedderName,
                    EmbeddingService.Dimensions,
                    Configuration.BinaryQuantizeVectors ? "binary-quantized" : "full precision");
                return true;
            }

            if (existing is { Count: > 0 })
            {
                _logger.LogInformation("Semantic search is off; removing the Meilisearch embedder and its stored vectors");
                await index.ResetEmbeddersAsync(cancellationToken).ConfigureAwait(false);
            }

            ForgetIndexedEmbeddingModel();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Vector support is optional; keyword search must survive its absence.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not configure the Meilisearch embedder; the next index access tries again. Keyword search is unaffected");
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Records that the index no longer holds vectors, so re-enabling semantic search does not look
    /// like an index that was already built with the selected model.
    /// </summary>
    /// <remarks>
    /// Dropping the embedder drops the vectors with it. Leaving the model recorded would leave the
    /// index claiming vectors it no longer has: no warning on the status page, and every semantic
    /// search silently keyword-only until someone thinks to rebuild.
    /// </remarks>
    private void ForgetIndexedEmbeddingModel()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrEmpty(plugin.Configuration.IndexedEmbeddingModelId))
        {
            return;
        }

        plugin.Configuration.IndexedEmbeddingModelId = string.Empty;
        plugin.SaveConfiguration();
    }

    /// <summary>
    /// Drops embedders left behind by a different embedding model.
    /// </summary>
    /// <remarks>
    /// Switching models otherwise leaves the old registration and its vectors in the index, where a
    /// hybrid search naming the new embedder would silently skip every document that only has old
    /// ones. Dropping them leaves the index consistently vector-less until the rebuild.
    /// </remarks>
    private async Task RemoveStaleEmbeddersAsync(
        global::Meilisearch.Index index,
        Dictionary<string, Embedder> existing,
        CancellationToken cancellationToken)
    {
        if (existing is not { Count: > 0 })
        {
            return;
        }

        var stale = existing.Keys
            .Where(name => !string.Equals(name, EmbeddingService.EmbedderName, StringComparison.Ordinal))
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Removing Meilisearch embedder(s) {Stale} left by a previously selected embedding model; "
            + "run 'Rebuild Meilisearch Index' to embed the library with {EmbedderName}",
            string.Join(", ", stale),
            EmbeddingService.EmbedderName);

        // Reset rather than a targeted removal: this clears the current model's registration too,
        // but the caller re-registers it immediately, and there is nothing else worth preserving.
        await index.ResetEmbeddersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the synonyms configuration string into a dictionary suitable for the Meilisearch API.
    /// Each non-empty line takes the form <c>key=v1,v2,v3</c>; malformed lines are silently skipped.
    /// </summary>
    private static Dictionary<string, IEnumerable<string>> ParseSynonyms(string? raw)
    {
        var result = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var lines = raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equalsIdx = line.IndexOf('=', StringComparison.Ordinal);
            if (equalsIdx <= 0 || equalsIdx >= line.Length - 1)
            {
                continue;
            }

            var key = line[..equalsIdx].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var values = line[(equalsIdx + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static v => v.Length > 0)
                .Select(static v => v.ToLower(CultureInfo.InvariantCulture))
                .ToArray();

            if (values.Length == 0)
            {
                continue;
            }

            result[key.ToLower(CultureInfo.InvariantCulture)] = values;
        }

        return result;
    }

    /// <summary>
    /// Releases the resources used by the <see cref="MeilisearchClientWrapper"/> instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="MeilisearchClientWrapper"/> and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clientLock.Dispose();
            _settingsLock.Dispose();
        }
    }
}
