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
        EnableSemanticSearch = false;
        AutoDownloadEmbeddingModel = true;
        EmbeddingModelPath = string.Empty;
        SemanticRatio = 50;
        EmbeddingMaxTokens = 256;
        EmbeddingBatchSize = 8;
        EmbeddingThreads = 0;
        EnableEmbeddingCache = true;
        EmbeddingCacheMaxEntries = 250000;
        BinaryQuantizeVectors = true;
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

    /// <summary>
    /// Gets or sets a value indicating whether semantic (vector) search is enabled.
    /// </summary>
    /// <remarks>
    /// Disabled by default. Turning this on makes the plugin download a local embedding model of
    /// several hundred megabytes, run it on the CPU for every indexed item, and store a 1024-float
    /// vector per document in Meilisearch. Nothing about embeddings is loaded or executed while this
    /// is off, so leaving it off keeps the plugin's footprint exactly as it was before.
    /// </remarks>
    public bool EnableSemanticSearch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the embedding model may be downloaded automatically
    /// once semantic search is enabled. When false, the model must be fetched with the
    /// "Download Meilisearch Embedding Model" scheduled task before semantic search becomes active.
    /// </summary>
    public bool AutoDownloadEmbeddingModel { get; set; }

    /// <summary>
    /// Gets or sets the directory the embedding model is stored in. Empty means a
    /// <c>meilisearch-embeddings</c> directory under Jellyfin's data path.
    /// </summary>
    public string EmbeddingModelPath { get; set; }

    /// <summary>
    /// Gets or sets the balance between keyword and vector matching, 0-100. Zero is pure keyword
    /// search, 100 is pure vector search.
    /// </summary>
    public int SemanticRatio { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of tokens embedded per item. Library metadata is short, and
    /// this caps the cost of the long overviews that dominate inference time.
    /// </summary>
    public int EmbeddingMaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the number of documents embedded per forward pass.
    /// </summary>
    public int EmbeddingBatchSize { get; set; }

    /// <summary>
    /// Gets or sets the number of CPU threads used for inference. Zero lets the plugin pick half the
    /// available processors, leaving headroom for transcoding and the rest of the server.
    /// </summary>
    public int EmbeddingThreads { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether computed vectors are kept on disk and reused.
    /// </summary>
    /// <remarks>
    /// On by default. A rebuild re-embeds the whole library, and for the overwhelming majority of
    /// items the metadata has not changed since the last run, so the vector is identical - this turns
    /// hours of inference back into a file read. The cost is roughly four kilobytes per item on disk.
    /// </remarks>
    public bool EnableEmbeddingCache { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Meilisearch stores vectors binary-quantized, at one
    /// bit per dimension instead of a 32-bit float.
    /// </summary>
    /// <remarks>
    /// On by default: it shrinks the stored vectors 32-fold - for a 300k-item library, from well over
    /// a gigabyte to a few tens of megabytes - which is what keeps them resident instead of being
    /// paged in per query. On a library measured cold, that paging was the difference between a
    /// search taking three seconds and taking a few hundred milliseconds.
    /// <para>
    /// The cost is ranking precision, and it is smaller than it sounds. Measured against the
    /// unquantized ranking on real library vectors, the quantized top ten agrees on roughly two
    /// thirds of its entries, but the ones it substitutes are near-ties drawn from just below the
    /// cut: their mean similarity is within about 1.5% of the entries they replace. It also only
    /// affects the vector half of a hybrid search - keyword matching is exact either way.
    /// </para>
    /// <para>
    /// Turning this off again does not restore precision on its own. Meilisearch discards the full
    /// vectors as it quantizes, so the index has to be rebuilt to get them back. That rebuild reads
    /// from the embedding cache, which always holds full-precision vectors, so it costs a re-upload
    /// rather than re-running the model over the library.
    /// </para>
    /// </remarks>
    public bool BinaryQuantizeVectors { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of vectors the cache stores, or zero for no limit. A full
    /// rebuild prunes entries it did not use, so this only bites on libraries larger than the limit.
    /// </summary>
    public int EmbeddingCacheMaxEntries { get; set; }
}
