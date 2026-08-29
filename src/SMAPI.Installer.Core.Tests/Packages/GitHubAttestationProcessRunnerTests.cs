using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
internal sealed class GitHubAttestationProcessRunnerTests
{
    private string TempDirectory = null!;
    private readonly GitHubAttestationProcessRunner Runner = new();

    [SetUp]
    public void SetUp()
    {
        this.TempDirectory = Path.Combine(Path.GetTempPath(), $"smapi-gh-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempDirectory))
            Directory.Delete(this.TempDirectory, recursive: true);
    }

    [Test]
    public async Task RunAsync_PassesLiteralArgumentsWithoutAShellAndClosesStandardInput()
    {
        string marker = Path.Combine(this.TempDirectory, "must-not-exist");
        string script = this.Script("""
            if IFS= read -r ignored; then
                /usr/bin/printf 'stdin-open'
                exit 9
            fi
            /usr/bin/printf '%s\n%s\nstdin-closed' "$1" "$2"
            """);
        string first = $"literal; /usr/bin/touch {marker}";
        string second = $"$(/usr/bin/touch {marker})";

        GitHubAttestationProcessResult result = await this.Run(script, [first, second]);

        result.StandardOutput.Should().Be($"{first}\n{second}\nstdin-closed");
        File.Exists(marker).Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_UsesOnlyCleanNoninteractiveEnvironment()
    {
        string? previousToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        string? previousProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        Environment.SetEnvironmentVariable("GH_TOKEN", "private-token");
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "https://private-proxy.invalid");
        try
        {
            string script = this.Script("/usr/bin/env");
            GitHubAttestationProcessResult result = await this.Run(script);

            result.StandardOutput.Should().MatchRegex($@"(?m)^HOME=/proc/{Environment.ProcessId}/fd/[0-9]+$");
            result.StandardOutput.Should().NotContain($"HOME={this.TempDirectory}");
            result.StandardOutput.Should().Contain("GH_PROMPT_DISABLED=1");
            result.StandardOutput.Should().Contain("GH_NO_UPDATE_NOTIFIER=1");
            string runtimeDirectory = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("XDG_RUNTIME_DIR=", StringComparison.Ordinal))["XDG_RUNTIME_DIR=".Length..];
            runtimeDirectory.Should().MatchRegex($@"^/proc/{Environment.ProcessId}/fd/[0-9]+$");
            result.StandardOutput.Should().Contain($"DBUS_SESSION_BUS_ADDRESS=unix:path={runtimeDirectory}/session-bus-unavailable");
            result.StandardOutput.Should().Contain($"DBUS_SYSTEM_BUS_ADDRESS=unix:path={runtimeDirectory}/system-bus-unavailable");
            result.StandardOutput.Should().NotContain("private-token").And.NotContain("private-proxy");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", previousToken);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", previousProxy);
        }
    }

    [Test]
    public async Task RunAsync_DBusEndpointsAreNonexistentInsideRetainedPrivateRuntimeDirectory()
    {
        string script = this.Script("""
            session=${DBUS_SESSION_BUS_ADDRESS#unix:path=}
            system=${DBUS_SYSTEM_BUS_ADDRESS#unix:path=}
            if [ "$session" = "$DBUS_SESSION_BUS_ADDRESS" ] || [ "$system" = "$DBUS_SYSTEM_BUS_ADDRESS" ]; then
                exit 11
            fi
            if [ -e "$session" ] || [ -e "$system" ]; then
                exit 12
            fi
            case "$session" in "$XDG_RUNTIME_DIR"/*) ;; *) exit 13 ;; esac
            case "$system" in "$XDG_RUNTIME_DIR"/*) ;; *) exit 14 ;; esac
            /usr/bin/printf 'isolated-runtime'
            """);

        GitHubAttestationProcessResult result = await this.Run(script);

        result.StandardOutput.Should().Be("isolated-runtime");
    }

    [Test]
    public async Task RunAsync_CreatesPrivateConfigurationInsteadOfUsingPrepopulatedRequestDirectory()
    {
        string config = Path.Combine(this.TempDirectory, "hosts.yml");
        await File.WriteAllTextAsync(config, "private-ambient-token");
        string script = this.Script("""
            /usr/bin/printf 'home=%s\nmode=' "$HOME"
            /usr/bin/stat -Lc '%a' "$HOME"
            if [ -e "$GH_CONFIG_DIR/hosts.yml" ]; then
                /usr/bin/printf 'ambient-config-visible'
                exit 17
            fi
            """);

        GitHubAttestationProcessResult result = await this.Run(script);

        result.StandardOutput.Should().Contain("mode=700");
        result.StandardOutput.Should().NotContain(this.TempDirectory).And.NotContain("ambient-config-visible");
        string home = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("home=", StringComparison.Ordinal))["home=".Length..];
        Directory.Exists(home).Should().BeFalse("the runner-owned configuration directory must be removed after completion");
        File.Exists(config).Should().BeTrue("the request directory is input only and must not be mutated");
    }

    [Test]
    public async Task RunAsync_RemovesNestedContentThroughRetainedPrivateDirectoryAuthority()
    {
        string? privateDirectory = null;
        GitHubAttestationProcessRunner runner = new(afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path);
        string script = this.Script("""
            /usr/bin/mkdir -p "$HOME/cache/nested"
            /usr/bin/printf 'temporary' > "$HOME/cache/nested/value"
            /usr/bin/printf 'verified-json'
            """);

        GitHubAttestationProcessResult result = await runner.RunAsync(this.Request(script));

        result.StandardOutput.Should().Be("verified-json");
        privateDirectory.Should().MatchRegex(@".*/smapi-attestation-private-[0-9a-f]{32}$");
        Directory.Exists(privateDirectory).Should().BeFalse("retained recursive cleanup should remove nested verifier state");
    }

    [Test]
    public async Task RunAsync_ExposesRetainedBundleThroughExactJsonlBridgeForProcessLifetimeAndCleansIt()
    {
        using TestBundleAuthority bundle = new("exact sealed bundle bytes");
        string retainedBundlePath = bundle.ProcPath;
        string? privateDirectory = null;
        string? createdBridge = null;
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            afterBundleBridgeCreatedForTesting: path => createdBridge = path
        );
        string script = this.Script("""
            bundle=
            while [ "$#" -gt 0 ]; do
                if [ "$1" = "--bundle" ]; then
                    shift
                    bundle="$1"
                    break
                fi
                shift
            done
            /usr/bin/printf '%s\n%s\n' "$bundle" "$(/usr/bin/readlink "$bundle")"
            /usr/bin/cat "$bundle"
            """);
        GitHubAttestationProcessRequest request = this.Request(
            script,
            ["attestation", "verify", "subject", "--bundle", GitHubAttestationProcessRequest.BundlePathPlaceholder],
            bundle: bundle.Authority
        );

        GitHubAttestationProcessResult result = await runner.RunAsync(request);

        string[] lines = result.StandardOutput.Split('\n');
        lines.Should().HaveCount(3);
        lines[0].Should().MatchRegex($@"^/proc/{Environment.ProcessId}/fd/[0-9]+/verified-attestation-bundle\.jsonl$");
        lines[0].Should().Be(createdBridge);
        lines[1].Should().Be(retainedBundlePath, "the extension bridge must resolve only to the retained descriptor");
        lines[2].Should().Be("exact sealed bundle bytes");
        privateDirectory.Should().MatchRegex(@".*/smapi-attestation-private-[0-9a-f]{32}$");
        Directory.Exists(privateDirectory).Should().BeFalse("the bridge and retained private directory must be cleaned after completion");
        File.Exists(retainedBundlePath).Should().BeTrue("the caller's retained bundle lease remains authoritative");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_RejectsBundleBridgeReplacementBeforeStartingProcess(bool symbolicLinkReplacement)
    {
        using TestBundleAuthority bundle = new("retained bundle");
        using TestBundleAuthority replacement = new("untrusted replacement");
        string processMarker = Path.Combine(this.TempDirectory, "bridge-swap-process-must-not-start");
        string? privateDirectory = null;
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            afterBundleBridgeCreatedForTesting: bridge =>
            {
                File.Delete(bridge);
                if (symbolicLinkReplacement)
                    File.CreateSymbolicLink(bridge, replacement.ProcPath);
                else
                    File.WriteAllText(bridge, "not a bridge");
            }
        );
        string script = this.Script($"/usr/bin/touch '{processMarker}'");
        GitHubAttestationProcessRequest request = this.Request(
            script,
            ["attestation", "verify", "subject", "--bundle", GitHubAttestationProcessRequest.BundlePathPlaceholder],
            bundle: bundle.Authority
        );

        Func<Task> action = async () => await runner.RunAsync(request);

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier couldn't be started safely.");
        exception.ToString().Should().NotContain(replacement.ProcPath).And.NotContain("untrusted replacement");
        File.Exists(processMarker).Should().BeFalse();
        Directory.Exists(privateDirectory).Should().BeFalse("retained cleanup must remove a rejected bridge replacement");
    }

    [Test]
    public async Task RunAsync_RevalidatesAnchoredBridgeIdentityImmediatelyBeforeProcessStart()
    {
        using TestBundleAuthority bundle = new("retained bundle");
        string processMarker = Path.Combine(this.TempDirectory, "late-bridge-swap-process-must-not-start");
        string? privateDirectory = null;
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            beforeProcessStartForTesting: bridge =>
            {
                string replacement = $"{bridge}.replacement";
                File.CreateSymbolicLink(replacement, bundle.ProcPath);
                File.Move(replacement, bridge, overwrite: true);
            }
        );
        string script = this.Script($"/usr/bin/touch '{processMarker}'");
        GitHubAttestationProcessRequest request = this.Request(
            script,
            ["--bundle", GitHubAttestationProcessRequest.BundlePathPlaceholder],
            bundle: bundle.Authority
        );

        Func<Task> action = async () => await runner.RunAsync(request);

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier couldn't be started safely.");
        File.Exists(processMarker).Should().BeFalse();
        Directory.Exists(privateDirectory).Should().BeFalse("cleanup must remove the identity-swapped late bridge");
    }

    [Test]
    public async Task RunAsync_RejectsCurrentProcessDescriptorWithoutImmutableKernelSeals()
    {
        string bundlePath = Path.Combine(this.TempDirectory, "unsealed-bundle.jsonl");
        await File.WriteAllTextAsync(bundlePath, "mutable bytes");
        await using FileStream unsealed = new(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        string procPath = $"/proc/{Environment.ProcessId}/fd/{checked((int)unsealed.SafeFileHandle.DangerousGetHandle())}";
        GitHubAttestationProcessBundleAuthority authority = new(procPath);
        string processMarker = Path.Combine(this.TempDirectory, "unsealed-process-must-not-start");
        string script = this.Script($"/usr/bin/touch '{processMarker}'");
        GitHubAttestationProcessRequest request = this.Request(
            script,
            ["--bundle", GitHubAttestationProcessRequest.BundlePathPlaceholder],
            bundle: authority
        );

        Func<Task> action = async () => await this.Runner.RunAsync(request);

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier couldn't be started safely.");
        File.Exists(processMarker).Should().BeFalse();
    }

    [TestCase("/tmp/unretained-bundle.jsonl")]
    [TestCase("/proc/1/fd/3")]
    [TestCase("/proc/999999999/fd/3")]
    public void BundleAuthority_RejectsPathWhichIsNotThisProcessRetainedDescriptor(string bundlePath)
    {
        Action action = () => _ = new GitHubAttestationProcessBundleAuthority(bundlePath);

        action.Should().Throw<ArgumentException>().WithParameterName("procPath");
    }

    [Test]
    public void Request_RequiresExactlyOneReservedSlotForTypedBundleAuthority()
    {
        using TestBundleAuthority bundle = new("retained bundle");
        string script = this.Script("exit 0");
        Action duplicateSlots = () => _ = this.Request(
            script,
            [GitHubAttestationProcessRequest.BundlePathPlaceholder, GitHubAttestationProcessRequest.BundlePathPlaceholder],
            bundle: bundle.Authority
        );
        Action missingSlot = () => _ = this.Request(script, ["--bundle", bundle.ProcPath], bundle: bundle.Authority);
        Action untypedSlot = () => _ = this.Request(script, [GitHubAttestationProcessRequest.BundlePathPlaceholder]);

        duplicateSlots.Should().Throw<ArgumentException>().WithParameterName("arguments");
        missingSlot.Should().Throw<ArgumentException>().WithParameterName("arguments");
        untypedSlot.Should().Throw<ArgumentException>().WithParameterName("arguments");
    }

    [Test]
    public async Task RunAsync_RejectsPrivateDirectoryPathSwapWithoutTouchingReplacement()
    {
        string? privateDirectory = null;
        string? movedOriginal = null;
        string? sentinel = null;
        string processMarker = Path.Combine(this.TempDirectory, "process-must-not-start");
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path =>
            {
                privateDirectory = path;
                movedOriginal = $"{path}-moved";
                Directory.Move(path, movedOriginal);
                Directory.CreateDirectory(path);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                sentinel = Path.Combine(path, "replacement-sentinel");
                File.WriteAllText(sentinel, "must survive");
            }
        );
        string script = this.Script($"/usr/bin/touch '{processMarker}'");

        try
        {
            Func<Task> action = async () => await runner.RunAsync(this.Request(script));

            PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.Message.Should().Be("The GitHub attestation verifier couldn't be started safely.");
            privateDirectory.Should().MatchRegex(@".*/smapi-attestation-private-[0-9a-f]{32}$");
            File.Exists(sentinel).Should().BeTrue("cleanup must not recurse into an identity-mismatched replacement");
            File.ReadAllText(sentinel!).Should().Be("must survive");
            Directory.Exists(movedOriginal).Should().BeTrue("the retained original may be safely leaked after its name is replaced");
            File.Exists(processMarker).Should().BeFalse("the process must not start after the directory authority is replaced");
        }
        finally
        {
            if (privateDirectory is not null && Directory.Exists(privateDirectory))
                Directory.Delete(privateDirectory, recursive: true);
            if (movedOriginal is not null && Directory.Exists(movedOriginal))
                Directory.Delete(movedOriginal, recursive: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_KillsAndSanitizesExcessiveOutput(bool standardError)
    {
        string secret = Path.Combine(this.TempDirectory, "private-secret");
        string redirect = standardError ? " >&2" : "";
        string script = this.Script($"/usr/bin/printf '%s' \"$1\"{redirect}\n/usr/bin/head -c 4097 /dev/zero{redirect}\n/usr/bin/sleep 30\n");
        GitHubAttestationProcessRequest request = this.Request(script, [secret], stdout: 4096, stderr: 4096);

        Func<Task> action = async () => await this.Runner.RunAsync(request);

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier produced excessive output.");
        exception.ToString().Should().NotContain(secret).And.NotContain("/dev/zero");
    }

    [Test]
    public async Task RunAsync_TimesOutKillsAndFullyReapsTheProcess()
    {
        string pidFile = Path.Combine(this.TempDirectory, "timeout.pid");
        string script = this.Script("""
            /usr/bin/printf '%s' "$$" > "$1"
            exec /usr/bin/sleep 30
            """);
        GitHubAttestationProcessRequest request = this.Request(script, [pidFile], timeout: TimeSpan.FromSeconds(1));

        Func<Task> action = async () => await this.Runner.RunAsync(request);

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier timed out.");
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
        Directory.Exists($"/proc/{pid}").Should().BeFalse("the timed-out child must be fully reaped");
    }

    [Test]
    public async Task RunAsync_ExternalCancellationPropagatesAndFullyReapsTheProcess()
    {
        string pidFile = Path.Combine(this.TempDirectory, "cancel.pid");
        string script = this.Script("""
            /usr/bin/printf '%s' "$$" > "$1"
            exec /usr/bin/sleep 30
            """);
        using CancellationTokenSource cancellation = new();
        Task<GitHubAttestationProcessResult> running = this.Runner.RunAsync(this.Request(script, [pidFile]), cancellation.Token);
        await WaitForFileAsync(pidFile);
        cancellation.Cancel();

        Func<Task> action = async () => await running;

        await action.Should().ThrowAsync<OperationCanceledException>();
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
        Directory.Exists($"/proc/{pid}").Should().BeFalse("the cancelled child must be fully reaped");
    }

    [Test]
    public async Task RunAsync_LeaderPidfdAcquisitionFailureTerminatesUnreapedChildAndExactDescendants()
    {
        string leaderPidFile = Path.Combine(this.TempDirectory, "startup-failure-leader.pid");
        string descendantPidFile = Path.Combine(this.TempDirectory, "startup-failure-descendant.pid");
        string processMarker = Path.Combine(this.TempDirectory, "startup-failure-must-not-continue");
        string privateSecret = Path.Combine(this.TempDirectory, "private-pidfd-error");
        string? privateDirectory = null;
        string script = this.Script("""
            /usr/bin/sleep 30 </dev/null >/dev/null 2>&1 &
            /usr/bin/printf '%s' "$!" > "$2"
            /usr/bin/printf '%s' "$$" > "$1"
            /usr/bin/sleep 1
            /usr/bin/touch "$3"
            exec /usr/bin/sleep 30
            """);
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            openLeaderPidfdForTesting: _ =>
            {
                WaitForFileSynchronously(descendantPidFile);
                throw new IOException($"synthetic private pidfd failure: {privateSecret}");
            }
        );

        Func<Task> action = async () => await runner.RunAsync(
            this.Request(script, [leaderPidFile, descendantPidFile, processMarker])
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier couldn't retain exact process authority.");
        exception.ToString().Should().NotContain(privateSecret).And.NotContain(descendantPidFile);
        int leaderPid = int.Parse(await File.ReadAllTextAsync(leaderPidFile));
        int descendantPid = int.Parse(await File.ReadAllTextAsync(descendantPidFile));
        Directory.Exists($"/proc/{leaderPid}").Should().BeFalse("the known direct child must be terminated and reaped");
        Directory.Exists($"/proc/{descendantPid}").Should().BeFalse("session descendants must be terminated through exact pidfds");
        File.Exists(processMarker).Should().BeFalse("the verifier must not continue after pidfd acquisition fails");
        Directory.Exists(privateDirectory).Should().BeFalse("the runner-owned private directory must still be cleaned");
    }

    [Test]
    public async Task RunAsync_LeaderExitWithInheritedPipesTimesOutAndKillsTheDescendantGroup()
    {
        string pidFile = Path.Combine(this.TempDirectory, "descendant-timeout.pid");
        string script = this.Script("""
            /usr/bin/sleep 30 &
            /usr/bin/printf '%s' "$!" > "$1"
            exit 0
            """);
        GitHubAttestationProcessRequest request = this.Request(script, [pidFile], timeout: TimeSpan.FromSeconds(1));

        Func<Task> action = async () => await this.Runner.RunAsync(request);

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier timed out.");
        int descendantPid = int.Parse(await File.ReadAllTextAsync(pidFile));
        await WaitForProcessExitAsync(descendantPid);
    }

    [Test]
    public async Task RunAsync_CancellationAfterLeaderExitKillsTheDescendantGroup()
    {
        string pidFile = Path.Combine(this.TempDirectory, "descendant-cancel.pid");
        string script = this.Script("""
            /usr/bin/sleep 30 &
            /usr/bin/printf '%s' "$!" > "$1"
            exit 0
            """);
        using CancellationTokenSource cancellation = new();
        Task<GitHubAttestationProcessResult> running = this.Runner.RunAsync(this.Request(script, [pidFile]), cancellation.Token);
        await WaitForFileAsync(pidFile);
        await Task.Delay(100);
        cancellation.Cancel();

        Func<Task> action = async () => await running;

        await action.Should().ThrowAsync<OperationCanceledException>();
        int descendantPid = int.Parse(await File.ReadAllTextAsync(pidFile));
        await WaitForProcessExitAsync(descendantPid);
    }

    [TestCase(0)]
    [TestCase(23)]
    public async Task RunAsync_TerminalExitKillsBackgroundProcessWhichClosedInheritedStreams(int exitCode)
    {
        string pidFile = Path.Combine(this.TempDirectory, $"closed-streams-{exitCode}.pid");
        string script = this.Script($$"""
            /usr/bin/sleep 30 </dev/null >/dev/null 2>&1 &
            /usr/bin/printf '%s' "$!" > "$1"
            /usr/bin/printf 'verified-json'
            exit {{exitCode}}
            """);

        if (exitCode == 0)
        {
            GitHubAttestationProcessResult result = await this.Runner.RunAsync(this.Request(script, [pidFile]));
            result.StandardOutput.Should().Be("verified-json");
        }
        else
        {
            Func<Task> action = async () => await this.Runner.RunAsync(this.Request(script, [pidFile]));
            PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.Message.Should().Be("The GitHub attestation verifier rejected the selected release evidence.");
        }

        int descendantPid = int.Parse(await File.ReadAllTextAsync(pidFile));
        await WaitForProcessExitAsync(descendantPid);
    }

    [Test]
    public async Task RunAsync_NonzeroExitDoesNotExposeStandardErrorOrPaths()
    {
        string secret = Path.Combine(this.TempDirectory, "private-secret");
        string script = this.Script("/usr/bin/printf '%s' \"$1\" >&2\nexit 23\n");

        Func<Task> action = async () => await this.Runner.RunAsync(this.Request(script, [secret]));

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier rejected the selected release evidence.");
        exception.ToString().Should().NotContain(secret);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_RejectsInvalidUtf8FromEitherOutput(bool standardError)
    {
        string redirect = standardError ? " >&2" : "";
        string script = this.Script($"/usr/bin/printf '\\377'{redirect}\n");

        Func<Task> action = async () => await this.Runner.RunAsync(this.Request(script));

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier returned invalid UTF-8 output.");
    }

    [Test]
    public async Task RunAsync_AllowsBoundedValidStandardErrorOnSuccessWithoutReturningIt()
    {
        string script = this.Script("/usr/bin/printf 'diagnostic-noise' >&2\n/usr/bin/printf 'verified-json'\n");

        GitHubAttestationProcessResult result = await this.Run(script);

        result.StandardOutput.Should().Be("verified-json");
    }

    [Test]
    public async Task RunAsync_StartFailureIsStableAndDoesNotExposeTheExecutablePath()
    {
        string missing = Path.Combine(this.TempDirectory, "private-missing-gh");

        Func<Task> action = async () => await this.Runner.RunAsync(this.Request(missing));

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier couldn't be started safely.");
        exception.ToString().Should().NotContain(missing);
    }

    [Test]
    public async Task RunAsync_RejectsSymlinkOrUserOwnedSetSidAuthority()
    {
        string symlink = Path.Combine(this.TempDirectory, "setsid-link");
        File.CreateSymbolicLink(symlink, "/usr/bin/setsid");
        string userOwned = Path.Combine(this.TempDirectory, "setsid-copy");
        File.Copy("/usr/bin/setsid", userOwned);
        File.SetUnixFileMode(
            userOwned,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute
        );
        string script = this.Script("/usr/bin/printf 'must-not-run'");

        foreach (string unsafeSetSid in new[] { symlink, userOwned })
        {
            GitHubAttestationProcessRunner runner = new(unsafeSetSid);
            Func<Task> action = async () => await runner.RunAsync(this.Request(script));

            PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.Message.Should().Be("The GitHub attestation verifier couldn't be started safely.");
            exception.ToString().Should().NotContain(unsafeSetSid);
        }
    }

    [TestCase("relative-gh")]
    [TestCase("/tmp/../tmp/gh")]
    public void Request_RejectsNonabsoluteOrNoncanonicalExecutable(string executable)
    {
        Action action = () => this.Request(executable);

        action.Should().Throw<ArgumentException>().WithParameterName("executablePath");
    }

    [TestCase(false, GitHubAttestationProcessRunner.SessionMemberSignalResult.Signaled, 1)]
    [TestCase(true, GitHubAttestationProcessRunner.SessionMemberSignalResult.GoneOrStale, 0)]
    public void ExactSessionMemberSignal_RevalidatesStartTimeBeforeSignaling(
        bool simulateNumericPidReuse,
        GitHubAttestationProcessRunner.SessionMemberSignalResult expected,
        int expectedSignals
    )
    {
        const int processId = 4242;
        const int sessionId = 3131;
        int reads = 0;
        int signals = 0;
        GitHubAttestationProcessRunner.ProcessSessionIdentity original = new(
            processId,
            sessionId,
            sessionId,
            StartTime: 100
        );
        GitHubAttestationProcessRunner.SessionMemberSignalResult result =
            GitHubAttestationProcessRunner.TrySignalValidatedSessionMember(
                processId,
                sessionId,
                _ =>
                {
                    reads++;
                    return simulateNumericPidReuse && reads == 2
                        ? original with { StartTime = 101 }
                        : original;
                },
                _ => File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                _ =>
                {
                    signals++;
                    return 0;
                }
            );

        result.Should().Be(expected);
        reads.Should().Be(2, "the numeric /proc identity must be checked before and after pidfd_open");
        signals.Should().Be(expectedSignals, "a stale or reused numeric PID must never receive a signal");
    }

    [Test]
    public void ExactSessionMemberSignal_FailsClosedWhenPidfdSignalFails()
    {
        const int processId = 4242;
        const int sessionId = 3131;
        GitHubAttestationProcessRunner.ProcessSessionIdentity identity = new(
            processId,
            sessionId,
            sessionId,
            StartTime: 100
        );

        GitHubAttestationProcessRunner.SessionMemberSignalResult result =
            GitHubAttestationProcessRunner.TrySignalValidatedSessionMember(
                processId,
                sessionId,
                _ => identity,
                _ => File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                _ => 1
            );

        result.Should().Be(GitHubAttestationProcessRunner.SessionMemberSignalResult.Failed);
    }

    private async Task<GitHubAttestationProcessResult> Run(string script, string[]? arguments = null)
    {
        return await this.Runner.RunAsync(this.Request(script, arguments ?? []));
    }

    private GitHubAttestationProcessRequest Request(
        string executable,
        string[]? arguments = null,
        TimeSpan? timeout = null,
        int stdout = 64 * 1024,
        int stderr = 64 * 1024,
        GitHubAttestationProcessBundleAuthority? bundle = null
    )
    {
        return new GitHubAttestationProcessRequest(
            executable,
            arguments ?? [],
            timeout ?? TimeSpan.FromSeconds(5),
            stdout,
            stderr,
            bundle
        );
    }

    private string Script(string body)
    {
        string path = Path.Combine(this.TempDirectory, $"fake-gh-{Guid.NewGuid():N}");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task WaitForFileAsync(string path)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
            await Task.Delay(10, timeout.Token);
    }

    private static void WaitForFileSynchronously(string path)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!File.Exists(path) && timeout.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(10);
        if (!File.Exists(path))
            throw new AssertionException("The startup-failure fixture didn't publish its descendant PID in time.");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Directory.Exists($"/proc/{processId}"))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class TestBundleAuthority : IDisposable
    {
        private readonly SafeFileHandle Handle;
        private readonly LinuxSealedFileLease Lease;

        public GitHubAttestationProcessBundleAuthority Authority { get; }
        public string ProcPath => this.Lease.ProcPath;

        public TestBundleAuthority(string contents)
        {
            this.Handle = LinuxSealedFile.CreateAnonymous("smapi-attestation-runner-test-bundle");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents);
                RandomAccess.Write(this.Handle, bytes, 0);
                LinuxSealedFile.SealImmutable(this.Handle);
                this.Lease = LinuxSealedFile.LeaseForExternalRead(this.Handle);
                this.Authority = new GitHubAttestationProcessBundleAuthority(this.Lease.ProcPath);
            }
            catch
            {
                this.Handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            this.Lease.Dispose();
            this.Handle.Dispose();
        }
    }
}
