using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Turns text into normalized vectors.
/// </summary>
public interface ITextEmbedder : IDisposable
{
    /// <summary>
    /// Gets the execution provider this embedder ended up running on, which is not necessarily the
    /// one that was configured - a provider the loaded runtime lacks, or that will not initialize,
    /// degrades to <see cref="EmbeddingExecutionProvider.Cpu"/>.
    /// </summary>
    EmbeddingExecutionProvider ExecutionProvider { get; }

    /// <summary>
    /// Embeds a batch of texts.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="maxTokens">Maximum tokens to keep per text.</param>
    /// <param name="priority">Who waits for whom.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One vector per input, in the same order. An entry is null when the corresponding text could
    /// not be embedded - it was empty, or the model was released mid-call - which the caller is
    /// expected to treat as "no vector for this item" rather than as a failure.
    /// </returns>
    IReadOnlyList<float[]?> Embed(
        IReadOnlyList<string> texts,
        int maxTokens,
        EmbeddingPriority priority,
        CancellationToken cancellationToken);
}
