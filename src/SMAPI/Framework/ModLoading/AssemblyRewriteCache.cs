using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI.Toolkit.Framework.BundledModData;

namespace StardewModdingAPI.Framework.ModLoading;

/// <summary>A persistent content-addressed cache for mod assembly rewrite results.</summary>
internal sealed class AssemblyRewriteCache
{
    /*********
    ** Fields
    *********/
    /// <summary>The binary cache format version.</summary>
    private const int FormatVersion = 2;

    /// <summary>The file header which identifies an assembly rewrite cache entry.</summary>
    private const ulong FileMagic = 0x314357524950414D; // "MAPIRWC1" in little-endian order

    /// <summary>The maximum accepted cache entry size.</summary>
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>The maximum number of cached log messages for one assembly.</summary>
    private const int MaxMessages = 16_384;

    /// <summary>The maximum UTF-8 size of one cached log message.</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>The SHA-256 checksum size appended to each cache entry.</summary>
    private const int ChecksumBytes = 32;

    /// <summary>The environment-specific directory containing cache entries, if caching is available.</summary>
    private readonly string? CachePath;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="rootPath">The root directory in which to store rewrite caches.</param>
    /// <param name="environmentKey">A stable key for everything outside the source assembly which can affect rewrite output or diagnostics.</param>
    public AssemblyRewriteCache(string rootPath, string environmentKey)
    {
        try
        {
            Directory.CreateDirectory(rootPath);

            string environmentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentKey)));
            this.CachePath = Path.Combine(rootPath, environmentHash);
            Directory.CreateDirectory(this.CachePath);

            // A new SMAPI/game/rewriter environment makes every older entry unreachable. Remove those old
            // directories eagerly so local development builds and game updates don't grow the cache forever.
            foreach (string directory in Directory.EnumerateDirectories(rootPath))
            {
                if (!string.Equals(directory, this.CachePath, StringComparison.Ordinal))
                    this.TryDeleteDirectory(directory);
            }
        }
        catch
        {
            // A cache must never prevent mods from loading (e.g. for read-only or restricted profiles).
            this.CachePath = null;
        }
    }

    /// <summary>Get a content key for an assembly and its optional symbols.</summary>
    /// <param name="assemblyBytes">The exact source assembly bytes.</param>
    /// <param name="symbolBytes">The exact source symbol bytes, or <c>null</c> if none exist.</param>
    public string GetKey(byte[] assemblyBytes, byte[]? symbolBytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        this.AppendLength(hash, assemblyBytes.Length);
        hash.AppendData(assemblyBytes);
        this.AppendLength(hash, symbolBytes?.Length ?? -1);
        if (symbolBytes != null)
            hash.AppendData(symbolBytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Try to read a cached rewrite result.</summary>
    /// <param name="key">The content key returned by <see cref="GetKey"/>.</param>
    /// <param name="entry">The cached result, if found and valid.</param>
    public bool TryGet(string key, out AssemblyRewriteCacheEntry? entry)
    {
        entry = null;
        if (this.CachePath == null)
            return false;

        string path = this.GetEntryPath(key);
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
                return false;
            if (file.Length <= ChecksumBytes || file.Length > MaxEntryBytes)
                throw new InvalidDataException("Invalid assembly rewrite cache entry size.");

            using FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!this.HasValidChecksum(stream, out long payloadLength))
                throw new InvalidDataException("Assembly rewrite cache checksum doesn't match.");
            stream.Position = 0;

            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt64() != FileMagic || reader.ReadInt32() != FormatVersion)
                throw new InvalidDataException("Unrecognized assembly rewrite cache format.");

            bool changed = reader.ReadBoolean();
            ModWarning warnings = (ModWarning)reader.ReadInt32();

            int messageCount = reader.ReadInt32();
            if (messageCount < 0 || messageCount > MaxMessages)
                throw new InvalidDataException("Invalid assembly rewrite cache message count.");
            string[] messages = new string[messageCount];
            for (int i = 0; i < messages.Length; i++)
                messages[i] = this.ReadString(reader, payloadLength);

            byte[]? assemblyBytes = this.ReadBytes(reader, payloadLength, required: changed);
            byte[]? symbolBytes = this.ReadBytes(reader, payloadLength, required: false);
            if (stream.Position != payloadLength || (!changed && (assemblyBytes != null || symbolBytes != null)))
                throw new InvalidDataException("Invalid assembly rewrite cache payload.");

            entry = new AssemblyRewriteCacheEntry(changed, warnings, messages, assemblyBytes, symbolBytes);
            return true;
        }
        catch
        {
            // Treat partial/corrupt entries as misses and remove them so they aren't reparsed next launch.
            this.TryDeleteFile(path);
            return false;
        }
    }

    /// <summary>Persist a rewrite result.</summary>
    /// <param name="key">The content key returned by <see cref="GetKey"/>.</param>
    /// <param name="entry">The result to cache.</param>
    public void Store(string key, AssemblyRewriteCacheEntry entry)
    {
        if (this.CachePath == null)
            return;
        if (entry.Messages.Count > MaxMessages)
            return;

        string path = this.GetEntryPath(key);
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using SHA256 checksum = SHA256.Create();
                using (CryptoStream hashingStream = new(stream, checksum, CryptoStreamMode.Write, leaveOpen: true))
                {
                    using (BinaryWriter writer = new(hashingStream, Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(FileMagic);
                        writer.Write(FormatVersion);
                        writer.Write(entry.Changed);
                        writer.Write((int)entry.Warnings);
                        writer.Write(entry.Messages.Count);
                        foreach (string message in entry.Messages)
                            this.WriteString(writer, message);
                        this.WriteBytes(writer, entry.AssemblyBytes);
                        this.WriteBytes(writer, entry.SymbolBytes);
                    }
                    hashingStream.FlushFinalBlock();
                }

                stream.Write(checksum.Hash ?? throw new InvalidDataException("Couldn't calculate assembly rewrite cache checksum."));
                if (stream.Length > MaxEntryBytes)
                    throw new InvalidDataException("Assembly rewrite cache entry is too large.");
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Loading the original assembly remains the authoritative fallback.
            this.TryDeleteFile(temporaryPath);
        }
    }

    /// <summary>Remove a cached rewrite result.</summary>
    /// <param name="key">The content key returned by <see cref="GetKey"/>.</param>
    public void Remove(string key)
    {
        if (this.CachePath != null)
            this.TryDeleteFile(this.GetEntryPath(key));
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Append a fixed-width length to a content hash.</summary>
    private void AppendLength(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(bytes, value);
        hash.AppendData(bytes);
    }

    /// <summary>Get the path for an entry key.</summary>
    private string GetEntryPath(string key)
    {
        return Path.Combine(this.CachePath!, $"{key}.bin");
    }

    /// <summary>Read an optional bounded byte array.</summary>
    private byte[]? ReadBytes(BinaryReader reader, long payloadLength, bool required)
    {
        int length = reader.ReadInt32();
        if (length == -1 && !required)
            return null;
        if (length < 0 || length > MaxEntryBytes || length > payloadLength - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid assembly rewrite cache byte length.");

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return bytes;
    }

    /// <summary>Write an optional byte array.</summary>
    private void WriteBytes(BinaryWriter writer, byte[]? bytes)
    {
        writer.Write(bytes?.Length ?? -1);
        if (bytes != null)
            writer.Write(bytes);
    }

    /// <summary>Read a bounded UTF-8 string.</summary>
    private string ReadString(BinaryReader reader, long payloadLength)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxMessageBytes || length > payloadLength - reader.BaseStream.Position)
            throw new InvalidDataException("Assembly rewrite cache message is too long.");

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Validate the checksum appended to a cache entry.</summary>
    private bool HasValidChecksum(FileStream stream, out long payloadLength)
    {
        payloadLength = stream.Length - ChecksumBytes;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            stream.Position = 0;
            long remaining = payloadLength;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    return false;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            Span<byte> expected = stackalloc byte[ChecksumBytes];
            int offset = 0;
            while (offset < expected.Length)
            {
                int read = stream.Read(expected[offset..]);
                if (read <= 0)
                    return false;
                offset += read;
            }

            byte[] actual = hash.GetHashAndReset();
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Write a bounded UTF-8 string.</summary>
    private void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxMessageBytes)
            throw new InvalidDataException("Assembly rewrite cache message is too long.");
        this.WriteBytes(writer, bytes);
    }

    /// <summary>Delete a cache file without surfacing cleanup failures.</summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cache cleanup
        }
    }

    /// <summary>Delete an obsolete cache directory without surfacing cleanup failures.</summary>
    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cache cleanup
        }
    }
}

/// <summary>A cached mod assembly rewrite result.</summary>
internal sealed class AssemblyRewriteCacheEntry
{
    /// <summary>Whether the rewritten assembly differs from the source.</summary>
    public bool Changed { get; }

    /// <summary>The mod warning flags detected while analyzing the assembly.</summary>
    public ModWarning Warnings { get; }

    /// <summary>The trace messages produced while analyzing or rewriting the assembly.</summary>
    public IReadOnlyList<string> Messages { get; }

    /// <summary>The rewritten assembly bytes, or <c>null</c> when <see cref="Changed"/> is false.</summary>
    public byte[]? AssemblyBytes { get; }

    /// <summary>The rewritten symbol bytes, if any.</summary>
    public byte[]? SymbolBytes { get; }

    /// <summary>Construct an instance.</summary>
    public AssemblyRewriteCacheEntry(bool changed, ModWarning warnings, IReadOnlyList<string> messages, byte[]? assemblyBytes, byte[]? symbolBytes)
    {
        if (changed != (assemblyBytes != null))
            throw new ArgumentException("A changed rewrite result must contain assembly bytes, and an unchanged result must not.", nameof(assemblyBytes));
        if (!changed && symbolBytes != null)
            throw new ArgumentException("An unchanged rewrite result can't contain symbol bytes.", nameof(symbolBytes));

        this.Changed = changed;
        this.Warnings = warnings;
        this.Messages = messages;
        this.AssemblyBytes = assemblyBytes;
        this.SymbolBytes = symbolBytes;
    }
}

/// <summary>Metadata captured while analyzing and rewriting an assembly.</summary>
internal sealed class AssemblyRewriteResult
{
    /// <summary>Whether the rewritten assembly differs from the source.</summary>
    public bool Changed { get; }

    /// <summary>The mod warning flags detected while analyzing the assembly.</summary>
    public ModWarning Warnings { get; }

    /// <summary>The trace messages produced while analyzing or rewriting the assembly.</summary>
    public IReadOnlyList<string> Messages { get; }

    /// <summary>Construct an instance.</summary>
    public AssemblyRewriteResult(bool changed, ModWarning warnings, IReadOnlyList<string> messages)
    {
        this.Changed = changed;
        this.Warnings = warnings;
        this.Messages = messages;
    }
}
