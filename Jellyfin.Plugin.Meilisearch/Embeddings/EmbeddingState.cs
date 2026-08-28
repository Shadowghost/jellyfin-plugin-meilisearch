namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// The lifecycle state of the local embedding model.
/// </summary>
public enum EmbeddingState
{
    /// <summary>
    /// Semantic search is switched off. Nothing is loaded and no model files are needed.
    /// </summary>
    Disabled,

    /// <summary>
    /// Enabled, but the model files are not on disk yet.
    /// </summary>
    NotDownloaded,

    /// <summary>
    /// The model is being downloaded or loaded.
    /// </summary>
    Initializing,

    /// <summary>
    /// The model is loaded and vectors can be produced.
    /// </summary>
    Ready,

    /// <summary>
    /// Initialization failed. Keyword search continues to work unaffected.
    /// </summary>
    Failed
}
