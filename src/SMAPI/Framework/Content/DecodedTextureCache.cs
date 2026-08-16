using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Xna.Framework;

namespace StardewModdingAPI.Framework.Content;

/// <summary>A byte-bounded cache of decoded image pixels.</summary>
/// <remarks>Cached arrays are never returned directly, since <see cref="IRawTextureData.Data"/> is publicly mutable.</remarks>
internal sealed class DecodedTextureCache : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>The maximum number of one-time load candidates to remember for second-use admission.</summary>
    private const int DefaultTrackedFirstLoads = 2048;

    /// <summary>The cached images by absolute file path.</summary>
    private readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>The cached images ordered from least to most recently used.</summary>
    private readonly LinkedList<CacheEntry> UsageOrder = [];

    /// <summary>Files decoded once but not admitted yet, indexed by absolute path.</summary>
    private readonly Dictionary<string, FileStamp> FirstLoads = new(StringComparer.Ordinal);

    /// <summary>The insertion order for <see cref="FirstLoads"/>, used to bound candidate bookkeeping.</summary>
    private readonly Queue<FirstLoadEntry> FirstLoadOrder = [];

    /// <summary>The maximum total retained pixel bytes.</summary>
    private readonly long MaxBytes;

    /// <summary>The maximum retained bytes for one image.</summary>
    private readonly long MaxEntryBytes;

    /// <summary>The maximum number of first-load candidates to retain.</summary>
    private readonly int MaxTrackedFirstLoads;

    /// <summary>Synchronizes cache access from asynchronous content loads.</summary>
    private readonly object SyncRoot = new();

    /// <summary>The total number of retained pixel bytes.</summary>
    private long SizeInBytesImpl;

    /// <summary>Whether this cache has been disposed.</summary>
    private bool IsDisposed;


    /*********
    ** Accessors
    *********/
    /// <summary>The number of decoded images currently retained.</summary>
    internal int Count
    {
        get
        {
            lock (this.SyncRoot)
                return this.Cache.Count;
        }
    }

    /// <summary>The total number of retained pixel bytes.</summary>
    internal long SizeInBytes
    {
        get
        {
            lock (this.SyncRoot)
                return this.SizeInBytesImpl;
        }
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance with a memory-adaptive default budget.</summary>
    public DecodedTextureCache()
        : this(DecodedTextureCache.GetDefaultMaxBytes()) { }

    /// <summary>Construct an instance.</summary>
    /// <param name="maxBytes">The maximum total retained pixel bytes.</param>
    /// <param name="maxEntryBytes">The maximum retained bytes for one image, or <c>null</c> for one quarter of the total budget.</param>
    /// <param name="maxTrackedFirstLoads">The maximum number of one-time load candidates to remember for second-use admission.</param>
    internal DecodedTextureCache(long maxBytes, long? maxEntryBytes = null, int maxTrackedFirstLoads = DecodedTextureCache.DefaultTrackedFirstLoads)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxEntryBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntryBytes));
        if (maxTrackedFirstLoads <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTrackedFirstLoads));

        this.MaxBytes = maxBytes;
        this.MaxEntryBytes = Math.Min(maxEntryBytes ?? Math.Max(1, maxBytes / 4), maxBytes);
        this.MaxTrackedFirstLoads = maxTrackedFirstLoads;
    }

    /// <summary>Get a copy of cached pixels for a file, if its current metadata still matches.</summary>
    /// <param name="file">The source image file.</param>
    /// <param name="data">The copied decoded pixels, if found.</param>
    public bool TryGetCopy(FileInfo file, [NotNullWhen(true)] out RawTextureData? data)
    {
        data = null;
        if (!DecodedTextureCache.TryGetStamp(file, out FileStamp stamp))
            return false;

        int width;
        int height;
        Color[] pixels;
        lock (this.SyncRoot)
        {
            if (this.IsDisposed || !this.Cache.TryGetValue(file.FullName, out CacheEntry? entry))
                return false;

            if (entry.Stamp != stamp)
            {
                this.Remove(entry);
                return false;
            }

            this.UsageOrder.Remove(entry.UsageNode);
            this.UsageOrder.AddLast(entry.UsageNode);
            width = entry.Width;
            height = entry.Height;
            pixels = entry.Pixels;
        }

        data = new RawTextureData(width, height, (Color[])pixels.Clone());
        return true;
    }

    /// <summary>Record newly decoded pixels, admitting them only after the same file is decoded twice unchanged.</summary>
    /// <param name="file">The source image file.</param>
    /// <param name="data">The newly decoded pixels.</param>
    public void Track(FileInfo file, RawTextureData data)
    {
        long size = checked(data.Data.LongLength * sizeof(uint));
        if (size > this.MaxEntryBytes || !DecodedTextureCache.TryGetStamp(file, out FileStamp stamp))
            return;

        string path = file.FullName;
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;

            if (this.Cache.TryGetValue(path, out CacheEntry? existing))
            {
                if (existing.Stamp == stamp)
                    return;

                this.Remove(existing);
            }

            // Don't let one-shot startup images fill the cache. The second unchanged decode admits the file.
            if (!this.FirstLoads.TryGetValue(path, out FileStamp firstStamp) || firstStamp != stamp)
            {
                this.FirstLoads[path] = stamp;
                this.FirstLoadOrder.Enqueue(new FirstLoadEntry(path, stamp));
                this.TrimFirstLoads();
                return;
            }
            this.FirstLoads.Remove(path);

            var entry = new CacheEntry(path, stamp, data.Width, data.Height, (Color[])data.Data.Clone(), size);
            entry.UsageNode = this.UsageOrder.AddLast(entry);
            this.Cache[path] = entry;
            this.SizeInBytesImpl += size;

            while (this.SizeInBytesImpl > this.MaxBytes && this.UsageOrder.First?.Value is { } oldest)
                this.Remove(oldest);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;

            this.IsDisposed = true;
            this.Cache.Clear();
            this.UsageOrder.Clear();
            this.FirstLoads.Clear();
            this.FirstLoadOrder.Clear();
            this.SizeInBytesImpl = 0;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get an adaptive cache budget which remains a small share of memory on constrained systems.</summary>
    private static long GetDefaultMaxBytes()
    {
        const long minBytes = 16L * 1024 * 1024;
        const long maxBytes = 128L * 1024 * 1024;
        long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return availableBytes > 0
            ? Math.Clamp(availableBytes / 128, minBytes, maxBytes)
            : 64L * 1024 * 1024;
    }

    /// <summary>Get the metadata used to validate a cached file.</summary>
    private static bool TryGetStamp(FileInfo file, out FileStamp stamp)
    {
        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                stamp = default;
                return false;
            }

            stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = default;
            return false;
        }
    }

    /// <summary>Remove a cached entry.</summary>
    private void Remove(CacheEntry entry)
    {
        this.Cache.Remove(entry.Path);
        this.UsageOrder.Remove(entry.UsageNode);
        this.SizeInBytesImpl -= entry.SizeInBytes;
    }

    /// <summary>Discard the oldest first-load candidates until their bookkeeping is bounded.</summary>
    private void TrimFirstLoads()
    {
        while (
            (this.FirstLoads.Count > this.MaxTrackedFirstLoads || this.FirstLoadOrder.Count > this.MaxTrackedFirstLoads * 2)
            && this.FirstLoadOrder.TryDequeue(out FirstLoadEntry candidate)
        )
        {
            if (this.FirstLoads.TryGetValue(candidate.Path, out FileStamp current) && current == candidate.Stamp)
                this.FirstLoads.Remove(candidate.Path);
        }
    }


    /*********
    ** Private models
    *********/
    /// <summary>A cached decoded image.</summary>
    private sealed class CacheEntry(string path, FileStamp stamp, int width, int height, Color[] pixels, long sizeInBytes)
    {
        public string Path { get; } = path;
        public FileStamp Stamp { get; } = stamp;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public Color[] Pixels { get; } = pixels;
        public long SizeInBytes { get; } = sizeInBytes;
        public LinkedListNode<CacheEntry> UsageNode { get; set; } = null!;
    }

    /// <summary>File metadata used to validate decoded pixels.</summary>
    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks);

    /// <summary>A first-load candidate in insertion order.</summary>
    private readonly record struct FirstLoadEntry(string Path, FileStamp Stamp);
}
