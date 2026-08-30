namespace Jellyfin.Plugin.Meilisearch.Embeddings.Qwen;

/// <summary>
/// The Qwen3-Embedding-0.6B model as the plugin runs it.
/// </summary>
public static class QwenEmbeddingModel
{
    /// <summary>
    /// The identifier stored in the configuration. It also names the model's directory on disk, so it
    /// must not change.
    /// </summary>
    public const string Id = "qwen3-embedding-0.6b";

    /// <summary>
    /// The number of dimensions the model's vectors have. Qwen3-Embedding-0.6B has a hidden size of
    /// 1024 and its pooled sentence embedding inherits that width.
    /// </summary>
    public const int Dimensions = 1024;

    /// <summary>
    /// The BPE vocabulary file.
    /// </summary>
    public const string VocabFile = "vocab.json";

    /// <summary>
    /// The BPE merge table.
    /// </summary>
    public const string MergesFile = "merges.txt";

    /// <summary>
    /// The added-tokens map.
    /// </summary>
    public const string AddedTokensFile = "added_tokens.json";

    /// <summary>
    /// Gets the model definition.
    /// </summary>
    public static EmbeddingModelDefinition Definition { get; } = new(
        Id: Id,
        DisplayName: "Qwen3-Embedding-0.6B (local)",
        Repository: "onnx-community/Qwen3-Embedding-0.6B-ONNX",
        ModelFile: "onnx/model_quantized.onnx",
        SupportFiles: [VocabFile, MergesFile, AddedTokensFile],
        Dimensions: Dimensions,
        EmbedderName: "qwen3",

        // Asymmetric retrieval models like this one are trained with an instruction on the query side
        // only; prefixing both sides measurably degrades results.
        QueryPrompt: "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:",
        ApproximateDownloadMegabytes: 610,
        CreateEmbedder: QwenOnnxEmbedder.Load);
}
