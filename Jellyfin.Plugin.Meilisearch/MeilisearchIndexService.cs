using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Meilisearch.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Hosted service that keeps the Meilisearch index synchronized with library changes.
/// </summary>
public class MeilisearchIndexService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly MeilisearchClientWrapper _client;
    private readonly ILogger<MeilisearchIndexService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchIndexService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="logger">The logger.</param>
    public MeilisearchIndexService(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        ILogger<MeilisearchIndexService> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _logger.LogInformation("Meilisearch index service started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        _logger.LogInformation("Meilisearch index service stopped");
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!Configuration.EnableRealTimeSync)
        {
            return;
        }

        _ = IndexItemAsync(e.Item);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (!Configuration.EnableRealTimeSync)
        {
            return;
        }

        _ = IndexItemAsync(e.Item);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (!Configuration.EnableRealTimeSync)
        {
            return;
        }

        _ = RemoveItemAsync(e.Item);
    }

    private async Task IndexItemAsync(BaseItem item)
    {
        if (!ShouldIndexItem(item))
        {
            return;
        }

        try
        {
            var document = CreateDocument(item);
            await _client.IndexDocumentAsync(document, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing item {ItemId} ({ItemName})", item.Id, item.Name);
        }
    }

    private async Task RemoveItemAsync(BaseItem item)
    {
        try
        {
            await _client.RemoveDocumentAsync(item.Id.ToString("N"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing item {ItemId} from index", item.Id);
        }
    }

    /// <summary>
    /// Determines whether an item should be indexed.
    /// Only index item types that would be returned by the standard SQL search.
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item should be indexed.</returns>
    internal static bool ShouldIndexItem(BaseItem item)
    {
        // Skip virtual items
        if (item.IsVirtualItem)
        {
            return false;
        }

        // Skip folders that aren't meaningful content
        if (item is Folder && item is not MusicAlbum && item is not Series)
        {
            return false;
        }

        // Only index specific item types that are meaningful search results
        // Season is excluded as users typically search for Series, not individual seasons
        return item.GetBaseItemKind() switch
        {
            BaseItemKind.Movie => true,
            BaseItemKind.Episode => true,
            BaseItemKind.Series => true,
            BaseItemKind.Audio => true,
            BaseItemKind.MusicAlbum => true,
            BaseItemKind.MusicArtist => true,
            BaseItemKind.MusicVideo => true,
            BaseItemKind.Book => true,
            BaseItemKind.AudioBook => true,
            BaseItemKind.BoxSet => true,
            BaseItemKind.Person => true,
            BaseItemKind.Trailer => true,
            BaseItemKind.LiveTvChannel => true,
            BaseItemKind.LiveTvProgram => true,
            BaseItemKind.Playlist => true,
            BaseItemKind.PlaylistsFolder => false,
            BaseItemKind.Genre => true,
            BaseItemKind.MusicGenre => true,
            BaseItemKind.Studio => true,
            BaseItemKind.Video => item.ExtraType.HasValue,
            _ => false
        };
    }

    /// <summary>
    /// Gets the type rank for custom ranking (higher = more important).
    /// </summary>
    /// <param name="itemKind">The item kind.</param>
    /// <returns>The rank value for the item type.</returns>
    internal static int GetTypeRank(BaseItemKind itemKind)
    {
        return itemKind switch
        {
            BaseItemKind.Movie => 100,
            BaseItemKind.Series => 100,
            BaseItemKind.MusicArtist => 100,
            BaseItemKind.MusicAlbum => 100,
            BaseItemKind.PhotoAlbum => 100,

            BaseItemKind.Episode => 90,
            BaseItemKind.BoxSet => 90,
            BaseItemKind.Playlist => 90,

            BaseItemKind.Book => 60,
            BaseItemKind.AudioBook => 60,
            BaseItemKind.MusicVideo => 60,

            BaseItemKind.Genre => 50,
            BaseItemKind.MusicGenre => 50,
            BaseItemKind.LiveTvChannel => 50,
            BaseItemKind.LiveTvProgram => 50,

            BaseItemKind.Studio => 30,
            BaseItemKind.Person => 30,

            BaseItemKind.Trailer => 20,

            BaseItemKind.Audio => 10,
            BaseItemKind.Video => 10,
            _ => 0
        };
    }

    /// <summary>
    /// Gets the type rank for custom ranking (higher = more important).
    /// </summary>
    /// <param name="extraType">The extra type.</param>
    /// <returns>The rank value for the item type.</returns>
    internal static int GetTypeRank(ExtraType extraType)
    {
        return extraType switch
        {
            ExtraType.BehindTheScenes => 25,
            ExtraType.DeletedScene => 25,
            ExtraType.Interview => 22,
            ExtraType.Featurette => 21,
            ExtraType.Short => 21,
            ExtraType.Trailer => 20,
            _ => 15
        };
    }

    /// <summary>
    /// Creates a Meilisearch document from a library item.
    /// </summary>
    /// <param name="item">The item to create a document for.</param>
    /// <returns>The Meilisearch document.</returns>
    internal static MeilisearchDocument CreateDocument(BaseItem item)
    {
        var itemKind = item.GetBaseItemKind();

        // Extras need to be handled manually
        var typeRank = GetTypeRank(itemKind);
        if (itemKind == BaseItemKind.Video && item.ExtraType.HasValue)
        {
            typeRank = GetTypeRank(item.ExtraType.Value);
        }

        var document = new MeilisearchDocument
        {
            // Basic identification
            Id = item.Id.ToString("N"),
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            SortName = item.SortName,
            ItemType = itemKind.ToString(),
            TypeRank = typeRank,

            // Descriptions
            Overview = item.Overview,
            Tagline = item.Tagline,

            // Dates and duration
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate?.ToUniversalTime().Ticks,
            RunTimeTicks = item.RunTimeTicks,

            // Ratings
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,

            // Categories
            Genres = item.Genres,
            Tags = item.Tags,
            Studios = item.Studios,
            ProductionLocations = item.ProductionLocations,

            // Hierarchy
            ParentId = item.ParentId != Guid.Empty ? item.ParentId.ToString("N") : null,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,

            // Technical
            Container = item.Container,

            // External IDs
            ProviderIds = item.ProviderIds?.Count > 0 ? item.ProviderIds : null
        };

        // Add episode-specific info
        if (item is Episode episode)
        {
            document.SeriesName = episode.SeriesName;
            document.SeriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId.ToString("N") : null;
            document.SeasonName = episode.SeasonName;
            document.SeasonId = episode.SeasonId != Guid.Empty ? episode.SeasonId.ToString("N") : null;
        }

        // Add season-specific info
        if (item is Season season)
        {
            document.SeriesName = season.SeriesName;
            document.SeriesId = season.SeriesId != Guid.Empty ? season.SeriesId.ToString("N") : null;
        }

        // Add audio-specific info
        if (item is Audio audio)
        {
            document.AlbumName = audio.Album;
            document.AlbumId = audio.AlbumEntity?.Id.ToString("N");
            document.Artists = audio.Artists?.Count > 0 ? audio.Artists : null;
            document.AlbumArtists = audio.AlbumArtists?.Count > 0 ? audio.AlbumArtists : null;
        }

        // Add music album info
        if (item is MusicAlbum album)
        {
            document.AlbumArtists = !string.IsNullOrEmpty(album.AlbumArtist)
                ? new[] { album.AlbumArtist }
                : null;
            document.Artists = album.Artists?.Count > 0 ? album.Artists : null;
        }

        return document;
    }
}
