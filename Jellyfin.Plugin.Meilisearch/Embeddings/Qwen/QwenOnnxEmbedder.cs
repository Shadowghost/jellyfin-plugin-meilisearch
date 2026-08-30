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

    // How long Dispose waits for a forward pass that is already running. Inference is bounded by the
    // token budget and batch size, so anything beyond this means the call is wedged rather than slow.
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly QwenEmbeddingTokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly InferenceSession _session;
    private readonly SessionOptions _sessionOptions;
    private readonly string[] _pastKeyValueNames;
    private readonly TensorElementType _pastKeyValueType;
#pragma warning disable CA2213 // Deliberately not disposed; see the remarks on Dispose.
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
#pragma warning restore CA2213
    private readonly object _disposeGate = new();
    private volatile bool _disposed;

    private QwenOnnxEmbedder(
        ILogger logger,
        QwenEmbeddingTokenizer tokenizer,
        int dimensions,
        InferenceSession session,
        SessionOptions sessionOptions)
    {
        _logger = logger;
        _tokenizer = tokenizer;
        _dimensions = dimensions;
        _session = session;
        _sessionOptions = sessionOptions;

        _pastKeyValueNames = [.. session.InputMetadata.Keys
            .Where(static name => name.StartsWith(PastKeyValuePrefix, StringComparison.Ordinal))];

        // The element type differs per exported variant - float32 for the quantized builds, float16
        // for the fp16 build - so it is read from the graph rather than assumed.
        _pastKeyValueType = _pastKeyValueNames.Length > 0
            ? session.InputMetadata[_pastKeyValueNames[0]].ElementDataType
            : TensorElementType.Float;
    }

    /// <summary>
    /// Loads the model and tokenizer from disk.
    /// </summary>
    /// <param name="descriptor">The model to load.</param>
    /// <param name="threads">Number of inference threads, or zero to pick a default.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The loaded embedder.</returns>
    public static QwenOnnxEmbedder Load(EmbeddingModelDescriptor descriptor, int threads, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(logger);

        // Must happen before the first ONNX Runtime P/Invoke, i.e. before InferenceSession below.
        OnnxRuntimeNativeLoader.EnsureRegistered(logger);

        var tokenizer = QwenEmbeddingTokenizer.Load(descriptor);

        // Default to half the processors: inference is CPU-bound and Jellyfin still has to serve
        // playback and transcodes while a reindex is embedding the library.
        var intraOpThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);

        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

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

        logger.LogInformation(
            "Loaded embedding model {Model} ({Dimensions} dimensions, {Threads} threads)",
            descriptor.Definition.DisplayName,
            descriptor.Definition.Dimensions,
            intraOpThreads.ToString(CultureInfo.InvariantCulture));

        return new QwenOnnxEmbedder(logger, tokenizer, descriptor.Definition.Dimensions, session, sessionOptions);
    }

    /// <summary>
    /// Embeds a batch of texts in a single forward pass.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="maxTokens">Maximum tokens to keep per text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One vector per input, in the same order. An entry is null when the corresponding text was
    /// empty and produced no tokens to pool, or when the model was released mid-call.
    /// </returns>
    public IReadOnlyList<float[]?> Embed(IReadOnlyList<string> texts, int maxTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0 || _disposed)
        {
            return new float[texts.Count][];
        }

        var rows = new long[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            rows[i] = _tokenizer.Encode(texts[i], maxTokens);
        }

        var results = new float[texts.Count][];
        var populated = rows.Select(static (row, index) => (row, index))
            .Where(static entry => entry.row.Length > 0)
            .ToList();

        if (populated.Count == 0)
        {
            return results;
        }

        // ONNX Runtime sessions are thread-safe, but concurrent Run calls each spin up their own
        // intra-op work; serializing keeps CPU use bounded to the configured thread count. The same
        // lock is what makes disposal safe - see Dispose.
        _inferenceLock.Wait(cancellationToken);
        try
        {
            // Re-checked while holding the lock. Dispose sets the flag and then takes this lock, so
            // once we are past this point the native session cannot be released underneath us.
            if (_disposed)
            {
                return results;
            }

            var batchRows = populated.Select(static entry => entry.row).ToList();
            var vectors = RunBatch(batchRows, cancellationToken);

            for (var i = 0; i < populated.Count; i++)
            {
                results[populated[i].index] = vectors[i];
            }
        }
        finally
        {
            _inferenceLock.Release();
        }

        return results;
    }

    private float[][] RunBatch(IReadOnlyList<long[]> rows, CancellationToken cancellationToken)
    {
        var batch = rows.Count;
        var maxLength = rows.Max(static row => row.Length);

        var inputIds = new long[batch * maxLength];
        var attentionMask = new long[batch * maxLength];
        var positionIds = new long[batch * maxLength];

        for (var b = 0; b < batch; b++)
        {
            var row = rows[b];
            for (var i = 0; i < maxLength; i++)
            {
                var offset = (b * maxLength) + i;
                positionIds[offset] = i;

                if (i < row.Length)
                {
                    inputIds[offset] = row[i];
                    attentionMask[offset] = 1;
                }
            }
        }

        long[] shape = [batch, maxLength];
        long[] emptyCacheShape = [batch, KeyValueHeads, 0, HeadDimensions];

        var names = new List<string>(3 + _pastKeyValueNames.Length);
        var values = new List<OrtValue>(names.Capacity);

        try
        {
            names.Add("input_ids");
            values.Add(OrtValue.CreateTensorValueFromMemory(inputIds, shape));

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
            using var outputs = _session.Run(runOptions, names, values, [HiddenStateOutput]);

            var hidden = outputs[0].GetTensorDataAsSpan<float>();
            var vectors = new float[batch][];

            for (var b = 0; b < batch; b++)
            {
                // Last-token pooling, as the model's pooling config specifies. Padding sits to the
                // right of the real tokens and attention here is causal, so the hidden state at the
                // final real token is identical to what it would be with no padding at all.
                var lastRealToken = rows[b].Length - 1;
                var start = ((b * maxLength) + lastRealToken) * _dimensions;

                var vector = new float[_dimensions];
                hidden.Slice(start, _dimensions).CopyTo(vector);
                Normalize(vector);

                vectors[b] = vector;
            }

            return vectors;
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
