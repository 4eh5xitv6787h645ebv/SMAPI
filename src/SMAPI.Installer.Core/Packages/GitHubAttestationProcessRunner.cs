using System.Buffers;
using System.Diagnostics;
using System.Text;

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
    public string IsolatedDirectory { get; }
    public TimeSpan Timeout { get; }
    public int MaximumStandardOutputBytes { get; }
    public int MaximumStandardErrorBytes { get; }

    public GitHubAttestationProcessRequest(
        string executablePath,
        IEnumerable<string> arguments,
        string isolatedDirectory,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes
    )
    {
        this.ExecutablePath = AssertCanonicalAbsolutePath(executablePath, nameof(executablePath));
        this.IsolatedDirectory = AssertCanonicalAbsolutePath(isolatedDirectory, nameof(isolatedDirectory));
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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<GitHubAttestationProcessResult> RunAsync(
        GitHubAttestationProcessRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.IsolatedDirectory))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        using Process process = new()
        {
            StartInfo = CreateStartInfo(request),
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The verifier process didn't start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            await KillAndReapAsync(process).ConfigureAwait(false);
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
        using CancellationTokenSource timeoutCancellation = new();
        Task timedOut = Task.Delay(request.Timeout, timeoutCancellation.Token);
        Task cancelled = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan);

        try
        {
            await Task.WhenAny(exited, outputFailure.Task, timedOut, cancelled).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await KillAndObserveAsync(process, stdout, stderr).ConfigureAwait(false);
                }
                finally
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (!exited.IsCompleted)
            {
                if (outputFailure.Task.IsCompleted)
                {
                    Exception failure = await outputFailure.Task.ConfigureAwait(false);
                    await KillAndObserveAsync(process, stdout, stderr).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw MapOutputFailure(failure);
                }

                await KillAndObserveAsync(process, stdout, stderr).ConfigureAwait(false);
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

    private static ProcessStartInfo CreateStartInfo(GitHubAttestationProcessRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.IsolatedDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = request.IsolatedDirectory;
        startInfo.Environment["GH_CONFIG_DIR"] = request.IsolatedDirectory;
        startInfo.Environment["XDG_CONFIG_HOME"] = request.IsolatedDirectory;
        startInfo.Environment["XDG_CACHE_HOME"] = request.IsolatedDirectory;
        startInfo.Environment["TMPDIR"] = request.IsolatedDirectory;
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
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

    private static async Task KillAndObserveAsync(Process process, Task stdout, Task stderr)
    {
        await KillAndReapAsync(process).ConfigureAwait(false);
        await ObserveAsync(stdout, stderr).ConfigureAwait(false);
    }

    private static async Task KillAndReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
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

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
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

    private sealed class OutputLimitExceededException : Exception
    {
    }
}
