using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
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

    private async Task<GitHubAttestationProcessResult> Run(string script, string[]? arguments = null)
    {
        return await this.Runner.RunAsync(this.Request(script, arguments ?? []));
    }

    private GitHubAttestationProcessRequest Request(
        string executable,
        string[]? arguments = null,
        TimeSpan? timeout = null,
        int stdout = 64 * 1024,
        int stderr = 64 * 1024
    )
    {
        return new GitHubAttestationProcessRequest(
            executable,
            arguments ?? [],
            timeout ?? TimeSpan.FromSeconds(5),
            stdout,
            stderr
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
}
