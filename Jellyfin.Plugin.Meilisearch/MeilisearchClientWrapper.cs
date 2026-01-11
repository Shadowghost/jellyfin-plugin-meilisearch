using System;
using System.Collections.Generic;
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
public class MeilisearchClientWrapper
{
    private readonly ILogger<MeilisearchClientWrapper> _logger;
    private MeilisearchClient? _client;
    private string? _currentUrl;
    private string? _currentApiKey;

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
    /// Gets the Meilisearch client, creating or recreating it if configuration changed.
    /// </summary>
    /// <returns>The Meilisearch client.</returns>
    private MeilisearchClient GetClient()
    {
        var config = Configuration;

        // Recreate client if configuration changed
        if (_client is null || _currentUrl != config.MeilisearchUrl || _currentApiKey != config.ApiKey)
        {
            _currentUrl = config.MeilisearchUrl;
            _currentApiKey = config.ApiKey;
            _client = string.IsNullOrWhiteSpace(config.ApiKey)
                ? new MeilisearchClient(config.MeilisearchUrl)
                : new MeilisearchClient(config.MeilisearchUrl, config.ApiKey);

            _logger.LogInformation("Created Meilisearch client for {Url}", config.MeilisearchUrl);
        }

        return _client;
    }

    /// <summary>
    /// Gets the index, creating it if it doesn't exist and ensuring settings are up to date.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Meilisearch index.</returns>
    private async Task<global::Meilisearch.Index> GetOrCreateIndexAsync(CancellationToken cancellationToken)
    {
        var client = GetClient();
        var indexName = Configuration.IndexName;
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
            await client.WaitForTaskAsync(task.TaskUid, cancellationToken: cancellationToken).ConfigureAwait(false);
            index = await client.GetIndexAsync(indexName, cancellationToken).ConfigureAwait(false);
            isNewIndex = true;
        }

        // Always ensure settings are up to date
        await ConfigureIndexSettingsAsync(index, isNewIndex, cancellationToken).ConfigureAwait(false);

        return index;
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

        // Configure searchable attributes (ordered by priority)
        await index.UpdateSearchableAttributesAsync(
            [
                "name",
                "originalTitle",
                "sortName",
                "seriesName",
                "seasonName",
                "albumName",
                "id",
                "providerIds.Imdb",
                "providerIds.Tmdb",
                "providerIds.Tvdb",
                "genres",
                "tags",
                "productionYear",
                "artists",
                "albumArtists",
                "people",
                "studios",
                "productionLocations",
                "overview",
                "tagline"
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure filterable attributes
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
                "container",
                "productionYear",
                "productionLocations",
                "providerIds.Imdb",
                "providerIds.Tmdb",
                "providerIds.Tvdb"
            ],
            cancellationToken).ConfigureAwait(false);

        // Configure sortable attributes
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

        // Configure custom ranking rules
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

        // Configure typo tolerance for fuzzy matching
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

        // Configure distinct attribute to deduplicate results by document id
        await index.UpdateDistinctAttributeAsync("id", cancellationToken).ConfigureAwait(false);
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
    /// <returns>A task representing the operation.</returns>
    public async Task IndexDocumentAsync(MeilisearchDocument document, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            await index.AddDocumentsAsync([document], cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Indexed document {Id} ({Name})", document.Id, document.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing document {Id}", document.Id);
        }
    }

    /// <summary>
    /// Indexes multiple documents in a batch.
    /// </summary>
    /// <param name="documents">The documents to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task IndexDocumentsAsync(IEnumerable<MeilisearchDocument> documents, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var index = await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
            var docList = documents.ToList();
            await index.AddDocumentsAsync(docList, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Indexed {Count} documents", docList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing documents batch");
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
            await client.WaitForTaskAsync(deleteTask.TaskUid, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            _logger.LogDebug("Index {IndexName} does not exist, nothing to delete", indexName);
        }

        // Recreate the index
        await GetOrCreateIndexAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Recreated Meilisearch index {IndexName}", indexName);
    }

    /// <summary>
    /// Tests the connection to Meilisearch.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if connection is successful.</returns>
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var client = GetClient();
            await client.HealthAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch connection test failed");
            return false;
        }
    }
}
