using System.Runtime.Versioning;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class LinuxGameDiscoveryTests
{
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-game-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public void Validate_RequiresAllAnchoredModernGameMarkers()
    {
        string game = this.CreateValidGame();

        LinuxGameFolderCandidate result = LinuxGameDiscovery.Validate(
            game,
            new Version(1, 0),
            CancellationToken.None
        );

        result.IsValid.Should().BeTrue();
        result.Status.Should().Be(LinuxGameFolderStatus.Valid);
        result.GameRoot.Should().NotBeNull();
        result.CanonicalPath.Should().Be(Path.GetFullPath(game));
        result.GameVersion.Should().NotBeNull();
    }

    [Test]
    public void Validate_AcceptsPinnedLinuxHardStateSyntheticAssemblyFixture()
    {
        string game = Path.Combine(this.TempRoot, $"game-{Guid.NewGuid():N}");
        Directory.CreateDirectory(game);
        string fixturePath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "LinuxGuiHardState",
            "Stardew Valley.dll.base64"
        );
        string encoded = File.ReadAllText(fixturePath);
        byte[] assembly = Convert.FromBase64String(string.Concat(encoded.Where(character => !char.IsWhiteSpace(character))));
        File.WriteAllBytes(Path.Combine(game, "Stardew Valley.dll"), assembly);
        this.WriteDependencies(game);
        string launcher = Path.Combine(game, "StardewValley");
        File.WriteAllText(launcher, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        LinuxGameFolderCandidate result = LinuxGameDiscovery.Validate(
            game,
            new Version(1, 6, 14),
            CancellationToken.None
        );

        result.Status.Should().Be(LinuxGameFolderStatus.Valid);
        result.GameVersion.Should().Be(new Version(1, 6, 15, 0));
    }

    [TestCase("Stardew Valley.dll", LinuxGameFolderStatus.MissingGameAssembly)]
    [TestCase("Stardew Valley.deps.json", LinuxGameFolderStatus.MissingGameDependencies)]
    [TestCase("StardewValley", LinuxGameFolderStatus.MissingLauncher)]
    public void Validate_ReportsMissingMarker(string marker, LinuxGameFolderStatus expected)
    {
        string game = this.CreateValidGame();
        File.Delete(Path.Combine(game, marker));

        LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).Status.Should().Be(expected);
    }

    [TestCase("Stardew Valley.dll", LinuxGameFolderStatus.UnsafeGameAssembly)]
    [TestCase("Stardew Valley.deps.json", LinuxGameFolderStatus.UnsafeGameDependencies)]
    [TestCase("StardewValley", LinuxGameFolderStatus.UnsafeLauncher)]
    public void Validate_RejectsLinkedMarker(string marker, LinuxGameFolderStatus expected)
    {
        string game = this.CreateValidGame();
        string path = Path.Combine(game, marker);
        string target = path + ".target";
        File.Move(path, target);
        File.CreateSymbolicLink(path, target);

        LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).Status.Should().Be(expected);
    }

    [Test]
    public void Validate_RejectsInvalidDependenciesAndNonExecutableLauncher()
    {
        string game = this.CreateValidGame();
        File.WriteAllText(Path.Combine(game, "Stardew Valley.deps.json"), "{}");
        LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).Status
            .Should().Be(LinuxGameFolderStatus.InvalidGameDependencies);

        this.WriteDependencies(game);
        File.SetUnixFileMode(Path.Combine(game, "StardewValley"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).Status
            .Should().Be(LinuxGameFolderStatus.UnsafeLauncher);
    }

    [Test]
    public void Validate_RejectsManagedAssemblyWithWrongIdentity()
    {
        string game = this.CreateValidGame();
        File.Copy(
            typeof(LinuxGameDiscovery).Assembly.Location,
            Path.Combine(game, "Stardew Valley.dll"),
            overwrite: true
        );

        LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).Status
            .Should().Be(LinuxGameFolderStatus.InvalidGameAssembly);
    }

    [Test]
    public void Validate_RejectsUnsupportedVersionAndHonorsCancellation()
    {
        string game = this.CreateValidGame();
        Version actual = LinuxGameDiscovery.Validate(game, new Version(1, 0), CancellationToken.None).GameVersion!;
        LinuxGameDiscovery.Validate(game, new Version(actual.Major + 1, 0), CancellationToken.None).Status
            .Should().Be(LinuxGameFolderStatus.UnsupportedGameVersion);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Action act = () => LinuxGameDiscovery.Validate(game, new Version(1, 0), cancellation.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void Discover_IsBoundedDeduplicatedAndKeepsManualInvalidResults()
    {
        string game = this.CreateValidGame();
        string missing = Path.Combine(this.TempRoot, "missing");
        string[] paths = Enumerable.Repeat(game, 63)
            .Append(missing)
            .Concat(Enumerable.Range(0, 200).Select(index => Path.Combine(this.TempRoot, $"extra-{index}")))
            .ToArray();

        IReadOnlyList<LinuxGameFolderCandidate> result = new LinuxGameDiscovery().Discover(
            paths,
            includeConventionalPaths: false,
            CancellationToken.None,
            new Version(1, 0)
        );

        result.Should().HaveCount(64);
        result.Should().ContainSingle(candidate => candidate.Status == LinuxGameFolderStatus.Valid);
        result.Should().Contain(candidate => candidate.CanonicalPath == missing && candidate.Status == LinuxGameFolderStatus.MissingDirectory);
    }

    private string CreateValidGame()
    {
        string game = Path.Combine(this.TempRoot, $"game-{Guid.NewGuid():N}");
        LinuxGameTestFolder.MakeValid(game);
        return game;
    }

    private void WriteDependencies(string game)
    {
        File.WriteAllText(
            Path.Combine(game, "Stardew Valley.deps.json"),
            JsonSerializer.Serialize(new
            {
                runtimeTarget = new { name = ".NETCoreApp,Version=v6.0/linux-x64" },
                targets = new Dictionary<string, object> { [".NETCoreApp,Version=v6.0/linux-x64"] = new { } }
            })
        );
    }
}
