using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Privacy;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Privacy;

[TestFixture]
public sealed class InstallerLogTests
{
    private string? TemporaryDirectory;

    [TearDown]
    public void TearDown()
    {
        if (this.TemporaryDirectory is not null && Directory.Exists(this.TemporaryDirectory))
            Directory.Delete(this.TemporaryDirectory, recursive: true);
        this.TemporaryDirectory = null;
    }

    [Test]
    public void GetStateRoot_UsesAbsoluteXdgStateHomeAndIgnoresRelativeValue()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "custom-state");
        InstallerStatePaths.GetStateRoot(name => name == "XDG_STATE_HOME" ? absolute : null, "/home/player")
            .Should().Be(Path.Combine(absolute, "smapi-installer"));
        InstallerStatePaths.GetStateRoot(name => name == "XDG_STATE_HOME" ? "relative" : null, "/home/player")
            .Should().Be("/home/player/.local/state/smapi-installer");
    }

    [Test]
    public void Write_RedactsPathsSecretsQueriesAndPrivateWorkloadCanaries()
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        string home = "/home/alice";
        string game = "/games/private/Stardew Valley";
        string mod = "PrivateModIdentifier";
        string save = "Blossom_123456";
        string report = "PRIVATE-REPORT-CONTENT";
        using InstallerLog log = new(new(state), operationId, DateTimeOffset.UnixEpoch, new[] { home, game, mod, save, report });

        log.Write(new(
            DateTimeOffset.UnixEpoch,
            operationId,
            InstallerLogLevel.Error,
            "download.failed",
            $"home={home}; game={game}; mod={mod}; save={save}; report={report}; https://objects.githubusercontent.com/a?token=secret&sig=bad Authorization: topsecret Bearer abc.def",
            "fork-linux-v1-alpha.1",
            NormalizedRelativePath.Parse("smapi-internal/SMAPI.dll"),
            "package_mismatch"
        )).Should().BeTrue();
        log.Dispose();

        string contents = File.ReadAllText(log.Path);
        contents.Should().NotContain(home).And.NotContain(game).And.NotContain(mod).And.NotContain(save).And.NotContain(report);
        contents.Should().NotContain("secret").And.NotContain("topsecret").And.NotContain("abc.def");
        contents.Should().Contain("https://objects.githubusercontent.com/a");
        contents.Should().Contain("[redacted]");
        contents.Should().Contain("smapi-internal/SMAPI.dll");
    }

    [Test]
    public void Write_StopsBeforeConfiguredByteBound()
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        string path;
        using (InstallerLog log = new(new(state, MaximumFileBytes: 1024, MaximumMessageCharacters: 512), operationId, DateTimeOffset.UnixEpoch))
        {
            path = log.Path;
            int written = 0;
            while (log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "bounded", new string('x', 400))))
                written++;
            written.Should().BeGreaterThan(0);
        }
        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(1024);
    }

    [Test]
    public void Constructor_RotatesOnlyInstallerJsonLogsToConfiguredCount()
    {
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        string logs = Path.Combine(state, "logs");
        for (int index = 0; index < 5; index++)
        {
            string path = Path.Combine(logs, $"20000101T00000{index}Z-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMinutes(index));
        }
        string unrelated = Path.Combine(logs, "preserve.txt");
        File.WriteAllText(unrelated, "preserve");
        string unrelatedJson = Path.Combine(logs, "notes.jsonl");
        File.WriteAllText(unrelatedJson, "unrelated");

        using InstallerLog log = new(new(state, MaximumFileCount: 3), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Directory.GetFiles(logs, "*-*.jsonl").Should().HaveCount(3);
        File.ReadAllText(unrelated).Should().Be("preserve");
        File.ReadAllText(unrelatedJson).Should().Be("unrelated");
    }

    [Test]
    public void Constructor_RejectsSymlinkedLogDirectoryWithoutTouchingTarget()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        Directory.Delete(Path.Combine(state, "logs"), recursive: true);
        string external = Path.Combine(this.TemporaryDirectory!, "external");
        Directory.CreateDirectory(external);
        string unrelated = Path.Combine(external, "notes.jsonl");
        File.WriteAllText(unrelated, "preserve");
        Directory.CreateSymbolicLink(Path.Combine(state, "logs"), external);

        Action action = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<IOException>();
        File.ReadAllText(unrelated).Should().Be("preserve");
    }

    [Test]
    public void Constructor_RejectsSymlinkedStateMarkerWithoutReadingTarget()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        Directory.CreateDirectory(state);
        string external = Path.Combine(this.TemporaryDirectory!, "external-marker");
        File.WriteAllText(external, "smapi-installer-state-v1\n");
        File.CreateSymbolicLink(Path.Combine(state, "state-version"), external);

        Action action = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<IOException>();
        File.ReadAllText(external).Should().Be("smapi-installer-state-v1\n");
        Directory.Exists(Path.Combine(state, "logs")).Should().BeFalse();
    }

    [Test]
    public void Constructor_RejectsHardlinkedStateMarker()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        Directory.CreateDirectory(state);
        string marker = Path.Combine(state, "state-version");
        string secondLink = Path.Combine(this.TemporaryDirectory!, "second-marker-link");
        File.WriteAllText(marker, "smapi-installer-state-v1\n");
        link(marker, secondLink).Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");

        Action action = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<IOException>().WithMessage("*multiple hard links*");
        File.ReadAllText(secondLink).Should().Be("smapi-installer-state-v1\n");
        Directory.Exists(Path.Combine(state, "logs")).Should().BeFalse();
    }

    [Test]
    public void Constructor_RejectsHardlinkedOwnedLogWithoutRotatingIt()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        string ownedLog = Path.Combine(state, "logs", $"20000101T000000Z-{Guid.NewGuid():N}.jsonl");
        string secondLink = Path.Combine(this.TemporaryDirectory!, "owned-log-second-link");
        File.WriteAllText(ownedLog, "preserve");
        link(ownedLog, secondLink).Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");

        Action action = () => _ = new InstallerLog(new(state, MaximumFileCount: 1), Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<IOException>().WithMessage("*multiple hard links*");
        File.ReadAllText(ownedLog).Should().Be("preserve");
        File.ReadAllText(secondLink).Should().Be("preserve");
    }

    [Test]
    public void Write_LogLeafReplacedWithSymlink_RejectsWithoutTouchingTarget()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        using InstallerLog log = new(new(state), operationId, DateTimeOffset.UnixEpoch);
        string captured = log.Path + ".captured";
        File.Move(log.Path, captured);
        string external = Path.Combine(this.TemporaryDirectory!, "outside-log");
        File.WriteAllText(external, "preserve");
        File.CreateSymbolicLink(log.Path, external);

        Action action = () => log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "path.swap", "message"));

        action.Should().Throw<IOException>();
        File.ReadAllText(external).Should().Be("preserve");
        new FileInfo(captured).Length.Should().Be(0);
    }

    [Test]
    public void Write_SelectedStateRootPathMoved_ContinuesOnlyInCapturedDirectory()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        using InstallerLog log = new(new(state), operationId, DateTimeOffset.UnixEpoch);
        string movedState = state + "-moved";
        string outside = Path.Combine(this.TemporaryDirectory!, "outside-state");
        Directory.Move(state, movedState);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(state, outside);

        log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "root.swap", "captured-only"))
            .Should().BeTrue();

        string movedLog = Path.Combine(movedState, "logs", Path.GetFileName(log.Path));
        File.ReadAllText(movedLog).Should().Contain("captured-only");
        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty();
    }

    [Test]
    public void Write_IsSerializedAndNeverExceedsBoundUnderConcurrency()
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        string path;
        using (InstallerLog log = new(new(state, MaximumFileBytes: 4096, MaximumMessageCharacters: 512), operationId, DateTimeOffset.UnixEpoch))
        {
            path = log.Path;
            Action action = () => Parallel.For(0, 100, index => log.Write(new(
                DateTimeOffset.UnixEpoch,
                operationId,
                InstallerLogLevel.Information,
                "parallel",
                $"entry-{index}-{new string('x', 200)}"
            )));
            action.Should().NotThrow();
        }

        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(4096);
        foreach (string line in File.ReadLines(path))
            System.Text.Json.JsonDocument.Parse(line).Dispose();
    }

    [Test]
    public void Write_RejectsMismatchedOperationId()
    {
        string state = this.CreateDirectory();
        using InstallerLog log = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Action action = () => log.Write(new(DateTimeOffset.UnixEpoch, Guid.NewGuid(), InstallerLogLevel.Warning, "mismatch", "message"));

        action.Should().Throw<ArgumentException>();
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void Constructor_SetsPrivateLinuxPermissions()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        using InstallerLog log = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Convert.ToInt32(File.GetUnixFileMode(log.Path) & (UnixFileMode)0x1ff).Should().Be(Convert.ToInt32("600", 8));
        Convert.ToInt32(File.GetUnixFileMode(Path.GetDirectoryName(log.Path)!) & (UnixFileMode)0x1ff).Should().Be(Convert.ToInt32("700", 8));
    }

    [Test]
    public void Write_RejectsPathOutsideOwnedNamespace()
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        using InstallerLog log = new(new(state), operationId, DateTimeOffset.UnixEpoch);

        Action action = () => log.Write(new(
            DateTimeOffset.UnixEpoch,
            operationId,
            InstallerLogLevel.Warning,
            "unsafe",
            "message",
            RelativeOwnedPath: NormalizedRelativePath.Parse("unrelated.txt")
        ));

        action.Should().Throw<ArgumentException>();
    }

    private string CreateDirectory()
    {
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-installer-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TemporaryDirectory);
        return Path.Combine(this.TemporaryDirectory, "state");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);
}
