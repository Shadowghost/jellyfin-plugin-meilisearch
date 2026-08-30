using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Meilisearch.Embeddings.Qwen;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// The embedding models this plugin can run.
/// </summary>
public static class EmbeddingModels
{
    /// <summary>
    /// The identifier used when the configuration names no model, or names one this build does not
    /// have. It is also the model every installation predating model selection was running.
    /// </summary>
    public const string DefaultId = QwenEmbeddingModel.Id;

    /// <summary>
    /// Gets every available model, in the order the settings page lists them.
    /// </summary>
    public static IReadOnlyList<EmbeddingModelDefinition> All { get; } = [QwenEmbeddingModel.Definition];

    /// <summary>
    /// Gets the model used when none is configured.
    /// </summary>
    public static EmbeddingModelDefinition Default { get; } = QwenEmbeddingModel.Definition;

    /// <summary>
    /// Resolves a configured identifier to a model.
    /// </summary>
    /// <param name="id">The configured identifier, which may be empty or unknown.</param>
    /// <returns>The matching model, or <see cref="Default"/> when there is none.</returns>
    public static EmbeddingModelDefinition Resolve(string? id)
        => TryResolve(id, out var definition) ? definition : Default;

    /// <summary>
    /// Looks up a model by identifier.
    /// </summary>
    /// <param name="id">The identifier to look up.</param>
    /// <param name="definition">The matching model, or <see cref="Default"/> when there is none.</param>
    /// <returns><c>true</c> when the identifier named a model this build has.</returns>
    public static bool TryResolve(string? id, out EmbeddingModelDefinition definition)
    {
        definition = Default;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var match = All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        definition = match;
        return true;
    }
}
