namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Describes the result of a Meilisearch health and authentication check.
/// </summary>
/// <param name="IsHealthy">Indicates whether the Meilisearch server is reachable.</param>
/// <param name="IsAuthenticated">Indicates whether the configured API key (if any) is accepted.</param>
/// <param name="Error">Optional error message describing the failure.</param>
public sealed record MeilisearchHealth(bool IsHealthy, bool IsAuthenticated, string? Error);
