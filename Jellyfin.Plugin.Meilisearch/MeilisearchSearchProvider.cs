using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Search provider that uses Meilisearch for fast, typo-tolerant search.
/// </summary>
public class MeilisearchSearchProvider : IExternalSearchProvider
{
    private readonly MeilisearchClientWrapper _client;
    private readonly ILogger<MeilisearchSearchProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchSearchProvider"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="logger">The logger.</param>
    public MeilisearchSearchProvider(
        MeilisearchClientWrapper client,
        ILogger<MeilisearchSearchProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Meilisearch";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query) => _client.IsConfigured;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            yield break;
        }

        var totalLimit = query.Limit ?? 100;
        var effectiveTypes = query.IncludeItemTypes;
        var nonTypeFilter = BuildNonTypeFilter(query);

        IReadOnlyList<(string Id, double Score)> results;
        try
        {
            if (effectiveTypes.Length > 1)
            {
                // Per-type quota search: each item type gets its own slice of the result budget so that
                // strongly-matching documents in one type (e.g. songs matching an artist name) cannot
                // crowd out weaker but still-relevant matches in other types (e.g. movies, episodes).
                results = await _client.MultiTypeSearchAsync(
                    query.SearchTerm,
                    effectiveTypes,
                    totalLimit,
                    nonTypeFilter,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? filter = nonTypeFilter;
                if (effectiveTypes.Length == 1)
                {
                    var typeFilter = $"itemType = \"{effectiveTypes[0]}\"";
                    filter = string.IsNullOrEmpty(filter) ? typeFilter : $"{typeFilter} AND {filter}";
                }

                results = await _client.SearchAsync(
                    query.SearchTerm,
                    totalLimit,
                    filter,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Meilisearch for term '{SearchTerm}'", query.SearchTerm);
            yield break;
        }

        foreach (var (id, score) in results)
        {
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty)
            {
                yield return new SearchResult(guid, (float)score);
            }
        }
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await foreach (var result in SearchAsync(query, cancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Builds the non-type portion of the Meilisearch filter: parent scope, media types, and type
    /// exclusions. Item-type filters are applied by the caller - either as a single
    /// <c>itemType = …</c> clause or via per-type sub-queries in a multi-search.
    /// </summary>
    private static string? BuildNonTypeFilter(SearchProviderQuery query)
    {
        var filters = new List<string>();

        // ParentId scopes to the whole subtree, not just direct children, so match against the
        // indexed ancestor chain. Filtering on parentId would drop everything nested more than one
        // level below the requested folder - every episode in a TV library, for instance.
        if (query.ParentId.HasValue && query.ParentId.Value != Guid.Empty)
        {
            filters.Add($"ancestorIds = \"{query.ParentId.Value:N}\"");
        }

        // Media types are an additional constraint alongside the item-type filter, never an
        // alternative to it.
        if (query.MediaTypes.Length > 0)
        {
            var mediaTypeClauses = query.MediaTypes
                .Distinct()
                .Select(mediaType => $"mediaType = \"{mediaType}\"")
                .ToList();

            filters.Add(mediaTypeClauses.Count == 1
                ? mediaTypeClauses[0]
                : $"({string.Join(" OR ", mediaTypeClauses)})");
        }

        // Exclusions only apply when the caller did not request specific item types.
        if (query.IncludeItemTypes.Length == 0 && query.ExcludeItemTypes.Length > 0)
        {
            foreach (var excludeType in query.ExcludeItemTypes.Distinct())
            {
                filters.Add($"itemType != \"{excludeType}\"");
            }
        }

        return filters.Count == 0 ? null : string.Join(" AND ", filters);
    }
}
