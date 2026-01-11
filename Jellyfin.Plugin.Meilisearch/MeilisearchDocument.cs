using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Document model for Meilisearch indexing.
/// </summary>
public class MeilisearchDocument
{
    /// <summary>
    /// Gets or sets the item ID (GUID as string).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original title (for foreign language content).
    /// </summary>
    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the sort name.
    /// </summary>
    [JsonPropertyName("sortName")]
    public string? SortName { get; set; }

    /// <summary>
    /// Gets or sets the item overview/description.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the tagline.
    /// </summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>
    /// Gets or sets the item type.
    /// </summary>
    [JsonPropertyName("itemType")]
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type rank for custom ranking.
    /// Higher values = higher priority (Movies, Series, Artists, Albums rank highest).
    /// </summary>
    [JsonPropertyName("typeRank")]
    public int TypeRank { get; set; }

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the premiere date as Unix timestamp.
    /// </summary>
    [JsonPropertyName("premiereDate")]
    public long? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the runtime in ticks.
    /// </summary>
    [JsonPropertyName("runTimeTicks")]
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the official content rating (G, PG, R, etc.).
    /// </summary>
    [JsonPropertyName("officialRating")]
    public string? OfficialRating { get; set; }

    /// <summary>
    /// Gets or sets the community rating.
    /// </summary>
    [JsonPropertyName("communityRating")]
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the critic rating.
    /// </summary>
    [JsonPropertyName("criticRating")]
    public float? CriticRating { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    [JsonPropertyName("genres")]
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>
    /// Gets or sets the tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>
    /// Gets or sets the studios.
    /// </summary>
    [JsonPropertyName("studios")]
    public IReadOnlyList<string>? Studios { get; set; }

    /// <summary>
    /// Gets or sets the production locations.
    /// </summary>
    [JsonPropertyName("productionLocations")]
    public IReadOnlyList<string>? ProductionLocations { get; set; }

    /// <summary>
    /// Gets or sets the series name (for episodes).
    /// </summary>
    [JsonPropertyName("seriesName")]
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the series ID (for episodes).
    /// </summary>
    [JsonPropertyName("seriesId")]
    public string? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season name (for episodes).
    /// </summary>
    [JsonPropertyName("seasonName")]
    public string? SeasonName { get; set; }

    /// <summary>
    /// Gets or sets the season ID (for episodes).
    /// </summary>
    [JsonPropertyName("seasonId")]
    public string? SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the index number (episode number, track number).
    /// </summary>
    [JsonPropertyName("indexNumber")]
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the parent index number (season number, disc number).
    /// </summary>
    [JsonPropertyName("parentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the album name (for audio).
    /// </summary>
    [JsonPropertyName("albumName")]
    public string? AlbumName { get; set; }

    /// <summary>
    /// Gets or sets the album ID (for audio).
    /// </summary>
    [JsonPropertyName("albumId")]
    public string? AlbumId { get; set; }

    /// <summary>
    /// Gets or sets the artist names (for audio).
    /// </summary>
    [JsonPropertyName("artists")]
    public IReadOnlyList<string>? Artists { get; set; }

    /// <summary>
    /// Gets or sets the album artist names (for audio).
    /// </summary>
    [JsonPropertyName("albumArtists")]
    public IReadOnlyList<string>? AlbumArtists { get; set; }

    /// <summary>
    /// Gets or sets the parent ID.
    /// </summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    /// <summary>
    /// Gets or sets the container format.
    /// </summary>
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the provider IDs (IMDB, TVDB, TMDB, etc.).
    /// </summary>
    [JsonPropertyName("providerIds")]
    public IReadOnlyDictionary<string, string>? ProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the ranking score returned by Meilisearch search.
    /// This property is populated only during search operations.
    /// </summary>
    [JsonPropertyName("_rankingScore")]
    public double? RankingScore { get; set; }
}
