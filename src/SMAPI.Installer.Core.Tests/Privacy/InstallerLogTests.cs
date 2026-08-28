using System.Runtime.Versioning;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Privacy;

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
            "smapi-internal/SMAPI.dll",
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
        string logs = Path.Combine(state, "logs");
        Directory.CreateDirectory(logs);
        for (int index = 0; index < 5; index++)
        {
            string path = Path.Combine(logs, $"old-{index}.jsonl");
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMinutes(index));
        }
        string unrelated = Path.Combine(logs, "preserve.txt");
        File.WriteAllText(unrelated, "preserve");

        using InstallerLog log = new(new(state, MaximumFileCount: 3), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Directory.GetFiles(logs, "*.jsonl").Should().HaveCount(3);
        File.ReadAllText(unrelated).Should().Be("preserve");
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

    [TestCase("/absolute/path")]
    [TestCase("../escape")]
    [TestCase("smapi-internal\\file")]
    public void Write_RejectsUnsafeRelativeOwnedPath(string path)
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        using InstallerLog log = new(new(state), operationId, DateTimeOffset.UnixEpoch);

        Action action = () => log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Warning, "unsafe", "message", RelativeOwnedPath: path));

        action.Should().Throw<ArgumentException>();
    }

    private string CreateDirectory()
    {
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-installer-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TemporaryDirectory);
        return this.TemporaryDirectory;
    }
}
