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

    // How often a parked worker re-checks whether sync has been resumed.
    private const int PausePollMilliseconds = 250;

    // A failed flush is retried, so back off to avoid spinning while Meilisearch is unreachable.
    private const int FlushRetryBaseDelayMilliseconds = 1_000;
    private const int FlushRetryMaxDelayMilliseconds = 30_000;

    // Give up on an operation that keeps failing rather than retrying it forever.
    private const int MaxFlushAttempts = 20;

    // How long shutdown waits for the worker to write out what it has before cancelling it.
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

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

    // Guards _pauseCount, and is held by the worker for the duration of a flush.
    private readonly SemaphoreSlim _pauseLock = new(1, 1);

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private Task? _restoreTask;
    private int _pauseCount;
    private int _consecutiveFlushFailures;
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;

        WarnOnStaleIndexSchema();

        _workerCts = new CancellationTokenSource();
        var workerToken = _workerCts.Token;
        _workerTask = Task.Run(() => RunWorkerAsync(workerToken), CancellationToken.None);

        // Deliberately not awaited. Restoring resolves every persisted id through the library, which
        // is up to ChannelCapacity database round-trips, and hosted services start before Jellyfin
        // runs its startup tasks - so awaiting here would hold up the whole server and would do the
        // lookups before the library's static dependencies are even wired up.
        _restoreTask = Task.Run(() => RestorePersistedOpsAsync(workerToken), CancellationToken.None);

        _logger.LogInformation("Meilisearch index service started");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Warns when the index was last built by a plugin version with a different document schema.
    /// </summary>
    private void WarnOnStaleIndexSchema()
    {
        var indexedVersion = Configuration.IndexSchemaVersion;
        if (indexedVersion == MeilisearchDocument.SchemaVersion)
        {
            return;
        }

        _logger.LogWarning(
            "The Meilisearch index was built with document schema v{IndexedVersion} but this plugin writes v{CurrentVersion}. "
            + "Filters on newly added fields cannot match older documents, so parent-scoped and media-type-scoped searches will "
            + "under-report until you run the 'Rebuild Meilisearch Index' task",
            indexedVersion,
            MeilisearchDocument.SchemaVersion);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        // Let the restore finish enqueueing before the channel is closed.
        if (_restoreTask is not null)
        {
            try
            {
                await _restoreTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down faster than the restore can drain; the queue file is left in place.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Meilisearch sync queue restore terminated with an error");
            }

            _restoreTask = null;
        }

        // Stop accepting new ops and signal the worker to drain.
        _channel.Writer.TryComplete();

        // Give the worker a bounded window to finish writing what it already has, then cancel.
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.WaitAsync(ShutdownDrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("Meilisearch sync worker did not drain in time; pending operations will be persisted");
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
        }

        // Await again after cancelling: the worker's finally block is what drains the channel into
        // _leftoverOps, and persisting before it runs would lose the queue.
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected once cancelled.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Meilisearch sync worker terminated with an error");
            }

            _workerTask = null;
        }

        if (_workerCts is not null)
        {
            _workerCts.Dispose();
            _workerCts = null;
        }

        await PersistRemainingOpsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Meilisearch index service stopped");
    }

    /// <summary>
    /// Pauses writes to Meilisearch. Library change events continue to be queued while paused and
    /// are written once sync resumes; only the flushing stops.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once any in-flight batch has finished.</returns>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        // The worker holds this lock while flushing, so acquiring it waits out an in-flight batch.
        await _pauseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pauseCount++;
        }
        finally
        {
            _pauseLock.Release();
        }
    }

    /// <summary>
    /// Resumes real-time sync after a previous <see cref="PauseAsync"/> call. Sync only actually
    /// resumes once the outstanding pause count returns to zero.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the pause count has been decremented.</returns>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _pauseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pauseCount > 0)
            {
                _pauseCount--;
            }
        }
        finally
        {
            _pauseLock.Release();
        }
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
            MediaType = item.MediaType == MediaType.Unknown ? null : item.MediaType.ToString(),
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
            TopParentId = TryGetTopParentId(item),

            // Full ancestor chain, so a parent-scoped search can match the whole subtree the way
            // the built-in SQL provider does rather than only direct children.
            AncestorIds = TryGetAncestorIds(item)
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

    private static IReadOnlyList<string>? TryGetAncestorIds(BaseItem item)
    {
        try
        {
            var ancestorIds = new List<string>();
            var seen = new HashSet<Guid>();
            foreach (var ancestorId in item.GetAncestorIds())
            {
                if (ancestorId != Guid.Empty && seen.Add(ancestorId))
                {
                    ancestorIds.Add(ancestorId.ToString("N"));
                }
            }

            return ancestorIds.Count > 0 ? ancestorIds : null;
        }
        catch (Exception)
        {
            // Depends on library state being wired up; same defensive treatment as GetTopParent.
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

        if (!Configuration.EnableRealTimeSync)
        {
            return;
        }

        if (!ShouldIndexItem(item))
        {
            EnqueueRemove(item);
            return;
        }

        Enqueue(new SyncOp(item.Id.ToString("N"), SyncOpKind.Upsert, item, 0));
    }

    private void EnqueueRemove(BaseItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!Configuration.EnableRealTimeSync)
        {
            return;
        }

        Enqueue(new SyncOp(item.Id.ToString("N"), SyncOpKind.Remove, null, 0));
    }

    /// <summary>
    /// Queues an operation. Queuing continues while sync is paused - the worker parks instead, and
    /// the bounded channel provides the backpressure.
    /// </summary>
    private void Enqueue(SyncOp op)
    {
        if (!_channel.Writer.TryWrite(op))
        {
            _logger.LogWarning(
                "Failed to enqueue Meilisearch {Kind} for item {ItemId}; queue closed",
                op.Kind,
                op.Id);
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
                }

                if (pending.Count > 0)
                {
                    // Take the pause lock for the whole flush so PauseAsync cannot return while a
                    // batch is still being written. Released and retaken between attempts so a pause
                    // requested mid-wait is honoured promptly.
                    while (true)
                    {
                        await _pauseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (_pauseCount == 0)
                        {
                            break;
                        }

                        _pauseLock.Release();
                        await Task.Delay(PausePollMilliseconds, cancellationToken).ConfigureAwait(false);
                    }

                    bool flushed;
                    try
                    {
                        flushed = await FlushBatchAsync(pending, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _pauseLock.Release();
                    }

                    if (!flushed)
                    {
                        // Back off outside the pause lock so a pause requested meanwhile isn't
                        // stuck waiting out the retry delay.
                        await Task.Delay(GetFlushRetryDelay(), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (reader.Count == 0)
                {
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

    private async Task<bool> FlushBatchAsync(Dictionary<string, SyncOp> pending, CancellationToken cancellationToken)
    {
        var docsToIndex = new List<MeilisearchDocument>(pending.Count);
        var idsToRemove = new List<string>();

        // Pre-fetch people for all upserts in this batch in a single DB query.
        var upsertItemIds = pending.Values
            .Where(op => op.Kind == SyncOpKind.Upsert && op.Item is not null && op.Item.SupportsPeople)
            .Select(op => op.Item!.Id)
            .Distinct()
            .ToArray();

        var peopleLookup = upsertItemIds.Length > 0
            ? _libraryManager.GetPeopleNamesByItems(upsertItemIds, [])
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
                // skip it - caller is responsible for resolving.
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

        var failed = false;
        try
        {
            if (docsToIndex.Count > 0)
            {
                failed |= await _client.IndexDocumentsAsync(docsToIndex, cancellationToken).ConfigureAwait(false) is null;
            }

            if (idsToRemove.Count > 0)
            {
                failed |= !await _client.RemoveDocumentsAsync(idsToRemove, cancellationToken).ConfigureAwait(false);
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
            failed = true;
        }

        if (failed)
        {
            _logger.LogWarning(
                "Meilisearch sync batch failed ({UpsertCount} upserts, {RemoveCount} removes); requeueing for retry",
                docsToIndex.Count,
                idsToRemove.Count);

            RequeueFailedBatch(pending);
            pending.Clear();
            _consecutiveFlushFailures++;
            return false;
        }

        if (docsToIndex.Count > 0 || idsToRemove.Count > 0)
        {
            _logger.LogDebug(
                "Flushed Meilisearch sync batch: {UpsertCount} upserts, {RemoveCount} removes",
                docsToIndex.Count,
                idsToRemove.Count);
        }

        _consecutiveFlushFailures = 0;
        pending.Clear();
        return true;
    }

    private int GetFlushRetryDelay()
    {
        var exponent = Math.Min(Math.Max(_consecutiveFlushFailures - 1, 0), 10);
        return Math.Min(FlushRetryMaxDelayMilliseconds, FlushRetryBaseDelayMilliseconds * (1 << exponent));
    }

    /// <summary>
    /// Returns the operations from a failed batch to the queue so they are retried.
    /// </summary>
    private void RequeueFailedBatch(Dictionary<string, SyncOp> pending)
    {
        var exhausted = 0;

        foreach (var op in pending.Values)
        {
            var attempt = op.Attempt + 1;
            if (attempt >= MaxFlushAttempts)
            {
                exhausted++;
                continue;
            }

            if (!_channel.Writer.TryWrite(op with { Attempt = attempt }))
            {
                exhausted++;
            }
        }

        if (exhausted > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} Meilisearch sync operations after {MaxAttempts} failed attempts; a full reindex is needed to reconcile them",
                exhausted,
                MaxFlushAttempts);
        }
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
                if (_channel.Writer.TryWrite(new SyncOp(entry.Id, SyncOpKind.Remove, null, 0)))
                {
                    restoredRemoves++;
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

            if (_channel.Writer.TryWrite(new SyncOp(entry.Id, SyncOpKind.Upsert, item, 0)))
            {
                restoredUpserts++;
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
    /// <param name="Attempt">How many times writing this operation has already failed.</param>
    private readonly record struct SyncOp(string Id, SyncOpKind Kind, BaseItem? Item, int Attempt);
}
