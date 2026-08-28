using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Meilisearch.Configuration;
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

    // Weight given to the newest sample in the rolling search-latency average. Low enough that one
    // slow query does not dominate the figure shown on the config page.
    private const double SearchTimeSmoothingFactor = 0.25;

    private static readonly string[] SupportedMatchingStrategies = ["frequency", "last", "all"];

    private readonly ILogger<MeilisearchClientWrapper> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly object _metricsLock = new();
    private MeilisearchClient? _client;
    private string? _currentUrl;
    private string? _currentApiKey;
    private double _averageSearchMilliseconds;
    private long _searchCount;

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
    /// Gets the rolling average round-trip time of the Meilisearch search requests issued since
    /// startup, in milliseconds, or null when no search has run yet.
    /// </summary>
    /// <remarks>
    /// An exponential moving average weighted by <see cref="SearchTimeSmoothingFactor"/>, not a mean
    /// over all searches: it tracks current behaviour rather than accumulating history. It measures
    /// only the HTTP call to Meilisearch, so it excludes the time Jellyfin spends loading the matched
    /// items and filtering them by user access.
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results with IDs and scores.</returns>
    public async Task<IReadOnlyList<(string Id, double Score)>> SearchAsync(
        string searchTerm,
        int limit,
        string? filter,
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
                        Filter = filter
                    };

                    var minScore = Configuration.MinimumMatchScore;
                    if (minScore is not null && minScore > 0)
                    {
                        searchParams.RankingScoreThreshold = minScore / 100m;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var results = await index.SearchAsync<MeilisearchDocument>(searchTerm, searchParams, ct).ConfigureAwait(false);
                    RecordSearchDuration(stopwatch.Elapsed.TotalMilliseconds);

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
    /// Performs one Meilisearch query per item type in a single HTTP multi-search request and merges the results.
    /// Each per-type query receives its own quota so that strongly-matching documents of one type (e.g. songs
    /// by an artist) cannot drown out weaker but relevant matches of other types (e.g. movies, episodes).
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="types">The item types to query, one Meilisearch query per type.</param>
    /// <param name="totalLimit">Maximum total number of results across all types.</param>
    /// <param name="extraFilter">Optional Meilisearch filter expression applied on top of the per-type filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Merged, de-duplicated list of (id, score) tuples, capped at <paramref name="totalLimit"/>.</returns>
    public async Task<IReadOnlyList<(string Id, double Score)>> MultiTypeSearchAsync(
        string searchTerm,
        IReadOnlyList<BaseItemKind> types,
        int totalLimit,
        string? extraFilter,
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
                    // Ensure the index exists and settings are applied before issuing the multi-search.
                    await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    var client = GetClient();
                    var indexUid = Configuration.IndexName;

                    // Per-type quota: give each type a fair share
                    var perTypeLimit = Math.Max(20, totalLimit / types.Count);
                    perTypeLimit = Math.Min(perTypeLimit, totalLimit);

                    var minScore = Configuration.MinimumMatchScore;
                    decimal? threshold = (minScore is not null && minScore > 0) ? minScore.Value / 100m : null;

                    var queries = new List<SearchQuery>(types.Count);
                    foreach (var type in types)
                    {
                        var typeFilter = $"itemType = \"{type}\"";
                        var combinedFilter = string.IsNullOrEmpty(extraFilter)
                            ? typeFilter
                            : $"{typeFilter} AND {extraFilter}";

                        var sq = new SearchQuery
                        {
                            IndexUid = indexUid,
                            Q = searchTerm,
                            Limit = perTypeLimit,
                            Filter = combinedFilter,
                            ShowRankingScore = true,
                            MatchingStrategy = matchingStrategy
                        };

                        if (threshold.HasValue)
                        {
                            sq.RankingScoreThreshold = threshold;
                        }

                        queries.Add(sq);
                    }

                    var multiQuery = new MultiSearchQuery { Queries = queries };
                    var stopwatch = Stopwatch.StartNew();
                    var result = await client.MultiSearchAsync(multiQuery, ct).ConfigureAwait(false);
                    RecordSearchDuration(stopwatch.Elapsed.TotalMilliseconds);

                    var merged = new List<(string Id, double Score)>(types.Count * perTypeLimit);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var subResult in result.Results)
                    {
                        foreach (var hit in subResult.Hits)
                        {
                            var root = hit.RootElement;
                            if (!root.TryGetProperty("id", out var idElement))
                            {
                                continue;
                            }

                            var id = idElement.GetString();
                            if (string.IsNullOrEmpty(id) || !seen.Add(id))
                            {
                                continue;
                            }

                            double score = 0;
                            if (root.TryGetProperty("_rankingScore", out var scoreElement)
                                && scoreElement.ValueKind == JsonValueKind.Number)
                            {
                                score = scoreElement.GetDouble();
                            }

                            merged.Add((id, score));
                        }
                    }

                    merged.Sort((a, b) =>
                    {
                        var byScore = b.Score.CompareTo(a.Score);
                        return byScore != 0 ? byScore : string.CompareOrdinal(a.Id, b.Id);
                    });

                    if (merged.Count > totalLimit)
                    {
                        merged.RemoveRange(totalLimit, merged.Count - totalLimit);
                    }

                    return merged;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing multi-type Meilisearch for term '{SearchTerm}'", searchTerm);
            return [];
        }
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
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
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
            return await ExecuteWithReconnectRetryAsync<int?>(
                async ct =>
                {
                    var index = await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                    var task = await index.AddDocumentsAsync(docList, cancellationToken: ct).ConfigureAwait(false);
                    _logger.LogDebug("Indexed {Count} documents", docList.Count);

                    return task.TaskUid;
                },
                cancellationToken).ConfigureAwait(false);
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
    /// Deletes and recreates the index.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task ResetIndexAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        await ExecuteWithReconnectRetryAsync(
            async ct =>
            {
                var client = GetClient();
                var indexName = Configuration.IndexName;

                try
                {
                    _logger.LogInformation("Deleting Meilisearch index {IndexName}", indexName);
                    var deleteTask = await client.DeleteIndexAsync(indexName, ct).ConfigureAwait(false);
                    await client.WaitForTaskAsync(deleteTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, ct).ConfigureAwait(false);
                }
                catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
                {
                    _logger.LogDebug("Index {IndexName} does not exist, nothing to delete", indexName);
                }

                // Invalidate caches so the next access re-applies settings.
                InvalidateIndexCache();

                // Recreate the index.
                await GetOrCreateIndexAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Recreated Meilisearch index {IndexName}", indexName);
            },
            cancellationToken).ConfigureAwait(false);
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
    /// Probing the server version up front would need the <c>version</c> action, which a restricted
    /// API key does not have, so support is inferred from the first rejection instead. The fallback
    /// is then remembered for the rest of the connection: exactly one query pays for it.
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
    /// Folds a search round-trip into the rolling average reported by the status endpoint.
    /// </summary>
    /// <param name="elapsedMilliseconds">The round-trip time of the search request.</param>
    private void RecordSearchDuration(double elapsedMilliseconds)
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
    /// Builds a cache key composed of the URL, API key and index name.
    /// </summary>
    private static string BuildCacheKey(PluginConfiguration config)
        => string.Concat(
            config.MeilisearchUrl ?? string.Empty,
            "|",
            config.ApiKey ?? string.Empty,
            "|",
            config.IndexName ?? string.Empty);

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

            await ConfigureIndexSettingsAsync(index, isNewIndex, cancellationToken).ConfigureAwait(false);
            _settingsAppliedKey = cacheKey;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Configures index settings. These operations are idempotent.
    /// </summary>
    private async Task ConfigureIndexSettingsAsync(global::Meilisearch.Index index, bool isNewIndex, CancellationToken cancellationToken)
    {
        if (isNewIndex)
        {
            _logger.LogInformation("Configuring Meilisearch index settings");
        }
        else
        {
            _logger.LogDebug("Applying Meilisearch index settings");
        }

        // Configure searchable attributes (ordered by priority, high to low).
        await index.UpdateSearchableAttributesAsync(
            [
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
                "tagline",
                "overview",

                // Lowest priority on purpose: a file-name match should never outrank a title or a
                // plot match, it only has to make the item findable by its release name.
                "path"
            ],
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

        // Restrict displayed attributes - the search provider only consumes id + _rankingScore.
        await index.UpdateDisplayedAttributesAsync(["id"], cancellationToken).ConfigureAwait(false);

        // Apply synonyms from configuration.
        var lastSettingsTask = await index.UpdateSynonymsAsync(ParseSynonyms(Configuration.Synonyms), cancellationToken).ConfigureAwait(false);
        if (isNewIndex)
        {
            await GetClient()
                .WaitForTaskAsync(lastSettingsTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken)
                .ConfigureAwait(false);
        }
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
