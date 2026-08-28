namespace StardewModdingAPI.Installer.Core.Privacy;

/// <summary>Resolves private per-user installer state without touching the filesystem.</summary>
public static class InstallerStatePaths
{
    /// <summary>Get the installer state root under XDG state storage.</summary>
    /// <param name="environment">An optional environment lookup for deterministic tests.</param>
    /// <param name="userProfile">An optional user profile override for deterministic tests.</param>
    public static string GetStateRoot(Func<string, string?>? environment = null, string? userProfile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string? xdgStateHome = environment("XDG_STATE_HOME");
        string basePath;
        if (!string.IsNullOrWhiteSpace(xdgStateHome) && Path.IsPathRooted(xdgStateHome))
            basePath = xdgStateHome;
        else
        {
            userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathRooted(userProfile))
                throw new InvalidOperationException("A private user state directory couldn't be resolved.");
            basePath = Path.Combine(userProfile, ".local", "state");
        }

        return Path.GetFullPath(Path.Combine(basePath, "smapi-installer"));
    }
}
