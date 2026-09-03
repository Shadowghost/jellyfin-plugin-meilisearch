namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// The result of discarding the cached vectors.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Cleared">
/// How many vectors were discarded. Zero when the cache was not open at the time, since the files
/// are then removed without being read.
/// </param>
public readonly record struct ClearCacheResult(ClearCacheOutcome Outcome, int Cleared);
