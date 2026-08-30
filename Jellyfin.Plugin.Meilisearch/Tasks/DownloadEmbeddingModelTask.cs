using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Meilisearch.Embeddings;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Scheduled task that downloads the local embedding model and loads it.
/// </summary>
public class DownloadEmbeddingModelTask : IScheduledTask
{
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<DownloadEmbeddingModelTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadEmbeddingModelTask"/> class.
    /// </summary>
    /// <param name="embeddings">The embedding service.</param>
    /// <param name="logger">The logger.</param>
    public DownloadEmbeddingModelTask(EmbeddingService embeddings, ILogger<DownloadEmbeddingModelTask> logger)
    {
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Download Meilisearch Embedding Model";

    /// <inheritdoc />
    public string Key => "MeilisearchDownloadEmbeddingModel";

    /// <inheritdoc />
    public string Description =>
        "Downloads the embedding model selected in the plugin settings and loads it into memory. "
        + "Requires several hundred megabytes of disk space.";

    /// <inheritdoc />
    public string Category => "Search";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Manual only: this downloads hundreds of megabytes and should never start on its own
        // schedule. The plugin fetches the model on its own when semantic search is switched on and
        // automatic download is left enabled.
        yield break;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!_embeddings.IsEnabled)
        {
            _logger.LogWarning(
                "Semantic search is disabled, so there is nothing to download. Enable it in the plugin settings first");
            progress.Report(100);
            return;
        }

        var descriptor = _embeddings.CreateDescriptor();
        _logger.LogInformation(
            "Downloading embedding model {Model} from {Repository} into {Directory}",
            descriptor.Definition.DisplayName,
            descriptor.Definition.Repository,
            descriptor.Directory);

        var downloader = new EmbeddingModelDownloader(_logger);
        await downloader.DownloadAsync(descriptor, progress, cancellationToken).ConfigureAwait(false);

        // Load straight away so the config page reports Ready and searches start using vectors
        // without waiting for a restart.
        if (await _embeddings.EnsureReadyAsync(null, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Embedding model ready. Run 'Rebuild Meilisearch Index' to embed the existing library");
        }

        progress.Report(100);
    }
}
