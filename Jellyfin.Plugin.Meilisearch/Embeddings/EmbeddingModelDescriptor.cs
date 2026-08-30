using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// One embedding model together with the local directory its files live in.
/// </summary>
public sealed class EmbeddingModelDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingModelDescriptor"/> class.
    /// </summary>
    /// <param name="definition">The model being described.</param>
    /// <param name="directory">The local directory its files are stored in.</param>
    public EmbeddingModelDescriptor(EmbeddingModelDefinition definition, string directory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Definition = definition;
        Directory = directory;
    }

    /// <summary>
    /// Gets the model being described.
    /// </summary>
    public EmbeddingModelDefinition Definition { get; }

    /// <summary>
    /// Gets the local directory the model files live in.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the local path of the ONNX weight file.
    /// </summary>
    public string ModelPath => GetFilePath(Definition.ModelFile);

    /// <summary>
    /// Resolves where one of the model's files is stored locally.
    /// </summary>
    /// <param name="repositoryPath">The repository-relative path, as named by the definition.</param>
    /// <returns>The local path of that file.</returns>
    public string GetFilePath(string repositoryPath)
        => Path.Combine(Directory, Path.GetFileName(repositoryPath));

    /// <summary>
    /// Gets every file that has to be present locally before the model can be loaded, paired with the
    /// URL it is fetched from.
    /// </summary>
    /// <returns>The required files as (local path, source URL) pairs.</returns>
    public IReadOnlyList<(string LocalPath, Uri Url)> GetRequiredFiles()
        => [.. Definition.Files.Select(file => (GetFilePath(file), BuildUrl(file)))];

    /// <summary>
    /// Determines whether every required file is present on disk and non-empty.
    /// </summary>
    /// <returns><c>true</c> when the model can be loaded without downloading anything.</returns>
    public bool IsComplete()
        => GetRequiredFiles().All(file => IsPresent(file.LocalPath));

    internal static bool IsPresent(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    private Uri BuildUrl(string repositoryPath)
        => new(string.Format(
            CultureInfo.InvariantCulture,
            "https://huggingface.co/{0}/resolve/main/{1}",
            Definition.Repository,
            repositoryPath));
}
