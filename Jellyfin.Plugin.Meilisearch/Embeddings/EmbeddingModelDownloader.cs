using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// Fetches the embedding model's files from Hugging Face into the local model directory.
/// </summary>
public sealed class EmbeddingModelDownloader
{
    private const int BufferSize = 128 * 1024;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingModelDownloader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public EmbeddingModelDownloader(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Downloads every file the model needs that is not already present.
    /// </summary>
    /// <param name="descriptor">The model to fetch.</param>
    /// <param name="progress">Receives overall progress in the range 0-100, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task DownloadAsync(
        EmbeddingModelDescriptor descriptor,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Directory.CreateDirectory(descriptor.Directory);

        var files = descriptor.GetRequiredFiles();

        // The weight file dwarfs the tokenizer files, so weighting every file equally would make
        // progress jump to near-complete and then sit there. Weight by the sizes the server reports.
        var sizes = new long[files.Count];
        var pending = new bool[files.Count];
        long totalBytes = 0;

        using var client = CreateClient();

        for (var i = 0; i < files.Count; i++)
        {
            var (localPath, url) = files[i];
            var info = new FileInfo(localPath);
            if (info.Exists && info.Length > 0)
            {
                continue;
            }

            pending[i] = true;
            sizes[i] = await GetContentLengthAsync(client, url, cancellationToken).ConfigureAwait(false);
            totalBytes += sizes[i];
        }

        if (Array.TrueForAll(pending, static p => !p))
        {
            _logger.LogInformation("Embedding model already present in {Directory}", descriptor.Directory);
            progress?.Report(100);
            return;
        }

        _logger.LogInformation(
            "Downloading embedding model {Repository} ({Variant}) to {Directory}; {TotalMegabytes} MB to fetch",
            EmbeddingModelDescriptor.Repository,
            EmbeddingModelDescriptor.Variant,
            descriptor.Directory,
            (totalBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));

        long completedBytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (!pending[i])
            {
                continue;
            }

            var (localPath, url) = files[i];
            var fileBytes = sizes[i];
            var alreadyDone = completedBytes;

            await DownloadFileAsync(
                client,
                url,
                localPath,
                bytesSoFar =>
                {
                    if (totalBytes > 0)
                    {
                        progress?.Report(Math.Min(100d, (alreadyDone + bytesSoFar) * 100d / totalBytes));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            completedBytes += fileBytes;
        }

        progress?.Report(100);
        _logger.LogInformation("Embedding model download complete");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // A few hundred megabytes over a slow link still has to finish.
            Timeout = TimeSpan.FromHours(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Plugin-Meilisearch");
        return client;
    }

    private static async Task<long> GetContentLengthAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Hugging Face serves LFS objects through a redirect that reports the real length; a missing
        // length is not fatal, it only makes the progress estimate coarser.
        return response.Content.Headers.ContentLength ?? 0;
    }

    private async Task DownloadFileAsync(
        HttpClient client,
        Uri url,
        string localPath,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        // Download to a temporary name and move into place, so an interrupted download never leaves a
        // truncated file that IsComplete() would accept.
        var tempPath = localPath + ".partial";

        _logger.LogInformation("Fetching {FileName}", Path.GetFileName(localPath));

        try
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                    await using (destination.ConfigureAwait(false))
                    {
                        var buffer = new byte[BufferSize];
                        long written = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            written += read;
                            onProgress(written);
                        }
                    }
                }
            }

            File.Move(tempPath, localPath, true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
#pragma warning disable CA1031 // Cleaning up after a failed download must not mask the original error.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove partial download {Path}", path);
        }
#pragma warning restore CA1031
    }
}
