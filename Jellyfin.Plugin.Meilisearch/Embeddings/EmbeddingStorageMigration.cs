using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Moves the model files and the vector cache written by versions that only ever ran one model into
/// the per-model layout.
/// </summary>
internal static class EmbeddingStorageMigration
{
    /// <summary>
    /// Moves a flat-layout model download into its per-model directory.
    /// </summary>
    /// <param name="descriptor">The model in its new location.</param>
    /// <param name="rootDirectory">The directory the files used to live in.</param>
    /// <param name="logger">The logger.</param>
    public static void MigrateModelFiles(EmbeddingModelDescriptor descriptor, string rootDirectory, ILogger logger)
    {
        if (!string.Equals(descriptor.Definition.Id, EmbeddingModels.DefaultId, StringComparison.Ordinal)
            || descriptor.IsComplete())
        {
            return;
        }

        var moves = descriptor.GetRequiredFiles()
            .Select(file => (Legacy: Path.Combine(rootDirectory, Path.GetFileName(file.LocalPath)), Target: file.LocalPath))
            .ToList();

        // All or nothing: a partial set in the root is a half-finished download from before the
        // upgrade, and moving it would only produce a half-finished download in the new place.
        if (!moves.TrueForAll(move => EmbeddingModelDescriptor.IsPresent(move.Legacy)))
        {
            return;
        }

        Move(moves, "embedding model", rootDirectory, descriptor.Directory, logger);
    }

    /// <summary>
    /// Moves a flat-layout vector cache into its per-model directory.
    /// </summary>
    /// <param name="modelId">The model the cache was written by.</param>
    /// <param name="rootDirectory">The directory the cache used to live in.</param>
    /// <param name="cacheDirectory">The per-model directory it now lives in.</param>
    /// <param name="logger">The logger.</param>
    public static void MigrateVectorCache(string modelId, string rootDirectory, string cacheDirectory, ILogger logger)
    {
        if (!string.Equals(modelId, EmbeddingModels.DefaultId, StringComparison.Ordinal)
            || File.Exists(Path.Combine(cacheDirectory, EmbeddingCache.KeysFileName)))
        {
            return;
        }

        var moves = new[] { EmbeddingCache.KeysFileName, EmbeddingCache.VectorsFileName }
            .Select(name => (Legacy: Path.Combine(rootDirectory, name), Target: Path.Combine(cacheDirectory, name)))
            .ToList();

        if (!moves.TrueForAll(move => File.Exists(move.Legacy)))
        {
            return;
        }

        Move(moves, "vector cache", rootDirectory, cacheDirectory, logger);
    }

    private static void Move(
        List<(string Legacy, string Target)> moves,
        string what,
        string from,
        string to,
        ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(to);

            foreach (var (legacy, target) in moves)
            {
                File.Move(legacy, target, true);
            }

            logger.LogInformation(
                "Moved the {What} from {From} into the per-model directory {To}",
                what,
                from,
                to);
        }
#pragma warning disable CA1031 // A failed move must cost a re-download, not the plugin.
        catch (Exception ex)
        {
            // Whatever was already moved stays where it landed; the caller then finds an incomplete
            // model or an empty cache and rebuilds it, which is slow but correct.
            logger.LogWarning(ex, "Could not move the {What} into {To}; it will be recreated there", what, to);
        }
#pragma warning restore CA1031
    }
}
