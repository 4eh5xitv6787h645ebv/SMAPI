using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A bounded request to the pinned GitHub attestation verifier process.</summary>
internal sealed class GitHubAttestationProcessRequest
{
    internal const int MaximumArgumentCount = 64;
    internal const int MaximumArgumentLength = 4096;
    internal const int MaximumOutputBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public TimeSpan Timeout { get; }
    public int MaximumStandardOutputBytes { get; }
    public int MaximumStandardErrorBytes { get; }

    public GitHubAttestationProcessRequest(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes
    )
    {
        this.ExecutablePath = AssertCanonicalAbsolutePath(executablePath, nameof(executablePath));
        ArgumentNullException.ThrowIfNull(arguments);
        string[] argumentValues = arguments.ToArray();
        if (
            argumentValues.Length > MaximumArgumentCount
            || argumentValues.Any(value => value is null || value.Length > MaximumArgumentLength || value.Any(char.IsControl))
        )
        {
            throw new ArgumentException("The GitHub attestation verifier arguments aren't bounded literal values.", nameof(arguments));
        }
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumStandardOutputBytes is <= 0 or > MaximumOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumStandardOutputBytes));
        if (maximumStandardErrorBytes is <= 0 or > MaximumOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumStandardErrorBytes));

        this.Arguments = Array.AsReadOnly(argumentValues);
        this.Timeout = timeout;
        this.MaximumStandardOutputBytes = maximumStandardOutputBytes;
        this.MaximumStandardErrorBytes = maximumStandardErrorBytes;
    }

    private static string AssertCanonicalAbsolutePath(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            if (
                value.Length == 0
                || value.Any(char.IsControl)
                || !Path.IsPathFullyQualified(value)
                || !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal)
            )
            {
                throw new ArgumentException("The GitHub attestation verifier process path must be canonical and absolute.", parameterName);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The GitHub attestation verifier process path must be canonical and absolute.", parameterName);
        }

        return value;
    }
}

/// <summary>The bounded standard output of one successful verifier process.</summary>
internal sealed record GitHubAttestationProcessResult(string StandardOutput);

/// <summary>Runs the pinned GitHub attestation verifier without a shell or ambient user configuration.</summary>
internal interface IGitHubAttestationProcessRunner
{
    Task<GitHubAttestationProcessResult> RunAsync(
        GitHubAttestationProcessRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The production no-shell GitHub attestation verifier process boundary.</summary>
internal sealed class GitHubAttestationProcessRunner : IGitHubAttestationProcessRunner
{
    private const string DefaultSetSidPath = "/usr/bin/setsid";
    private const int ErrorNoSuchProcess = 3;
    private const int SignalKill = 9;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);
    private readonly string SetSidPath;
    private readonly Action<string>? AfterPrivateDirectoryCreatedForTesting;

    internal GitHubAttestationProcessRunner(
        string setSidPath = DefaultSetSidPath,
        Action<string>? afterPrivateDirectoryCreatedForTesting = null
    )
    {
        this.SetSidPath = setSidPath ?? throw new ArgumentNullException(nameof(setSidPath));
        this.AfterPrivateDirectoryCreatedForTesting = afterPrivateDirectoryCreatedForTesting;
    }

    public async Task<GitHubAttestationProcessResult> RunAsync(
        GitHubAttestationProcessRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.ExecutablePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        }
        GitHubAttestationPrivateDirectory privateDirectory;
        try
        {
            privateDirectory = GitHubAttestationPrivateDirectory.Create(this.AfterPrivateDirectoryCreatedForTesting);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        }

        await using (privateDirectory.ConfigureAwait(false))
        {
            SystemSetSidAuthority setSid;
            try
            {
                setSid = SystemSetSidAuthority.Open(this.SetSidPath);
            }
            catch (PackageSecurityException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            using SystemSetSidAuthority retainedSetSid = setSid;
            ProcessStartInfo startInfo = CreateStartInfo(request, privateDirectory.ProcPath, retainedSetSid.ProcPath);
            using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            int processGroupId;
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The verifier process didn't start.");
                processGroupId = process.Id;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception)
            {
                await KillAndObserveAsync(process, processGroupId, Task.CompletedTask, Task.CompletedTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }

            TaskCompletionSource<Exception> outputFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<byte[]> stdout = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                request.MaximumStandardOutputBytes,
                outputFailure
            );
            Task<byte[]> stderr = ReadBoundedAsync(
                process.StandardError.BaseStream,
                request.MaximumStandardErrorBytes,
                outputFailure
            );
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            Task completed = Task.WhenAll(exited, Task.WhenAll(stdout, stderr));
            using CancellationTokenSource timeoutCancellation = new();
            Task timedOut = Task.Delay(request.Timeout, timeoutCancellation.Token);
            Task cancelled = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan);

            try
            {
                await Task.WhenAny(completed, outputFailure.Task, timedOut, cancelled).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await KillAndObserveAsync(process, processGroupId, stdout, stderr).ConfigureAwait(false);
                    }
                    finally
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                if (!completed.IsCompleted)
                {
                    if (outputFailure.Task.IsCompleted)
                    {
                        Exception failure = await outputFailure.Task.ConfigureAwait(false);
                        await KillAndObserveAsync(process, processGroupId, stdout, stderr).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw MapOutputFailure(failure);
                    }

                    await KillAndObserveAsync(process, processGroupId, stdout, stderr).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PackageSecurityException("The GitHub attestation verifier timed out.");
                }

                byte[] standardOutput;
                byte[] standardError;
                try
                {
                    standardOutput = await stdout.ConfigureAwait(false);
                    standardError = await stderr.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await ObserveAsync(stdout, stderr).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw MapOutputFailure(exception);
                }

                string output;
                try
                {
                    output = StrictUtf8.GetString(standardOutput);
                    _ = StrictUtf8.GetString(standardError);
                }
                catch (DecoderFallbackException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PackageSecurityException("The GitHub attestation verifier returned invalid UTF-8 output.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new PackageSecurityException("The GitHub attestation verifier rejected the selected release evidence.");
                return new GitHubAttestationProcessResult(output);
            }
            finally
            {
                timeoutCancellation.Cancel();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        GitHubAttestationProcessRequest request,
        string privateDirectory,
        string setSidProcPath
    )
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = setSidProcPath,
            WorkingDirectory = privateDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(request.ExecutablePath);
        foreach (string argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = privateDirectory;
        startInfo.Environment["GH_CONFIG_DIR"] = privateDirectory;
        startInfo.Environment["XDG_CONFIG_HOME"] = privateDirectory;
        startInfo.Environment["XDG_CACHE_HOME"] = privateDirectory;
        startInfo.Environment["TMPDIR"] = privateDirectory;
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_PAGER"] = "";
        startInfo.Environment["PAGER"] = "";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        return startInfo;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        TaskCompletionSource<Exception> failure
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using MemoryStream output = new(Math.Min(maximumBytes, 16 * 1024));
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > maximumBytes)
                    throw new OutputLimitExceededException();
                output.Write(buffer, 0, read);
            }
        }
        catch (Exception exception)
        {
            failure.TrySetResult(exception);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static PackageSecurityException MapOutputFailure(Exception exception)
    {
        return exception is OutputLimitExceededException
            ? new PackageSecurityException("The GitHub attestation verifier produced excessive output.")
            : new PackageSecurityException("The GitHub attestation verifier output couldn't be read safely.");
    }

    private static async Task KillAndObserveAsync(Process process, int processGroupId, Task stdout, Task stderr)
    {
        KillProcessGroup(processGroupId);
        TryKillDirectProcess(process);

        Task reaped = ReapAsync(process);
        Task observed = ObserveAsync(stdout, stderr);
        Task cleanup = Task.WhenAll(reaped, observed);
        Task deadline = Task.Delay(TeardownTimeout);
        if (await Task.WhenAny(cleanup, deadline).ConfigureAwait(false) == cleanup)
        {
            await cleanup.ConfigureAwait(false);
            return;
        }

        TryClose(process.StandardOutput);
        TryClose(process.StandardError);
        KillProcessGroup(processGroupId);
        TryKillDirectProcess(process);
        ObserveEventually(cleanup);
    }

    private static void KillProcessGroup(int processGroupId)
    {
        if (processGroupId <= 0)
            return;
        int result = kill(-processGroupId, SignalKill);
        if (result != 0 && Marshal.GetLastWin32Error() != ErrorNoSuchProcess)
            return;
    }

    private static void TryKillDirectProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ReapAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ObserveAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static void ObserveEventually(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static void TryClose(StreamReader reader)
    {
        try
        {
            reader.Close();
        }
        catch (Exception)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private sealed class SystemSetSidAuthority : IDisposable
    {
        private const int AtEmptyPath = 0x1000;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint StatxBasicStats = 0x7ff;
        private const ushort FileTypeMask = 0xf000;
        private const ushort FileTypeRegular = 0x8000;
        private const int RequiredReadExecuteMode = 0x16d; // 0555
        private const int GroupOtherWriteMode = 0x12; // 0022
        private const int SpecialMode = 0xe00; // 07000
        private const long MaximumSetSidBytes = 4L * 1024 * 1024;

        private readonly LinuxAnchoredFileSystem FileSystem;
        private readonly LinuxAnchoredFile File;

        public string ProcPath { get; }

        private SystemSetSidAuthority(LinuxAnchoredFileSystem fileSystem, LinuxAnchoredFile file)
        {
            this.FileSystem = fileSystem;
            this.File = file;
            this.ProcPath = $"/proc/{Environment.ProcessId}/fd/{checked((int)file.Handle.DangerousGetHandle())}";
        }

        public static SystemSetSidAuthority Open(string path)
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

            LinuxAnchoredFileSystem? fileSystem = null;
            LinuxAnchoredFile? file = null;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string? parent = Path.GetDirectoryName(fullPath);
                string leaf = Path.GetFileName(fullPath);
                if (
                    string.IsNullOrEmpty(parent)
                    || string.IsNullOrEmpty(leaf)
                    || !string.Equals(fullPath, path, StringComparison.Ordinal)
                )
                {
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                }

                fileSystem = new LinuxAnchoredFileSystem(parent);
                file = fileSystem.OpenRegularFileForRead(leaf);
                if (
                    statx(
                        file.Handle,
                        "",
                        AtEmptyPath | AtSymlinkNoFollow,
                        StatxBasicStats,
                        out SystemSetSidStatx metadata
                    ) != 0
                )
                {
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                }

                int mode = metadata.Mode;
                if (
                    (mode & FileTypeMask) != FileTypeRegular
                    || metadata.UserId != 0
                    || metadata.LinkCount != 1
                    || metadata.Size is 0 or > MaximumSetSidBytes
                    || (mode & RequiredReadExecuteMode) != RequiredReadExecuteMode
                    || (mode & GroupOtherWriteMode) != 0
                    || (mode & SpecialMode) != 0
                )
                {
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                }

                SystemSetSidAuthority result = new(fileSystem, file);
                fileSystem = null;
                file = null;
                return result;
            }
            catch (PackageSecurityException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or PathTooLongException
            )
            {
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }
            finally
            {
                file?.Dispose();
                fileSystem?.Dispose();
            }
        }

        public void Dispose()
        {
            this.File.Dispose();
            this.FileSystem.Dispose();
        }

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int statx(
            SafeFileHandle directory,
            string path,
            int flags,
            uint mask,
            out SystemSetSidStatx data
        );

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct SystemSetSidStatx
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCount;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
            public ushort Spare0;
            public ulong Inode;
            public ulong Size;
            public ulong Blocks;
            public ulong AttributesMask;
        }
    }

    private sealed class OutputLimitExceededException : Exception
    {
    }
}
