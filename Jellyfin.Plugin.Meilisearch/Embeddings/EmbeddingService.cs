using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Meilisearch.Configuration;
using Jellyfin.Plugin.Meilisearch.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Owns the local embedding model: downloads it when needed, loads it once, and hands out vectors.
/// </summary>
public sealed class EmbeddingService : IHostedService, IDisposable
{
    /// <summary>
    /// The <see cref="PluginConfiguration.SemanticRatio"/> above which a vector match starts to
    /// outrank an exact keyword match.
    /// </summary>
    /// <remarks>
    /// Hybrid search picks per hit rather than blending: the larger of
    /// <c>keywordScore * (1 - ratio)</c> and <c>semanticScore * ratio</c> wins. A keyword hit on a
    /// title scores 1.0, while Meilisearch reports vector similarity as <c>(cosine + 1) / 2</c>, so
    /// an unrelated item still scores around 0.7 - measured against a 330k-item library, where rank
    /// 100 of a vector search sat at 0.70 and rank 5 at 0.73. Solving <c>1 - r &gt; 0.7r</c> puts the
    /// crossover just under 0.6: above it every vector near-miss outranks every exact title match,
    /// which is what buries a title the moment someone types the first word of it. Advisory only -
    /// the ratio is passed to Meilisearch either way.
    /// </remarks>
    public const int KeywordOutrankedSemanticRatio = 60;

    // Texts handed to the embedder per call. Each gets its own forward pass regardless, so this only
    // sets how often a long run checks for cancellation and reports progress - about twice a second.
    private const int EmbedChunkSize = 8;

    private const int QueryVectorCacheSize = 64;
    private const double QueryEmbeddingSmoothingFactor = 0.2;

    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<EmbeddingService> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly object _queryVectorGate = new();
    private readonly Dictionary<string, LinkedListNode<QueryVector>> _queryVectors = new(StringComparer.Ordinal);
    private readonly LinkedList<QueryVector> _queryVectorOrder = new();

    private long _lastUseTicks = DateTime.UtcNow.Ticks;

    private Timer? _idleTimer;
    private ITextEmbedder? _embedder;
    private EmbeddingCache? _cache;
    private string? _loadedKey;
    private EmbeddingState _state = EmbeddingState.Disabled;
    private string? _error;
    private Task? _backgroundInit;
    private CancellationTokenSource? _cts;
    private EventHandler<BasePluginConfiguration>? _configurationChangedHandler;
    private long _queryVectorHits;
    private long _queryVectorMisses;
    private long _queryEmbeddingCount;
    private double _averageQueryEmbeddingMilliseconds;
    private bool _warnedSemanticRatio;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="applicationPaths">Application paths, used to locate the default model directory.</param>
    public EmbeddingService(ILogger<EmbeddingService> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
    }

    /// <summary>
    /// Gets the model the configuration currently selects.
    /// </summary>
    public static EmbeddingModelDefinition ActiveModel => EmbeddingModels.Resolve(Configuration.EmbeddingModelId);

    /// <summary>
    /// Gets the name the vector field is registered under in Meilisearch.
    /// </summary>
    public static string EmbedderName => ActiveModel.EmbedderName;

    /// <summary>
    /// Gets the width of the vectors this service produces.
    /// </summary>
    public static int Dimensions => ActiveModel.Dimensions;

    /// <summary>
    /// Gets the token budget actually used per text, with the configured value brought into a range
    /// the model can run.
    /// </summary>
    public static int EffectiveMaxTokens => Math.Clamp(Configuration.EmbeddingMaxTokens, 16, 8192);

    /// <summary>
    /// Gets a value indicating whether semantic search is switched on in the configuration.
    /// </summary>
    public bool IsEnabled => Configuration.EnableSemanticSearch;

    /// <summary>
    /// Gets a value indicating whether vectors can be produced right now.
    /// </summary>
    public bool IsReady => IsEnabled && _state == EmbeddingState.Ready && _embedder is not null;

    /// <summary>
    /// Gets the current lifecycle state.
    /// </summary>
    public EmbeddingState State => IsEnabled ? _state : EmbeddingState.Disabled;

    /// <summary>
    /// Gets the last initialization error, if any.
    /// </summary>
    public string? Error => _error;

    /// <summary>
    /// Gets the execution provider inference is running on, or null when nothing is loaded. This is
    /// what was actually negotiated with ONNX Runtime rather than what the configuration asked for.
    /// </summary>
    public EmbeddingExecutionProvider? ActiveExecutionProvider => _embedder?.ExecutionProvider;

    /// <summary>
    /// Gets the execution providers the loaded ONNX Runtime offers. On a stock install this is the
    /// CPU provider alone, which is the answer to "why did selecting CUDA change nothing".
    /// </summary>
    public IReadOnlyCollection<string> AvailableExecutionProviders
        => ExecutionProviderSelector.GetAvailableProviders(_logger);

    /// <summary>
    /// Gets the keyword/vector balance to pass to Meilisearch, in the range 0.0-1.0.
    /// </summary>
    public double SemanticRatio => Math.Clamp(Configuration.SemanticRatio, 0, 100) / 100d;

    /// <summary>
    /// Gets the number of vectors currently held in the on-disk cache, or null when it is not open.
    /// </summary>
    public int? CachedVectorCount => _cache?.Count;

    /// <summary>
    /// Gets the share of embedding lookups served from the cache since it was opened, 0.0-1.0, or
    /// null when nothing has been looked up yet.
    /// </summary>
    public double? CacheHitRate
    {
        get
        {
            if (_cache is not { } cache)
            {
                return null;
            }

            var total = cache.Hits + cache.Misses;
            return total == 0 ? null : (double)cache.Hits / total;
        }
    }

    /// <summary>
    /// Gets the rolling average time spent embedding a query, or null before the first one.
    /// </summary>
    public double? AverageQueryEmbeddingMilliseconds
    {
        get
        {
            lock (_queryVectorGate)
            {
                return _queryEmbeddingCount == 0 ? null : _averageQueryEmbeddingMilliseconds;
            }
        }
    }

    /// <summary>
    /// Gets the share of query embeddings served from the in-memory query cache, 0.0-1.0, or null
    /// before the first query.
    /// </summary>
    public double? QueryVectorCacheHitRate
    {
        get
        {
            lock (_queryVectorGate)
            {
                var total = _queryVectorHits + _queryVectorMisses;
                return total == 0 ? null : (double)_queryVectorHits / total;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the configured semantic ratio still leaves exact keyword
    /// matches on top. See <see cref="KeywordOutrankedSemanticRatio"/>.
    /// </summary>
    public bool IsSemanticRatioBalanced => Configuration.SemanticRatio < KeywordOutrankedSemanticRatio;

    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Builds the text that represents an item to the embedding model.
    /// </summary>
    /// <param name="document">The document to describe.</param>
    /// <returns>A single string combining the item's identifying and descriptive metadata.</returns>
    public static string BuildDocumentText(MeilisearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder(512);

        Append(builder, document.Name);
        Append(builder, string.Equals(document.OriginalTitle, document.Name, StringComparison.OrdinalIgnoreCase)
            ? null
            : document.OriginalTitle);
        Append(builder, document.SeriesName);
        Append(builder, document.AlbumName);
        AppendList(builder, document.AlbumArtists ?? document.Artists);
        Append(builder, document.ItemType);
        Append(builder, document.ProductionYear?.ToString(CultureInfo.InvariantCulture));
        AppendList(builder, document.Genres);
        AppendList(builder, document.Studios);
        AppendList(builder, document.Tags);
        AppendList(builder, document.People);
        Append(builder, document.Tagline);
        Append(builder, document.Overview);

        return builder.ToString();
    }

    /// <summary>
    /// Turns a lower-case reason fragment, which reads correctly inside a log message, into a
    /// standalone sentence for the settings page.
    /// </summary>
    private static string Describe(string? reason)
        => string.IsNullOrEmpty(reason)
            ? "This platform is not supported."
            : char.ToUpperInvariant(reason[0]) + reason[1..] + ".";

    private static void Append(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(". ");
        }

        builder.Append(value.Trim());
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        // Cap the list: a cast of forty actors would otherwise crowd out the overview inside the
        // token budget without adding much the first few names don't already carry.
        Append(builder, string.Join(", ", values.Where(static v => !string.IsNullOrWhiteSpace(v)).Take(8)));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();

        _configurationChangedHandler = OnConfigurationChanged;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += _configurationChangedHandler;
        }

        if (Configuration.EnableSemanticSearch)
        {
            StartBackgroundInitialization();
        }

        MarkUsed();
        _idleTimer = new Timer(static state => ((EmbeddingService)state!).OnIdleCheck(), this, IdleCheckInterval, IdleCheckInterval);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var idleTimer = _idleTimer;
        _idleTimer = null;
        if (idleTimer is not null)
        {
            await idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (Plugin.Instance is { } plugin && _configurationChangedHandler is not null)
        {
            plugin.ConfigurationChanged -= _configurationChangedHandler;
            _configurationChangedHandler = null;
        }

        if (_cts is not null)
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        var pending = _backgroundInit;
        if (pending is not null)
        {
            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down before initialization finished.
            }
#pragma warning disable CA1031 // Shutdown must not surface an initialization failure.
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Embedding initialization ended with an error during shutdown");
            }
#pragma warning restore CA1031
        }

        Unload();
    }

    /// <summary>
    /// Makes sure the model is downloaded and loaded, doing the work inline.
    /// </summary>
    /// <param name="progress">Receives download progress in the range 0-100, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the model is ready to produce vectors.</returns>
    public async Task<bool> EnsureReadyAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!Configuration.EnableSemanticSearch)
        {
            Unload();
            _state = EmbeddingState.Disabled;
            return false;
        }

        var descriptor = CreateDescriptor();
        var key = BuildKey(descriptor);

        if (_embedder is not null && _loadedKey == key)
        {
            return true;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_embedder is not null && _loadedKey == key)
            {
                return true;
            }

            // A changed model or directory invalidates whatever is loaded.
            Unload();

            _state = EmbeddingState.Initializing;
            _error = null;

            // Asked before the download, not after: a host ONNX Runtime has no native library for
            // cannot run any model, and finding that out at the P/Invoke would mean fetching several
            // hundred megabytes first.
            if (!OnnxRuntimeNativeLoader.IsNativeLibraryAvailable(_logger, out var unsupportedReason))
            {
                _state = EmbeddingState.Unsupported;
                _error = Describe(unsupportedReason);
                _logger.LogWarning(
                    "Semantic search is enabled but cannot run on this platform: {Reason}. "
                    + "No model will be downloaded; turn semantic search off to stop retrying, or install "
                    + "ONNX Runtime system-wide. Keyword search is unaffected",
                    unsupportedReason);
                return false;
            }

            EmbeddingStorageMigration.MigrateModelFiles(descriptor, GetModelRootDirectory(), _logger);

            if (!descriptor.IsComplete())
            {
                if (!Configuration.AutoDownloadEmbeddingModel)
                {
                    _state = EmbeddingState.NotDownloaded;
                    _error = "Model not downloaded. Run the \"Download Meilisearch Embedding Model\" task.";
                    _logger.LogInformation(
                        "Semantic search is enabled but the embedding model is missing and automatic download is off; run the download task");
                    return false;
                }

                var downloader = new EmbeddingModelDownloader(_logger);
                await downloader.DownloadAsync(descriptor, progress, cancellationToken).ConfigureAwait(false);
            }

            _embedder = descriptor.Definition.CreateEmbedder(descriptor, Configuration.EmbeddingThreads, _logger);
            _cache = OpenCache(descriptor.Definition);
            _loadedKey = key;

            // Loading takes minutes when it includes the download, and semantic search may have been
            // switched off in the meantime. The unload that switch triggered ran against a service
            // that had nothing loaded yet, so without this re-check a gigabyte of model would sit in
            // memory that nothing can reach and nothing will release.
            if (!Configuration.EnableSemanticSearch)
            {
                _logger.LogInformation("Semantic search was disabled while the model was loading; releasing it again");
                Unload();
                _state = EmbeddingState.Disabled;
                return false;
            }

            _state = EmbeddingState.Ready;

            _logger.LogInformation("Semantic search ready");
            return true;
        }
        catch (OperationCanceledException)
        {
            _state = EmbeddingState.Failed;
            _error = "Initialization cancelled";
            throw;
        }
#pragma warning disable CA1031 // A missing or broken model must degrade to keyword search, not break the plugin.
        catch (Exception ex)
        {
            Unload();
            _state = EmbeddingState.Failed;
            _error = ex.Message;
            _logger.LogError(
                ex,
                "Could not initialize the embedding model; semantic search stays off and keyword search is unaffected");
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Embeds a search term, applying the model's query instruction prefix.
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query vector, or null when semantic search is not available.</returns>
    public double[]? EmbedQuery(string searchTerm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return null;
        }

        if (!IsEnabled)
        {
            return null;
        }

        // Before the model is touched: at a ratio of zero Meilisearch ignores the vector entirely,
        // so the forward pass would be tens of milliseconds spent on a number it discards.
        if (Configuration.SemanticRatio <= 0)
        {
            return null;
        }

        WarnIfSemanticRatioOutranksKeywords();

        MarkUsed();

        var queryPrompt = ActiveModel.QueryPrompt;
        var prompt = queryPrompt.Length == 0 ? searchTerm : queryPrompt + " " + searchTerm;

        if (TryGetCachedQueryVector(prompt) is { } cached)
        {
            return cached;
        }

        if (!IsReady)
        {
            // An idle unload has to be undone by someone, and a search is the only thing that will
            // ask. Loading takes seconds, which is far too long to hold a search open, so this one
            // runs keyword-only and the next is served semantically.
            ReloadAfterIdleUnload();
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        var vectors = EmbedInternal([prompt], EmbeddingPriority.Interactive, cancellationToken);
        RecordQueryEmbedding(stopwatch.Elapsed.TotalMilliseconds);

        if (vectors.Count == 0 || vectors[0] is not { Length: > 0 } vector)
        {
            return null;
        }

        var result = new double[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i];
        }

        StoreQueryVector(prompt, result);
        return result;
    }

    /// <summary>
    /// Looks up a query vector computed for an earlier search.
    /// </summary>
    /// <param name="prompt">The prompted query text, exactly as it would be embedded.</param>
    /// <returns>A copy of the cached vector, or null when it is not cached.</returns>
    private double[]? TryGetCachedQueryVector(string prompt)
    {
        lock (_queryVectorGate)
        {
            if (!_queryVectors.TryGetValue(prompt, out var node))
            {
                _queryVectorMisses++;
                return null;
            }

            _queryVectorOrder.Remove(node);
            _queryVectorOrder.AddFirst(node);
            _queryVectorHits++;

            // Copied: callers own what they get, and concurrent searches must not share an array.
            return (double[])node.Value.Vector.Clone();
        }
    }

    private void StoreQueryVector(string prompt, double[] vector)
    {
        lock (_queryVectorGate)
        {
            if (_queryVectors.ContainsKey(prompt))
            {
                return;
            }

            var node = _queryVectorOrder.AddFirst(new QueryVector(prompt, (double[])vector.Clone()));
            _queryVectors[prompt] = node;

            while (_queryVectors.Count > QueryVectorCacheSize && _queryVectorOrder.Last is { } oldest)
            {
                _queryVectorOrder.RemoveLast();
                _queryVectors.Remove(oldest.Value.Prompt);
            }
        }
    }

    /// <summary>
    /// Drops every cached query vector, since vectors from one model mean nothing to another.
    /// </summary>
    private void ClearQueryVectors()
    {
        lock (_queryVectorGate)
        {
            _queryVectors.Clear();
            _queryVectorOrder.Clear();
        }
    }

    private void RecordQueryEmbedding(double elapsedMilliseconds)
    {
        lock (_queryVectorGate)
        {
            _averageQueryEmbeddingMilliseconds = _queryEmbeddingCount == 0
                ? elapsedMilliseconds
                : (QueryEmbeddingSmoothingFactor * elapsedMilliseconds)
                    + ((1 - QueryEmbeddingSmoothingFactor) * _averageQueryEmbeddingMilliseconds);
            _queryEmbeddingCount++;
        }
    }

    private void WarnIfSemanticRatioOutranksKeywords()
    {
        if (IsSemanticRatioBalanced)
        {
            return;
        }

        lock (_queryVectorGate)
        {
            if (_warnedSemanticRatio)
            {
                return;
            }

            _warnedSemanticRatio = true;
        }

        _logger.LogWarning(
            "The semantic ratio is {Ratio}, at or above the {Crossover} where a merely similar item outranks "
            + "an exact title match, so searching for the first words of a title can push it off the page. "
            + "Lower it to around 50 unless that is what you want",
            Configuration.SemanticRatio.ToString(CultureInfo.InvariantCulture),
            KeywordOutrankedSemanticRatio.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Embeds documents for indexing.
    /// </summary>
    /// <param name="texts">The document texts, as built by <see cref="BuildDocumentText"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input in the same order; an entry is null when it could not be embedded.</returns>
    public IReadOnlyList<float[]?> EmbedDocuments(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => EmbedDocuments(texts, null, cancellationToken);

    /// <summary>
    /// Embeds documents for indexing, reporting progress as it goes.
    /// </summary>
    /// <param name="texts">The document texts, as built by <see cref="BuildDocumentText"/>.</param>
    /// <param name="onProgress">
    /// Called as documents are finished, on the calling thread. Invoked once for the cache hits and
    /// then once per forward pass, so a caller can distinguish a batch that read its vectors from
    /// disk in milliseconds from one that is grinding through them on the CPU.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input in the same order; an entry is null when it could not be embedded.</returns>
    public IReadOnlyList<float[]?> EmbedDocuments(
        IReadOnlyList<string> texts,
        Action<EmbeddingProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return new float[texts.Count][];
        }

        MarkUsed();

        if (!IsReady)
        {
            ReloadAfterIdleUnload();
            return new float[texts.Count][];
        }

        if (_cache is not { } cache)
        {
            return EmbedInternal(
                texts,
                onProgress is null
                    ? null
                    : computed => onProgress(new EmbeddingProgress(computed, texts.Count, 0, computed)),
                EmbeddingPriority.Batch,
                cancellationToken);
        }

        var results = new float[texts.Count][];
        List<int>? missIndexes = null;
        List<string>? missTexts = null;

        for (var i = 0; i < texts.Count; i++)
        {
            if (cache.TryGet(texts[i]) is { } cached)
            {
                results[i] = cached;
                continue;
            }

            (missIndexes ??= []).Add(i);
            (missTexts ??= []).Add(texts[i]);
        }

        var cacheHits = texts.Count - (missTexts?.Count ?? 0);
        onProgress?.Invoke(new EmbeddingProgress(cacheHits, texts.Count, cacheHits, 0));

        if (missTexts is null)
        {
            return results;
        }

        var embedded = EmbedInternal(
            missTexts,
            onProgress is null
                ? null
                : computed => onProgress(new EmbeddingProgress(cacheHits + computed, texts.Count, cacheHits, computed)),
            EmbeddingPriority.Batch,
            cancellationToken);

        for (var i = 0; i < missIndexes!.Count && i < embedded.Count; i++)
        {
            if (embedded[i] is not { Length: > 0 } vector)
            {
                continue;
            }

            results[missIndexes[i]] = vector;
            cache.Add(missTexts[i], vector);
        }

        return results;
    }

    /// <summary>
    /// Starts recording which cached vectors are in use, so <see cref="EndCacheRetention"/> can drop
    /// the rest. A no-op when the cache is not open.
    /// </summary>
    public void BeginCacheRetention() => _cache?.BeginRetentionScope();

    /// <summary>
    /// Ends the recording started by <see cref="BeginCacheRetention"/>.
    /// </summary>
    /// <param name="prune">
    /// When true, cached vectors not used since the scope began are removed. Pass false if the run
    /// did not finish, since it then never saw the whole library.
    /// </param>
    public void EndCacheRetention(bool prune) => _cache?.EndRetentionScope(prune);

    /// <summary>
    /// Embeds a batch of documents and attaches the vectors to them in place.
    /// </summary>
    /// <param name="documents">The documents to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void AttachVectors(IReadOnlyList<MeilisearchDocument> documents, CancellationToken cancellationToken)
        => AttachVectors(documents, null, cancellationToken);

    /// <summary>
    /// Embeds a batch of documents and attaches the vectors to them in place, reporting progress as
    /// it goes.
    /// </summary>
    /// <param name="documents">The documents to embed.</param>
    /// <param name="onProgress">
    /// Called as documents are finished, on the calling thread. A reindex batch is thousands of
    /// items and a cache-cold forward pass is slow, so this is what lets a long batch report
    /// something other than silence.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void AttachVectors(
        IReadOnlyList<MeilisearchDocument> documents,
        Action<EmbeddingProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return;
        }

        MarkUsed();

        if (!IsReady)
        {
            ReloadAfterIdleUnload();
            return;
        }

        var texts = new string[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            texts[i] = BuildDocumentText(documents[i]);
        }

        var vectors = EmbedDocuments(texts, onProgress, cancellationToken);

        for (var i = 0; i < documents.Count && i < vectors.Count; i++)
        {
            if (vectors[i] is { Length: > 0 } vector)
            {
                documents[i].Vectors = new Dictionary<string, MeilisearchVector>(StringComparer.Ordinal)
                {
                    [EmbedderName] = new MeilisearchVector { Embeddings = vector, Regenerate = false }
                };
            }
        }
    }

    /// <summary>
    /// Releases the model from memory without turning semantic search off.
    /// </summary>
    /// <returns>What happened.</returns>
    public UnloadOutcome RequestUnload() => RequestUnload("on request");

    /// <summary>
    /// Discards every cached vector for the selected model.
    /// </summary>
    /// <returns>What happened, and how many vectors were discarded.</returns>
    /// <remarks>
    /// The index keeps the vectors it already holds; this only stops the next rebuild from handing
    /// them straight back. Changing the model or the token budget invalidates the cache on its own -
    /// this is for the case where the vectors are suspect rather than stale, and every one it drops
    /// costs a forward pass to produce again.
    /// </remarks>
    public ClearCacheResult ClearVectorCache()
    {
        // Same refusals as an unload: a reindex reads and writes the cache throughout, and clearing
        // it underneath one would have half the library embedded against a file that is being
        // truncated as it goes.
        if (!ReindexCoordinator.Gate.Wait(0))
        {
            _logger.LogInformation("Refusing to clear the vector cache: a reindex is running");
            return new ClearCacheResult(ClearCacheOutcome.ReindexRunning, 0);
        }

        try
        {
            if (!_initLock.Wait(0))
            {
                _logger.LogInformation("Refusing to clear the vector cache: the model is still downloading or loading");
                return new ClearCacheResult(ClearCacheOutcome.Busy, 0);
            }

            try
            {
                if (_cache is { } cache)
                {
                    var cleared = cache.Clear();
                    return new ClearCacheResult(
                        cleared == 0 ? ClearCacheOutcome.Empty : ClearCacheOutcome.Cleared,
                        cleared);
                }

                // Nothing is open - semantic search is off, the model is unloaded, or caching is
                // disabled - so the files are unlocked and deleting them is the whole job.
                return DeleteCacheFiles();
            }
            finally
            {
                _initLock.Release();
            }
        }
        finally
        {
            ReindexCoordinator.Gate.Release();
        }
    }

    /// <summary>
    /// Removes the cache files of the selected model from disk. Only valid while nothing holds them
    /// open, since they are opened with <see cref="FileShare.None"/>.
    /// </summary>
    private ClearCacheResult DeleteCacheFiles()
    {
        var directory = Path.Combine(GetCacheRootDirectory(), ActiveModel.Id);

        try
        {
            if (!Directory.Exists(directory))
            {
                return new ClearCacheResult(ClearCacheOutcome.Empty, 0);
            }

            var removed = false;
            foreach (var name in new[] { EmbeddingCache.KeysFileName, EmbeddingCache.VectorsFileName })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }

            if (removed)
            {
                _logger.LogInformation("Deleted the vector cache in {Directory}", directory);
            }

            return new ClearCacheResult(removed ? ClearCacheOutcome.Cleared : ClearCacheOutcome.Empty, 0);
        }
#pragma warning disable CA1031 // A cache that will not delete is reported back, not thrown at the caller.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the vector cache in {Directory}", directory);
            return new ClearCacheResult(ClearCacheOutcome.Failed, 0);
        }
#pragma warning restore CA1031
    }

    private UnloadOutcome RequestUnload(string reason)
    {
        // Held for the whole operation rather than merely sampled, so a reindex cannot start between
        // the check and the disposal.
        if (!ReindexCoordinator.Gate.Wait(0))
        {
            _logger.LogInformation("Refusing to unload the embedding model: a reindex is running");
            return UnloadOutcome.ReindexRunning;
        }

        try
        {
            if (!_initLock.Wait(0))
            {
                _logger.LogInformation("Refusing to unload the embedding model: it is still downloading or loading");
                return UnloadOutcome.Busy;
            }

            try
            {
                if (_embedder is null)
                {
                    return UnloadOutcome.NotLoaded;
                }

                _logger.LogInformation(
                    "Releasing the embedding model from memory {Reason}; searches run keyword-only until it is loaded again",
                    reason);

                Unload();
                _state = EmbeddingState.Unloaded;
                _error = null;
                return UnloadOutcome.Unloaded;
            }
            finally
            {
                _initLock.Release();
            }
        }
        finally
        {
            ReindexCoordinator.Gate.Release();
        }
    }

    private void MarkUsed() => Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks);

    private void ReloadAfterIdleUnload()
    {
        if (_state == EmbeddingState.Unloaded && Configuration.EnableSemanticSearch)
        {
            StartBackgroundInitialization();
        }
    }

    private void OnIdleCheck()
    {
        var idleMinutes = Configuration.EmbeddingIdleUnloadMinutes;
        if (idleMinutes <= 0 || _disposed || _state != EmbeddingState.Ready)
        {
            return;
        }

        var idleFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastUseTicks), DateTimeKind.Utc);
        if (idleFor < TimeSpan.FromMinutes(idleMinutes))
        {
            return;
        }

        // Goes through the same refusals a manual unload does, so an idle window that expires in the
        // middle of a reindex - which can spend a long time on database and network work between
        // batches - leaves the model where it is.
        RequestUnload(string.Create(
            CultureInfo.InvariantCulture,
            $"after {idleMinutes} minutes without a vector request"));
    }

    /// <summary>
    /// Determines whether this host can run a local embedding model at all.
    /// </summary>
    /// <param name="reason">When it cannot, a short explanation suitable for an admin.</param>
    /// <returns><c>true</c> when ONNX Runtime's native library is available here.</returns>
    public bool IsPlatformSupported(out string? reason)
        => OnnxRuntimeNativeLoader.IsNativeLibraryAvailable(_logger, out reason);

    /// <summary>
    /// Gets the directory the selected model's files live in.
    /// </summary>
    /// <returns>The absolute path of the model directory.</returns>
    public string GetModelDirectory()
        => Path.Combine(GetModelRootDirectory(), ActiveModel.Id);

    /// <summary>
    /// Builds a descriptor for the configured model.
    /// </summary>
    /// <returns>The descriptor.</returns>
    public EmbeddingModelDescriptor CreateDescriptor()
        => new(ActiveModel, GetModelDirectory());

    private string GetModelRootDirectory()
    {
        var configured = Configuration.EmbeddingModelPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_applicationPaths.DataPath, "meilisearch-embeddings")
            : configured;
    }

    private IReadOnlyList<float[]?> EmbedInternal(
        IReadOnlyList<string> texts,
        EmbeddingPriority priority,
        CancellationToken cancellationToken)
        => EmbedInternal(texts, null, priority, cancellationToken);

    private IReadOnlyList<float[]?> EmbedInternal(
        IReadOnlyList<string> texts,
        Action<int>? onComputed,
        EmbeddingPriority priority,
        CancellationToken cancellationToken)
    {
        var embedder = _embedder;
        if (embedder is null)
        {
            return new float[texts.Count][];
        }

        var maxTokens = EffectiveMaxTokens;

        try
        {
            if (texts.Count <= EmbedChunkSize)
            {
                var single = embedder.Embed(texts, maxTokens, priority, cancellationToken);
                onComputed?.Invoke(texts.Count);
                return single;
            }

            var results = new List<float[]?>(texts.Count);
            for (var offset = 0; offset < texts.Count; offset += EmbedChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slice = texts.Skip(offset).Take(EmbedChunkSize).ToList();
                results.AddRange(embedder.Embed(slice, maxTokens, priority, cancellationToken));
                onComputed?.Invoke(results.Count);
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // An inference failure degrades to keyword-only behaviour.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding failed for a batch of {Count} texts", texts.Count);
            return new float[texts.Count][];
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Identifies what is loaded, so a configuration change that invalidates it forces a reload.
    /// </summary>
    /// <remarks>
    /// The token budget is part of it so that changing it reopens the cache, which is what discards
    /// the vectors the old budget produced. Without that the next rebuild would hand back exactly
    /// the vectors the change was meant to replace.
    /// </remarks>
    private static string BuildKey(EmbeddingModelDescriptor descriptor)
        => string.Join(
            '|',
            descriptor.Definition.Id,
            descriptor.Directory,
            Configuration.EmbeddingThreads.ToString(CultureInfo.InvariantCulture),
            Configuration.EmbeddingOnnxRuntimePath,
            Configuration.EnableEmbeddingCache ? "cache" : "nocache",
            Configuration.EmbeddingCacheMaxEntries.ToString(CultureInfo.InvariantCulture),
            EffectiveMaxTokens.ToString(CultureInfo.InvariantCulture));

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (!Configuration.EnableSemanticSearch)
        {
            if (_embedder is not null)
            {
                _logger.LogInformation("Semantic search disabled; releasing the embedding model");
                Unload();
            }

            _state = EmbeddingState.Disabled;
            return;
        }

        // The ratio may have moved either side of the crossover, so let the warning fire again.
        lock (_queryVectorGate)
        {
            _warnedSemanticRatio = false;
        }

        // Re-initialize on a background task: this runs on the caller's config-save request.
        StartBackgroundInitialization();
    }

    private void StartBackgroundInitialization()
    {
        var token = _cts?.Token ?? CancellationToken.None;

        var pending = _backgroundInit;
        if (pending is not null && !pending.IsCompleted)
        {
            return;
        }

        _backgroundInit = Task.Run(
            async () =>
            {
                try
                {
                    await EnsureReadyAsync(null, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Server is shutting down.
                }
#pragma warning disable CA1031 // Nothing observes this task until shutdown; it must not fault silently.
                catch (Exception ex)
                {
                    _state = EmbeddingState.Failed;
                    _error = ex.Message;
                    _logger.LogError(ex, "Background initialization of the embedding model failed");
                }
#pragma warning restore CA1031
            },
            CancellationToken.None);
    }

    private string GetCacheRootDirectory()
        => Path.Combine(_applicationPaths.DataPath, "meilisearch-embedding-cache");

    private EmbeddingCache? OpenCache(EmbeddingModelDefinition definition)
    {
        if (!Configuration.EnableEmbeddingCache)
        {
            return null;
        }

        var root = GetCacheRootDirectory();
        var directory = Path.Combine(root, definition.Id);
        EmbeddingStorageMigration.MigrateVectorCache(definition.Id, root, directory, _logger);

        return EmbeddingCache.Open(
            directory,
            string.Create(CultureInfo.InvariantCulture, $"{definition.Fingerprint}|tokens={EffectiveMaxTokens}"),
            definition.Dimensions,
            Math.Max(0, Configuration.EmbeddingCacheMaxEntries),
            _logger);
    }

    private void Unload()
    {
        var embedder = _embedder;
        var cache = _cache;
        _embedder = null;
        _cache = null;
        _loadedKey = null;

        ClearQueryVectors();

        // Disposing the embedder blocks until any forward pass already running has finished, so the
        // cache outlives every call that could still want to write to it.
        embedder?.Dispose();
        cache?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();
        _idleTimer = null;
        Unload();
        _cts?.Dispose();
        _cts = null;
        _initLock.Dispose();
    }

    /// <summary>
    /// One cached query vector, keyed by the prompted text that produced it.
    /// </summary>
    private readonly record struct QueryVector(string Prompt, double[] Vector);
}
