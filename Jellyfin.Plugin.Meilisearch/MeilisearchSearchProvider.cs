using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MeilisearchSearchProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchSearchProvider"/> class.
    /// </summary>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public MeilisearchSearchProvider(
        MeilisearchClientWrapper client,
        ILibraryManager libraryManager,
        ILogger<MeilisearchSearchProvider> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
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

        IReadOnlyList<(string Id, double Score)> results;
        try
        {
            var filter = BuildFilter(query);
            results = await _client.SearchAsync(
                query.SearchTerm,
                query.Limit ?? 100,
                filter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Meilisearch for term '{SearchTerm}'", query.SearchTerm);
            yield break;
        }

        if (results.Count == 0)
        {
            yield break;
        }

        // Pre-fetch items if requested
        Dictionary<Guid, BaseItem>? itemsById = null;
        if (query.IncludeItemData)
        {
            var itemIds = results
                .Select(r => Guid.TryParse(r.Id, out var guid) ? guid : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = itemIds,
                DtoOptions = new DtoOptions(false)
            });
            itemsById = items.ToDictionary(i => i.Id);
        }

        foreach (var (id, score) in results)
        {
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty)
            {
                BaseItem? item = null;
                itemsById?.TryGetValue(guid, out item);
                yield return new SearchResult(guid, (float)score, item);
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
    /// Builds a Meilisearch filter expression from the query parameters.
    /// </summary>
    private static string? BuildFilter(SearchProviderQuery query)
    {
        var filters = new List<string>();

        if (query.IncludeItemTypes.Length > 0)
        {
            var typeFilters = new StringBuilder();
            typeFilters.Append('(');
            for (var i = 0; i < query.IncludeItemTypes.Length; i++)
            {
                if (i > 0)
                {
                    typeFilters.Append(" OR ");
                }

                typeFilters.Append("itemType = \"");
                typeFilters.Append(query.IncludeItemTypes[i].ToString());
                typeFilters.Append('"');
            }

            typeFilters.Append(')');
            filters.Add(typeFilters.ToString());
        }

        if (query.ExcludeItemTypes.Length > 0)
        {
            foreach (var excludeType in query.ExcludeItemTypes)
            {
                filters.Add($"itemType != \"{excludeType}\"");
            }
        }

        if (query.ParentId.HasValue && query.ParentId.Value != Guid.Empty)
        {
            filters.Add($"parentId = \"{query.ParentId.Value:N}\"");
        }

        if (query.MediaTypes.Length > 0)
        {
            var mediaTypeFilters = new StringBuilder();
            mediaTypeFilters.Append('(');
            var first = true;
            foreach (var mediaType in query.MediaTypes)
            {
                var itemTypes = MapMediaTypeToItemTypes(mediaType);
                foreach (var itemType in itemTypes)
                {
                    if (!first)
                    {
                        mediaTypeFilters.Append(" OR ");
                    }

                    first = false;
                    mediaTypeFilters.Append("itemType = \"");
                    mediaTypeFilters.Append(itemType.ToString());
                    mediaTypeFilters.Append('"');
                }
            }

            mediaTypeFilters.Append(')');
            if (!first)
            {
                filters.Add(mediaTypeFilters.ToString());
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
