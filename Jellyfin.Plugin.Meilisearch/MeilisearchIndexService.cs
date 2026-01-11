using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Meilisearch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Hosted service that keeps the Meilisearch index synchronized with library changes.
/// Real-time sync events are coalesced through a bounded channel and flushed in batches.
/// </summary>
public class MeilisearchIndexService : IHostedService, IDisposable
{
    // Bounded but very large; on overflow we drop the oldest pending op. A full reindex
    // will re-cover anything we lose during a runaway scan, so capping memory is preferable.
    private const int ChannelCapacity = 100_000;

    // Cap the number of people names we serialize per document to keep index size bounded.

    private readonly ILibraryManager _libraryManager;
    private readonly MeilisearchClientWrapper _client;
    private readonly ILogger<MeilisearchIndexService> _logger;
    private readonly SyncQueuePersistence _persistence;

    private readonly Channel<SyncOp> _channel = Channel.CreateBounded<SyncOp>(new BoundedChannelOptions(ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    // Signalled when the worker is currently idle (no in-flight batch and queue empty).
    private readonly ManualResetEventSlim _idleEvent = new(true);

    // Used by FlushAsync to force-flush any pending operations on demand.
    private readonly SemaphoreSlim _flushSignal = new(0);

    // Stops new writes while paused; coordinates pause/resume.
    private readonly SemaphoreSlim _pauseLock = new(1, 1);

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private int _paused;
    private List<PersistedSyncOp>? _leftoverOps;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeilisearchIndexService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="client">The Meilisearch client wrapper.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="applicationPaths">The application paths used to locate the sync queue persistence file.</param>
    public MeilisearchIndexService(
        ILibraryManager libraryManager,
        MeilisearchClientWrapper client,
        ILogger<MeilisearchIndexService> logger,
        IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
        _persistence = new SyncQueuePersistence(applicationPaths, logger);
    }

    /// <summary>
    /// Identifies whether a queued sync operation is an upsert or a remove.
    /// </summary>
    private enum SyncOpKind
    {
        /// <summary>
        /// The document should be (re)indexed.
        /// </summary>
        Upsert,

        /// <summary>
        /// The document should be removed from the index.
        /// </summary>
        Remove
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunWorkerAsync(_workerCts.Token), CancellationToken.None);

        await RestorePersistedOpsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Meilisearch index service started");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        // Stop accepting new ops and signal the worker to drain.
        _channel.Writer.TryComplete();

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Server is shutting down impatiently; we'll persist whatever remains below.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Meilisearch sync worker terminated with an error");
            }
        }

        if (_workerCts is not null)
        {
            await _workerCts.CancelAsync().ConfigureAwait(false);
            _workerCts.Dispose();
            _workerCts = null;
        }

        await PersistRemainingOpsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Meilisearch index service stopped");
    }

    /// <summary>
    /// Pauses real-time sync. New library change events will be dropped while paused
    /// (a subsequent reindex is expected to cover the gap). Any in-flight batch is
    /// flushed and the worker is left idle before this method returns.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the queue is drained and the worker is idle.</returns>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _pauseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _paused, 1);
        }
        finally
        {
            _pauseLock.Release();
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes real-time sync after a previous <see cref="PauseAsync"/> call.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the paused flag has been cleared.</returns>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _pauseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _paused, 0);
        }
        finally
        {
            _pauseLock.Release();
        }
    }

    /// <summary>
    /// Forces the worker to flush any queued operations now and awaits completion.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the queue is drained.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // Wake the worker so it stops waiting for the debounce window.
        try
        {
            _flushSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending; that's fine.
        }

        // Wait until the worker reports idle. Poll on the wait handle with the user's token.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_idleEvent.Wait(50, cancellationToken))
            {
                // Re-check the channel to make sure no last-millisecond writer slipped a new op in.
                if (_channel.Reader.Count == 0)
                {
                    return;
                }

                // New ops arrived while we were waking up; loop and let the worker process them.
                try
                {
                    _flushSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already pending.
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Determines whether an item should be indexed.
    /// Only index item types that would be returned by the standard SQL search.
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item should be indexed.</returns>
    public static bool ShouldIndexItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Skip virtual items.
        if (item.IsVirtualItem)
        {
            return false;
        }

        // Skip folders that aren't meaningful content.
        if (item is Folder && item is not MusicAlbum && item is not Series)
        {
            return false;
        }

        // Only index specific item types that are meaningful search results.
        // Season is excluded as users typically search for Series, not individual seasons.
        return item.GetBaseItemKind() switch
        {
            BaseItemKind.Movie => true,
            BaseItemKind.Episode => true,
            BaseItemKind.Series => true,
            BaseItemKind.Audio => true,
            BaseItemKind.MusicAlbum => true,
            BaseItemKind.MusicArtist => true,
            BaseItemKind.MusicVideo => true,
            BaseItemKind.Book => true,
            BaseItemKind.AudioBook => true,
            BaseItemKind.BoxSet => true,
            BaseItemKind.Person => true,
            BaseItemKind.Trailer => true,
            BaseItemKind.LiveTvChannel => true,
            BaseItemKind.LiveTvProgram => true,
            BaseItemKind.Playlist => true,
            BaseItemKind.PlaylistsFolder => false,
            BaseItemKind.Genre => true,
            BaseItemKind.MusicGenre => true,
            BaseItemKind.Studio => true,
            BaseItemKind.Video => item.ExtraType.HasValue,
            _ => false
        };
    }

    /// <summary>
    /// Creates a Meilisearch document from a library item.
    /// </summary>
    /// <param name="item">The item to create a document for.</param>
    /// <param name="libraryManager">Optional library manager used to populate the <c>People</c> field via per-item lookup. When null and no <paramref name="peopleLookup"/> is supplied, people are not populated.</param>
    /// <param name="peopleLookup">Optional pre-fetched people lookup keyed by item id. When provided this is preferred over <paramref name="libraryManager"/> and avoids the per-item DB roundtrip.</param>
    /// <returns>The Meilisearch document.</returns>
    public static MeilisearchDocument CreateDocument(
        BaseItem item,
        ILibraryManager? libraryManager = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? peopleLookup = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var itemKind = item.GetBaseItemKind();

        // Extras need to be handled manually.
        var typeRank = GetTypeRank(itemKind);
        if (itemKind == BaseItemKind.Video && item.ExtraType.HasValue)
        {
            typeRank = GetTypeRank(item.ExtraType.Value);
        }

        var document = new MeilisearchDocument
        {
            // Basic identification.
            Id = item.Id.ToString("N"),
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            SortName = item.SortName,
            ItemType = itemKind.ToString(),
            TypeRank = typeRank,

            // Descriptions.
            Overview = item.Overview,
            Tagline = item.Tagline,

            // Dates and duration.
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate?.ToUniversalTime().Ticks,
            RunTimeTicks = item.RunTimeTicks,

            // Ratings.
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,

            // Categories.
            Genres = item.Genres,
            Tags = item.Tags,
            Studios = item.Studios,
            ProductionLocations = item.ProductionLocations,

            // Hierarchy.
            ParentId = item.ParentId != Guid.Empty ? item.ParentId.ToString("N") : null,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,

            // Technical.
            Container = item.Container,

            // External IDs.
            ProviderIds = item.ProviderIds?.Count > 0 ? item.ProviderIds : null,
            // Top parent (library id) for per-library scoping. GetTopParent can throw if the
            // library context isn't ready, so guard it defensively.
            TopParentId = TryGetTopParentId(item)
        };

        // Add episode-specific info.
        if (item is Episode episode)
        {
            document.SeriesName = episode.SeriesName;
            document.SeriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId.ToString("N") : null;
            document.SeasonName = episode.SeasonName;
            document.SeasonId = episode.SeasonId != Guid.Empty ? episode.SeasonId.ToString("N") : null;
        }

        // Add audio-specific info.
        if (item is Audio audio)
        {
            document.AlbumName = audio.Album;
            document.AlbumId = audio.AlbumEntity?.Id.ToString("N");
            document.Artists = audio.Artists?.Count > 0 ? audio.Artists : null;
            document.AlbumArtists = audio.AlbumArtists?.Count > 0 ? audio.AlbumArtists : null;
        }

        // Add music album info. Prefer the full AlbumArtists collection (B10).
        if (item is MusicAlbum album)
        {
            if (album.AlbumArtists is { Count: > 0 } albumArtists)
            {
                document.AlbumArtists = albumArtists;
            }
            else if (!string.IsNullOrEmpty(album.AlbumArtist))
            {
                document.AlbumArtists = new[] { album.AlbumArtist };
            }

            document.Artists = album.Artists is { Count: > 0 } ? album.Artists : null;
        }

        // Populate people names (actor/director search). Prefer the pre-fetched batch lookup
        // (one DB query per batch) over the per-item GetPeople fallback (N+1).
        if (item.SupportsPeople)
        {
            if (peopleLookup is not null)
            {
                if (peopleLookup.TryGetValue(item.Id, out var names) && names.Count > 0)
                {
                    document.People = names;
                }
            }
            else if (libraryManager is not null)
            {
                document.People = TryGetPeopleNames(libraryManager, item);
            }
        }

        return document;
    }

    /// <summary>
    /// Gets the type rank for custom ranking (higher = more important).
    /// </summary>
    /// <param name="itemKind">The item kind.</param>
    /// <returns>The rank value for the item type.</returns>
    internal static int GetTypeRank(BaseItemKind itemKind)
    {
        return itemKind switch
        {
            BaseItemKind.Movie => 100,
            BaseItemKind.Series => 100,
            BaseItemKind.MusicArtist => 100,
            BaseItemKind.MusicAlbum => 100,
            BaseItemKind.PhotoAlbum => 100,

            BaseItemKind.Episode => 90,
            BaseItemKind.BoxSet => 90,
            BaseItemKind.Playlist => 90,

            BaseItemKind.Book => 60,
            BaseItemKind.AudioBook => 60,
            BaseItemKind.MusicVideo => 60,

            BaseItemKind.Genre => 50,
            BaseItemKind.MusicGenre => 50,
            BaseItemKind.LiveTvChannel => 50,
            BaseItemKind.LiveTvProgram => 50,

            BaseItemKind.Studio => 30,
            BaseItemKind.Person => 30,

            BaseItemKind.Trailer => 20,

            BaseItemKind.Audio => 10,
            BaseItemKind.Video => 10,
            _ => 0
        };
    }

    /// <summary>
    /// Gets the type rank for custom ranking (higher = more important).
    /// </summary>
    /// <param name="extraType">The extra type.</param>
    /// <returns>The rank value for the item type.</returns>
    internal static int GetTypeRank(ExtraType extraType)
    {
        return extraType switch
        {
            ExtraType.BehindTheScenes => 25,
            ExtraType.DeletedScene => 25,
            ExtraType.Interview => 22,
            ExtraType.Featurette => 21,
            ExtraType.Short => 21,
            ExtraType.Trailer => 20,
            _ => 15
        };
    }

    private static string? TryGetTopParentId(BaseItem item)
    {
        try
        {
            var top = item.GetTopParent();
            if (top is null || top.Id == Guid.Empty)
            {
                return null;
            }

            return top.Id.ToString("N");
        }
        catch (Exception)
        {
            // GetTopParent depends on library state; ignore failures silently here. The caller's
            // log channel doesn't expose this method so we can't log without a logger reference.
            return null;
        }
    }

    private static IReadOnlyList<string>? TryGetPeopleNames(ILibraryManager libraryManager, BaseItem item)
    {
        try
        {
            var people = libraryManager.GetPeople(item);
            if (people is null || people.Count == 0)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>(people.Count);
            foreach (var person in people)
            {
                if (string.IsNullOrWhiteSpace(person?.Name))
                {
                    continue;
                }

                if (!seen.Add(person.Name))
                {
                    continue;
                }

                names.Add(person.Name);
            }

            return names.Count > 0 ? names : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        EnqueueUpsert(e.Item);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        EnqueueUpsert(e.Item);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        EnqueueRemove(e.Item);
    }

    private void EnqueueUpsert(BaseItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!Configuration.EnableRealTimeSync || Volatile.Read(ref _paused) != 0)
        {
            return;
        }

        if (!ShouldIndexItem(item))
        {
            return;
        }

        var op = new SyncOp(item.Id.ToString("N"), SyncOpKind.Upsert, item);
        if (!_channel.Writer.TryWrite(op))
        {
            _logger.LogWarning("Failed to enqueue Meilisearch upsert for item {ItemId}; queue closed", op.Id);
        }
        else
        {
            _idleEvent.Reset();
        }
    }

    private void EnqueueRemove(BaseItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!Configuration.EnableRealTimeSync || Volatile.Read(ref _paused) != 0)
        {
            return;
        }

        var op = new SyncOp(item.Id.ToString("N"), SyncOpKind.Remove, null);
        if (!_channel.Writer.TryWrite(op))
        {
            _logger.LogWarning("Failed to enqueue Meilisearch remove for item {ItemId}; queue closed", op.Id);
        }
        else
        {
            _idleEvent.Reset();
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        var pending = new Dictionary<string, SyncOp>(StringComparer.Ordinal);

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.Clear();
                _idleEvent.Reset();

                // Drain whatever is immediately available.
                while (reader.TryRead(out var op))
                {
                    Coalesce(pending, op);
                }

                var batchSize = Math.Max(1, Configuration.SyncBatchSize);
                var debounceMs = Math.Max(0, Configuration.SyncBatchDebounceMilliseconds);
                var deadline = DateTime.UtcNow.AddMilliseconds(debounceMs);

                // Keep accumulating up to batchSize or the debounce deadline (whichever first).
                while (pending.Count < batchSize)
                {
                    var remainingMs = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);

                    if (remainingMs == 0 && pending.Count > 0)
                    {
                        break;
                    }

                    using var debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (remainingMs > 0)
                    {
                        debounceCts.CancelAfter(remainingMs);
                    }

                    bool more;
                    try
                    {
                        more = await reader.WaitToReadAsync(debounceCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (debounceCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // Debounce window elapsed; flush what we have.
                        break;
                    }

                    if (!more)
                    {
                        // Channel completed; flush remaining and exit outer loop.
                        break;
                    }

                    while (pending.Count < batchSize && reader.TryRead(out var op))
                    {
                        Coalesce(pending, op);
                    }

                    // If a forced flush was requested, stop waiting.
                    if (_flushSignal.Wait(0, CancellationToken.None))
                    {
                        break;
                    }
                }

                if (pending.Count > 0)
                {
                    await FlushBatchAsync(pending, cancellationToken).ConfigureAwait(false);
                }

                if (reader.Count == 0)
                {
                    _idleEvent.Set();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meilisearch sync worker crashed; remaining ops will be persisted");
        }
        finally
        {
            // Drain anything still in the channel into the pending dictionary so StopAsync can persist it.
            while (reader.TryRead(out var op))
            {
                Coalesce(pending, op);
            }

            _leftoverOps = pending.Count > 0
                ? pending.Values.Select(static o => new PersistedSyncOp(o.Id, o.Kind.ToString())).ToList()
                : null;

            _idleEvent.Set();
        }
    }

    private static void Coalesce(Dictionary<string, SyncOp> pending, SyncOp op)
    {
        if (pending.TryGetValue(op.Id, out var existing))
        {
            // A remove for an id always wins over an upsert; for same-kind ops the newer one wins (which we already are).
            if (existing.Kind == SyncOpKind.Remove && op.Kind == SyncOpKind.Upsert)
            {
                // Keep the existing Remove.
                return;
            }
        }

        pending[op.Id] = op;
    }

    private async Task FlushBatchAsync(Dictionary<string, SyncOp> pending, CancellationToken cancellationToken)
    {
        var docsToIndex = new List<MeilisearchDocument>(pending.Count);
        var idsToRemove = new List<string>();

        // Pre-fetch people for all upserts in this batch in a single DB query.
        var upsertItemIds = pending.Values
            .Where(op => op.Kind == SyncOpKind.Upsert && op.Item is not null && op.Item.SupportsPeople)
            .Select(op => op.Item!.Id)
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<Guid, IReadOnlyList<string>> peopleLookup = upsertItemIds.Length > 0
            ? _libraryManager.GetPeopleNamesByItem(upsertItemIds, Array.Empty<string>())
            : new Dictionary<Guid, IReadOnlyList<string>>();

        foreach (var op in pending.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (op.Kind == SyncOpKind.Remove)
            {
                idsToRemove.Add(op.Id);
                continue;
            }

            if (op.Item is null)
            {
                // Upsert without an item reference (e.g. from a persisted restore where the item is missing);
                // skip it — caller is responsible for resolving.
                continue;
            }

            try
            {
                docsToIndex.Add(CreateDocument(op.Item, peopleLookup: peopleLookup));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to build Meilisearch document for item {ItemId}; skipping", op.Id);
            }
        }

        try
        {
            if (docsToIndex.Count > 0)
            {
                await _client.IndexDocumentsAsync(docsToIndex, cancellationToken).ConfigureAwait(false);
            }

            if (idsToRemove.Count > 0)
            {
                await _client.RemoveDocumentsAsync(idsToRemove, cancellationToken).ConfigureAwait(false);
            }

            if (docsToIndex.Count > 0 || idsToRemove.Count > 0)
            {
                _logger.LogInformation(
                    "Flushed Meilisearch sync batch: {UpsertCount} upserts, {RemoveCount} removes",
                    docsToIndex.Count,
                    idsToRemove.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error flushing Meilisearch sync batch ({UpsertCount} upserts, {RemoveCount} removes)",
                docsToIndex.Count,
                idsToRemove.Count);
        }

        pending.Clear();
    }

    private async Task RestorePersistedOpsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PersistedSyncOp> persisted;
        try
        {
            persisted = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted Meilisearch sync queue; ignoring");
            return;
        }

        if (persisted.Count == 0)
        {
            return;
        }

        var restoredUpserts = 0;
        var restoredRemoves = 0;
        var dropped = 0;

        foreach (var entry in persisted)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                dropped++;
                continue;
            }

            if (string.Equals(entry.Kind, nameof(SyncOpKind.Remove), StringComparison.Ordinal))
            {
                if (_channel.Writer.TryWrite(new SyncOp(entry.Id, SyncOpKind.Remove, null)))
                {
                    restoredRemoves++;
                    _idleEvent.Reset();
                }
                else
                {
                    dropped++;
                }

                continue;
            }

            if (!string.Equals(entry.Kind, nameof(SyncOpKind.Upsert), StringComparison.Ordinal))
            {
                dropped++;
                continue;
            }

            if (!Guid.TryParseExact(entry.Id, "N", out var guid))
            {
                dropped++;
                continue;
            }

            BaseItem? item = null;
            try
            {
                item = _libraryManager.GetItemById(guid);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve persisted Meilisearch item {ItemId}", entry.Id);
            }

            if (item is null)
            {
                dropped++;
                continue;
            }

            if (_channel.Writer.TryWrite(new SyncOp(entry.Id, SyncOpKind.Upsert, item)))
            {
                restoredUpserts++;
                _idleEvent.Reset();
            }
            else
            {
                dropped++;
            }
        }

        _logger.LogInformation(
            "Restored Meilisearch sync queue: {UpsertCount} upserts, {RemoveCount} removes, {DroppedCount} dropped",
            restoredUpserts,
            restoredRemoves,
            dropped);

        try
        {
            await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear Meilisearch sync queue after restore");
        }
    }

    private async Task PersistRemainingOpsAsync(CancellationToken cancellationToken)
    {
        var leftover = _leftoverOps;
        _leftoverOps = null;

        // Also drain anything that may have been written between worker exit and now.
        var extras = new List<PersistedSyncOp>();
        while (_channel.Reader.TryRead(out var op))
        {
            extras.Add(new PersistedSyncOp(op.Id, op.Kind.ToString()));
        }

        if ((leftover is null || leftover.Count == 0) && extras.Count == 0)
        {
            // Ensure stale file doesn't linger.
            try
            {
                await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear Meilisearch sync queue on shutdown");
            }

            return;
        }

        var combined = new List<PersistedSyncOp>((leftover?.Count ?? 0) + extras.Count);
        if (leftover is not null)
        {
            combined.AddRange(leftover);
        }

        combined.AddRange(extras);

        try
        {
            await _persistence.SaveAsync(combined, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist {Count} pending Meilisearch sync operations on shutdown",
                combined.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Releases the resources used by the service.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed and unmanaged resources used by the service.
    /// </summary>
    /// <param name="disposing">True to release managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _workerCts?.Dispose();
            _idleEvent.Dispose();
            _flushSignal.Dispose();
            _pauseLock.Dispose();
            _persistence.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// A single queued sync operation for the worker channel.
    /// </summary>
    /// <param name="Id">The document id (GUID, "N" format).</param>
    /// <param name="Kind">Whether to upsert or remove.</param>
    /// <param name="Item">The library item to upsert; null for remove operations.</param>
    private readonly record struct SyncOp(string Id, SyncOpKind Kind, BaseItem? Item);
}
