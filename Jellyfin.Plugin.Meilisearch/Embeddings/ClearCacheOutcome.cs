using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// What happened when discarding the cached vectors was requested.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClearCacheOutcome>))]
public enum ClearCacheOutcome
{
    /// <summary>
    /// The cache is now empty. The next rebuild re-embeds the whole library.
    /// </summary>
    Cleared,

    /// <summary>
    /// There was nothing cached to discard.
    /// </summary>
    Empty,

    /// <summary>
    /// A full or incremental reindex is running. It reads and writes the cache as it goes, so the
    /// request was refused.
    /// </summary>
    ReindexRunning,

    /// <summary>
    /// The model is being downloaded or loaded right now, which is when the cache is opened, so the
    /// request was refused.
    /// </summary>
    Busy,

    /// <summary>
    /// The cache files could not be removed; the log says why.
    /// </summary>
    Failed
}
