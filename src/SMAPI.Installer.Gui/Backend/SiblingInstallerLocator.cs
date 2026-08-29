namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Resolves the sole backend executable which the graphical installer is allowed to launch.</summary>
internal static class SiblingInstallerLocator
{
    internal const string InstallerFileName = "SMAPI.Installer";

    public static string Locate(string guiExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guiExecutablePath);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The graphical installer backend is currently supported only on Linux.");
        if (!Path.IsPathFullyQualified(guiExecutablePath))
            throw new InvalidOperationException("The graphical installer executable path must be absolute.");

        string canonicalGuiPath = Path.GetFullPath(guiExecutablePath);
        string? directory = Path.GetDirectoryName(canonicalGuiPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The graphical installer executable has no containing directory.");

        string candidate = Path.Combine(directory, InstallerFileName);
        FileInfo file = new(candidate);
        file.Refresh();
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || file.LinkTarget is not null)
            throw new InvalidOperationException("The packaged installer backend is missing or isn't a regular file.");

        UnixFileMode mode = File.GetUnixFileMode(candidate);
        UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & executable) == 0)
            throw new InvalidOperationException("The packaged installer backend isn't executable.");

        return candidate;
    }
}
