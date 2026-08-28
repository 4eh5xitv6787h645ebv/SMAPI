using System.Text.Json;
using StardewModdingAPI.Installer.Core.Engine;

namespace StardewModdingAPI.Installer.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxGameTestFolder
{
    public static void MakeValid(string gameRoot)
    {
        Directory.CreateDirectory(gameRoot);
        string assembly = Path.Combine(gameRoot, "Stardew Valley.dll");
        if (!File.Exists(assembly))
            File.Copy(typeof(SyntheticStardewValleyAssembly.Marker).Assembly.Location, assembly);

        string runtime = ".NETCoreApp,Version=v6.0/linux-x64";
        string dependencies = Path.Combine(gameRoot, "Stardew Valley.deps.json");
        if (!File.Exists(dependencies))
        {
            File.WriteAllText(
                dependencies,
                JsonSerializer.Serialize(new
                {
                    runtimeTarget = new { name = runtime },
                    targets = new Dictionary<string, object> { [runtime] = new { } }
                })
            );
        }

        string launcher = Path.Combine(gameRoot, "StardewValley");
        if (!File.Exists(launcher))
            File.WriteAllText(launcher, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(launcher, (UnixFileMode)0x1ed);
    }
}
