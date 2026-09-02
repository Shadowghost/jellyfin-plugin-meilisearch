using System;
using System.Collections.Generic;
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
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<EmbeddingService> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SemaphoreSlim _initLock = new(1, 1);

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

        MarkUsed();

        if (!IsReady)
        {
            // An idle unload has to be undone by someone, and a search is the only thing that will
            // ask. Loading takes seconds, which is far too long to hold a search open, so this one
            // runs keyword-only and the next is served semantically.
            ReloadAfterIdleUnload();
            return null;
        }

        var queryPrompt = ActiveModel.QueryPrompt;
        var prompt = queryPrompt.Length == 0 ? searchTerm : queryPrompt + " " + searchTerm;
        var vectors = EmbedInternal([prompt], cancellationToken);
        if (vectors.Count == 0 || vectors[0] is not { Length: > 0 } vector)
        {
            return null;
        }

        var result = new double[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i];
        }

        return result;
    }

    /// <summary>
    /// Embeds documents for indexing.
    /// </summary>
    /// <param name="texts">The document texts, as built by <see cref="BuildDocumentText"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input in the same order; an entry is null when it could not be embedded.</returns>
    public IReadOnlyList<float[]?> EmbedDocuments(IReadOnlyList<string> texts, CancellationToken cancellationToken)
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
            return EmbedInternal(texts, cancellationToken);
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

        if (missTexts is null)
        {
            return results;
        }

        var embedded = EmbedInternal(missTexts, cancellationToken);

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

        var vectors = EmbedDocuments(texts, cancellationToken);

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

    private IReadOnlyList<float[]?> EmbedInternal(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var embedder = _embedder;
        if (embedder is null)
        {
            return new float[texts.Count][];
        }

        var maxTokens = Math.Clamp(Configuration.EmbeddingMaxTokens, 16, 8192);
        var batchSize = Math.Clamp(Configuration.EmbeddingBatchSize, 1, 64);

        try
        {
            if (texts.Count <= batchSize)
            {
                return embedder.Embed(texts, maxTokens, cancellationToken);
            }

            var results = new List<float[]?>(texts.Count);
            for (var offset = 0; offset < texts.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slice = texts.Skip(offset).Take(batchSize).ToList();
                results.AddRange(embedder.Embed(slice, maxTokens, cancellationToken));
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

    private static string BuildKey(EmbeddingModelDescriptor descriptor)
        => string.Join(
            '|',
            descriptor.Definition.Id,
            descriptor.Directory,
            Configuration.EmbeddingThreads.ToString(CultureInfo.InvariantCulture),
            Configuration.EmbeddingOnnxRuntimePath,
            Configuration.EnableEmbeddingCache ? "cache" : "nocache",
            Configuration.EmbeddingCacheMaxEntries.ToString(CultureInfo.InvariantCulture));

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

    private EmbeddingCache? OpenCache(EmbeddingModelDefinition definition)
    {
        if (!Configuration.EnableEmbeddingCache)
        {
            return null;
        }

        var root = Path.Combine(_applicationPaths.DataPath, "meilisearch-embedding-cache");
        var directory = Path.Combine(root, definition.Id);
        EmbeddingStorageMigration.MigrateVectorCache(definition.Id, root, directory, _logger);

        return EmbeddingCache.Open(
            directory,
            definition.Fingerprint,
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
}
