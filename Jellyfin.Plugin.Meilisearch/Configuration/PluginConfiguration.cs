using System;
using Jellyfin.Plugin.Meilisearch.Embeddings;
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
        EmbeddingModelId = EmbeddingModels.DefaultId;
        IndexedEmbeddingModelId = string.Empty;
        AutoDownloadEmbeddingModel = true;
        EmbeddingModelPath = string.Empty;
        SemanticRatio = 50;
        EmbeddingMaxTokens = 256;
        EmbeddingThreads = 0;
        EmbeddingOnnxRuntimePath = string.Empty;
        EmbeddingIdleUnloadMinutes = 5;
        EnableEmbeddingCache = true;
        EmbeddingCacheMaxEntries = 0;
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
    /// Gets or sets the identifier of the embedding model to use, as listed by
    /// <see cref="EmbeddingModels"/>. Empty or unknown falls back to <see cref="EmbeddingModels.DefaultId"/>.
    /// </summary>
    /// <remarks>
    /// Only models this build ships code for can be selected: the tokenizer, the graph inputs and the
    /// pooling are part of a model, not settings around it. Each one gets its own directory on disk,
    /// its own vector cache and its own Meilisearch embedder, so switching is reversible - but the
    /// index has to be rebuilt afterwards, since vectors from one model mean nothing to another.
    /// </remarks>
    public string EmbeddingModelId { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="EmbeddingModelId"/> that was in effect at the last successful full
    /// reindex, or empty when the index holds no vectors. Used to warn when the selected model no
    /// longer matches what the index was built with.
    /// </summary>
    public string IndexedEmbeddingModelId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the embedding model may be downloaded automatically
    /// once semantic search is enabled. When false, the model must be fetched with the
    /// "Download Meilisearch Embedding Model" scheduled task before semantic search becomes active.
    /// </summary>
    public bool AutoDownloadEmbeddingModel { get; set; }

    /// <summary>
    /// Gets or sets the directory embedding models are stored in. Empty means a
    /// <c>meilisearch-embeddings</c> directory under Jellyfin's data path. Each model gets a
    /// subdirectory named after its identifier.
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
    /// Gets or sets the number of CPU threads used for inference. Zero lets the plugin pick half the
    /// available processors, leaving headroom for transcoding and the rest of the server.
    /// </summary>
    public int EmbeddingThreads { get; set; }

    /// <summary>
    /// Gets or sets the path to an alternative ONNX Runtime native library - either the file itself
    /// or a directory holding it. Empty means the CPU build bundled with the plugin.
    /// </summary>
    public string EmbeddingOnnxRuntimePath { get; set; }

    /// <summary>
    /// Gets or sets how many minutes the model may sit unused before it is released from memory,
    /// or zero to keep it loaded for the life of the server.
    /// </summary>
    public int EmbeddingIdleUnloadMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether computed vectors are kept on disk and reused.
    /// </summary>
    /// <remarks>
    /// On by default. A rebuild re-embeds the whole library, and unchanged metadata yields an
    /// identical vector, so this turns hours of inference back into a file read. Costs about four
    /// kilobytes per item on disk.
    /// </remarks>
    public bool EnableEmbeddingCache { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Meilisearch stores vectors binary-quantized, at one
    /// bit per dimension instead of a 32-bit float.
    /// </summary>
    /// <remarks>
    /// On by default: 32 times smaller - a gigabyte down to tens of megabytes for a 300k-item library
    /// - which keeps the vectors resident instead of paged in per query, worth seconds on a cold
    /// search. The cost is some ranking precision in the vector half of a hybrid search, and the
    /// entries it swaps in are near-ties: measured on real library vectors, within about 1.5% of the
    /// similarity of what they replace.
    /// <para>
    /// Turning it off needs a rebuild - Meilisearch discards the full vectors as it quantizes - but
    /// that rebuild reads from the embedding cache, which always keeps full precision, so it
    /// re-uploads rather than re-running the model.
    /// </para>
    /// </remarks>
    public bool BinaryQuantizeVectors { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of vectors the cache stores, or zero for no limit, which is
    /// the default. A full rebuild already prunes entries it did not use, so the cache tracks the
    /// library's size on its own and a limit only matters when disk space has to be capped.
    /// </summary>
    public int EmbeddingCacheMaxEntries { get; set; }
}
