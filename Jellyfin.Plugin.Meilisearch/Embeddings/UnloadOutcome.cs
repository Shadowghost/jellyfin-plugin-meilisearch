using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// What happened when releasing the embedding model from memory was requested.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnloadOutcome>))]
public enum UnloadOutcome
{
    /// <summary>
    /// The model was released. Semantic search falls back to keyword-only until something loads it
    /// again - a reindex, or saving the plugin configuration.
    /// </summary>
    Unloaded,

    /// <summary>
    /// Nothing was loaded, so there was nothing to release.
    /// </summary>
    NotLoaded,

    /// <summary>
    /// A full or incremental reindex is running. Releasing the model underneath it would leave every
    /// item indexed from that point on without a vector, so the request was refused.
    /// </summary>
    ReindexRunning,

    /// <summary>
    /// The model is being downloaded or loaded right now. Releasing it mid-load would race that
    /// work, so the request was refused.
    /// </summary>
    Busy
}
