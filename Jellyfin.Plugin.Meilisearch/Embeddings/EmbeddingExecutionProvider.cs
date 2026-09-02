namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Where embedding inference actually runs.
/// </summary>
public enum EmbeddingExecutionProvider
{
    /// <summary>
    /// The CPU, which every ONNX Runtime build offers and which is the fallback for all the rest.
    /// </summary>
    Cpu,

    /// <summary>
    /// NVIDIA CUDA.
    /// </summary>
    Cuda,

    /// <summary>
    /// AMD ROCm.
    /// </summary>
    Rocm,

    /// <summary>
    /// DirectML, which covers any Direct3D 12 GPU on Windows.
    /// </summary>
    DirectMl,

    /// <summary>
    /// Apple CoreML, which uses the Neural Engine or the GPU on Apple silicon.
    /// </summary>
    CoreMl,

    /// <summary>
    /// Intel OpenVINO, which covers Intel GPUs and NPUs.
    /// </summary>
    OpenVino
}
