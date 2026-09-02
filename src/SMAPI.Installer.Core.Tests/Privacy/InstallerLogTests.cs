using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Privacy;

namespace StardewModdingAPI.Installer.Core.Tests.Privacy;

[TestFixture]
[SupportedOSPlatform("linux")]
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
            while (log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "bounded", new string('x', 100))))
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
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string marker = Path.Combine(state, "state-version");
        string secondLink = Path.Combine(this.TemporaryDirectory!, "second-marker-link");
        File.WriteAllText(marker, "smapi-installer-state-v1\n");
        File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        File.SetUnixFileMode(ownedLog, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    [Test]
    public void Constructor_BoundsSensitiveValuesBeforeFilesystemAccess()
    {
        string state = this.CreateDirectory();
        InstallerLogOptions countOptions = new(state, MaximumSensitiveValueCount: 4);

        Action excessiveCount = () => _ = new InstallerLog(countOptions, Guid.NewGuid(), DateTimeOffset.UnixEpoch, EndlessValues());
        Action excessiveLength = () => _ = new InstallerLog(
            new(state, MaximumSensitiveValueCharacters: 64),
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            new[] { new string('s', 65) }
        );

        excessiveCount.Should().Throw<ArgumentException>().WithMessage("*bounded count*");
        excessiveLength.Should().Throw<ArgumentException>().WithMessage("*bounded character count*");
        Directory.Exists(state).Should().BeFalse();
    }

    [Test]
    public void Redact_BoundsRawInputAndEscapesUnsafeUnicodeDeterministically()
    {
        string state = this.CreateDirectory();
        using InstallerLog log = new(
            new(state, MaximumMessageCharacters: 128, MaximumRawMessageCharacters: 128),
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            new[] { "secret" }
        );

        log.Redact(new string('x', 129) + "secret").Should().Be("[message omitted: raw input exceeded limit]");
        log.Redact("first\r\nsecond\t\u202E\u2066\u2028\u2029\uD800end")
            .Should().Be("first\\u000D\\u000Asecond\\u0009\\u202E\\u2066\\u2028\\u2029\\uFFFDend");
    }

    [Test]
    public void RedactionRegexes_HaveFiniteTimeouts()
    {
        foreach (string name in new[] { "OwnedLogFilename", "UriQuery", "Credential", "Bearer" })
        {
            Regex regex = (Regex)typeof(InstallerLog)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            regex.MatchTimeout.Should().NotBe(Regex.InfiniteMatchTimeout);
        }
    }

    [Test]
    public void Write_EmitsOneDurableTruncationMarkerAndPreservesTerminalReserve()
    {
        string state = this.CreateDirectory();
        Guid operationId = Guid.NewGuid();
        string path;
        using (InstallerLog log = new(
            new(state, MaximumFileBytes: 2048, MaximumMessageCharacters: 128, MaximumRawMessageCharacters: 128, MaximumEntryCount: 1),
            operationId,
            DateTimeOffset.UnixEpoch
        ))
        {
            path = log.Path;
            log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "entry.one", "one"))
                .Should().BeTrue();
            for (int index = 0; index < 100; index++)
            {
                log.Write(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "entry.flood", "flood"))
                    .Should().BeFalse();
            }
            log.WriteTerminal(new(DateTimeOffset.UnixEpoch, operationId, InstallerLogLevel.Information, "session.closed", "The installer session closed."))
                .Should().BeTrue();
        }

        string[] lines = File.ReadAllLines(path);
        lines.Should().HaveCount(3);
        lines.Count(line => JsonDocument.Parse(line).RootElement.GetProperty("eventCode").GetString() == "log.truncated")
            .Should().Be(1);
        JsonDocument.Parse(lines[^1]).RootElement.GetProperty("eventCode").GetString().Should().Be("session.closed");
        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(2048);
    }

    [Test]
    public void Constructor_BoundsDirectoryEnumerationWithoutRemovingUnrelatedEntries()
    {
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        string logs = Path.Combine(state, "logs");
        for (int index = 0; index < 3; index++)
            File.WriteAllText(Path.Combine(logs, $"unrelated-{index}.txt"), "preserve");

        Action action = () => _ = new InstallerLog(
            new(state, MaximumFileCount: 1, MaximumDirectoryEntries: 4),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow
        );

        action.Should().Throw<IOException>().WithMessage("*bounded entry limit*");
        Directory.GetFiles(logs, "unrelated-*.txt").Should().HaveCount(3);
        Directory.GetFiles(logs, "*.jsonl").Should().ContainSingle();
    }

    [Test]
    public void Constructor_EnforcesAggregateAndPerFileBoundsWithoutRemovingUnrelatedEntries()
    {
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        string logs = Path.Combine(state, "logs");
        File.Delete(Directory.GetFiles(logs, "*.jsonl").Single());
        for (int index = 0; index < 3; index++)
        {
            string path = Path.Combine(logs, $"20000101T00000{index}Z-{Guid.NewGuid():N}.jsonl");
            File.WriteAllBytes(path, new byte[600]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMinutes(index));
        }
        string unrelated = Path.Combine(logs, "preserve.bin");
        File.WriteAllBytes(unrelated, new byte[3000]);

        using (InstallerLog aggregate = new(
            new(state, MaximumFileBytes: 1024, MaximumFileCount: 5, MaximumAggregateBytes: 2048),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow
        ))
        {
            Directory.GetFiles(logs, "*.jsonl").Should().HaveCount(2);
        }
        new FileInfo(unrelated).Length.Should().Be(3000);

        string oversized = Path.Combine(logs, $"20010101T000000Z-{Guid.NewGuid():N}.jsonl");
        File.WriteAllBytes(oversized, new byte[1025]);
        File.SetUnixFileMode(oversized, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Action perFile = () => _ = new InstallerLog(new(state, MaximumFileBytes: 1024), Guid.NewGuid(), DateTimeOffset.UtcNow);
        perFile.Should().Throw<IOException>().WithMessage("*per-file byte bound*");
        File.Exists(oversized).Should().BeTrue();
        new FileInfo(unrelated).Length.Should().Be(3000);
    }

    [Test]
    public void Constructor_ActiveLogRetainsExclusiveRotationLock()
    {
        string state = this.CreateDirectory();
        using InstallerLog active = new(new(state, MaximumFileCount: 1), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        active.Write(new(DateTimeOffset.UnixEpoch, GetOperationId(active.Path), InstallerLogLevel.Information, "active", "preserve"));

        Action second = () => _ = new InstallerLog(new(state, MaximumFileCount: 1), Guid.NewGuid(), DateTimeOffset.UtcNow);

        second.Should().Throw<IOException>().WithMessage("*exclusive lock*");
        File.ReadAllText(active.Path).Should().Contain("preserve");
    }

    [Test]
    public async Task Constructor_ConcurrentCreators_AdmitsExactlyOneUntilItReleasesLock()
    {
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        using Barrier start = new(8);
        ConcurrentBag<InstallerLog> admitted = new();
        Task[] attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                admitted.Add(new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow));
            }
            catch (IOException)
            {
            }
        })).ToArray();

        await Task.WhenAll(attempts);
        admitted.Should().ContainSingle();
        admitted.Single().Dispose();
        using InstallerLog afterRelease = new(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    [Test]
    public void Constructor_RejectsNonPrivateModesAndUnsafeLockLinks()
    {
        string state = this.CreateDirectory();
        using (InstallerLog initial = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch)) { }
        string logs = Path.Combine(state, "logs");
        string marker = Path.Combine(state, "state-version");
        string lockPath = Path.Combine(logs, "installer-log.lock");
        File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        Action looseMarker = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);
        looseMarker.Should().Throw<IOException>().WithMessage("*state marker*exact private*");
        File.GetUnixFileMode(marker).Should().HaveFlag(UnixFileMode.GroupRead);

        File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(lockPath);
        string external = Path.Combine(this.TemporaryDirectory!, "external-lock");
        File.WriteAllText(external, "preserve");
        File.CreateSymbolicLink(lockPath, external);
        Action symlink = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);
        symlink.Should().Throw<IOException>();
        File.ReadAllText(external).Should().Be("preserve");

        File.Delete(lockPath);
        File.WriteAllText(lockPath, "lock");
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string secondLink = Path.Combine(this.TemporaryDirectory!, "lock-second-link");
        link(lockPath, secondLink).Should().Be(0);
        Action hardlink = () => _ = new InstallerLog(new(state), Guid.NewGuid(), DateTimeOffset.UtcNow);
        hardlink.Should().Throw<IOException>().WithMessage("*multiple hard links*");
        File.ReadAllText(secondLink).Should().Be("lock");
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void Constructor_UsesCurrentOwnerAndExactModesWithoutSpecialBits()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string state = this.CreateDirectory();
        using InstallerLog log = new(new(state), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        string logs = Path.Combine(state, "logs");
        string marker = Path.Combine(state, "state-version");
        string lockPath = Path.Combine(logs, "installer-log.lock");

        foreach (string directory in new[] { state, logs })
        {
            GetUnixIdentity(directory).UserId.Should().Be(geteuid());
            (GetUnixIdentity(directory).Mode & 0xfff).Should().Be(0x1c0);
        }
        foreach (string file in new[] { marker, lockPath, log.Path })
        {
            GetUnixIdentity(file).UserId.Should().Be(geteuid());
            (GetUnixIdentity(file).Mode & 0xfff).Should().Be(0x180);
            GetUnixIdentity(file).LinkCount.Should().Be(1);
        }
    }

    private static IEnumerable<string> EndlessValues()
    {
        while (true)
            yield return "sensitive";
    }

    private static Guid GetOperationId(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return Guid.ParseExact(name[^32..], "N");
    }

    private static (uint UserId, ushort Mode, uint LinkCount) GetUnixIdentity(string path)
    {
        statx(-100, path, 0x100, 0x7ff, out Statx data).Should().Be(0);
        return (data.UserId, data.Mode, data.LinkCount);
    }

    private string CreateDirectory()
    {
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-installer-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TemporaryDirectory);
        return Path.Combine(this.TemporaryDirectory, "state");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int directory, string path, int flags, uint mask, out Statx data);

    [DllImport("libc")]
    private static extern uint geteuid();

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
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
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }
}
