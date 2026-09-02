using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// How far through a call to <see cref="EmbeddingService.AttachVectors(IReadOnlyList{MeilisearchDocument}, Action{EmbeddingProgress}, CancellationToken)"/>
/// the embedding work has got.
/// </summary>
/// <param name="Completed">Documents that now have a vector, whether computed or read from the cache.</param>
/// <param name="Total">Documents in this call.</param>
/// <param name="CacheHits">Of <paramref name="Completed"/>, how many came from the on-disk cache.</param>
/// <param name="Computed">Of <paramref name="Completed"/>, how many required a forward pass.</param>
public readonly record struct EmbeddingProgress(int Completed, int Total, int CacheHits, int Computed);
