using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Meilisearch.Api;

/// <summary>
/// Status response payload for <see cref="MeilisearchController.GetStats"/>.
/// </summary>
/// <param name="DocumentCount">Number of documents currently in the index.</param>
/// <param name="IsIndexing">Whether the index is currently processing tasks.</param>
/// <param name="DatabaseSize">Raw database size of the index in bytes.</param>
/// <param name="FieldDistribution">Per-field document counts reported by Meilisearch.</param>
/// <param name="IsHealthy">Whether the Meilisearch server is reachable.</param>
/// <param name="IsAuthenticated">Whether the configured API key is accepted by Meilisearch.</param>
/// <param name="LastIncrementalReindexUtc">Timestamp of the last incremental reindex run, if any.</param>
/// <param name="Error">Optional error message when the connection or auth check failed.</param>
/// <param name="SemanticSearchEnabled">Whether semantic search is switched on in the configuration.</param>
/// <param name="EmbeddingState">The embedding model's lifecycle state.</param>
/// <param name="EmbeddingModel">Display name of the selected embedding model.</param>
/// <param name="EmbeddingModelDirectory">Where the embedding model is stored on disk.</param>
/// <param name="EmbeddingModelRebuildRequired">Whether the index holds vectors from a different embedding model than the one now selected, so a rebuild is needed.</param>
/// <param name="EmbeddingError">Optional error message from the last embedding initialization attempt.</param>
/// <param name="EmbeddingCacheCount">Number of vectors held in the on-disk embedding cache, or null when it is not open.</param>
/// <param name="EmbeddingCacheHitRate">Share of embedding lookups served from that cache since it was opened, 0.0-1.0, or null before the first lookup.</param>
/// <param name="EmbeddingExecutionProvider">The execution provider inference is running on, or null when no model is loaded. What was negotiated with ONNX Runtime, not what was configured.</param>
/// <param name="EmbeddingAvailableProviders">Every execution provider the loaded ONNX Runtime offers, so the settings page can say why a GPU choice did not take effect.</param>
/// <param name="MatchingStrategy">The Meilisearch matching strategy queries are currently sent with.</param>
/// <param name="AverageSearchTimeMilliseconds">Rolling average round-trip time of search requests, or null before the first search.</param>
/// <param name="SearchCount">Number of search requests issued since startup.</param>
public sealed record MeilisearchStatsResponse(
    long? DocumentCount,
    bool? IsIndexing,
    long? DatabaseSize,
    Dictionary<string, int>? FieldDistribution,
    bool IsHealthy,
    bool IsAuthenticated,
    DateTime? LastIncrementalReindexUtc,
    string? Error,
    bool SemanticSearchEnabled,
    string EmbeddingState,
    string? EmbeddingModel,
    string? EmbeddingModelDirectory,
    bool EmbeddingModelRebuildRequired,
    string? EmbeddingError,
    int? EmbeddingCacheCount,
    double? EmbeddingCacheHitRate,
    string? EmbeddingExecutionProvider,
    IReadOnlyCollection<string>? EmbeddingAvailableProviders,
    string MatchingStrategy,
    double? AverageSearchTimeMilliseconds,
    long SearchCount);
