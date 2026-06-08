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
        var effectiveTypes = ResolveEffectiveTypes(query);
        var nonTypeFilter = BuildNonTypeFilter(query, hasResolvedTypes: effectiveTypes.Count > 0);

        IReadOnlyList<(string Id, double Score)> results;
        try
        {
            if (effectiveTypes.Count > 1)
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
                if (effectiveTypes.Count == 1)
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
    /// Resolves the effective set of item types for the query by combining <see cref="SearchProviderQuery.IncludeItemTypes"/>
    /// with item types derived from <see cref="SearchProviderQuery.MediaTypes"/>, then subtracting
    /// <see cref="SearchProviderQuery.ExcludeItemTypes"/>. Returns an empty list when the caller did not
    /// constrain item types at all (in which case the search runs across all types).
    /// </summary>
    private static IReadOnlyList<BaseItemKind> ResolveEffectiveTypes(SearchProviderQuery query)
    {
        var types = new List<BaseItemKind>();

        foreach (var kind in query.IncludeItemTypes)
        {
            if (!types.Contains(kind))
            {
                types.Add(kind);
            }
        }

        foreach (var mediaType in query.MediaTypes)
        {
            foreach (var kind in MapMediaTypeToItemTypes(mediaType))
            {
                if (!types.Contains(kind))
                {
                    types.Add(kind);
                }
            }
        }

        if (types.Count > 0 && query.ExcludeItemTypes.Length > 0)
        {
            types.RemoveAll(t => query.ExcludeItemTypes.Contains(t));
        }

        return types;
    }

    /// <summary>
    /// Builds the non-type portion of the Meilisearch filter (parent scope, plus type exclusions when no
    /// item types were resolved). Type-scoped filters are applied by the caller - either as a single
    /// <c>itemType = …</c> clause or via per-type sub-queries in a multi-search.
    /// </summary>
    private static string? BuildNonTypeFilter(SearchProviderQuery query, bool hasResolvedTypes)
    {
        var filters = new List<string>();

        if (query.ParentId.HasValue && query.ParentId.Value != Guid.Empty)
        {
            filters.Add($"parentId = \"{query.ParentId.Value:N}\"");
        }

        // Exclusions are normally folded into the resolved type list. When no include/media types
        // were supplied we couldn't subtract them, so emit explicit `!=` clauses to honor the request.
        if (!hasResolvedTypes && query.ExcludeItemTypes.Length > 0)
        {
            foreach (var excludeType in query.ExcludeItemTypes)
            {
                filters.Add($"itemType != \"{excludeType}\"");
            }
        }

        return filters.Count == 0 ? null : string.Join(" AND ", filters);
    }

    /// <summary>
    /// Maps a media type to corresponding item types.
    /// </summary>
    private static BaseItemKind[] MapMediaTypeToItemTypes(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Video => [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo],
            MediaType.Audio => [BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist],
            MediaType.Photo => [BaseItemKind.Photo],
            MediaType.Book => [BaseItemKind.Book, BaseItemKind.AudioBook],
            _ => []
        };
    }
}
