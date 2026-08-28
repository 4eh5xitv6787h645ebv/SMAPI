using System.Collections.ObjectModel;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>How one observed path relates to verified installer ownership.</summary>
public enum InventoryClassification
{
    /// <summary>No current filesystem entry exists at this manifest or receipt path.</summary>
    Absent,

    /// <summary>The current file exactly matches its committed receipt.</summary>
    UnchangedOwned,

    /// <summary>The current file differs from its committed receipt.</summary>
    ModifiedOwned,

    /// <summary>The path is recognized from a legacy installer but has no ownership receipt.</summary>
    Legacy,

    /// <summary>An unowned current file collides with a relevant installer path.</summary>
    UnknownCollision,

    /// <summary>The path is explicitly user-owned and must be preserved.</summary>
    Preserved
}

/// <summary>A currently observed regular file.</summary>
public sealed record CurrentFile
{
    /// <summary>The canonical installation-relative path.</summary>
    public NormalizedRelativePath Path { get; }

    /// <summary>The observed file digest.</summary>
    public Sha256Digest Sha256 { get; }

    /// <summary>The observed Unix permission bits.</summary>
    public int UnixMode { get; }

    /// <summary>Construct an immutable current-file observation.</summary>
    internal CurrentFile(NormalizedRelativePath path, Sha256Digest sha256, int unixMode)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sha256);
        if (unixMode is < 0 or > 511)
            throw new ArgumentOutOfRangeException(nameof(unixMode));
        this.Path = path;
        this.Sha256 = sha256;
        this.UnixMode = unixMode;
    }
}

/// <summary>The ownership facts for one canonical relative path.</summary>
public sealed class InventoryEntry
{
    /// <summary>The canonical relative path.</summary>
    public NormalizedRelativePath Path { get; }

    /// <summary>The ownership classification.</summary>
    public InventoryClassification Classification { get; }

    /// <summary>The target package entry, if the selected release intends to install it.</summary>
    public PackageManifestEntry? Target { get; }

    /// <summary>The committed receipt entry, if a prior transaction owned it.</summary>
    public InstallationReceiptEntry? Installed { get; }

    /// <summary>The current file, if present.</summary>
    public CurrentFile? Current { get; }

    internal InventoryEntry(
        NormalizedRelativePath path,
        InventoryClassification classification,
        PackageManifestEntry? target,
        InstallationReceiptEntry? installed,
        CurrentFile? current
    )
    {
        this.Path = path;
        this.Classification = classification;
        this.Target = target;
        this.Installed = installed;
        this.Current = current;
    }
}

/// <summary>An immutable, deterministically ordered installation inventory.</summary>
public sealed class InstallationInventory
{
    /// <summary>All relevant paths in canonical ordinal order.</summary>
    public IReadOnlyList<InventoryEntry> Entries { get; }

    private InstallationInventory(InventoryEntry[] entries)
    {
        this.Entries = new ReadOnlyCollection<InventoryEntry>(entries);
    }

    /// <summary>Classify observed files against an optional target manifest and installed receipt.</summary>
    /// <param name="manifest">The selected release manifest, if the action has one.</param>
    /// <param name="receipt">The committed installed receipt, if present.</param>
    /// <param name="currentFiles">Scoped current-file observations. This must not be an unbounded game-folder scan.</param>
    /// <param name="preservedPaths">Explicitly user-owned paths in the scoped inventory.</param>
    /// <param name="legacyPaths">Recognized legacy SMAPI candidates which lack a receipt.</param>
    internal static InstallationInventory Create(
        PackageManifest? manifest,
        InstallationReceipt? receipt,
        IEnumerable<CurrentFile> currentFiles,
        IEnumerable<NormalizedRelativePath>? preservedPaths = null,
        IEnumerable<NormalizedRelativePath>? legacyPaths = null
    )
    {
        ArgumentNullException.ThrowIfNull(currentFiles);
        Dictionary<string, PackageManifestEntry> targets = (manifest?.Entries ?? Array.Empty<PackageManifestEntry>())
            .ToDictionary(entry => entry.Path.Value, StringComparer.Ordinal);
        Dictionary<string, InstallationReceiptEntry> installed = (receipt?.Entries ?? Array.Empty<InstallationReceiptEntry>())
            .ToDictionary(entry => entry.Path.Value, StringComparer.Ordinal);
        Dictionary<string, CurrentFile> current = ToUniqueDictionary(currentFiles, file => file.Path, nameof(currentFiles));
        HashSet<string> preserved = ToUniquePathSet(preservedPaths, nameof(preservedPaths));
        HashSet<string> legacy = ToUniquePathSet(legacyPaths, nameof(legacyPaths));

        foreach ((string path, PackageManifestEntry target) in targets)
        {
            if (installed.TryGetValue(path, out InstallationReceiptEntry? prior) && target.Kind != prior.Kind)
                throw new ArgumentException($"Target and receipt ownership kinds disagree for '{path}'.", nameof(manifest));
        }

        string[] paths = targets.Keys
            .Concat(installed.Keys)
            .Concat(current.Keys)
            .Concat(preserved)
            .Concat(legacy)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        List<InventoryEntry> entries = new(paths.Length);
        foreach (string pathValue in paths)
        {
            targets.TryGetValue(pathValue, out PackageManifestEntry? target);
            installed.TryGetValue(pathValue, out InstallationReceiptEntry? prior);
            current.TryGetValue(pathValue, out CurrentFile? observed);

            InventoryClassification classification;
            if (preserved.Contains(pathValue))
                classification = InventoryClassification.Preserved;
            else if (observed == null)
                classification = InventoryClassification.Absent;
            else if (prior != null)
            {
                classification = observed.Sha256 == prior.InstalledSha256 && observed.UnixMode == prior.UnixMode
                    ? InventoryClassification.UnchangedOwned
                    : InventoryClassification.ModifiedOwned;
            }
            else if (legacy.Contains(pathValue))
                classification = InventoryClassification.Legacy;
            else
                classification = InventoryClassification.UnknownCollision;

            NormalizedRelativePath path = target?.Path ?? prior?.Path ?? observed?.Path ?? NormalizedRelativePath.Parse(pathValue);
            entries.Add(new InventoryEntry(path, classification, target, prior, observed));
        }

        return new InstallationInventory(entries.ToArray());
    }

    private static Dictionary<string, T> ToUniqueDictionary<T>(IEnumerable<T> values, Func<T, NormalizedRelativePath> getPath, string parameterName)
    {
        Dictionary<string, T> result = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitive = new(StringComparer.OrdinalIgnoreCase);
        foreach (T value in values)
        {
            NormalizedRelativePath path = getPath(value);
            if (!caseInsensitive.Add(path.Value))
                throw new ArgumentException("Current inventory paths must be unique even on case-insensitive filesystems.", parameterName);
            result.Add(path.Value, value);
        }
        return result;
    }

    private static HashSet<string> ToUniquePathSet(IEnumerable<NormalizedRelativePath>? paths, string parameterName)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitive = new(StringComparer.OrdinalIgnoreCase);
        foreach (NormalizedRelativePath path in paths ?? Array.Empty<NormalizedRelativePath>())
        {
            if (!caseInsensitive.Add(path.Value))
                throw new ArgumentException("Inventory path sets must be unique even on case-insensitive filesystems.", parameterName);
            result.Add(path.Value);
        }
        return result;
    }
}
