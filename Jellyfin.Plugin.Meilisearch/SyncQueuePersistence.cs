using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Persists pending real-time sync operations to disk so they survive plugin restarts.
/// </summary>
internal sealed class SyncQueuePersistence : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueuePersistence"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths used to locate the plugin configuration directory.</param>
    /// <param name="logger">The logger.</param>
    public SyncQueuePersistence(IApplicationPaths applicationPaths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "Meilisearch.queue.json");
        _logger = logger;
    }

    /// <summary>
    /// Loads any previously persisted sync operations from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted operations, or an empty list if none.</returns>
    public async Task<IReadOnlyList<PersistedSyncOp>> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            try
            {
                using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var ops = await JsonSerializer
                    .DeserializeAsync<List<PersistedSyncOp>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (ops is null)
                {
                    _logger.LogWarning("Meilisearch sync queue file at {Path} was empty or null", _filePath);
                    return [];
                }

                _logger.LogInformation("Loaded {Count} persisted Meilisearch sync operations from {Path}", ops.Count, _filePath);
                return ops;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Meilisearch sync queue file at {Path}; ignoring", _filePath);
                return [];
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read Meilisearch sync queue file at {Path}; ignoring", _filePath);
                return [];
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Persists the supplied sync operations to disk, overwriting any existing file.
    /// </summary>
    /// <param name="ops">The operations to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SaveAsync(IEnumerable<PersistedSyncOp> ops, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var list = new List<PersistedSyncOp>(ops);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (list.Count == 0)
            {
                // Nothing to persist; delete any existing file so we don't replay stale ops.
                if (File.Exists(_filePath))
                {
                    try
                    {
                        File.Delete(_filePath);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove empty Meilisearch sync queue file at {Path}", _filePath);
                    }
                }

                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(
                    _filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);

                await JsonSerializer
                    .SerializeAsync(stream, list, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("Saved {Count} pending Meilisearch sync operations to {Path}", list.Count, _filePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to persist Meilisearch sync queue to {Path}", _filePath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Removes any persisted sync queue file. Safe to call when no file exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the clear operation.</returns>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                File.Delete(_filePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete Meilisearch sync queue file at {Path}", _filePath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fileLock.Dispose();
    }
}
