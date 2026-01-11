using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    private readonly ILogger<MeilisearchClientWrapper> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private MeilisearchClient? _client;
    private string? _currentUrl;
    private string? _currentApiKey;
    private global::Meilisearch.Index? _cachedIndex;
    private string? _cachedIndexKey;
    private string? _settingsAppliedKey;

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
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            var searchParams = new SearchQuery
            {
                Limit = limit,
                ShowRankingScore = true,
                MatchingStrategy = "last",
                Filter = filter
            };

            var minScore = Configuration.MinimumMatchScore;
            if (minScore is not null && minScore > 0)
            {
                searchParams.RankingScoreThreshold = minScore / 100m;
            }

            var results = await index.SearchAsync<MeilisearchDocument>(searchTerm, searchParams, cancellationToken).ConfigureAwait(false);

            return results.Hits
                .Select(hit => (hit.Id, hit.RankingScore ?? 0.0))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Meilisearch for term '{SearchTerm}'", searchTerm);
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
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            var task = await index.AddDocumentsAsync([document], cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Indexed document {Id} ({Name})", document.Id, document.Name);

            return task.TaskUid;
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
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            var docList = documents.ToList();
            var task = await index.AddDocumentsAsync(docList, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Indexed {Count} documents", docList.Count);

            return task.TaskUid;
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
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            await index.DeleteOneDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Removed document {Id}", documentId);
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
    /// <returns>A task representing the operation.</returns>
    public async Task RemoveDocumentsAsync(IEnumerable<string> documentIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            var idList = documentIds.ToList();
            await index.DeleteDocumentsAsync(idList, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Removed {Count} documents", idList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing documents batch");
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

        var client = GetClient();
        var indexName = Configuration.IndexName;

        try
        {
            _logger.LogInformation("Deleting Meilisearch index {IndexName}", indexName);
            var deleteTask = await client.DeleteIndexAsync(indexName, cancellationToken).ConfigureAwait(false);
            await client.WaitForTaskAsync(deleteTask.TaskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            _logger.LogDebug("Index {IndexName} does not exist, nothing to delete", indexName);
        }

        // Invalidate caches so the next access re-applies settings.
        InvalidateIndexCache();

        // Recreate the index.
        await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Recreated Meilisearch index {IndexName}", indexName);
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

        MeilisearchClient client;
        try
        {
            client = GetClient();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Meilisearch client during health check");
            return new MeilisearchHealth(false, false, ex.Message);
        }

        try
        {
            await client.HealthAsync(cancellationToken).ConfigureAwait(false);
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
            await client.GetStatsAsync(cancellationToken).ConfigureAwait(false);
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
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            return await index.GetStatsAsync(cancellationToken).ConfigureAwait(false);
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
            var client = GetClient();
            var resource = await client.WaitForTaskAsync(taskUid, TaskWaitTimeoutMs, TaskWaitIntervalMs, cancellationToken).ConfigureAwait(false);
            return resource.Status == TaskInfoStatus.Succeeded;
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

                // Configuration changed; invalidate cached index/settings.
                _cachedIndex = null;
                _cachedIndexKey = null;
                _settingsAppliedKey = null;

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
    /// Builds a cache key composed of the URL, API key and index name.
    /// </summary>
    private static string BuildCacheKey(PluginConfiguration config)
        => string.Concat(config.MeilisearchUrl ?? string.Empty, "|", config.ApiKey ?? string.Empty, "|", config.IndexName ?? string.Empty);

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
                "overview"
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure filterable attributes.
        await index.UpdateFilterableAttributesAsync(
            [
                "itemType",
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
                "exactness",
                "typeRank:desc",
                "attribute",
                "sort",
                "productionYear:desc",
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

        // Restrict displayed attributes — the search provider only consumes id + _rankingScore.
        await index.UpdateDisplayedAttributesAsync(["id"], cancellationToken).ConfigureAwait(false);

        // Apply synonyms from configuration.
        await index.UpdateSynonymsAsync(ParseSynonyms(Configuration.Synonyms), cancellationToken).ConfigureAwait(false);
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
