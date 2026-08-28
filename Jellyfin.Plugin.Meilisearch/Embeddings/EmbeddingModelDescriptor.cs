using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Describes the embedding model the plugin uses: which files make it up, where they come from and
/// what shape of vector it produces.
/// </summary>
/// <remarks>
/// The model is fixed, not configurable. Everything around it is specialized to Qwen3-Embedding-0.6B:
/// the Qwen byte-level BPE tokenizer and its split pattern, last-token pooling, the 1024-wide output,
/// the 8 grouped-query key/value heads the empty cache tensors are shaped for, and the instruction
/// prefix the query side expects. Pointing this at another repository would not produce a different
/// model, it would produce quietly wrong vectors - right shape, right norm, wrong meaning - or a load
/// failure. A different model is a code change, not a setting.
/// </remarks>
public sealed class EmbeddingModelDescriptor
{
    /// <summary>
    /// The Hugging Face repository the model is fetched from.
    /// </summary>
    public const string Repository = "onnx-community/Qwen3-Embedding-0.6B-ONNX";

    /// <summary>
    /// The ONNX weight file used from that repository.
    /// </summary>
    /// <remarks>
    /// The int8-quantized build: one self-contained 610 MB file, where the fp32 and fp16 exports keep
    /// their weights in a companion <c>.onnx_data</c> file and run several times slower on a CPU for
    /// a difference in retrieval quality that short library metadata does not surface.
    /// </remarks>
    public const string Variant = "model_quantized.onnx";

    /// <summary>
    /// The number of dimensions the model's vectors have. Qwen3-Embedding-0.6B has a hidden size of
    /// 1024 and its pooled sentence embedding inherits that width.
    /// </summary>
    public const int Dimensions = 1024;

    /// <summary>
    /// The name the vector field is registered under in Meilisearch. Changing this orphans the
    /// vectors already stored in the index, so it is deliberately not configurable.
    /// </summary>
    public const string EmbedderName = "qwen3";

    /// <summary>
    /// The instruction prefix Qwen3-Embedding expects on the query side. Documents are embedded
    /// without a prefix; asymmetric retrieval models like this one are trained that way, and
    /// prefixing both sides measurably degrades results.
    /// </summary>
    public const string QueryPrompt =
        "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:";

    /// <summary>
    /// The tokenizer and config files, which are small and always fetched together.
    /// </summary>
    private static readonly string[] _supportFiles = ["vocab.json", "merges.txt", "added_tokens.json"];

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingModelDescriptor"/> class.
    /// </summary>
    /// <param name="directory">The local directory the files are stored in.</param>
    public EmbeddingModelDescriptor(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory = directory;
    }

    /// <summary>
    /// Gets the local directory the model files live in.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the local path of the ONNX weight file.
    /// </summary>
    public string ModelPath => Path.Combine(Directory, Variant);

    /// <summary>
    /// Gets the local path of the BPE vocabulary.
    /// </summary>
    public string VocabPath => Path.Combine(Directory, "vocab.json");

    /// <summary>
    /// Gets the local path of the BPE merge table.
    /// </summary>
    public string MergesPath => Path.Combine(Directory, "merges.txt");

    /// <summary>
    /// Gets the local path of the added-tokens map.
    /// </summary>
    public string AddedTokensPath => Path.Combine(Directory, "added_tokens.json");

    /// <summary>
    /// Gets every file that has to be present locally before the model can be loaded, paired with the
    /// URL it is fetched from.
    /// </summary>
    /// <returns>The required files as (local path, source URL) pairs.</returns>
    public IReadOnlyList<(string LocalPath, Uri Url)> GetRequiredFiles()
    {
        var files = new List<(string, Uri)>(_supportFiles.Length + 1)
        {
            (ModelPath, BuildUrl("onnx/" + Variant))
        };

        files.AddRange(_supportFiles.Select(name => (Path.Combine(Directory, name), BuildUrl(name))));

        return files;
    }

    /// <summary>
    /// Determines whether every required file is present on disk and non-empty.
    /// </summary>
    /// <returns><c>true</c> when the model can be loaded without downloading anything.</returns>
    public bool IsComplete()
        => GetRequiredFiles().All(file =>
        {
            var info = new FileInfo(file.LocalPath);
            return info.Exists && info.Length > 0;
        });

    private static Uri BuildUrl(string path)
        => new(string.Format(
            CultureInfo.InvariantCulture,
            "https://huggingface.co/{0}/resolve/main/{1}",
            Repository,
            path));
}
