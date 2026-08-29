using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>One caller-selected release asset opened through a stable ordinary-file handle.</summary>
internal sealed class RetainedReleaseAssetFile : IDisposable
{
    private readonly LinuxAnchoredFileSystem? LinuxFileSystem;
    private readonly LinuxAnchoredFile? LinuxFile;
    private readonly bool OwnsLinuxFileSystem;
    private readonly FileStream? PortableFile;
    private readonly long CapturedSize;

    /// <summary>The exact size observed when the retained handle was opened.</summary>
    public long Size => this.CapturedSize;

    private RetainedReleaseAssetFile(
        LinuxAnchoredFileSystem? linuxFileSystem,
        LinuxAnchoredFile? linuxFile,
        FileStream? portableFile,
        long capturedSize,
        bool ownsLinuxFileSystem = true
    )
    {
        this.LinuxFileSystem = linuxFileSystem;
        this.LinuxFile = linuxFile;
        this.PortableFile = portableFile;
        this.CapturedSize = capturedSize;
        this.OwnsLinuxFileSystem = ownsLinuxFileSystem;
    }

    /// <summary>Adopt one already-retained Linux asset captured by a stricter aggregate authority.</summary>
    internal static RetainedReleaseAssetFile Adopt(LinuxAnchoredFileSystem fileSystem, LinuxAnchoredFile file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(file);
        return new RetainedReleaseAssetFile(fileSystem, file, null, file.Identity.Size, ownsLinuxFileSystem: false);
    }

    /// <summary>
    /// Open an absolute or relative path as one retained ordinary-file handle. On Linux, every directory segment and
    /// the leaf are opened without following links, the leaf must have exactly one link, and special files are opened
    /// nonblocking and rejected before any read.
    /// </summary>
    public static RetainedReleaseAssetFile Open(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"The {description} path is required.", nameof(path));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A release-asset description is required.", nameof(description));

        string fullPath = Path.GetFullPath(path);
        string? parentPath = Path.GetDirectoryName(fullPath);
        string leaf = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(leaf))
            throw new PackageSecurityException($"The selected {description} path isn't a regular file path.");

        if (OperatingSystem.IsLinux())
        {
            LinuxAnchoredFileSystem? fileSystem = null;
            LinuxAnchoredFile? file = null;
            try
            {
                fileSystem = new LinuxAnchoredFileSystem(parentPath);
                file = fileSystem.OpenRegularFileForRead(leaf);
                return new RetainedReleaseAssetFile(fileSystem, file, null, file.Identity.Size);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                file?.Dispose();
                fileSystem?.Dispose();
                throw new PackageSecurityException($"The selected {description} isn't a safe accessible single-link regular file.", ex);
            }
        }

        try
        {
            FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            try
            {
                return new RetainedReleaseAssetFile(null, null, stream, stream.Length);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackageSecurityException($"The selected {description} isn't an accessible regular file.", ex);
        }
    }

    /// <summary>Read exact bounded bytes through the retained handle.</summary>
    public async Task<byte[]> ReadAllBytesAsync(int maximumBytes, bool requireNonEmpty, CancellationToken cancellationToken)
    {
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (this.CapturedSize > maximumBytes || (requireNonEmpty && this.CapturedSize <= 0))
            throw new PackageSecurityException("A selected release asset has an invalid or excessive size.");

        try
        {
            if (this.LinuxFileSystem is not null)
                return this.LinuxFileSystem.ReadAllBytes(this.LinuxFile!, maximumBytes, cancellationToken);

            FileStream stream = this.PortableFile ?? throw new ObjectDisposedException(nameof(RetainedReleaseAssetFile));
            byte[] result = new byte[checked((int)this.CapturedSize)];
            stream.Position = 0;
            int offset = 0;
            while (offset < result.Length)
            {
                int count = await stream.ReadAsync(result.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException("The selected release asset ended early.");
                offset += count;
            }
            if (stream.Length != this.CapturedSize || await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new IOException("The selected release asset changed while it was read.");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new PackageSecurityException("The selected release asset changed or failed validation while it was read.", ex);
        }
    }

    /// <summary>Read bounded text through the retained handle using strict UTF-8.</summary>
    public async Task<string> ReadUtf8TextAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] bytes = await this.ReadAllBytesAsync(maximumBytes, requireNonEmpty: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new PackageSecurityException("A selected release metadata document isn't valid UTF-8.", ex);
        }
    }

    /// <summary>Copy and hash exact bounded bytes through the retained handle.</summary>
    public async Task<string> CopyAndHashAsync(Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (this.CapturedSize <= 0 || this.CapturedSize > maximumBytes)
            throw new PackageSecurityException("The selected installer package has an invalid or excessive size.");

        try
        {
            if (this.LinuxFileSystem is not null)
                return await this.LinuxFileSystem.CopyAndHashAsync(this.LinuxFile!, destination, maximumBytes, cancellationToken).ConfigureAwait(false);

            FileStream stream = this.PortableFile ?? throw new ObjectDisposedException(nameof(RetainedReleaseAssetFile));
            if (!destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0)
                throw new ArgumentException("The copy destination must be writable, seekable, empty, and positioned at zero.", nameof(destination));
            stream.Position = 0;
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long copied = 0;
                while (copied < this.CapturedSize)
                {
                    int count = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, this.CapturedSize - copied)),
                        cancellationToken
                    ).ConfigureAwait(false);
                    if (count == 0)
                        throw new EndOfStreamException("The selected installer package ended early.");
                    copied = checked(copied + count);
                    hasher.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (stream.Length != this.CapturedSize || await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    throw new IOException("The selected installer package changed while it was copied.");
                return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new PackageSecurityException("The selected installer package changed or failed validation while it was staged.", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.PortableFile?.Dispose();
        this.LinuxFile?.Dispose();
        if (this.OwnsLinuxFileSystem)
            this.LinuxFileSystem?.Dispose();
    }
}


/// <summary>Opens exact logical release-asset names from one retained aggregate authority.</summary>
internal interface IRetainedReleaseAssetSource : IDisposable
{
    RetainedReleaseAssetFile Open(string logicalName, string description);
}
