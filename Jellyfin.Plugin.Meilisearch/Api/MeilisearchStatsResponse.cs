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
public sealed record MeilisearchStatsResponse(
    long? DocumentCount,
    bool? IsIndexing,
    long? DatabaseSize,
    Dictionary<string, int>? FieldDistribution,
    bool IsHealthy,
    bool IsAuthenticated,
    DateTime? LastIncrementalReindexUtc,
    string? Error);
