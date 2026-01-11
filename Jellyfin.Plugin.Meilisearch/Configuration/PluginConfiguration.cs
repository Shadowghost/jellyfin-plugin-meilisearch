using System;
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
        SyncBatchSize = 500;
        SyncBatchDebounceMilliseconds = 2000;
        ReindexBatchSize = 2000;
        ReindexParallelism = 2;
        EnableHealthMonitor = true;
        HealthCheckIntervalSeconds = 60;
        Synonyms = string.Empty;
        LastIncrementalReindexUtc = null;
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

    /// <summary>
    /// Gets or sets the maximum number of items per real-time sync flush.
    /// </summary>
    public int SyncBatchSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum wait time in milliseconds before flushing a partial real-time sync batch.
    /// </summary>
    public int SyncBatchDebounceMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the number of items per reindex push.
    /// </summary>
    public int ReindexBatchSize { get; set; }

    /// <summary>
    /// Gets or sets the number of concurrent batches during reindex.
    /// </summary>
    public int ReindexParallelism { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the background health monitor is enabled.
    /// </summary>
    public bool EnableHealthMonitor { get; set; }

    /// <summary>
    /// Gets or sets the interval in seconds between background health checks.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets the synonyms configuration as newline-separated entries.
    /// Each line takes the form <c>term=alt1,alt2</c>.
    /// </summary>
    public string Synonyms { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last incremental reindex run.
    /// </summary>
    public DateTime? LastIncrementalReindexUtc { get; set; }
}
