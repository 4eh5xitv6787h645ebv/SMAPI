using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The exact immutable local bundle descriptor which the runner may bridge for GitHub CLI.</summary>
internal sealed class GitHubAttestationProcessBundleAuthority
{
    public string ProcPath { get; }

    public GitHubAttestationProcessBundleAuthority(string procPath)
    {
        this.ProcPath = GitHubAttestationProcessRequest.AssertCurrentProcessDescriptorPath(
            procPath,
            nameof(procPath),
            "attestation bundle"
        );
    }
}

/// <summary>A bounded request to the pinned GitHub attestation verifier process.</summary>
internal sealed class GitHubAttestationProcessRequest
{
    internal const string BundlePathPlaceholder = "SMAPI_INTERNAL_VERIFIED_ATTESTATION_BUNDLE_JSONL_PATH";
    internal const int MaximumArgumentCount = 64;
    internal const int MaximumArgumentLength = 4096;
    internal const int MaximumOutputBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public TimeSpan Timeout { get; }
    public int MaximumStandardOutputBytes { get; }
    public int MaximumStandardErrorBytes { get; }
    public GitHubAttestationProcessBundleAuthority? BundleAuthority { get; }
    public int? BundleArgumentIndex { get; }

    public GitHubAttestationProcessRequest(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        GitHubAttestationProcessBundleAuthority? bundleAuthority = null
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

        int[] bundleSlots = argumentValues
            .Select((value, index) => (value, index))
            .Where(value => string.Equals(value.value, BundlePathPlaceholder, StringComparison.Ordinal))
            .Select(value => value.index)
            .ToArray();
        if ((bundleAuthority is null && bundleSlots.Length != 0) || (bundleAuthority is not null && bundleSlots.Length != 1))
        {
            throw new ArgumentException(
                "The GitHub attestation verifier bundle authority must have one exact reserved argument slot.",
                nameof(arguments)
            );
        }

        this.Arguments = Array.AsReadOnly(argumentValues);
        this.Timeout = timeout;
        this.MaximumStandardOutputBytes = maximumStandardOutputBytes;
        this.MaximumStandardErrorBytes = maximumStandardErrorBytes;
        this.BundleAuthority = bundleAuthority;
        this.BundleArgumentIndex = bundleSlots.Length == 1 ? bundleSlots[0] : null;
    }

    internal static string AssertCurrentProcessDescriptorPath(string? value, string parameterName, string authorityName)
    {
        string pathValue = value ?? "";
        string prefix = $"/proc/{Environment.ProcessId}/fd/";
        string descriptor = pathValue.StartsWith(prefix, StringComparison.Ordinal) ? pathValue[prefix.Length..] : "";
        if (
            !int.TryParse(descriptor, NumberStyles.None, CultureInfo.InvariantCulture, out int descriptorNumber)
            || descriptorNumber < 0
            || !string.Equals(descriptor, descriptorNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                $"The {authorityName} must be exposed through this process's retained file descriptor.",
                parameterName
            );
        }
        return pathValue;
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
    private const string DefaultFlockPath = "/usr/bin/flock";
    private const string BundleBridgeFileName = "verified-attestation-bundle.jsonl";
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FileTypeRegular = 0x8000;
    private const int GetSeals = 1034;
    private const int GateHelperTimeoutSeconds = 3;
    private const int RequiredImmutableSeals = 0x0f;
    private const int SignalKill = 9;
    private const int ErrorNoProcess = 3;
    private const int ErrorFunctionNotImplemented = 38;
    private const int MaximumProcEntries = 32_768;
    private const long SystemCallPidfdSendSignal = 424;
    private const long SystemCallPidfdOpen = 434;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim StartupBoundary = new(1, 1);
    private static readonly object QuarantinedGateLock = new();
    // Catastrophic gated-helper retention is intentionally fail-stop: one locked FD is kept for the process lifetime,
    // every later launch requires a restart, and containment after this parent process itself exits isn't claimed.
    private static SafeFileHandle? QuarantinedGate;
    private readonly string SetSidPath;
    private readonly string FlockPath;
    private readonly Action<string>? AfterPrivateDirectoryCreatedForTesting;
    private readonly Action<string>? AfterBundleBridgeCreatedForTesting;
    private readonly Action<string>? BeforeProcessStartForTesting;
    private readonly Action<int>? BeforeLeaderPidfdOpenForTesting;
    private readonly Func<ProcessSessionIdentity, ProcessSessionIdentity>? TransformGateHelperIdentityForTesting;

    internal GitHubAttestationProcessRunner(
        string setSidPath = DefaultSetSidPath,
        string flockPath = DefaultFlockPath,
        Action<string>? afterPrivateDirectoryCreatedForTesting = null,
        Action<string>? afterBundleBridgeCreatedForTesting = null,
        Action<string>? beforeProcessStartForTesting = null,
        Action<int>? beforeLeaderPidfdOpenForTesting = null,
        Func<ProcessSessionIdentity, ProcessSessionIdentity>? transformGateHelperIdentityForTesting = null
    )
    {
        this.SetSidPath = setSidPath ?? throw new ArgumentNullException(nameof(setSidPath));
        this.FlockPath = flockPath ?? throw new ArgumentNullException(nameof(flockPath));
        this.AfterPrivateDirectoryCreatedForTesting = afterPrivateDirectoryCreatedForTesting;
        this.AfterBundleBridgeCreatedForTesting = afterBundleBridgeCreatedForTesting;
        this.BeforeProcessStartForTesting = beforeProcessStartForTesting;
        this.BeforeLeaderPidfdOpenForTesting = beforeLeaderPidfdOpenForTesting;
        this.TransformGateHelperIdentityForTesting = transformGateHelperIdentityForTesting;
    }

    public async Task<GitHubAttestationProcessResult> RunAsync(
        GitHubAttestationProcessRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StartupBoundary.Wait(0))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        try
        {
            lock (QuarantinedGateLock)
            {
                if (QuarantinedGate is not null)
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }
            return await this.RunSerializedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StartupBoundary.Release();
        }
    }

    private async Task<GitHubAttestationProcessResult> RunSerializedAsync(
        GitHubAttestationProcessRequest request,
        CancellationToken cancellationToken
    )
    {
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
            string[] processArguments;
            try
            {
                processArguments = this.CreateProcessArguments(request, privateDirectory);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PackageSecurityException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or DecoderFallbackException
                    or IOException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException
                    or UnauthorizedAccessException
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }

            SystemExecutableAuthority setSid;
            SystemExecutableAuthority flock;
            try
            {
                setSid = SystemExecutableAuthority.Open(this.SetSidPath);
                try
                {
                    flock = SystemExecutableAuthority.Open(this.FlockPath);
                }
                catch
                {
                    setSid.Dispose();
                    throw;
                }
            }
            catch (PackageSecurityException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            using SystemExecutableAuthority retainedSetSid = setSid;
            using SystemExecutableAuthority retainedFlock = flock;
            using PreExecGate gate = PreExecGate.CreateLocked();
            ProcessStartInfo startInfo = CreateStartInfo(
                request,
                processArguments,
                privateDirectory.ProcPath,
                retainedSetSid.ProcPath,
                retainedFlock.ProcPath,
                gate.ProcPath,
                GateHelperTimeoutSeconds
            );
            // Prove pidfd support before spawning and reserve one descriptor slot for the exact leader handle.
            using SafeFileHandle leaderPidfdReservation = OpenRequiredPidfd(Environment.ProcessId);
            // The retained gate prevents the verifier from executing until its exact leader pidfd is acquired.
            using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = false };
            int? startedSessionId = null;
            SafeFileHandle? leaderPidfd = null;
            try
            {
                if (request.BundleAuthority is not null)
                {
                    this.BeforeProcessStartForTesting?.Invoke(processArguments[request.BundleArgumentIndex!.Value]);
                    privateDirectory.AssertBundleBridge(request.BundleAuthority.ProcPath);
                    AssertExactBundleBridgeAuthority(request.BundleAuthority.ProcPath, processArguments[request.BundleArgumentIndex!.Value]);
                }
                if (!process.Start())
                    throw new InvalidOperationException("The verifier process didn't start.");
                leaderPidfdReservation.Dispose();
                try
                {
                    startedSessionId = process.Id;
                    this.BeforeLeaderPidfdOpenForTesting?.Invoke(startedSessionId.Value);
                    leaderPidfd = OpenRequiredPidfd(startedSessionId.Value);
                    bool exactGateHelper = await WaitForExactGateHelperAsync(
                        startedSessionId.Value,
                        leaderPidfd,
                        retainedFlock.Identity,
                        gate.Identity,
                        TimeSpan.FromMilliseconds((GateHelperTimeoutSeconds * 1000) - 250),
                        this.TransformGateHelperIdentityForTesting
                    ).ConfigureAwait(false);
                    if (!exactGateHelper)
                        throw new PackageSecurityException("The GitHub attestation verifier couldn't retain exact process authority.");
                    cancellationToken.ThrowIfCancellationRequested();
                    gate.Release();
                }
                catch (Exception)
                {
                    bool cleaned = await ReapGatedStartupProcessAsync(
                        process,
                        TeardownTimeout
                    ).ConfigureAwait(false);
                    if (!cleaned)
                        gate.QuarantineLocked();
                    leaderPidfd?.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PackageSecurityException(
                        cleaned
                            ? "The GitHub attestation verifier couldn't retain exact process authority."
                            : "The GitHub attestation verifier couldn't retain or terminate exact process authority."
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PackageSecurityException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or DecoderFallbackException
                    or IOException
                    or InvalidOperationException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException
                    or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }
            int sessionId = startedSessionId
                ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't retain exact process authority.");
            using SafeFileHandle retainedLeaderPidfd = leaderPidfd
                ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't retain exact process authority.");

            Task stdout = Task.CompletedTask;
            Task stderr = Task.CompletedTask;
            Task<byte[]>? stdoutBytes = null;
            Task<byte[]>? stderrBytes = null;
            try
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                }

                TaskCompletionSource<Exception> outputFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stdoutBytes = ReadBoundedAsync(
                    process.StandardOutput.BaseStream,
                    request.MaximumStandardOutputBytes,
                    outputFailure
                );
                stderrBytes = ReadBoundedAsync(
                    process.StandardError.BaseStream,
                    request.MaximumStandardErrorBytes,
                    outputFailure
                );
                stdout = stdoutBytes;
                stderr = stderrBytes;
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
                        cancellationToken.ThrowIfCancellationRequested();

                    if (!completed.IsCompleted)
                    {
                        if (outputFailure.Task.IsCompleted)
                        {
                            Exception failure = await outputFailure.Task.ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw MapOutputFailure(failure);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        throw new PackageSecurityException("The GitHub attestation verifier timed out.");
                    }

                    byte[] standardOutput;
                    byte[] standardError;
                    try
                    {
                        standardOutput = await stdoutBytes.ConfigureAwait(false);
                        standardError = await stderrBytes.ConfigureAwait(false);
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
                        throw new PackageSecurityException(
                            PackageSecurityFailureKind.ProvenanceRejected,
                            "The GitHub attestation verifier rejected the selected release evidence."
                        );
                    return new GitHubAttestationProcessResult(output);
                }
                finally
                {
                    timeoutCancellation.Cancel();
                }
            }
            finally
            {
                bool cleaned = await KillAndObserveAsync(
                    process,
                    sessionId,
                    retainedLeaderPidfd,
                    stdout,
                    stderr
                ).ConfigureAwait(false);
                if (!cleaned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PackageSecurityException("The GitHub attestation verifier process couldn't be terminated safely.");
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        GitHubAttestationProcessRequest request,
        IReadOnlyList<string> processArguments,
        string privateDirectory,
        string setSidProcPath,
        string flockProcPath,
        string gateProcPath,
        int gateHelperTimeoutSeconds
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
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(flockProcPath);
        startInfo.ArgumentList.Add("--exclusive");
        startInfo.ArgumentList.Add("--close");
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(gateHelperTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(gateProcPath);
        startInfo.ArgumentList.Add(request.ExecutablePath);
        foreach (string argument in processArguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = privateDirectory;
        startInfo.Environment["GH_CONFIG_DIR"] = privateDirectory;
        startInfo.Environment["XDG_CONFIG_HOME"] = privateDirectory;
        startInfo.Environment["XDG_CACHE_HOME"] = privateDirectory;
        startInfo.Environment["XDG_RUNTIME_DIR"] = privateDirectory;
        startInfo.Environment["TMPDIR"] = privateDirectory;
        startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={privateDirectory}/session-bus-unavailable";
        startInfo.Environment["DBUS_SYSTEM_BUS_ADDRESS"] = $"unix:path={privateDirectory}/system-bus-unavailable";
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

    /// <summary>
    /// GitHub CLI 2.92 selects the local bundle decoder from its filename extension. Keep the immutable descriptor as
    /// the sole byte authority, but expose it through one fixed extension-bearing symlink below the retained private
    /// directory for the lifetime of the verifier process.
    /// </summary>
    private string[] CreateProcessArguments(
        GitHubAttestationProcessRequest request,
        GitHubAttestationPrivateDirectory privateDirectory
    )
    {
        string[] arguments = request.Arguments.ToArray();
        if (request.BundleAuthority is null)
            return arguments;

        string retainedBundlePath = request.BundleAuthority.ProcPath;
        string bridgePath = privateDirectory.CreateBundleBridge(
            BundleBridgeFileName,
            retainedBundlePath,
            this.AfterBundleBridgeCreatedForTesting
        );
        AssertExactBundleBridgeAuthority(retainedBundlePath, bridgePath);
        arguments[request.BundleArgumentIndex!.Value] = bridgePath;
        return arguments;
    }

    private static void AssertExactBundleBridgeAuthority(string retainedBundlePath, string bridgePath)
    {
        using SafeFileHandle retainedBundle = OpenBundleForIdentity(retainedBundlePath);
        using SafeFileHandle bridgedBundle = OpenBundleForIdentity(bridgePath);
        BundleFileIdentity retainedIdentity = GetBundleFileIdentity(retainedBundle);
        BundleFileIdentity bridgedIdentity = GetBundleFileIdentity(bridgedBundle);
        if (!retainedIdentity.Equals(bridgedIdentity))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
    }

    private static SafeFileHandle OpenBundleForIdentity(string path)
    {
        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    private static BundleFileIdentity GetBundleFileIdentity(SafeFileHandle handle)
    {
        if (statx(handle, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasicStats, out BundleStatx data) != 0)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        int seals = fcntl(handle, GetSeals, 0);
        if ((data.Mode & FileTypeMask) != FileTypeRegular || seals < 0 || (seals & RequiredImmutableSeals) != RequiredImmutableSeals)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        return new BundleFileIdentity(data.Inode, data.DeviceMajor, data.DeviceMinor, data.Size, data.Mode, seals);
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

    /// <summary>
    /// Wait until the pidfd-retained session leader is the exact retained flock executable and has opened the exact
    /// parent-locked gate. Signal-zero checks on both sides bind the numeric proc observations to the live pidfd task.
    /// </summary>
    private static async Task<bool> WaitForExactGateHelperAsync(
        int processId,
        SafeFileHandle leaderPidfd,
        KernelFileIdentity expectedExecutable,
        KernelFileIdentity expectedGate,
        TimeSpan timeout,
        Func<ProcessSessionIdentity, ProcessSessionIdentity>? transformIdentityForTesting
    )
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (!IsPidfdAlive(leaderPidfd))
                return false;

            ProcessSessionIdentity? process = ReadProcessIdentity(processId);
            if (process is not null && transformIdentityForTesting is not null)
                process = transformIdentityForTesting(process.Value);
            KernelFileIdentity? executable = TryReadPathIdentity($"/proc/{processId}/exe");
            int? gateDescriptorCount = CountExactOpenDescriptors(processId, expectedGate);
            if (IsExactGateHelperSnapshot(
                processId,
                process,
                expectedExecutable,
                executable,
                gateDescriptorCount,
                IsPidfdAlive(leaderPidfd)
            ))
            {
                return true;
            }

            TimeSpan remaining = timeout - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10, remaining.TotalMilliseconds))).ConfigureAwait(false);
        }
        return false;
    }

    internal static bool IsExactGateHelperSnapshot(
        int processId,
        ProcessSessionIdentity? process,
        KernelFileIdentity expectedExecutable,
        KernelFileIdentity? executable,
        int? gateDescriptorCount,
        bool pidfdAliveAfter
    )
    {
        return process is not null
            && process.Value.ProcessId == processId
            && process.Value.ProcessGroupId == processId
            && process.Value.SessionId == processId
            && executable == expectedExecutable
            && gateDescriptorCount == 1
            && pidfdAliveAfter;
    }

    private static int? CountExactOpenDescriptors(int processId, KernelFileIdentity expected)
    {
        try
        {
            int count = 0;
            int matches = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries($"/proc/{processId}/fd"))
            {
                if (++count > 1024)
                    return null;
                if (TryReadPathIdentity(path) == expected)
                    matches++;
            }
            return matches;
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException
        )
        {
        }
        return null;
    }

    private static KernelFileIdentity? TryReadPathIdentity(string path)
    {
        try
        {
            if (statx_path(-100, path, 0, StatxBasicStats, out BundleStatx metadata) != 0)
                return null;
            return new KernelFileIdentity(metadata.Inode, metadata.DeviceMajor, metadata.DeviceMinor);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
        )
        {
            return null;
        }
    }

    private static KernelFileIdentity GetKernelFileIdentity(SafeFileHandle handle)
    {
        if (statx(handle, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasicStats, out BundleStatx metadata) != 0)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        return new KernelFileIdentity(metadata.Inode, metadata.DeviceMajor, metadata.DeviceMinor);
    }

    private static bool IsPidfdAlive(SafeFileHandle pidfd)
    {
        long result = syscall_pidfd_send_signal(SystemCallPidfdSendSignal, pidfd, 0, IntPtr.Zero, 0);
        return result == 0;
    }

    /// <summary>
    /// Reap the retained system helper while the pre-exec gate stays locked. Its bounded flock timeout is the only
    /// termination mechanism here; no numeric process authority or descendant scan is used before exact validation.
    /// </summary>
    private static async Task<bool> ReapGatedStartupProcessAsync(Process process, TimeSpan timeout)
    {
        TryClose(process.StandardInput);
        TryClose(process.StandardOutput);
        TryClose(process.StandardError);
        Task<bool> reaped = ReapStartupAsync(process);
        if (await Task.WhenAny(reaped, Task.Delay(timeout)).ConfigureAwait(false) != reaped)
        {
            ObserveEventually(reaped);
            return false;
        }
        return await reaped.ConfigureAwait(false);
    }

    private static async Task<bool> ReapStartupAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> KillAndObserveAsync(
        Process process,
        int sessionId,
        SafeFileHandle leaderPidfd,
        Task stdout,
        Task stderr
    )
    {
        Task reaped = ReapAsync(process);
        Task observed = ObserveAsync(stdout, stderr);
        Task cleanup = Task.WhenAll(reaped, observed);
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TeardownTimeout)
        {
            bool? sessionExists = TrySignalExactSessionMembers(sessionId, deadline);
            if (!sessionExists.HasValue || !TrySignalPidfd(leaderPidfd))
                break;

            if (cleanup.IsCompleted && sessionExists.Value == false)
            {
                await cleanup.ConfigureAwait(false);
                return true;
            }

            TimeSpan remaining = TeardownTimeout - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            Task shortDelay = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(25, remaining.TotalMilliseconds)));
            if (cleanup.IsCompleted)
                await shortDelay.ConfigureAwait(false);
            else
                await Task.WhenAny(cleanup, shortDelay).ConfigureAwait(false);
        }

        TryClose(process.StandardOutput);
        TryClose(process.StandardError);
        _ = TrySignalExactSessionMembers(sessionId, deadline);
        _ = TrySignalPidfd(leaderPidfd);
        ObserveEventually(cleanup);
        return false;
    }

    /// <summary>
    /// Signal only pidfd-retained tasks whose PID, process group, session, and start time stay unchanged across the
    /// numeric /proc lookup. A wholly new same-UID session can theoretically reuse the numeric session ID between
    /// bounded scans; that non-hostile-same-UID residual is not claimed as race-free process containment.
    /// </summary>
    private static bool? TrySignalExactSessionMembers(int sessionId, Stopwatch deadline)
    {
        if (sessionId <= 0)
            return false;
        try
        {
            bool found = false;
            int count = 0;
            foreach (string path in Directory.EnumerateDirectories("/proc"))
            {
                if (++count > MaximumProcEntries || deadline.Elapsed >= TeardownTimeout)
                    return null;
                if (!int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
                    continue;
                SessionMemberSignalResult result = TrySignalValidatedSessionMember(
                    processId,
                    sessionId,
                    ReadProcessIdentity,
                    TryOpenPidfd,
                    SendSignalToPidfd
                );
                if (result == SessionMemberSignalResult.Failed)
                    return null;
                if (result is SessionMemberSignalResult.Signaled or SessionMemberSignalResult.GoneOrStale)
                    found = true;
            }
            return found;
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
                or IOException
                or PackageSecurityException
                or UnauthorizedAccessException
        )
        {
            return null;
        }
    }

    /// <summary>Open, revalidate, and signal one exact session task. The delegates are an internal deterministic race seam.</summary>
    internal static SessionMemberSignalResult TrySignalValidatedSessionMember(
        int processId,
        int sessionId,
        Func<int, ProcessSessionIdentity?> readIdentity,
        Func<int, SafeFileHandle?> openPidfd,
        Func<SafeFileHandle, int> sendSignal
    )
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(openPidfd);
        ArgumentNullException.ThrowIfNull(sendSignal);

        ProcessSessionIdentity? before = readIdentity(processId);
        if (before is null || !before.Value.IsSessionMember(processId, sessionId))
            return SessionMemberSignalResult.NotMember;

        using SafeFileHandle? pidfd = openPidfd(processId);
        if (pidfd is null)
            return SessionMemberSignalResult.GoneOrStale;
        if (pidfd.IsInvalid || pidfd.IsClosed)
            return SessionMemberSignalResult.Failed;

        ProcessSessionIdentity? after = readIdentity(processId);
        if (after is null || after.Value != before.Value || !after.Value.IsSessionMember(processId, sessionId))
            return SessionMemberSignalResult.GoneOrStale;

        int error = sendSignal(pidfd);
        return error switch
        {
            0 => SessionMemberSignalResult.Signaled,
            ErrorNoProcess => SessionMemberSignalResult.GoneOrStale,
            _ => SessionMemberSignalResult.Failed
        };
    }

    private static ProcessSessionIdentity? ReadProcessIdentity(int processId)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
            int commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                return null;
            string[] fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (
                fields.Length < 20
                || !int.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int group)
                || !int.TryParse(fields[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int session)
                || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startTime)
            )
            {
                return null;
            }
            return new ProcessSessionIdentity(processId, group, session, startTime);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static SafeFileHandle OpenRequiredPidfd(int processId)
    {
        SafeFileHandle? pidfd = TryOpenPidfd(processId, allowGone: false);
        return pidfd ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't retain exact process authority.");
    }

    private static SafeFileHandle? TryOpenPidfd(int processId)
    {
        return TryOpenPidfd(processId, allowGone: true);
    }

    private static SafeFileHandle? TryOpenPidfd(int processId, bool allowGone)
    {
        long descriptor = syscall_pidfd_open(SystemCallPidfdOpen, processId, 0);
        if (descriptor >= 0)
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        int error = Marshal.GetLastWin32Error();
        if (allowGone && error == ErrorNoProcess)
            return null;
        string message = error == ErrorFunctionNotImplemented
            ? "This Linux runtime doesn't support exact pidfd process authority."
            : "Linux couldn't retain exact verifier process authority.";
        throw new PackageSecurityException(message, new LinuxNativeIOException("pidfd_open failed", error));
    }

    private static bool TrySignalPidfd(SafeFileHandle pidfd)
    {
        int error = SendSignalToPidfd(pidfd);
        return error is 0 or ErrorNoProcess;
    }

    private static int SendSignalToPidfd(SafeFileHandle pidfd)
    {
        long result = syscall_pidfd_send_signal(SystemCallPidfdSendSignal, pidfd, SignalKill, IntPtr.Zero, 0);
        return result == 0 ? 0 : Marshal.GetLastWin32Error();
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

    private static void TryClose(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (Exception)
        {
        }
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall_pidfd_open(long systemCallNumber, int processId, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall_pidfd_send_signal(
        long systemCallNumber,
        SafeFileHandle pidfd,
        int signal,
        IntPtr information,
        uint flags
    );

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(
        SafeFileHandle directory,
        string path,
        int flags,
        uint mask,
        out BundleStatx data
    );

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx_path(
        int directory,
        string path,
        int flags,
        uint mask,
        out BundleStatx data
    );

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle descriptor, int operation);

    private readonly record struct BundleFileIdentity(
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Size,
        ushort Mode,
        int Seals
    );

    internal readonly record struct KernelFileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct BundleStatx
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
        public BundleStatxTimestamp AccessTime;
        public BundleStatxTimestamp BirthTime;
        public BundleStatxTimestamp ChangeTime;
        public BundleStatxTimestamp ModificationTime;
        public uint RootDeviceMajor;
        public uint RootDeviceMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BundleStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    internal readonly record struct ProcessSessionIdentity(
        int ProcessId,
        int ProcessGroupId,
        int SessionId,
        ulong StartTime
    )
    {
        public bool IsSessionMember(int expectedProcessId, int expectedSessionId)
        {
            return this.ProcessId == expectedProcessId
                && this.SessionId == expectedSessionId;
        }
    }

    internal enum SessionMemberSignalResult
    {
        NotMember,
        GoneOrStale,
        Signaled,
        Failed
    }

    /// <summary>
    /// A parent-held anonymous advisory lock which prevents the retained flock helper from executing the verifier
    /// until exact pidfd authority has been acquired for the new session leader.
    /// </summary>
    private sealed class PreExecGate : IDisposable
    {
        private const int LockExclusive = 2;
        private const int LockNonBlocking = 4;
        private const int LockUnlock = 8;
        private const int GetDescriptorFlags = 1;
        private const int CloseOnExec = 1;

        private readonly SafeFileHandle Handle;
        private bool Released;
        private bool Quarantined;

        public string ProcPath { get; }
        public KernelFileIdentity Identity { get; }

        private PreExecGate(SafeFileHandle handle)
        {
            int descriptorFlags = fcntl(handle, GetDescriptorFlags, 0);
            if (descriptorFlags < 0 || (descriptorFlags & CloseOnExec) == 0)
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            this.Handle = handle;
            this.ProcPath = $"/proc/{Environment.ProcessId}/fd/{checked((int)handle.DangerousGetHandle())}";
            this.Identity = GetKernelFileIdentity(handle);
        }

        public static PreExecGate CreateLocked()
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = LinuxSealedFile.CreateAnonymous("smapi-attestation-pre-exec-gate");
                if (flock(handle, LockExclusive | LockNonBlocking) != 0)
                    throw new LinuxNativeIOException("flock failed", Marshal.GetLastWin32Error());
                PreExecGate result = new(handle);
                handle = null;
                return result;
            }
            catch (PackageSecurityException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or NotSupportedException
                    or UnauthorizedAccessException
            )
            {
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public void Release()
        {
            if (this.Released)
                return;
            if (flock(this.Handle, LockUnlock) != 0)
                throw new PackageSecurityException("The GitHub attestation verifier couldn't retain exact process authority.");
            this.Released = true;
        }

        public void QuarantineLocked()
        {
            if (this.Released || this.Quarantined)
                return;
            lock (QuarantinedGateLock)
            {
                if (QuarantinedGate is not null)
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't retain or terminate exact process authority.");
                QuarantinedGate = this.Handle;
                this.Quarantined = true;
            }
        }

        public void Dispose()
        {
            if (!this.Quarantined)
                this.Handle.Dispose();
        }
    }

    private sealed class SystemExecutableAuthority : IDisposable
    {
        private const int AtEmptyPath = 0x1000;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint StatxBasicStats = 0x7ff;
        private const ushort FileTypeMask = 0xf000;
        private const ushort FileTypeRegular = 0x8000;
        private const int RequiredReadExecuteMode = 0x16d; // 0555
        private const int GroupOtherWriteMode = 0x12; // 0022
        private const int SpecialMode = 0xe00; // 07000
        private const long MaximumExecutableBytes = 4L * 1024 * 1024;

        private readonly LinuxAnchoredFileSystem FileSystem;
        private readonly LinuxAnchoredFile File;

        public string ProcPath { get; }
        public KernelFileIdentity Identity { get; }

        private SystemExecutableAuthority(LinuxAnchoredFileSystem fileSystem, LinuxAnchoredFile file)
        {
            this.FileSystem = fileSystem;
            this.File = file;
            this.ProcPath = $"/proc/{Environment.ProcessId}/fd/{checked((int)file.Handle.DangerousGetHandle())}";
            this.Identity = GetKernelFileIdentity(file.Handle);
        }

        public static SystemExecutableAuthority Open(string path)
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
                        out SystemExecutableStatx metadata
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
                    || metadata.Size is 0 or > MaximumExecutableBytes
                    || (mode & RequiredReadExecuteMode) != RequiredReadExecuteMode
                    || (mode & GroupOtherWriteMode) != 0
                    || (mode & SpecialMode) != 0
                )
                {
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                }

                SystemExecutableAuthority result = new(fileSystem, file);
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
            out SystemExecutableStatx data
        );

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct SystemExecutableStatx
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
