using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Everything the plugin needs to know about one embedding model: where its files come from, what
/// shape of vector it produces, and how to load it.
/// </summary>
/// <remarks>
/// A definition is not a way to point the plugin at an arbitrary repository. Tokenization, the graph
/// inputs and the pooling are specific to a model family and live in an <see cref="ITextEmbedder"/>
/// implementation, so a definition only ever describes a model this plugin has code for - which is
/// why <see cref="EmbeddingModels"/> is a fixed list rather than user input. Pointing an existing
/// definition at a different repository would not produce a different model, it would produce
/// quietly wrong vectors - right shape, right norm, wrong meaning - or a load failure.
/// </remarks>
/// <param name="Id">
/// The stable identifier stored in the configuration. It also names the model's directory on disk
/// and scopes its vector cache, so it must not change once released.
/// </param>
/// <param name="DisplayName">The name shown in the plugin settings.</param>
/// <param name="Repository">The Hugging Face repository the files are fetched from.</param>
/// <param name="ModelFile">The repository-relative path of the ONNX weight file.</param>
/// <param name="SupportFiles">
/// The repository-relative paths of the tokenizer and config files, which are small and always
/// fetched together.
/// </param>
/// <param name="Dimensions">The width of the vectors the model produces.</param>
/// <param name="EmbedderName">
/// The name the vector field is registered under in Meilisearch. Distinct per model: two models
/// produce incompatible vectors of possibly different widths, and sharing a name would leave the
/// index holding a mixture of both.
/// </param>
/// <param name="QueryPrompt">
/// The instruction prefix the model expects on the query side, or empty for a symmetric model that
/// wants none. Documents are always embedded without a prefix.
/// </param>
/// <param name="ApproximateDownloadMegabytes">
/// Rough download size, shown in the settings so the cost is visible before the switch is flipped.
/// </param>
/// <param name="CreateEmbedder">
/// Loads the model from a local directory. Takes the descriptor, the configured inference thread
/// count (zero for automatic) and a logger.
/// </param>
public sealed record EmbeddingModelDefinition(
    string Id,
    string DisplayName,
    string Repository,
    string ModelFile,
    IReadOnlyList<string> SupportFiles,
    int Dimensions,
    string EmbedderName,
    string QueryPrompt,
    int ApproximateDownloadMegabytes,
    Func<EmbeddingModelDescriptor, int, ILogger, ITextEmbedder> CreateEmbedder)
{
    /// <summary>
    /// How this plugin version turns text into a vector, independent of which model does it.
    /// </summary>
    /// <remarks>
    /// Bumped when the procedure changes such that older vectors are no longer comparable with new
    /// ones from the same model file. Revision 2 stopped batching and padding, which moved the
    /// quantized model's activation scales. Carried in <see cref="Fingerprint"/> and
    /// <see cref="IndexIdentity"/> so the cache and the built index are both treated as stale.
    /// </remarks>
    public const int EmbeddingRevision = 2;

    /// <summary>
    /// Gets every repository-relative file the model is made of, weights first.
    /// </summary>
    public IEnumerable<string> Files => SupportFiles.Prepend(ModelFile);

    /// <summary>
    /// Gets the model's identity as stored in the vector cache header, so a cache written by a
    /// different model - or by a different export or embedding procedure - is never read back as
    /// this one's.
    /// </summary>
    public string Fingerprint
        => Repository + "/" + Path.GetFileName(ModelFile) + "@r"
            + EmbeddingRevision.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets what gets recorded against a built index, so that a rebuild is asked for when either the
    /// model or the way it is used changes.
    /// </summary>
    public string IndexIdentity => Id + "@r" + EmbeddingRevision.ToString(CultureInfo.InvariantCulture);
}
