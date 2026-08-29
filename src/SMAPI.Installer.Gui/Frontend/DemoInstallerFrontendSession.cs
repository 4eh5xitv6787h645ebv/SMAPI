using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Frontend;

/// <summary>A deterministic, synthetic session which performs no filesystem or network access.</summary>
internal sealed class DemoInstallerFrontendSession
{
    public DemoInstallerFrontendSession()
    {
        foreach (FolderChoice choice in this.Folders)
            DemoText.ValidateMany(choice.Label, choice.Path, choice.Detail);
        foreach (ReleaseChoice choice in this.Releases)
            DemoText.ValidateMany(choice.Label, choice.VersionLabel, choice.Detail);
        foreach (OperationChoice choice in this.Operations)
            DemoText.ValidateMany(choice.Label, choice.Summary);
    }

    public IReadOnlyList<FolderChoice> Folders { get; } = Array.AsReadOnly<FolderChoice>(
    [
        new("Steam example", "/home/demo/Games/Stardew Valley", "Synthetic Steam-style location; it is never inspected."),
        new("GOG example", "/home/demo/GOG Games/Stardew Valley", "Synthetic GOG-style location; it is never inspected.")
    ]);

    public IReadOnlyList<ReleaseChoice> Releases { get; } = Array.AsReadOnly<ReleaseChoice>(
    [
        new("Preview candidate (synthetic)", "linux-demo.1", "Display-only example with no download or release lookup."),
        new("Rollback example (synthetic)", "linux-demo.previous", "Display-only previous-version example.")
    ]);

    public IReadOnlyList<OperationChoice> Operations { get; } = Array.AsReadOnly<OperationChoice>(
    [
        new(InstallerOperation.Install, "Install", "Preview a first installation plan.", false),
        new(InstallerOperation.Update, "Update", "Preview an update while preserving recoverability.", false),
        new(InstallerOperation.Repair, "Repair", "Preview verification and repair of managed files.", false),
        new(InstallerOperation.Backup, "Backup", "Preview a recoverable backup operation.", false),
        new(InstallerOperation.Rollback, "Rollback", "Preview restoring a prior managed state.", true),
        new(InstallerOperation.Uninstall, "Uninstall", "Preview removal of installer-managed files only.", true)
    ]);

    public FrontendPreview CreatePreview(FolderChoice folder, ReleaseChoice release, OperationChoice operation)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(operation);
        if (!this.Folders.Contains(folder) || !this.Releases.Contains(release) || !this.Operations.Contains(operation))
            throw new ArgumentException("Demo previews accept only the fixed choices supplied by this exact session.");

        string caution = operation.IsDestructive
            ? "A real session would require an explicit, recoverable confirmation before this operation."
            : "A real session would inspect and confirm a verified plan before making changes.";

        FrontendPreview preview = new(
            $"Synthetic {operation.Label.ToLowerInvariant()} preview ready",
            $"{operation.Summary} {caution}",
            "Unchanged — backend disconnected",
            ProtocolDurableState.Unchanged,
            Array.AsReadOnly<string>(
            [
                $"DEMO  Selected folder: {folder.Label} ({folder.Path})",
                $"DEMO  Selected release: {release.Label} [{release.VersionLabel}]",
                $"DEMO  Prepared a synthetic {operation.Label.ToLowerInvariant()} preview.",
                "SAFE  No installer backend or app download ran; no game, Mods, save, or package was inspected or changed."
            ])
        );

        DemoText.ValidateMany(preview.Heading, preview.Summary, preview.StateLabel);
        DemoText.ValidateMany(preview.LogEntries);
        return preview;
    }
}

/// <summary>Fail-closed display limits for the fixed synthetic constants in this shell.</summary>
internal static class DemoText
{
    public const int MaxDisplayLength = 512;

    public static void ValidateMany(IEnumerable<string> values)
    {
        foreach (string value in values)
            Validate(value);
    }

    public static void ValidateMany(params string[] values)
    {
        ValidateMany(values.AsEnumerable());
    }

    public static void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > MaxDisplayLength)
            throw new ArgumentOutOfRangeException(nameof(value), $"Demo display text must contain 1–{MaxDisplayLength} characters.");

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format || char.IsSurrogate(character))
                throw new ArgumentException("Demo display text cannot contain control, bidirectional-format, or surrogate characters.", nameof(value));
        }
    }
}
