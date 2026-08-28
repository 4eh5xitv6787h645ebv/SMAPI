using System;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Build a stable description of the measured runtime.</summary>
internal static class RuntimeEnvironment
{
    /// <summary>The target framework of this executable.</summary>
    private const string TargetFramework = "net6.0";

    /// <summary>Get the current runtime target.</summary>
    public static RuntimeTarget GetCurrent()
    {
        return new RuntimeTarget(
            Framework: RuntimeEnvironment.TargetFramework,
            RuntimeVersion: Environment.Version.ToString(),
            Rid: RuntimeEnvironment.GetPortableRuntimeIdentifier(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ServerGarbageCollection: GCSettings.IsServerGC,
            TieredCompilation: RuntimeEnvironment.IsTieredCompilationEnabled()
        );
    }

    /// <summary>Get a portable OS and architecture runtime identifier.</summary>
    private static string GetPortableRuntimeIdentifier()
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? "linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "osx"
                    : "unknown";
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{architecture}";
    }

    /// <summary>Get whether tiered compilation is enabled through the runtime environment.</summary>
    private static bool IsTieredCompilationEnabled()
    {
        object? appContextValue = AppContext.GetData("System.Runtime.TieredCompilation");
        if (appContextValue is bool enabled)
            return enabled;
        if (appContextValue is string text && bool.TryParse(text, out enabled))
            return enabled;

        string? value = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
            ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
