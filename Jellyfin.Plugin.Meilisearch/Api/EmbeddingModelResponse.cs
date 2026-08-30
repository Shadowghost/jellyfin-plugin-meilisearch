namespace Jellyfin.Plugin.Meilisearch.Api;

/// <summary>
/// One embedding model the plugin can run, as offered to the settings page.
/// </summary>
/// <param name="Id">The identifier stored in the configuration.</param>
/// <param name="DisplayName">The name to show in the model picker.</param>
/// <param name="Dimensions">The width of the vectors the model produces.</param>
/// <param name="ApproximateDownloadMegabytes">Rough download size, so the cost is visible before the model is selected.</param>
/// <param name="Repository">The Hugging Face repository the model is fetched from.</param>
public sealed record EmbeddingModelResponse(
    string Id,
    string DisplayName,
    int Dimensions,
    int ApproximateDownloadMegabytes,
    string Repository);
