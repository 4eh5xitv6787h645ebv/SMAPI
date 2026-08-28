using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace SMAPI.Tests.Core;

/// <summary>Linux runtime dispatcher regression tests.</summary>
[TestFixture]
internal class LinuxRuntimeDispatcherTests
{
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
            await LinuxRuntimeDispatcherTests.WriteGameDeps(root, "current dependency metadata\n");
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
                await LinuxRuntimeDispatcherTests.WriteGameDeps(root, "dependency metadata\n");
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepairsMissingOrStaleNet6DependencyMetadata(bool createStaleTarget)
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            string expected = "current dependency metadata\n";
            string targetPath = Path.Combine(root, "StardewModdingAPI-net6.deps.json");
            await LinuxRuntimeDispatcherTests.WriteGameDeps(root, expected);
            if (createStaleTarget)
                await File.WriteAllTextAsync(targetPath, "stale dependency metadata\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "exit 0");

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6");

            result.ExitCode.Should().Be(0, result.StandardError);
            (await File.ReadAllTextAsync(targetPath)).Should().Be(expected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RejectsMissingGameDependencyMetadataBeforeStartingNet6Host()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        try
        {
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "printf 'host started\\n'");

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6");

            string sourcePath = Path.Combine(root, "Stardew Valley.deps.json");
            result.ExitCode.Should().Be(1);
            result.StandardOutput.Should().BeEmpty();
            result.StandardError.Should().Be($"SMAPI can't find the game's dependency metadata: {sourcePath}\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CleansTemporaryDependencyMetadataWhenRepairFails()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot();
        UnixFileMode writableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            string targetPath = Path.Combine(root, "StardewModdingAPI-net6.deps.json");
            await LinuxRuntimeDispatcherTests.WriteGameDeps(root, "current dependency metadata\n");
            await File.WriteAllTextAsync(targetPath, "stale dependency metadata\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6", "exit 0");
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            DispatcherResult result = await LinuxRuntimeDispatcherTests.RunDispatcher(root, runtime: "net6");

            result.ExitCode.Should().Be(1);
            result.StandardError.Should().Contain("SMAPI couldn't update its game-runtime dependency metadata:");
            File.SetUnixFileMode(root, writableMode);
            (await File.ReadAllTextAsync(targetPath)).Should().Be("stale dependency metadata\n");
            Directory.EnumerateFileSystemEntries(root, "StardewModdingAPI-net6.deps.json.tmp.*").Should().BeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(root, writableMode);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PreservesArgumentsWhenDispatcherPathContainsSpaces()
    {
        LinuxRuntimeDispatcherTests.RequireLinux();

        string root = LinuxRuntimeDispatcherTests.CreateTestRoot(includeSpaces: true);
        try
        {
            await LinuxRuntimeDispatcherTests.WriteGameDeps(root, "dependency metadata\n");
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
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new DispatcherResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>Create a fake runtime host with the given shell body.</summary>
    private static async Task WriteHost(string root, string runtime, string shellBody, bool executable = true)
    {
        string path = Path.Combine(root, $"StardewModdingAPI-{runtime}");
        await File.WriteAllTextAsync(path, $"#!/bin/sh\n{shellBody}\n");
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

    /// <summary>The result from a dispatcher process.</summary>
    private sealed record DispatcherResult(int ExitCode, string StandardOutput, string StandardError);
}
