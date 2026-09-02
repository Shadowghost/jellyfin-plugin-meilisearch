using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.Meilisearch.Embeddings.Qwen;

/// <summary>
/// Runs Qwen3-Embedding locally through ONNX Runtime and turns text into normalized vectors.
/// </summary>
public sealed class QwenOnnxEmbedder : ITextEmbedder
{
    private const string PastKeyValuePrefix = "past_key_values";
    private const string HiddenStateOutput = "last_hidden_state";

    // Qwen3-0.6B: 8 grouped-query key/value heads of 128 dimensions each. The key/value cache is
    // passed in empty because embedding is always a single forward pass with nothing cached.
    private const int KeyValueHeads = 8;
    private const int HeadDimensions = 128;

    // How many times a batch caller gives up its turn to arriving searches before taking the model
    // anyway. Without a bound, a steady stream of searches would stall indexing indefinitely.
    private const int MaxBatchYields = 32;

    // How long Dispose waits for a running forward pass. One pass is bounded by the token budget, so
    // anything beyond this means the call is wedged rather than slow.
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly QwenEmbeddingTokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly InferenceSession _session;
    private readonly SessionOptions _sessionOptions;
    private readonly string[] _pastKeyValueNames;
    private readonly TensorElementType _pastKeyValueType;
    private readonly EmbeddingExecutionProvider _provider;
#pragma warning disable CA2213 // Deliberately not disposed; see the remarks on Dispose.
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly ManualResetEventSlim _noSearchWaiting = new(true);
#pragma warning restore CA2213
    private readonly object _disposeGate = new();

    private int _searchesWaiting;
    private volatile bool _disposed;

    private QwenOnnxEmbedder(
        ILogger logger,
        QwenEmbeddingTokenizer tokenizer,
        int dimensions,
        InferenceSession session,
        SessionOptions sessionOptions,
        EmbeddingExecutionProvider provider)
    {
        _logger = logger;
        _tokenizer = tokenizer;
        _dimensions = dimensions;
        _session = session;
        _sessionOptions = sessionOptions;
        _provider = provider;

        _pastKeyValueNames = [.. session.InputMetadata.Keys
            .Where(static name => name.StartsWith(PastKeyValuePrefix, StringComparison.Ordinal))];

        // The element type differs per exported variant - float32 for the quantized builds, float16
        // for the fp16 build - so it is read from the graph rather than assumed.
        _pastKeyValueType = _pastKeyValueNames.Length > 0
            ? session.InputMetadata[_pastKeyValueNames[0]].ElementDataType
            : TensorElementType.Float;
    }

    /// <inheritdoc />
    public EmbeddingExecutionProvider ExecutionProvider => _provider;

    /// <summary>
    /// Loads the model and tokenizer from disk.
    /// </summary>
    /// <param name="descriptor">The model to load.</param>
    /// <param name="threads">Number of inference threads, or zero to pick a default. CPU only.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The loaded embedder.</returns>
    public static QwenOnnxEmbedder Load(EmbeddingModelDescriptor descriptor, int threads, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(logger);

        // Must happen before the first ONNX Runtime P/Invoke, i.e. before the provider query and the
        // InferenceSession below.
        OnnxRuntimeNativeLoader.EnsureRegistered(logger);

        var tokenizer = QwenEmbeddingTokenizer.Load(descriptor);

        // Default to half the processors: CPU inference still has to leave Jellyfin room to serve
        // playback and transcodes while a reindex is embedding the library.
        var intraOpThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);

        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        EmbeddingExecutionProvider provider;
        try
        {
            provider = ExecutionProviderSelector.Apply(sessionOptions, logger);

            // The weights are the bulk of what a loaded session holds - hundreds of megabytes - and by
            // default they come out of the same arena the per-pass activations do.
            sessionOptions.AddSessionConfigEntry("session.use_device_allocator_for_initializers", "1");
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }

        InferenceSession session;
        try
        {
            session = new InferenceSession(descriptor.ModelPath, sessionOptions);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }

        if (provider == EmbeddingExecutionProvider.Cpu)
        {
            logger.LogInformation(
                "Loaded embedding model {Model} ({Dimensions} dimensions) on the CPU with {Threads} threads",
                descriptor.Definition.DisplayName,
                descriptor.Definition.Dimensions,
                intraOpThreads.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            logger.LogInformation(
                "Loaded embedding model {Model} ({Dimensions} dimensions) on {Provider}",
                descriptor.Definition.DisplayName,
                descriptor.Definition.Dimensions,
                provider);
        }

        return new QwenOnnxEmbedder(
            logger,
            tokenizer,
            descriptor.Definition.Dimensions,
            session,
            sessionOptions,
            provider);
    }

    /// <summary>
    /// Embeds texts, one forward pass each.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="maxTokens">Maximum tokens to keep per text.</param>
    /// <param name="priority">Whether this request may be made to wait for searches.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One vector per input, in the same order. An entry is null when the corresponding text was
    /// empty and produced no tokens to pool, or when the model was released mid-call.
    /// </returns>
    public IReadOnlyList<float[]?> Embed(
        IReadOnlyList<string> texts,
        int maxTokens,
        EmbeddingPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new float[texts.Count][];
        if (texts.Count == 0 || _disposed)
        {
            return results;
        }

        var populated = new List<(long[] Row, int Index)>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var row = _tokenizer.Encode(texts[i], maxTokens);
            if (row.Length > 0)
            {
                populated.Add((row, i));
            }
        }

        if (populated.Count == 0)
        {
            return results;
        }

        // One text per pass at its own token length, not a tuning choice: the model is dynamically
        // quantized, so the activation scales come from whatever shares the input tensor. Batched
        // next to one other text of identical length, a vector came back at cosine 0.937 to the same
        // text alone - and a query is always embedded alone.
        foreach (var (row, index) in populated)
        {
            EnterInference(priority, cancellationToken);
            try
            {
                // Re-checked while holding the lock. Dispose sets the flag and then takes this lock,
                // so once we are past this point the native session cannot be released under us.
                if (_disposed)
                {
                    return results;
                }

                results[index] = RunSingle(row, cancellationToken);
            }
            finally
            {
                ExitInference(priority);
            }
        }

        return results;
    }

    /// <summary>
    /// Acquires the model.
    /// </summary>
    /// <remarks>
    /// Sessions are thread-safe, but concurrent Run calls each spin up their own intra-op work, so
    /// serializing keeps CPU use within the configured thread count; it is also what makes disposal
    /// safe. A pass cannot be interrupted, so indexing yields by not starting one while a search
    /// waits.
    /// </remarks>
    private void EnterInference(EmbeddingPriority priority, CancellationToken cancellationToken)
    {
        if (priority == EmbeddingPriority.Interactive)
        {
            if (Interlocked.Increment(ref _searchesWaiting) == 1)
            {
                _noSearchWaiting.Reset();
            }

            try
            {
                _inferenceLock.Wait(cancellationToken);
            }
            catch
            {
                LeaveSearchQueue();
                throw;
            }

            return;
        }

        for (var yields = 0; ; yields++)
        {
            _noSearchWaiting.Wait(cancellationToken);
            _inferenceLock.Wait(cancellationToken);

            // A search may have arrived while this was queueing for the lock, and the semaphore
            // makes no ordering promise anyway. Step aside for it - but only so many times, or a
            // steady stream of searches would stall indexing for good.
            if (Volatile.Read(ref _searchesWaiting) == 0 || yields >= MaxBatchYields)
            {
                return;
            }

            _inferenceLock.Release();
        }
    }

    private void ExitInference(EmbeddingPriority priority)
    {
        _inferenceLock.Release();

        if (priority == EmbeddingPriority.Interactive)
        {
            LeaveSearchQueue();
        }
    }

    private void LeaveSearchQueue()
    {
        if (Interlocked.Decrement(ref _searchesWaiting) == 0)
        {
            _noSearchWaiting.Set();
        }
    }

    /// <summary>
    /// Runs one text through the model and pools its vector.
    /// </summary>
    /// <param name="row">The token ids, at exactly their own length - no padding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The normalized vector.</returns>
    /// <remarks>
    /// Each distinct length costs one execution plan the first time it is seen. The arena still only
    /// grows to the largest, which is now one row of the token cap rather than a whole batch of it.
    /// </remarks>
    private float[] RunSingle(long[] row, CancellationToken cancellationToken)
    {
        var length = row.Length;

        var attentionMask = new long[length];
        var positionIds = new long[length];
        for (var i = 0; i < length; i++)
        {
            attentionMask[i] = 1;
            positionIds[i] = i;
        }

        long[] shape = [1, length];
        long[] emptyCacheShape = [1, KeyValueHeads, 0, HeadDimensions];

        var names = new List<string>(3 + _pastKeyValueNames.Length);
        var values = new List<OrtValue>(names.Capacity);

        try
        {
            names.Add("input_ids");
            values.Add(OrtValue.CreateTensorValueFromMemory(row, shape));

            names.Add("attention_mask");
            values.Add(OrtValue.CreateTensorValueFromMemory(attentionMask, shape));

            names.Add("position_ids");
            values.Add(OrtValue.CreateTensorValueFromMemory(positionIds, shape));

            foreach (var name in _pastKeyValueNames)
            {
                names.Add(name);
                values.Add(OrtValue.CreateAllocatedTensorValue(
                    OrtAllocator.DefaultInstance,
                    _pastKeyValueType,
                    emptyCacheShape));
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var runOptions = new RunOptions();

            // Embedding comes in bursts, and without this the arena keeps whatever the burst needed
            // for the life of the server. CPU only: on a GPU provider the same round trip is a
            // device synchronization per pass, and the idle unload already returns that memory.
            if (_provider == EmbeddingExecutionProvider.Cpu)
            {
                runOptions.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
            }

            using var outputs = _session.Run(runOptions, names, values, [HiddenStateOutput]);

            // Last-token pooling, as the model's pooling config specifies. With no padding, the last
            // token of the sequence is the last real token.
            var hidden = outputs[0].GetTensorDataAsSpan<float>();
            var vector = new float[_dimensions];
            hidden.Slice((length - 1) * _dimensions, _dimensions).CopyTo(vector);
            Normalize(vector);

            return vector;
        }
        finally
        {
            foreach (var value in values)
            {
                value.Dispose();
            }
        }
    }

    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        if (sumOfSquares <= 0)
        {
            return;
        }

        var norm = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return;
            }

            // Set before waiting: callers that arrive from here on take the lock, see the flag and
            // return without touching the session, so the wait below cannot be extended indefinitely.
            _disposed = true;
        }

        if (!_inferenceLock.Wait(DisposeDrainTimeout))
        {
            // Leaking the session costs memory until the process exits. Releasing it while native
            // code is still running on it costs the whole server, so this is the better trade.
            _logger.LogWarning(
                "Embedding inference did not finish within {Timeout} seconds; leaving the model loaded rather than freeing it mid-inference",
                DisposeDrainTimeout.TotalSeconds);
            return;
        }

        try
        {
            _session.Dispose();
            _sessionOptions.Dispose();
        }
        finally
        {
            _inferenceLock.Release();
        }

        _logger.LogDebug("Released embedding model");
    }
}
