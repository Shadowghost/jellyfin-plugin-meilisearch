namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Why a vector is being asked for, which decides who waits for whom when the model can only run
/// one forward pass at a time.
/// </summary>
public enum EmbeddingPriority
{
    /// <summary>
    /// Indexing, which yields the model to any waiting <see cref="Interactive"/> request.
    /// </summary>
    Batch = 0,

    /// <summary>
    /// A search someone is waiting on, admitted ahead of indexing.
    /// </summary>
    Interactive = 1
}
