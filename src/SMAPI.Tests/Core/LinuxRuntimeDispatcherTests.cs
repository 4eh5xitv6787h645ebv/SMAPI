using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace SMAPI.Tests.Core;

/// <summary>Linux runtime dispatcher regression tests.</summary>
[TestFixture]
internal class LinuxRuntimeDispatcherTests
{
    [TestCase(null, "net6")]
    [TestCase("auto", "net6")]
    [TestCase("net6", "net6")]
    [TestCase("net10", "net10")]
    public async Task SelectsExpectedRuntime(string? configuredRuntime, string expectedRuntime)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("The runtime dispatcher only applies to Linux.");

        string root = Path.Combine(Path.GetTempPath(), "smapi-runtime-dispatcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourceScript = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestAssets", "smapi-runtime-dispatcher.sh");
            string dispatcherPath = Path.Combine(root, "StardewModdingAPI");
            File.Copy(sourceScript, dispatcherPath);
            await File.WriteAllTextAsync(Path.Combine(root, "Stardew Valley.deps.json"), "{}\n");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net6");
            await LinuxRuntimeDispatcherTests.WriteHost(root, "net10");

            ProcessStartInfo start = new("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = root
            };
            start.ArgumentList.Add(dispatcherPath);
            start.Environment["SMAPI_GC_MODE"] = "workstation";
            if (configuredRuntime is null)
                start.Environment.Remove("SMAPI_DOTNET_RUNTIME");
            else
                start.Environment["SMAPI_DOTNET_RUNTIME"] = configuredRuntime;

            using Process process = Process.Start(start)!;
            string standardOutput = await process.StandardOutput.ReadToEndAsync();
            string standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, standardError);
            standardOutput.Trim().Should().Be(expectedRuntime);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Create a fake runtime host which prints the selected runtime.</summary>
    private static async Task WriteHost(string root, string runtime)
    {
        string path = Path.Combine(root, $"StardewModdingAPI-{runtime}");
        await File.WriteAllTextAsync(path, $"#!/bin/sh\nprintf '%s\\n' '{runtime}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
