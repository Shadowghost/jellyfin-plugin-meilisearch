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
    /// Enabled, but this host cannot run a local model: ONNX Runtime has no native library for it.
    /// Nothing is downloaded and keyword search continues unaffected.
    /// </summary>
    Unsupported,

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
    /// Enabled and downloaded, but released from memory on request. Searches run keyword-only until
    /// something loads it again - a reindex, or saving the plugin configuration.
    /// </summary>
    Unloaded,

    /// <summary>
    /// Initialization failed. Keyword search continues to work unaffected.
    /// </summary>
    Failed
}
