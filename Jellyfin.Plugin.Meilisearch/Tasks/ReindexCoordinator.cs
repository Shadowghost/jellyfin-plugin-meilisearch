using System.Threading;

namespace Jellyfin.Plugin.Meilisearch.Tasks;

/// <summary>
/// Process-wide mutual exclusion between the full and incremental reindex tasks.
/// Both tasks acquire <see cref="Gate"/> before running and release on exit.
/// </summary>
internal static class ReindexCoordinator
{
    /// <summary>
    /// Gets the gate held while a full or incremental reindex is executing.
    /// Use <c>WaitAsync(0, ct)</c> to attempt a non-blocking acquire and bail on contention.
    /// </summary>
    public static SemaphoreSlim Gate { get; } = new(1, 1);
}
