using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// A user-provided embedding as Meilisearch expects it in a document's <c>_vectors</c> map.
/// </summary>
public class MeilisearchVector
{
    /// <summary>
    /// Gets or sets the embedding.
    /// </summary>
    [JsonPropertyName("embeddings")]
    public IReadOnlyList<float> Embeddings { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether Meilisearch should regenerate this vector itself.
    /// Always false: the embedder is registered as <c>userProvided</c>, so Meilisearch has no model
    /// of its own to regenerate with.
    /// </summary>
    [JsonPropertyName("regenerate")]
    public bool Regenerate { get; set; }
}
