using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch.Configuration;

/// <summary>
/// Plugin configuration for Meilisearch integration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MeilisearchUrl = "http://localhost:7700";
        ApiKey = string.Empty;
        IndexName = "jellyfin";
        EnableRealTimeSync = true;
        MinimumMatchScore = 50;
    }

    /// <summary>
    /// Gets or sets the Meilisearch server URL.
    /// </summary>
    public string MeilisearchUrl { get; set; }

    /// <summary>
    /// Gets or sets the Meilisearch API key.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the name of the Meilisearch index.
    /// </summary>
    public string IndexName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether real-time sync is enabled.
    /// </summary>
    public bool EnableRealTimeSync { get; set; }

    /// <summary>
    /// Gets or sets the minimum match score threshold (0-100).
    /// Results with a score below this threshold will be filtered out.
    /// </summary>
    public int? MinimumMatchScore { get; set; }
}
