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
        SearchOverviews = true;
        SearchFilePaths = true;
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
        MinimumSemanticScore = 0;
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
    /// Gets or sets a value indicating whether plot summaries are searched by keyword.
    /// </summary>
    public bool SearchOverviews { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether file paths are searched by keyword. On by default,
    /// since it is what makes items findable by release name; worth turning off if searches feel
    /// padded, because a path carries the name of every directory above the file too.
    /// </summary>
    public bool SearchFilePaths { get; set; }

    /// <summary>
    /// Gets or sets the Meilisearch matching strategy: <c>frequency</c>, <c>last</c> or <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <c>frequency</c> drops the library's most common word when a query has no exact match,
    /// <c>last</c> drops words from the end, <c>all</c> requires every word. <c>frequency</c> needs
    /// Meilisearch 1.11 or newer; older servers fall back to
    /// <see cref="FallbackMatchingStrategy"/> after the first rejection.
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
    /// Off by default: turning it on downloads several hundred megabytes of model, runs it on the CPU
    /// for every indexed item and stores a 1024-float vector per document. Nothing embedding-related
    /// loads or runs while it is off.
    /// </remarks>
    public bool EnableSemanticSearch { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the embedding model to use, as listed by
    /// <see cref="EmbeddingModels"/>. Empty or unknown falls back to <see cref="EmbeddingModels.DefaultId"/>.
    /// </summary>
    /// <remarks>
    /// Only models this build ships code for, since tokenizer, graph inputs and pooling are part of a
    /// model rather than settings around it. Each gets its own directory, vector cache and embedder,
    /// so switching is reversible - but needs a rebuild, as vectors do not carry across models.
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
    /// search - the query is then not embedded at all - and 100 is pure vector search.
    /// </summary>
    /// <remarks>
    /// The useful range is narrower than it looks: at or above
    /// <see cref="EmbeddingService.KeywordOutrankedSemanticRatio"/> a merely similar item outranks
    /// an exact title match, so the default sits below it and leaves vector hits to fill in beneath
    /// the keyword ones. See the remarks there.
    /// </remarks>
    public int SemanticRatio { get; set; }

    /// <summary>
    /// Gets or sets the score, 0-100, a vector match has to reach to be returned at all, or zero to
    /// return whatever the vector search ranks highest.
    /// </summary>
    /// <remarks>
    /// A vector search has no notion of "no match", so without a floor a query matching nothing still
    /// fills the page. Meilisearch reports similarity as <c>(cosine + 1) / 2</c>, which puts even
    /// unrelated items around 0.7, so a floor that bites has to be set close to that. Meilisearch
    /// applies one threshold to both halves of a hybrid search, so this also raises the bar for
    /// keyword hits; the code takes the higher of the two floors. The useful value depends on the
    /// library, hence a default of zero.
    /// </remarks>
    public int MinimumSemanticScore { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of tokens embedded per item. Library metadata is short, and
    /// this caps the cost of the long overviews that dominate inference time.
    /// </summary>
    /// <remarks>
    /// Part of the vector cache's identity: a vector computed under one budget is not the vector the
    /// next budget would produce, so changing this starts a new cache rather than handing back what
    /// the old setting produced. The index keeps its old vectors until a full rebuild.
    /// </remarks>
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
    /// On by default: unchanged metadata yields an identical vector, so a rebuild becomes a file read
    /// instead of hours of inference, at about four kilobytes per item on disk.
    /// </remarks>
    public bool EnableEmbeddingCache { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Meilisearch stores vectors binary-quantized, at one
    /// bit per dimension instead of a 32-bit float.
    /// </summary>
    /// <remarks>
    /// On by default: 32 times smaller - a gigabyte down to tens of megabytes for 300k items - which
    /// keeps vectors resident rather than paged in per query. The cost is some ranking precision, and
    /// the entries it swaps in are near-ties, within about 1.5% of what they replace. Turning it off
    /// needs a rebuild, but that reads full precision from the embedding cache rather than re-embeds.
    /// </remarks>
    public bool BinaryQuantizeVectors { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of vectors the cache stores, or zero for no limit, which is
    /// the default. A full rebuild already prunes entries it did not use, so the cache tracks the
    /// library's size on its own and a limit only matters when disk space has to be capped.
    /// </summary>
    public int EmbeddingCacheMaxEntries { get; set; }
}
