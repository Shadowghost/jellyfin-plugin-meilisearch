using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch.Configuration;

/// <summary>
/// Plugin configuration for Meilisearch integration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The matching strategy used when none is configured. <c>frequency</c> drops the term that is
    /// most common across the library first, which for media titles beats <c>last</c>'s "drop the
    /// last word typed" - a search for "the matrix reloaded" keeps "reloaded" and discards "the".
    /// </summary>
    public const string DefaultMatchingStrategy = "frequency";

    /// <summary>
    /// The matching strategy every Meilisearch version supports. Used when the configured value is
    /// unknown, and as the automatic fallback when the server rejects <see cref="DefaultMatchingStrategy"/>.
    /// </summary>
    public const string FallbackMatchingStrategy = "last";

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
        MatchingStrategy = DefaultMatchingStrategy;
        SyncBatchSize = 500;
        SyncBatchDebounceMilliseconds = 2000;
        ReindexBatchSize = 2000;
        ReindexParallelism = 2;
        EnableHealthMonitor = true;
        HealthCheckIntervalSeconds = 60;
        Synonyms = string.Empty;
        LastIncrementalReindexUtc = null;
        IndexSchemaVersion = 0;
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
    /// Gets or sets the Meilisearch matching strategy: <c>frequency</c>, <c>last</c> or <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <c>frequency</c> (the default) discards the most common word in the library when a query has
    /// no exact match; <c>last</c> discards words from the end of the query; <c>all</c> returns only
    /// documents matching every word, trading recall for precision. <c>frequency</c> requires
    /// Meilisearch 1.11 or newer - on an older server the plugin notices the rejection once and
    /// falls back to <see cref="FallbackMatchingStrategy"/> for the rest of the session.
    /// </remarks>
    public string MatchingStrategy { get; set; }

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

    /// <summary>
    /// Gets or sets the <see cref="MeilisearchDocument.SchemaVersion"/> that was current at the last
    /// successful full reindex. Zero means the index predates schema tracking.
    /// </summary>
    public int IndexSchemaVersion { get; set; }
}
