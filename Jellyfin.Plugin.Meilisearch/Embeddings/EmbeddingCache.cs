using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Meilisearch.Embeddings;

/// <summary>
/// A disk-backed store of document vectors, keyed by the exact text they were produced from.
/// </summary>
/// <remarks>
/// Embedding is by far the most expensive part of indexing - minutes of CPU for a small library,
/// hours for a large one - and a rebuild re-embeds text that has not changed since the last run. This
/// turns that back into a file read. A cache hit is only ever returned for byte-identical text, so an
/// item whose metadata changed is re-embedded as it should be.
/// <para>
/// The layout is two parallel fixed-stride files: <c>keys.bin</c> holds a header followed by one
/// 16-byte key per entry, <c>vectors.bin</c> holds one vector of <c>dimensions</c> floats per entry at
/// the matching position. Only the keys are read at startup, which is a megabyte or two rather than
/// the gigabyte the vectors occupy. Appends write the vector before the key, so a crash mid-append
/// leaves an unreferenced vector rather than a key pointing at garbage, and the length reconciliation
/// on open discards it.
/// </para>
/// <para>
/// This lives under Jellyfin's data path rather than its cache path on purpose. The content is
/// regenerable in principle, which is what the cache path is for, but regenerating it costs hours of
/// CPU, so it should survive the routine cache clears that path invites.
/// </para>
/// </remarks>
internal sealed class EmbeddingCache : IDisposable
{
    private const int HeaderSize = 32;
    private const int KeySize = 16;
    private const int FormatVersion = 1;

    // Appends between forced flushes to physical media. Writes reach the operating system
    // immediately, so nothing here is needed to survive Jellyfin restarting or being killed - only a
    // host that loses power can lose what the OS has not written out yet. One flush per thousand
    // vectors bounds that to a few seconds of re-embedding while costing nothing next to the thousand
    // forward passes that produced them.
    private const int FlushInterval = 1000;

    private static readonly byte[] Magic = "MSEC"u8.ToArray();

    private readonly ILogger _logger;
    private readonly string _directory;
    private readonly int _dimensions;
    private readonly int _recordSize;
    private readonly int _maxEntries;

    // Guards every field below it as well as both file handles: appends have to keep the two files
    // in step, and a retention rewrite replaces them wholesale.
    private readonly object _gate = new();

    private readonly Dictionary<CacheKey, int> _index;

    private SafeFileHandle _keysHandle;
    private SafeFileHandle _vectorsHandle;
    private int _count;
    private int _appendsSinceFlush;
    private long _hits;
    private long _misses;
    private bool _warnedFull;
    private HashSet<CacheKey>? _retained;
    private bool _disposed;

    private EmbeddingCache(
        ILogger logger,
        string directory,
        int dimensions,
        int maxEntries,
        SafeFileHandle keysHandle,
        SafeFileHandle vectorsHandle,
        Dictionary<CacheKey, int> index)
    {
        _logger = logger;
        _directory = directory;
        _dimensions = dimensions;
        _recordSize = dimensions * sizeof(float);
        _maxEntries = maxEntries;
        _keysHandle = keysHandle;
        _vectorsHandle = vectorsHandle;
        _index = index;
        _count = index.Count;
    }

    /// <summary>
    /// Gets the number of vectors currently stored.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Gets the number of lookups served from the cache since it was opened.
    /// </summary>
    public long Hits
    {
        get
        {
            lock (_gate)
            {
                return _hits;
            }
        }
    }

    /// <summary>
    /// Gets the number of lookups since it was opened that had to be embedded.
    /// </summary>
    public long Misses
    {
        get
        {
            lock (_gate)
            {
                return _misses;
            }
        }
    }

    /// <summary>
    /// Opens - creating or resetting as needed - the cache for a given model.
    /// </summary>
    /// <param name="directory">The directory the cache files live in.</param>
    /// <param name="fingerprint">Identifies the model whose vectors these are. A change discards the cache.</param>
    /// <param name="dimensions">The width of the stored vectors.</param>
    /// <param name="maxEntries">The maximum number of entries to store, or zero for no limit.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The opened cache, or null when it could not be opened.</returns>
    public static EmbeddingCache? Open(
        string directory,
        string fingerprint,
        int dimensions,
        int maxEntries,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentNullException.ThrowIfNull(logger);

        SafeFileHandle? keysHandle = null;
        SafeFileHandle? vectorsHandle = null;

        try
        {
            Directory.CreateDirectory(directory);

            keysHandle = File.OpenHandle(
                Path.Combine(directory, "keys.bin"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            vectorsHandle = File.OpenHandle(
                Path.Combine(directory, "vectors.bin"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            var fingerprintHash = HashFingerprint(fingerprint, dimensions);
            var recordSize = (long)dimensions * sizeof(float);

            if (!TryReadHeader(keysHandle, out var storedVersion, out var storedDimensions, out var storedFingerprint)
                || storedVersion != FormatVersion
                || storedDimensions != dimensions
                || storedFingerprint != fingerprintHash)
            {
                logger.LogInformation("Starting a new embedding cache in {Directory}", directory);
                Reset(keysHandle, vectorsHandle, fingerprintHash, dimensions);
            }

            // A crash can leave either file longer than the other. The shorter one is the truth:
            // every entry it describes is complete in both.
            var keyCount = Math.Max(0, (RandomAccess.GetLength(keysHandle) - HeaderSize) / KeySize);
            var vectorCount = RandomAccess.GetLength(vectorsHandle) / recordSize;
            var count = (int)Math.Min(int.MaxValue, Math.Min(keyCount, vectorCount));

            RandomAccess.SetLength(keysHandle, HeaderSize + ((long)count * KeySize));
            RandomAccess.SetLength(vectorsHandle, count * recordSize);

            var index = ReadIndex(keysHandle, count);

            logger.LogInformation(
                "Embedding cache ready with {Count} vectors in {Directory}",
                index.Count.ToString(CultureInfo.InvariantCulture),
                directory);

            var cache = new EmbeddingCache(logger, directory, dimensions, maxEntries, keysHandle, vectorsHandle, index);
            keysHandle = null;
            vectorsHandle = null;
            return cache;
        }
#pragma warning disable CA1031 // A cache that cannot be opened must not stop embedding from working.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the embedding cache in {Directory}; vectors will be recomputed every run", directory);
            return null;
        }
#pragma warning restore CA1031
        finally
        {
            keysHandle?.Dispose();
            vectorsHandle?.Dispose();
        }
    }

    /// <summary>
    /// Looks up the vector previously stored for a text.
    /// </summary>
    /// <param name="text">The exact text that was embedded.</param>
    /// <returns>A copy of the stored vector, or null when the text is not cached.</returns>
    public float[]? TryGet(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var key = ComputeKey(text);

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (!_index.TryGetValue(key, out var slot))
            {
                _misses++;
                return null;
            }

            _retained?.Add(key);

            var vector = new float[_dimensions];
            try
            {
                var buffer = MemoryMarshal.AsBytes(vector.AsSpan());
                var read = RandomAccess.Read(_vectorsHandle, buffer, (long)slot * _recordSize);
                if (read != buffer.Length)
                {
                    _misses++;
                    return null;
                }
            }
#pragma warning disable CA1031 // A damaged cache file degrades to a miss, never to a failed index run.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read a cached embedding; it will be recomputed");
                _misses++;
                return null;
            }
#pragma warning restore CA1031

            _hits++;
            return vector;
        }
    }

    /// <summary>
    /// Stores the vector for a text, replacing nothing: a text already present is left as it is.
    /// </summary>
    /// <param name="text">The text that was embedded.</param>
    /// <param name="vector">The vector it produced.</param>
    public void Add(string text, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(vector);

        if (vector.Length != _dimensions)
        {
            return;
        }

        var key = ComputeKey(text);

        lock (_gate)
        {
            if (_disposed || _index.ContainsKey(key))
            {
                return;
            }

            if (_maxEntries > 0 && _count >= _maxEntries)
            {
                if (!_warnedFull)
                {
                    _warnedFull = true;
                    _logger.LogInformation(
                        "Embedding cache reached its limit of {MaxEntries} entries; further vectors are computed without being cached. A full rebuild prunes entries no longer in the library",
                        _maxEntries.ToString(CultureInfo.InvariantCulture));
                }

                return;
            }

            try
            {
                // Vector first: a crash between the two writes leaves an orphaned vector, which the
                // length reconciliation in Open discards. The other order would leave a key pointing
                // at bytes that were never written.
                RandomAccess.Write(_vectorsHandle, MemoryMarshal.AsBytes(vector.AsSpan()), (long)_count * _recordSize);

                Span<byte> keyBytes = stackalloc byte[KeySize];
                key.WriteTo(keyBytes);
                RandomAccess.Write(_keysHandle, keyBytes, HeaderSize + ((long)_count * KeySize));
            }
#pragma warning disable CA1031 // A cache that cannot be written to must not fail the index run.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write to the embedding cache; continuing without caching this vector");
                return;
            }
#pragma warning restore CA1031

            _index[key] = _count;
            _retained?.Add(key);
            _count++;

            if (++_appendsSinceFlush >= FlushInterval)
            {
                FlushCore();
            }
        }
    }

    /// <summary>
    /// Forces everything written so far out to physical media.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                FlushCore();
            }
        }
    }

    /// <summary>
    /// Starts recording which entries are used, so <see cref="EndRetentionScope"/> can drop the rest.
    /// </summary>
    /// <remarks>
    /// Meant to bracket a full rebuild, which embeds every item in the library and therefore touches
    /// exactly the entries worth keeping. Everything else is metadata that has since been edited or
    /// items that have since been deleted.
    /// </remarks>
    public void BeginRetentionScope()
    {
        lock (_gate)
        {
            _retained = [];
        }
    }

    /// <summary>
    /// Ends a retention scope.
    /// </summary>
    /// <param name="prune">
    /// When true, entries not used during the scope are removed. Pass false when the run that opened
    /// the scope did not finish, since its record of what is in use is then incomplete.
    /// </param>
    public void EndRetentionScope(bool prune)
    {
        lock (_gate)
        {
            var retained = _retained;
            _retained = null;

            if (_disposed)
            {
                return;
            }

            if (!prune || retained is null || retained.Count == _count)
            {
                // A rebuild is the largest batch of work the cache ever accumulates, so it is worth
                // making durable even against a host that loses power, pruned or not.
                FlushCore();
                return;
            }

            try
            {
                Prune(retained);
            }
#pragma warning disable CA1031 // Failing to prune leaves a larger cache, which is not worth failing a rebuild over.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune the embedding cache; it keeps its current contents");
            }
#pragma warning restore CA1031
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retained = null;
            FlushCore();
            _keysHandle.Dispose();
            _vectorsHandle.Dispose();
        }
    }

    /// <summary>
    /// Forces both files out to physical media. Called with <see cref="_gate"/> held.
    /// </summary>
    /// <remarks>
    /// Vectors before keys, matching the order they are appended in, so a power loss between the two
    /// leaves vectors that no key references rather than keys pointing at vectors that never landed.
    /// The reconciliation in <see cref="Open"/> discards the former; the latter would be unreadable.
    /// </remarks>
    private void FlushCore()
    {
        try
        {
            RandomAccess.FlushToDisk(_vectorsHandle);
            RandomAccess.FlushToDisk(_keysHandle);
            _appendsSinceFlush = 0;
        }
#pragma warning disable CA1031 // A filesystem that will not flush is not a reason to fail indexing.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not flush the embedding cache to disk");
        }
#pragma warning restore CA1031
    }

    private static long HashFingerprint(string fingerprint, int dimensions)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{fingerprint}|{dimensions}")));

        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static bool TryReadHeader(SafeFileHandle handle, out int version, out int dimensions, out long fingerprint)
    {
        version = 0;
        dimensions = 0;
        fingerprint = 0;

        Span<byte> header = stackalloc byte[HeaderSize];
        if (RandomAccess.GetLength(handle) < HeaderSize || RandomAccess.Read(handle, header, 0) != HeaderSize)
        {
            return false;
        }

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        dimensions = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        fingerprint = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
        return true;
    }

    private static void Reset(SafeFileHandle keysHandle, SafeFileHandle vectorsHandle, long fingerprint, int dimensions)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], dimensions);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], fingerprint);

        RandomAccess.SetLength(keysHandle, 0);
        RandomAccess.Write(keysHandle, header, 0);
        RandomAccess.SetLength(vectorsHandle, 0);
    }

    private static Dictionary<CacheKey, int> ReadIndex(SafeFileHandle handle, int count)
    {
        var index = new Dictionary<CacheKey, int>(count);
        if (count == 0)
        {
            return index;
        }

        var buffer = new byte[count * KeySize];
        var read = RandomAccess.Read(handle, buffer, HeaderSize);

        for (var slot = 0; slot < read / KeySize; slot++)
        {
            // A duplicate key can only come from a torn write; the first slot wins and the later one
            // becomes dead space that the next prune reclaims.
            index.TryAdd(CacheKey.ReadFrom(buffer.AsSpan(slot * KeySize, KeySize)), slot);
        }

        return index;
    }

    private static CacheKey ComputeKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return CacheKey.ReadFrom(hash);
    }

    /// <summary>
    /// Rewrites both files keeping only the given keys. Called with <see cref="_gate"/> held.
    /// </summary>
    private void Prune(HashSet<CacheKey> retained)
    {
        var survivors = new List<KeyValuePair<CacheKey, int>>(retained.Count);
        foreach (var entry in _index)
        {
            if (retained.Contains(entry.Key))
            {
                survivors.Add(entry);
            }
        }

        // Copying in stored order keeps the reads sequential rather than scattering them across a
        // file that is several gigabytes for a large library.
        survivors.Sort(static (left, right) => left.Value.CompareTo(right.Value));

        var keysPath = Path.Combine(_directory, "keys.bin");
        var vectorsPath = Path.Combine(_directory, "vectors.bin");
        var keysTempPath = keysPath + ".tmp";
        var vectorsTempPath = vectorsPath + ".tmp";

        var dropped = _count - survivors.Count;

        using (var keysTemp = File.OpenHandle(keysTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var vectorsTemp = File.OpenHandle(vectorsTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            TryReadHeader(_keysHandle, out _, out _, out var fingerprint);
            Reset(keysTemp, vectorsTemp, fingerprint, _dimensions);

            var vector = new byte[_recordSize];
            Span<byte> keyBytes = stackalloc byte[KeySize];

            for (var slot = 0; slot < survivors.Count; slot++)
            {
                if (RandomAccess.Read(_vectorsHandle, vector, (long)survivors[slot].Value * _recordSize) != vector.Length)
                {
                    throw new InvalidDataException("The embedding cache ended before a vector it claims to hold.");
                }

                RandomAccess.Write(vectorsTemp, vector, (long)slot * _recordSize);

                survivors[slot].Key.WriteTo(keyBytes);
                RandomAccess.Write(keysTemp, keyBytes, HeaderSize + ((long)slot * KeySize));
            }

            // Both replacements have to be on disk before the originals are moved aside.
            RandomAccess.FlushToDisk(vectorsTemp);
            RandomAccess.FlushToDisk(keysTemp);
        }

        _keysHandle.Dispose();
        _vectorsHandle.Dispose();

        File.Move(keysTempPath, keysPath, overwrite: true);
        File.Move(vectorsTempPath, vectorsPath, overwrite: true);

        _keysHandle = File.OpenHandle(keysPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _vectorsHandle = File.OpenHandle(vectorsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        _index.Clear();
        for (var slot = 0; slot < survivors.Count; slot++)
        {
            _index[survivors[slot].Key] = slot;
        }

        _count = survivors.Count;
        _appendsSinceFlush = 0;
        _warnedFull = false;

        _logger.LogInformation(
            "Pruned {Dropped} stale vectors from the embedding cache, {Kept} remain",
            dropped.ToString(CultureInfo.InvariantCulture),
            _count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The first 128 bits of the SHA-256 of the embedded text. Wide enough that a collision across a
    /// library is not a practical concern, and half the size of the full digest to store.
    /// </summary>
    private readonly record struct CacheKey(ulong Low, ulong High)
    {
        public static CacheKey ReadFrom(ReadOnlySpan<byte> source)
            => new(
                BinaryPrimitives.ReadUInt64LittleEndian(source),
                BinaryPrimitives.ReadUInt64LittleEndian(source[sizeof(ulong)..]));

        public void WriteTo(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, Low);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], High);
        }
    }
}
