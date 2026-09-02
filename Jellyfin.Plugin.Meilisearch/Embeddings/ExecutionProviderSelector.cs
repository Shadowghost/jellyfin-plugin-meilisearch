using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Picks the execution provider a session runs on and registers it.
/// </summary>
internal static class ExecutionProviderSelector
{
    /// <summary>
    /// ONNX Runtime's own name for the CPU provider, which is always present.
    /// </summary>
    internal const string CpuProviderName = "CPUExecutionProvider";

    // Best first. TensorRT and MIGraphX are deliberately absent even though ONNX Runtime can offer
    // them: both compile a kernel per input shape on first use, which for a batch-and-sequence-shaped
    // workload means minutes of stall repeated across shapes. That is a decision with tradeoffs, and
    // nothing here makes decisions with tradeoffs.
    private static readonly EmbeddingExecutionProvider[] Preference =
    [
        EmbeddingExecutionProvider.Cuda,
        EmbeddingExecutionProvider.Rocm,
        EmbeddingExecutionProvider.DirectMl,
        EmbeddingExecutionProvider.CoreMl,
        EmbeddingExecutionProvider.OpenVino
    ];

    private static readonly ImmutableDictionary<EmbeddingExecutionProvider, string> ProviderNames =
        new Dictionary<EmbeddingExecutionProvider, string>
        {
            [EmbeddingExecutionProvider.Cpu] = CpuProviderName,
            [EmbeddingExecutionProvider.Cuda] = "CUDAExecutionProvider",
            [EmbeddingExecutionProvider.Rocm] = "ROCMExecutionProvider",
            [EmbeddingExecutionProvider.DirectMl] = "DmlExecutionProvider",
            [EmbeddingExecutionProvider.CoreMl] = "CoreMLExecutionProvider",
            [EmbeddingExecutionProvider.OpenVino] = "OpenVINOExecutionProvider"
        }.ToImmutableDictionary();

    /// <summary>
    /// Registers the best available provider on a set of session options.
    /// </summary>
    /// <param name="options">The session options to append the provider to.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>
    /// The provider that was registered, or <see cref="EmbeddingExecutionProvider.Cpu"/> when nothing
    /// else was available or registration failed - in which case ONNX Runtime's implicit CPU provider
    /// runs the graph.
    /// </returns>
    public static EmbeddingExecutionProvider Apply(SessionOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Asked rather than assumed. A provider that is not on this list has no entry point to call
        // and appending it throws, so the list is what keeps a GPU attempt from ever being a risk -
        // and on a stock install it holds the CPU alone, since the library the plugin bundles is the
        // CPU build.
        var available = GetAvailableProviders(logger);

        var choice = Preference.FirstOrDefault(
            candidate => available.Contains(ProviderNames[candidate]),
            EmbeddingExecutionProvider.Cpu);

        if (choice == EmbeddingExecutionProvider.Cpu)
        {
            logger.LogInformation(
                "No hardware-accelerated ONNX Runtime execution provider is available; embedding on the CPU. "
                + "Available providers: {Providers}",
                string.Join(", ", available));

            return EmbeddingExecutionProvider.Cpu;
        }

        if (!TryAppend(options, choice, logger))
        {
            return EmbeddingExecutionProvider.Cpu;
        }

        logger.LogInformation(
            "Using the {Provider} execution provider, chosen from {Providers}",
            choice,
            string.Join(", ", available));

        return choice;
    }

    /// <summary>
    /// Lists the execution providers the loaded ONNX Runtime offers.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <returns>The provider names, or just the CPU provider when they cannot be read.</returns>
    public static IReadOnlyCollection<string> GetAvailableProviders(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return OrtEnv.Instance().GetAvailableProviders();
        }
#pragma warning disable CA1031 // Not being able to enumerate providers is no reason to give up on the CPU one.
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enumerate ONNX Runtime execution providers");
            return [CpuProviderName];
        }
#pragma warning restore CA1031
    }

    private static bool TryAppend(SessionOptions options, EmbeddingExecutionProvider provider, ILogger logger)
    {
        // Device 0. Choosing among several GPUs would be a setting, and there are no GPU settings
        // here; the first device is what anything else on a single-GPU box would take anyway.
        const int DeviceId = 0;

        // ONNX Runtime falls back to the CPU provider on its own for any node the appended provider
        // cannot take, so there is nothing to append after this one.
        try
        {
            switch (provider)
            {
                case EmbeddingExecutionProvider.Cuda:
                    options.AppendExecutionProvider_CUDA(DeviceId);
                    break;
                case EmbeddingExecutionProvider.Rocm:
                    options.AppendExecutionProvider_ROCm(DeviceId);
                    break;
                case EmbeddingExecutionProvider.DirectMl:
                    options.AppendExecutionProvider_DML(DeviceId);
                    break;
                case EmbeddingExecutionProvider.CoreMl:
                    options.AppendExecutionProvider_CoreML();
                    break;
                case EmbeddingExecutionProvider.OpenVino:
                    options.AppendExecutionProvider_OpenVINO();
                    break;
                default:
                    return false;
            }

            return true;
        }
#pragma warning disable CA1031 // A provider that will not initialize degrades to the CPU, not to no search.
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "The {Provider} execution provider is present but would not initialize; embedding on the CPU "
                + "instead. Its runtime dependencies are the usual cause - CUDA for CUDA, the ROCm stack for "
                + "ROCm - and they have to match the versions the ONNX Runtime build expects",
                provider);

            return false;
        }
#pragma warning restore CA1031
    }
}
