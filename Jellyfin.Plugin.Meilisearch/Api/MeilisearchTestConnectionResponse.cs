namespace Jellyfin.Plugin.Meilisearch.Api;

/// <summary>
/// Response payload for <see cref="MeilisearchController.TestConnection"/>.
/// </summary>
/// <param name="IsHealthy">Whether the Meilisearch server is reachable.</param>
/// <param name="IsAuthenticated">Whether the configured API key is accepted by Meilisearch.</param>
/// <param name="Error">Optional error message when the check failed.</param>
public sealed record MeilisearchTestConnectionResponse(bool IsHealthy, bool IsAuthenticated, string? Error);
