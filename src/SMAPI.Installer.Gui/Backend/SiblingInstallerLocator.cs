using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Resolves the sole backend executable which the graphical installer is allowed to launch.</summary>
internal static class SiblingInstallerLocator
{
    internal const string InstallerFileName = "SMAPI.Installer";

    /// <summary>Atomically retain the exact regular sibling inode used by production process launch.</summary>
    public static LinuxExternalExecutableLease OpenForCurrentProcess()
    {
        string guiExecutablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The graphical installer executable path isn't available.");
        return OpenSibling(guiExecutablePath);
    }

    internal static LinuxExternalExecutableLease OpenSibling(string guiExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guiExecutablePath);
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(guiExecutablePath))
            throw new InvalidOperationException("The graphical installer executable path must be absolute on Linux.");
        string? directory = Path.GetDirectoryName(Path.GetFullPath(guiExecutablePath));
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The graphical installer executable has no containing directory.");
        try
        {
            return LinuxExternalExecutableLease.Open(Path.Combine(directory, InstallerFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException("The packaged installer backend isn't a safe executable sibling.");
        }
    }
}
