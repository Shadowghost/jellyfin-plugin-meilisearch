using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.Meilisearch.Embeddings;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Embeds a reindex batch while keeping the outside world informed about it.
/// </summary>
internal sealed class ReindexEmbeddingReporter
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly EmbeddingService _embeddings;
    private readonly ILogger _logger;
    private readonly IProgress<double> _progress;
    private readonly Func<double, double> _mapFraction;
    private readonly int _totalItems;

    private int _totalComputed;
    private int _totalFromCache;
    private double _committedSeconds;
    private long? _batchStart;

    private int _batchComputed;
    private int _batchFromCache;
    private double _batchSeconds;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexEmbeddingReporter"/> class.
    /// </summary>
    /// <param name="embeddings">The embedding service to attach vectors with.</param>
    /// <param name="logger">The owning task's logger, so the lines keep its category.</param>
    /// <param name="progress">The task's progress sink.</param>
    /// <param name="mapFraction">
    /// Maps a completion fraction of the whole run, 0.0-1.0, onto the percentage the task reports.
    /// The two tasks reserve different amounts of their range for setup and for the final wait on
    /// Meilisearch, so the mapping belongs to the caller.
    /// </param>
    /// <param name="totalItems">Total items in the run, used for the estimate.</param>
    public ReindexEmbeddingReporter(
        EmbeddingService embeddings,
        ILogger logger,
        IProgress<double> progress,
        Func<double, double> mapFraction,
        int totalItems)
    {
        _embeddings = embeddings;
        _logger = logger;
        _progress = progress;
        _mapFraction = mapFraction;
        _totalItems = totalItems;
    }

    /// <summary>
    /// Gets the number of vectors this run has computed, as opposed to read from the cache.
    /// </summary>
    public int TotalComputed => _totalComputed;

    private double SecondsSpentEmbedding
        => _committedSeconds + (_batchStart is { } start
            ? Stopwatch.GetElapsedTime(start).TotalSeconds
            : 0d);

    /// <summary>
    /// Embeds one batch, reporting progress and logging a heartbeat while it runs.
    /// </summary>
    /// <param name="batch">The documents to attach vectors to.</param>
    /// <param name="batchNumber">The batch's ordinal, for the log lines.</param>
    /// <param name="itemsBeforeBatch">Items of the run already accounted for before this batch.</param>
    /// <param name="itemsInBatch">
    /// Items this batch accounts for, which is the id chunk's length rather than
    /// <paramref name="batch"/>'s count: some of the chunk is filtered out before it gets here, and
    /// progress is measured against the run's snapshot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void AttachVectors(
        IReadOnlyList<MeilisearchDocument> batch,
        int batchNumber,
        int itemsBeforeBatch,
        int itemsInBatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        _batchComputed = 0;
        _batchFromCache = 0;
        _batchSeconds = 0;

        if (!_embeddings.IsReady)
        {
            // Nothing to report: with no model loaded AttachVectors returns immediately, and the
            // batch's cost is the database read and the push, which the caller already logs.
            _embeddings.AttachVectors(batch, cancellationToken);
            return;
        }

        var lastHeartbeat = Stopwatch.GetTimestamp();
        _batchStart = lastHeartbeat;

        try
        {
            _embeddings.AttachVectors(batch, OnProgress, cancellationToken);
        }
        finally
        {
            _batchSeconds = Stopwatch.GetElapsedTime(_batchStart.Value).TotalSeconds;
            _committedSeconds += _batchSeconds;
            _batchStart = null;

            _totalComputed += _batchComputed;
            _totalFromCache += _batchFromCache;
        }

        void OnProgress(EmbeddingProgress embedded)
        {
            _batchComputed = embedded.Computed;
            _batchFromCache = embedded.CacheHits;

            var fractionOfBatch = embedded.Total <= 0
                ? 1d
                : (double)embedded.Completed / embedded.Total;

            ReportProgress(itemsBeforeBatch + (fractionOfBatch * itemsInBatch));

            if (Stopwatch.GetElapsedTime(lastHeartbeat) < HeartbeatInterval)
            {
                return;
            }

            lastHeartbeat = Stopwatch.GetTimestamp();

            // The counters below are the run's, not the batch's: mid-batch they still exclude what
            // this batch has done, so they are folded in for the sake of the line.
            _logger.LogInformation(
                "Still embedding batch {BatchNumber}: {Completed}/{Total} vectors of this batch "
                + "({Computed} computed, {Cached} from cache) after {Elapsed}{Rate}{Remaining}",
                batchNumber,
                embedded.Completed,
                embedded.Total,
                embedded.Computed,
                embedded.CacheHits,
                Format(Stopwatch.GetElapsedTime(_batchStart!.Value)),
                DescribeRate(_totalComputed + embedded.Computed),
                DescribeRemaining(
                    itemsBeforeBatch + embedded.Completed,
                    _totalComputed + embedded.Computed));
        }
    }

    /// <summary>
    /// Reports the run's completion as a percentage through the caller's mapping.
    /// </summary>
    /// <param name="itemsCompleted">Items of the run finished so far.</param>
    /// <returns>The percentage that was reported, for the caller's log line.</returns>
    public double ReportProgress(double itemsCompleted)
    {
        var fraction = _totalItems <= 0
            ? 1d
            : Math.Clamp(itemsCompleted / _totalItems, 0d, 1d);

        var percent = _mapFraction(fraction);
        _progress.Report(percent);
        return percent;
    }

    /// <summary>
    /// Describes the embedding work the last batch did, to append to the caller's per-batch line.
    /// </summary>
    /// <returns>
    /// A clause starting with a comma, or an empty string when the batch embedded nothing - so on an
    /// install without semantic search the line reads exactly as it always did.
    /// </returns>
    public string DescribeLastBatch()
    {
        if (_batchComputed == 0 && _batchFromCache == 0)
        {
            return string.Empty;
        }

        if (_batchComputed == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $", {_batchFromCache} vectors from cache");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $", embedded {_batchComputed} vectors ({_batchFromCache} from cache) in "
                + $"{Format(TimeSpan.FromSeconds(_batchSeconds))}");
    }

    /// <summary>
    /// Estimates how much embedding the run has left, to append to the caller's per-batch line.
    /// </summary>
    /// <param name="itemsCompleted">Items of the run finished so far.</param>
    /// <returns>A clause starting with a comma, or an empty string when there is nothing to base it on.</returns>
    public string DescribeRemaining(double itemsCompleted)
        => DescribeRemaining(itemsCompleted, _totalComputed);

    /// <summary>
    /// Summarizes the run's embedding work, to append to the caller's completion line.
    /// </summary>
    /// <returns>A sentence starting with a full stop, or an empty string when nothing was embedded.</returns>
    public string DescribeRun()
    {
        if (_totalComputed == 0 && _totalFromCache == 0)
        {
            return string.Empty;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $". Embedded {_totalComputed} vectors ({_totalFromCache} served from cache) in "
                + $"{Format(TimeSpan.FromSeconds(_committedSeconds))}{DescribeRate(_totalComputed)}");
    }

    /// <summary>
    /// Throughput in computed vectors per second of embedding time, or null when nothing has been
    /// computed yet.
    /// </summary>
    /// <param name="computed">
    /// Vectors computed. Taken as a parameter rather than read from the run total so a heartbeat can
    /// include the batch it is reporting on: the run total does not absorb a batch until the batch
    /// ends, and the first batch of a cold run is both the longest and the one where an estimate is
    /// most wanted - measuring it against a total of zero would report no rate at all for as long as
    /// it lasts.
    /// </param>
    private double? RateFor(int computed)
    {
        var seconds = SecondsSpentEmbedding;
        return computed > 0 && seconds > 0.001 ? computed / seconds : null;
    }

    private static string Format(TimeSpan value)
    {
        if (value.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:F0}s");
        }

        if (value.TotalHours < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}m {value.Seconds}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}h {value.Minutes}m");
    }

    private string DescribeRate(int computed)
        => RateFor(computed) is { } rate
            ? string.Create(CultureInfo.InvariantCulture, $", {rate:F1} vectors/s")
            : string.Empty;

    private string DescribeRemaining(double itemsCompleted, int computed)
    {
        // An upper bound, priced at the measured cost per computed vector and charged to every item
        // that is left.
        if (_totalItems <= 0 || RateFor(computed) is not { } vectorsPerSecond)
        {
            return string.Empty;
        }

        var itemsRemaining = _totalItems - itemsCompleted;
        if (itemsRemaining <= 0)
        {
            return string.Empty;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $", up to ~{Format(TimeSpan.FromSeconds(itemsRemaining / vectorsPerSecond))} of embedding left");
    }
}
