namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>The semantic ownership category for a package or receipt entry.</summary>
public enum OwnedEntryKind
{
    /// <summary>A top-level SMAPI runtime file.</summary>
    RuntimeFile,

    /// <summary>A file below <c>smapi-internal</c>.</summary>
    InternalFile,

    /// <summary>A file in one of the two installer-owned bundled mods.</summary>
    BundledModFile,

    /// <summary>The installed Linux launcher at <c>StardewValley</c>.</summary>
    Launcher,

    /// <summary>The original Linux launcher at <c>StardewValley-original</c>, usable only by recovery state.</summary>
    RecoveryLauncherBackup,

    /// <summary>A generated SMAPI file derived from a game-owned source.</summary>
    GeneratedFile
}

/// <summary>Compiled constraints on every namespace the installer is permitted to own.</summary>
public static class OwnedNamespacePolicy
{
    private static readonly HashSet<string> RuntimeFiles = new(StringComparer.Ordinal)
    {
        "StardewModdingAPI",
        "StardewModdingAPI.deps.json",
        "StardewModdingAPI.dll",
        "StardewModdingAPI.exe",
        "StardewModdingAPI.exe.config",
        "StardewModdingAPI.runtimeconfig.json",
        "StardewModdingAPI.xml",
        "StardewModdingAPI-net6",
        "StardewModdingAPI-net6.dll",
        "StardewModdingAPI-net6.runtimeconfig.json",
        "StardewModdingAPI-net10",
        "StardewModdingAPI-net10.deps.json",
        "StardewModdingAPI-net10.dll",
        "StardewModdingAPI-net10.runtimeconfig.json",
        "steam_appid.txt"
    };

    private static readonly HashSet<string> GeneratedFiles = new(StringComparer.Ordinal)
    {
        "StardewModdingAPI-net6.deps.json"
    };

    private static readonly HashSet<string> RecognizedLegacyFiles = new(
        RuntimeFiles.Where(path => path != "steam_appid.txt").Concat(GeneratedFiles),
        StringComparer.Ordinal
    );

    /// <summary>
    /// Get whether an exact manifest destination is a compiled legacy-SMAPI candidate when present without a receipt.
    /// This intentionally excludes generic game-adjacent names which aren't evidence of an older SMAPI installation.
    /// </summary>
    internal static bool IsRecognizedLegacyCandidate(PackageManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecognizedLegacyFiles.Contains(entry.Path.Value);
    }

    /// <summary>Map one exact nested-package source path to its compiled installation destination and kind.</summary>
    internal static bool TryMapPackageSource(
        string sourcePath,
        out NormalizedRelativePath? destination,
        out OwnedEntryKind kind
    )
    {
        if (sourcePath == "unix-launcher.sh")
        {
            destination = NormalizedRelativePath.Parse("StardewValley");
            kind = OwnedEntryKind.Launcher;
            return true;
        }

        NormalizedRelativePath parsed;
        try
        {
            parsed = NormalizedRelativePath.Parse(sourcePath);
        }
        catch (ArgumentException)
        {
            destination = null;
            kind = default;
            return false;
        }

        if (OwnedNamespacePolicy.RuntimeFiles.Contains(sourcePath))
            kind = OwnedEntryKind.RuntimeFile;
        else if (sourcePath.StartsWith("smapi-internal/", StringComparison.Ordinal) && sourcePath != "smapi-internal/config.user.json")
            kind = OwnedEntryKind.InternalFile;
        else if (sourcePath.StartsWith("Mods/ConsoleCommands/", StringComparison.Ordinal) || sourcePath.StartsWith("Mods/SaveBackup/", StringComparison.Ordinal))
            kind = OwnedEntryKind.BundledModFile;
        else
        {
            destination = null;
            kind = default;
            return false;
        }

        destination = parsed;
        return true;
    }

    /// <summary>Get whether a path is in the complete compiled transaction destination allowlist.</summary>
    /// <remarks>This is intentionally independent of package or receipt input. Transaction execution must fail closed even if a caller bypasses the planner.</remarks>
    public static bool IsAllowedTransactionDestination(NormalizedRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string value = path.Value;
        return value is "StardewValley" or "StardewValley-original"
            || OwnedNamespacePolicy.RuntimeFiles.Contains(value)
            || OwnedNamespacePolicy.GeneratedFiles.Contains(value)
            || (value.StartsWith("smapi-internal/", StringComparison.Ordinal) && value != "smapi-internal/config.user.json")
            || value.StartsWith("Mods/ConsoleCommands/", StringComparison.Ordinal)
            || value.StartsWith("Mods/SaveBackup/", StringComparison.Ordinal);
    }

    /// <summary>Validate that a path belongs to the exact compiled namespace for its declared kind.</summary>
    public static void AssertAllowed(NormalizedRelativePath path, OwnedEntryKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        string value = path.Value;
        bool allowed = kind switch
        {
            OwnedEntryKind.RuntimeFile => OwnedNamespacePolicy.RuntimeFiles.Contains(value),
            OwnedEntryKind.GeneratedFile => OwnedNamespacePolicy.GeneratedFiles.Contains(value),
            OwnedEntryKind.Launcher => value == "StardewValley",
            OwnedEntryKind.RecoveryLauncherBackup => false,
            OwnedEntryKind.InternalFile => value.StartsWith("smapi-internal/", StringComparison.Ordinal)
                && value != "smapi-internal/config.user.json",
            OwnedEntryKind.BundledModFile => value.StartsWith("Mods/ConsoleCommands/", StringComparison.Ordinal)
                || value.StartsWith("Mods/SaveBackup/", StringComparison.Ordinal),
            _ => false
        };

        if (!allowed)
            throw new ArgumentException($"Path '{value}' isn't in the compiled installer-owned namespace for {kind}.", nameof(path));
    }

    /// <summary>Validate a path for a recovery snapshot, including the one recovery-only launcher backup rule.</summary>
    public static void AssertRecoveryAllowed(NormalizedRelativePath path, OwnedEntryKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (kind == OwnedEntryKind.RecoveryLauncherBackup)
        {
            if (path.Value != "StardewValley-original")
                throw new ArgumentException($"Path '{path}' isn't the recovery-only original-launcher path.", nameof(path));
            return;
        }

        AssertAllowed(path, kind);
    }
}
