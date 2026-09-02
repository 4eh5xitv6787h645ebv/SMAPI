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
    public async Task RunAsync_DoesNotExecuteVerifierBeforeLeaderPidfdAcquisition()
    {
        string processMarker = Path.Combine(this.TempDirectory, "pidfd-gate-success");
        bool gateObserved = false;
        string script = this.Script("/usr/bin/touch \"$1\"\n/usr/bin/printf 'verified-json'");
        GitHubAttestationProcessRunner runner = new(
            beforeLeaderPidfdOpenForTesting: pid =>
            {
                Directory.Exists($"/proc/{pid}").Should().BeTrue("the retained system helper should be alive behind the gate");
                File.Exists(processMarker).Should().BeFalse("the verifier must not execute before exact pidfd acquisition");
                gateObserved = true;
            }
        );

        GitHubAttestationProcessResult result = await runner.RunAsync(this.Request(script, [processMarker]));

        gateObserved.Should().BeTrue();
        result.StandardOutput.Should().Be("verified-json");
        File.Exists(processMarker).Should().BeTrue("the verifier may execute only after exact pidfd acquisition");
    }

    [Test]
    public async Task RunAsync_RejectsConcurrentLaunchBeforeStartWithoutDisturbingActiveVerifier()
    {
        string firstMarker = Path.Combine(this.TempDirectory, "first-verifier-running");
        string secondMarker = Path.Combine(this.TempDirectory, "concurrent-verifier-must-not-run");
        string firstScript = this.Script("/usr/bin/touch \"$1\"\n/usr/bin/sleep 1\n/usr/bin/printf 'first-verified'");
        string secondScript = this.Script("/usr/bin/touch \"$1\"");
        string? secondPrivateDirectory = null;
        GitHubAttestationProcessRunner secondRunner = new(
            afterPrivateDirectoryCreatedForTesting: path => secondPrivateDirectory = path
        );
        Task<GitHubAttestationProcessResult> first = this.Runner.RunAsync(this.Request(firstScript, [firstMarker]));
        await WaitForFileAsync(firstMarker);

        Func<Task> second = async () => await secondRunner.RunAsync(this.Request(secondScript, [secondMarker]));

        await second.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier couldn't be started safely.");
        secondPrivateDirectory.Should().BeNull("a concurrent verifier must be rejected before any private state or process is created");
        File.Exists(secondMarker).Should().BeFalse();
        GitHubAttestationProcessResult firstResult = await first;
        firstResult.StandardOutput.Should().Be("first-verified", "the rejected launch must not disturb the active verifier");
        _ = await secondRunner.RunAsync(this.Request(secondScript, [secondMarker]));
        File.Exists(secondMarker).Should().BeTrue("the serialized launch boundary must reopen after normal completion");
    }

    [Test]
    public async Task RunAsync_LeaderPidfdAcquisitionFailureKeepsVerifierGatedAndReapsDirectHelper()
    {
        string processMarker = Path.Combine(this.TempDirectory, "pidfd-gate-failure-must-not-run");
        string privateSecret = Path.Combine(this.TempDirectory, "private-pidfd-error");
        string? privateDirectory = null;
        int? helperPid = null;
        string script = this.Script("/usr/bin/touch \"$1\"\nexec /usr/bin/sleep 30");
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            beforeLeaderPidfdOpenForTesting: pid =>
            {
                helperPid = pid;
                Directory.Exists($"/proc/{pid}").Should().BeTrue();
                File.Exists(processMarker).Should().BeFalse("the verifier must remain blocked behind the parent-held gate");
                throw new IOException($"synthetic private pidfd failure: {privateSecret}");
            }
        );

        Func<Task> action = async () => await runner.RunAsync(this.Request(script, [processMarker]));

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Be("The GitHub attestation verifier couldn't retain exact process authority.");
        exception.ToString().Should().NotContain(privateSecret);
        helperPid.Should().NotBeNull();
        Directory.Exists($"/proc/{helperPid}").Should().BeFalse("the still-gated direct helper must be terminated and reaped");
        File.Exists(processMarker).Should().BeFalse("the verifier must never execute after pidfd acquisition fails");
        Directory.Exists(privateDirectory).Should().BeFalse("the runner-owned private directory must still be cleaned");
    }

    [Test]
    public async Task RunAsync_CancellationBeforeGateReleaseKeepsVerifierGatedAndReapsDirectHelper()
    {
        string processMarker = Path.Combine(this.TempDirectory, "cancelled-gate-must-not-run");
        string? privateDirectory = null;
        int? helperPid = null;
        using CancellationTokenSource cancellation = new();
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            beforeLeaderPidfdOpenForTesting: pid =>
            {
                helperPid = pid;
                File.Exists(processMarker).Should().BeFalse();
                cancellation.Cancel();
            }
        );
        string script = this.Script("/usr/bin/touch \"$1\"\nexec /usr/bin/sleep 30");

        Func<Task> action = async () => await runner.RunAsync(this.Request(script, [processMarker]), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        helperPid.Should().NotBeNull();
        Directory.Exists($"/proc/{helperPid}").Should().BeFalse("the gated helper must be terminated and reaped before cancellation propagates");
        File.Exists(processMarker).Should().BeFalse("the cancelled verifier must never execute");
        Directory.Exists(privateDirectory).Should().BeFalse("the runner-owned private directory must still be cleaned");
    }

    [Test]
    public async Task RunAsync_RejectsEarlySystemHelperExitBeforeExactGateValidation()
    {
        string processMarker = Path.Combine(this.TempDirectory, "early-helper-exit-must-not-run");
        string? privateDirectory = null;
        GitHubAttestationProcessRunner runner = new(
            flockPath: "/usr/bin/true",
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path
        );
        string script = this.Script("/usr/bin/touch \"$1\"");

        Func<Task> action = async () => await runner.RunAsync(this.Request(script, [processMarker]));

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier couldn't retain exact process authority.");
        File.Exists(processMarker).Should().BeFalse("an exited or invalid helper must never release the verifier gate");
        Directory.Exists(privateDirectory).Should().BeFalse("the runner-owned private directory must still be cleaned");
    }

    [Test]
    public async Task RunAsync_LiveHelperWithRejectedSessionEvidenceNeverReleasesGate()
    {
        string processMarker = Path.Combine(this.TempDirectory, "invalid-live-helper-must-not-run");
        int? helperPid = null;
        string? privateDirectory = null;
        GitHubAttestationProcessRunner runner = new(
            afterPrivateDirectoryCreatedForTesting: path => privateDirectory = path,
            beforeLeaderPidfdOpenForTesting: pid => helperPid = pid,
            transformGateHelperIdentityForTesting: identity => identity with { SessionId = identity.SessionId + 1 }
        );
        string script = this.Script("/usr/bin/touch \"$1\"");

        Func<Task> action = async () => await runner.RunAsync(this.Request(script, [processMarker]));

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier couldn't retain exact process authority.");
        helperPid.Should().NotBeNull();
        Directory.Exists($"/proc/{helperPid}").Should().BeFalse("the live but unvalidated helper must time out and be reaped");
        File.Exists(processMarker).Should().BeFalse("rejected live session evidence must never unlock the verifier");
        Directory.Exists(privateDirectory).Should().BeFalse();
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
            exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
            exception.Message.Should().Be("The pinned attestation verifier process did not complete successfully.");
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
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        exception.Message.Should().Be("The pinned attestation verifier process did not complete successfully.");
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
    public async Task RunAsync_RejectsSymlinkOrUserOwnedSystemExecutableAuthority()
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
        string processMarker = Path.Combine(this.TempDirectory, "unsafe-system-executable-must-not-run");
        string script = this.Script($"/usr/bin/touch '{processMarker}'");

        foreach (string unsafeSystemExecutable in new[] { symlink, userOwned })
        {
            foreach (GitHubAttestationProcessRunner runner in new[]
            {
                new GitHubAttestationProcessRunner(setSidPath: unsafeSystemExecutable),
                new GitHubAttestationProcessRunner(flockPath: unsafeSystemExecutable)
            })
            {
                Func<Task> action = async () => await runner.RunAsync(this.Request(script));

                PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
                exception.Message.Should().Be("The GitHub attestation verifier couldn't be started safely.");
                exception.ToString().Should().NotContain(unsafeSystemExecutable);
                File.Exists(processMarker).Should().BeFalse("a symlink swap or user-owned helper must be rejected before process start");
            }
        }
    }

    [TestCase("relative-gh")]
    [TestCase("/tmp/../tmp/gh")]
    public void Request_RejectsNonabsoluteOrNoncanonicalExecutable(string executable)
    {
        Action action = () => this.Request(executable);

        action.Should().Throw<ArgumentException>().WithParameterName("executablePath");
    }

    [Test]
    public void ExactGateHelperSnapshot_RequiresLiveExactExecutableSessionAndOneGateDescriptor()
    {
        const int processId = 4242;
        GitHubAttestationProcessRunner.ProcessSessionIdentity session = new(processId, processId, processId, 100);
        GitHubAttestationProcessRunner.KernelFileIdentity flock = new(11, 8, 1);
        GitHubAttestationProcessRunner.KernelFileIdentity other = new(12, 8, 1);

        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session, flock, flock, 1, true)
            .Should().BeTrue();
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session, flock, other, 1, true)
            .Should().BeFalse("a different executable must never release the gate");
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session with { ProcessGroupId = 7 }, flock, flock, 1, true)
            .Should().BeFalse("the helper must be its exact process-group and session leader");
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session with { SessionId = 7 }, flock, flock, 1, true)
            .Should().BeFalse("a different session must never release the gate");
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session, flock, flock, 0, true)
            .Should().BeFalse("the exact gate must already be open");
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session, flock, flock, 2, true)
            .Should().BeFalse("the close-on-exec gate must have exactly one helper descriptor");
        GitHubAttestationProcessRunner.IsExactGateHelperSnapshot(processId, session, flock, flock, 1, false)
            .Should().BeFalse("the final pidfd liveness check must succeed");
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
