using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace SMAPI.Tests.Core;

/// <summary>Linux runtime dispatcher regression tests.</summary>
[TestFixture]
internal class LinuxRuntimeDispatcherTests
{
    private const long MaximumDependencyMetadataBytes = 16L * 1024 * 1024;
    private const string DependencyRepairGuidance =
        "SMAPI can't safely launch with the game's .NET runtime because dependency metadata is missing, unsafe, or out of date.\n"
        + "Run the Linux installer with --repair --game-path \"<your game folder>\", then try again.\n";

    private static readonly string[] NativeGcSettings =
    {
        "DOTNET_gcServer",
        "COMPlus_gcServer",
        "DOTNET_GCHeapCount",
        "COMPlus_GCHeapCount"
    };

    [TestCase(null, "net6")]
    [TestCase("auto", "net6")]
    [TestCase("net6", "net6")]
    [TestCase("net10", "net10")]
    public async Task SelectsExpectedRuntime(string? configuredRuntime, string expectedRuntime)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            await LinuxRuntimeDispatcherTests.WriteMatchingDeps(root, "current dependency metadata\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "printf '%s\\n' 'net6'");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net10", "printf '%s\\n' 'net10'");

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, configuredRuntime, gcMode: "workstation");

            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Trim().Should().Be(expectedRuntime);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RejectsInvalidRuntimeWithUsageExitCode()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "future");

            result.ExitCode.Should().Be(64);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be("Invalid SMAPI_DOTNET_RUNTIME value 'future'. Expected 'auto', 'net6', or 'net10'.\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RejectsInvalidGcModeWithUsageExitCode()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net10", gcMode: "parallel");

            result.ExitCode.Should().Be(64);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be("Invalid SMAPI_GC_MODE value 'parallel'. Expected 'auto', 'workstation', or 'server4'.\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("net6", false)]
    [TestCase("net6", true)]
    [TestCase("net10", false)]
    [TestCase("net10", true)]
    public async Task RejectsMissingOrNonExecutableRuntimeHost(string runtime, bool createNonExecutableHost)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            if (runtime == "net6")
                await LinuxRuntimeDispatcherTests.WriteMatchingDeps(root, "dependency metadata\n");
            if (createNonExecutableHost)
                await LinuxRuntimeDispatcherTests.WriteHost(root, runtime, "exit 0", executable: false);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime, gcMode: "workstation");

            string hostPath = Path.Combine(root, $"StardewModdingAPI-{runtime}");
            result.ExitCode.Should().Be(1);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be($"SMAPI's {runtime} runtime host is missing or isn't executable: {hostPath}\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("missing-source")]
    [TestCase("missing-target")]
    [TestCase("mismatch")]
    [TestCase("mode-mismatch")]
    public async Task RefusesMissingOrMismatchedDependencyMetadataWithoutWriting(string scenario)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            if (scenario != "missing-source")
                await LinuxRuntimeDispatcherTests.WriteGameDeps(root, "current dependency metadata\n");
            if (scenario != "missing-target")
                await LinuxRuntimeDispatcherTests.WriteTargetDeps(root, scenario == "mismatch" ? "stale dependency metadata\n" : "current dependency metadata\n");
            if (scenario == "mode-mismatch")
                File.SetUnixFileMode(Path.Combine(root, "StardewModdingAPI-net6.deps.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "printf '%s\\n' 'HOST_STARTED'");
            string before = await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6");

            result.ExitCode.Should().Be(1);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be(LinuxRuntimeDispatcherTests.DependencyRepairGuidance);
            (await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root)).Should().Be(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("source", "symlink")]
    [TestCase("target", "symlink")]
    [TestCase("source", "hardlink")]
    [TestCase("target", "hardlink")]
    [TestCase("source", "fifo")]
    [TestCase("target", "fifo")]
    [TestCase("source", "directory")]
    [TestCase("target", "directory")]
    [TestCase("source", "empty")]
    [TestCase("target", "empty")]
    [TestCase("source", "oversize")]
    [TestCase("target", "oversize")]
    public async Task RefusesUnsafeDependencyMetadataWithoutFollowingOrWriting(string selectedEntry, string unsafeKind)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        string external = LinuxRuntimeDispatcherTests.CreatePlainTestRoot();
        try
        {
            await LinuxRuntimeDispatcherTests.WriteMatchingDeps(root, "current dependency metadata\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "printf '%s\\n' 'HOST_STARTED'");
            string selectedPath = Path.Combine(root, selectedEntry == "source" ? "Stardew Valley.deps.json" : "StardewModdingAPI-net6.deps.json");
            File.Delete(selectedPath);
            string externalEntry = Path.Combine(external, "external-dependency-metadata.json");
            await File.WriteAllTextAsync(externalEntry, "external sentinel dependency metadata\n");
            string externalSentinel = Path.Combine(external, "unrelated-sentinel.txt");
            await File.WriteAllTextAsync(externalSentinel, "preserve this external sentinel exactly\n");

            switch (unsafeKind)
            {
                case "symlink":
                    File.CreateSymbolicLink(selectedPath, externalEntry);
                    break;
                case "hardlink":
                    LinuxRuntimeDispatcherTests.CreateHardLink(externalEntry, selectedPath);
                    break;
                case "fifo":
                    LinuxRuntimeDispatcherTests.CreateFifo(selectedPath);
                    break;
                case "directory":
                    Directory.CreateDirectory(selectedPath);
                    break;
                case "empty":
                    await File.WriteAllBytesAsync(selectedPath, Array.Empty<byte>());
                    break;
                case "oversize":
                    await using (FileStream stream = File.Create(selectedPath))
                        stream.SetLength(LinuxRuntimeDispatcherTests.MaximumDependencyMetadataBytes + 1);
                    break;
                default:
                    throw new AssertionException($"Unknown unsafe fixture '{unsafeKind}'.");
            }
            string beforeRoot = await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root);
            string beforeExternal = await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(external);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6");

            result.ExitCode.Should().Be(1);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be(LinuxRuntimeDispatcherTests.DependencyRepairGuidance);
            (await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root)).Should().Be(beforeRoot);
            (await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(external)).Should().Be(beforeExternal);
            (await File.ReadAllTextAsync(externalSentinel)).Should().Be("preserve this external sentinel exactly\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [TestCase("net6", "symlink")]
    [TestCase("net6", "hardlink")]
    [TestCase("net6", "fifo")]
    [TestCase("net6", "directory")]
    [TestCase("net6", "empty")]
    [TestCase("net10", "symlink")]
    [TestCase("net10", "hardlink")]
    [TestCase("net10", "fifo")]
    [TestCase("net10", "directory")]
    [TestCase("net10", "empty")]
    public async Task RejectsUnsafeRuntimeHostWithoutFollowingOrWriting(string runtime, string unsafeKind)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        string external = LinuxRuntimeDispatcherTests.CreatePlainTestRoot();
        try
        {
            if (runtime == "net6")
                await LinuxRuntimeDispatcherTests.WriteMatchingDeps(root, "current dependency metadata\n");
            string hostPath = Path.Combine(root, $"StardewModdingAPI-{runtime}");
            string externalHost = Path.Combine(external, "external-host");
            await LinuxRuntimeDispatcherTests.WriteExecutableFile(externalHost, "#!/bin/sh\nprintf '%s\\n' 'EXTERNAL_HOST_STARTED'\n");
            await File.WriteAllTextAsync(Path.Combine(external, "unrelated-sentinel.txt"), "external host sentinel\n");

            switch (unsafeKind)
            {
                case "symlink":
                    File.CreateSymbolicLink(hostPath, externalHost);
                    break;
                case "hardlink":
                    LinuxRuntimeDispatcherTests.CreateHardLink(externalHost, hostPath);
                    break;
                case "fifo":
                    LinuxRuntimeDispatcherTests.CreateFifo(hostPath);
                    File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    break;
                case "directory":
                    Directory.CreateDirectory(hostPath);
                    File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    break;
                case "empty":
                    await LinuxRuntimeDispatcherTests.WriteExecutableFile(hostPath, "");
                    break;
                default:
                    throw new AssertionException($"Unknown unsafe fixture '{unsafeKind}'.");
            }
            string beforeRoot = await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root);
            string beforeExternal = await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(external);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: runtime, gcMode: "workstation");

            result.ExitCode.Should().Be(1);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be($"SMAPI's {runtime} runtime host is missing or isn't executable: {hostPath}\n");
            (await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(root)).Should().Be(beforeRoot);
            (await LinuxRuntimeDispatcherTests.CaptureTreeSnapshot(external)).Should().Be(beforeExternal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Test]
    public async Task PreservesArgumentsWhenDispatcherPathContainsSpaces()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot(includeSpaces: true);
        try
        {
            await LinuxRuntimeDispatcherTests.WriteMatchingDeps(root, "dependency metadata\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "for argument in \"$@\"; do printf '<%s>\\n' \"$argument\"; done");
            string[] arguments = { "", "two words", "literal*?[x]", "quote'\"$" };

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6", arguments: arguments);

            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Be("<>\n<two words>\n<literal*?[x]>\n<quote'\"$>\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("workstation", "0", "unset")]
    [TestCase("server4", "1", "4")]
    public async Task AppliesExplicitNet10GcPolicy(string gcMode, string expectedServerGc, string expectedHeapCount)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            await LinuxRuntimeDispatcherTests.WriteGcReportingHost(root);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net10", gcMode: gcMode);

            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Be(LinuxRuntimeDispatcherTests.FormatGcSettings(new Dictionary<string, string>
            {
                ["DOTNET_gcServer"] = expectedServerGc,
                ["DOTNET_GCHeapCount"] = expectedHeapCount
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("DOTNET_gcServer")]
    [TestCase("COMPlus_gcServer")]
    [TestCase("DOTNET_GCHeapCount")]
    [TestCase("COMPlus_GCHeapCount")]
    public async Task PreservesNativeGcOverrideWithoutLayeringSmapiPolicy(string nativeSetting)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            await LinuxRuntimeDispatcherTests.WriteGcReportingHost(root);
            Dictionary<string, string> environment = new() { [nativeSetting] = "native-value" };

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net10", gcMode: "auto", environment: environment);

            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Be(LinuxRuntimeDispatcherTests.FormatGcSettings(environment));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Create an isolated test directory containing a copy of the runtime dispatcher.</summary>
    private static string CreateTestRoot(bool includeSpaces = false)
    {
        string name = $"{(includeSpaces ? "dispatcher path with spaces " : "")}{Guid.NewGuid():N}";
        string root = Path.Combine(Path.GetTempPath(), "smapi-runtime-dispatcher-tests", name);
        Directory.CreateDirectory(root);
        string sourceScript = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestAssets", "smapi-runtime-dispatcher.sh");
        File.Copy(sourceScript, Path.Combine(root, "StardewModdingAPI"));
        return root;
    }

    /// <summary>Create an empty isolated directory which doesn't contain the dispatcher.</summary>
    private static string CreatePlainTestRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "smapi-runtime-dispatcher-tests", $"external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Capture entry identities and regular-file bytes without following links or opening special files.</summary>
    private static async Task<string> CaptureTreeSnapshot(string root)
    {
        ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("find -P \"$1\" -printf '%P|%y|%D|%i|%n|%s|%m|%l\\n' | LC_ALL=C sort; find -P \"$1\" -type f -exec sha256sum -- {} + | LC_ALL=C sort");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(root);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await error);
        return await output;
    }

    /// <summary>Format the native GC settings printed by the test host.</summary>
    private static string FormatGcSettings(IReadOnlyDictionary<string, string> values)
    {
        return string.Concat(LinuxRuntimeDispatcherTests.NativeGcSettings.Select(name => $"{name}={(values.TryGetValue(name, out string? value) ? value : "unset")}\n"));
    }

    /// <summary>Skip a test when it runs on a platform to which the dispatcher doesn't apply.</summary>
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("The runtime dispatcher only applies to Linux.");
    }

    /// <summary>Run the dispatcher and capture its output.</summary>
    private static async Task<DispatcherResult> RunDispatcher(
        string root,
        string? runtime = null,
        string? gcMode = null,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "StardewModdingAPI"));
        foreach (string argument in arguments ?? Array.Empty<string>())
            start.ArgumentList.Add(argument);

        start.Environment.Remove("SMAPI_DOTNET_RUNTIME");
        start.Environment.Remove("SMAPI_GC_MODE");
        foreach (string name in LinuxRuntimeDispatcherTests.NativeGcSettings)
            start.Environment.Remove(name);
        if (runtime is not null)
            start.Environment["SMAPI_DOTNET_RUNTIME"] = runtime;
        if (gcMode is not null)
            start.Environment["SMAPI_GC_MODE"] = gcMode;
        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
                start.Environment[name] = value;
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The runtime dispatcher did not terminate within ten seconds.");
        }
        string standardOutput = await output;
        string standardError = await error;
        return new DispatcherResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>Create a fake runtime host with the given shell body.</summary>
    private static async Task WriteHost(string root, string runtime, string shellBody, bool executable = true)
    {
        string path = Path.Combine(root, $"StardewModdingAPI-{runtime}");
        await LinuxRuntimeDispatcherTests.WriteExecutableFile(path, $"#!/bin/sh\n{shellBody}\n", executable);
    }

    /// <summary>Write a regular file with a selected executable mode.</summary>
    private static async Task WriteExecutableFile(string path, string contents, bool executable = true)
    {
        await File.WriteAllTextAsync(path, contents);
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (executable)
            mode |= UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>Create a fake runtime host which prints all native GC settings.</summary>
    private static Task WriteGcReportingHost(string root)
    {
        const string shellBody = "printf 'DOTNET_gcServer=%s\\n' \"${DOTNET_gcServer-unset}\"; printf 'COMPlus_gcServer=%s\\n' \"${COMPlus_gcServer-unset}\"; printf 'DOTNET_GCHeapCount=%s\\n' \"${DOTNET_GCHeapCount-unset}\"; printf 'COMPlus_GCHeapCount=%s\\n' \"${COMPlus_GCHeapCount-unset}\"";
        return LinuxRuntimeDispatcherTests.WriteHost(root, "net10", shellBody);
    }

    /// <summary>Write the game dependency metadata consumed by the net6 dispatcher.</summary>
    private static Task WriteGameDeps(string root, string contents)
    {
        return File.WriteAllTextAsync(Path.Combine(root, "Stardew Valley.deps.json"), contents);
    }

    /// <summary>Write the installer-owned dependency metadata consumed by the net6 host.</summary>
    private static Task WriteTargetDeps(string root, string contents)
    {
        return File.WriteAllTextAsync(Path.Combine(root, "StardewModdingAPI-net6.deps.json"), contents);
    }

    /// <summary>Write matching game and installer-owned dependency metadata.</summary>
    private static async Task WriteMatchingDeps(string root, string contents)
    {
        await LinuxRuntimeDispatcherTests.WriteGameDeps(root, contents);
        await LinuxRuntimeDispatcherTests.WriteTargetDeps(root, contents);
    }

    /// <summary>Create a hard link and fail with the native error when unsupported.</summary>
    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (link(existingPath, newPath) != 0)
            throw new AssertionException($"link(2) failed with errno {Marshal.GetLastWin32Error()}.");
    }

    /// <summary>Create a FIFO fixture without ever opening it.</summary>
    private static void CreateFifo(string path)
    {
        if (mkfifo(path, 0x180) != 0)
            throw new AssertionException($"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, int mode);

    /// <summary>The result from a dispatcher process.</summary>
    private sealed record DispatcherResult(int ExitCode, string StandardOutput, string StandardError);
}
