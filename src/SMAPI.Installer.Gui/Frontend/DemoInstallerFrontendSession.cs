using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Frontend;

/// <summary>A deterministic, synthetic session which performs no filesystem or network access.</summary>
public sealed class DemoInstallerFrontendSession : IInstallerFrontendSession
{
    public bool IsDemoMode => true;

    public IReadOnlyList<FolderChoice> Folders { get; } =
    [
        new("Steam example", "/home/demo/Games/Stardew Valley", "Synthetic Steam-style location; it is never inspected."),
        new("GOG example", "/home/demo/GOG Games/Stardew Valley", "Synthetic GOG-style location; it is never inspected.")
    ];

    public IReadOnlyList<ReleaseChoice> Releases { get; } =
    [
        new("Preview candidate (synthetic)", "linux-demo.1", "Display-only example with no download or release lookup."),
        new("Rollback example (synthetic)", "linux-demo.previous", "Display-only previous-version example.")
    ];

    public IReadOnlyList<OperationChoice> Operations { get; } =
    [
        new(InstallerOperation.Install, "Install", "Preview a first installation plan.", false),
        new(InstallerOperation.Update, "Update", "Preview an update while preserving recoverability.", false),
        new(InstallerOperation.Repair, "Repair", "Preview verification and repair of managed files.", false),
        new(InstallerOperation.Backup, "Backup", "Preview a recoverable backup operation.", false),
        new(InstallerOperation.Rollback, "Rollback", "Preview restoring a prior managed state.", true),
        new(InstallerOperation.Uninstall, "Uninstall", "Preview removal of installer-managed files only.", true)
    ];

    public FrontendPreview CreatePreview(FolderChoice folder, ReleaseChoice release, OperationChoice operation)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(operation);

        string caution = operation.IsDestructive
            ? "A real session would require an explicit, recoverable confirmation before this operation."
            : "A real session would inspect and confirm a verified plan before making changes.";

        return new FrontendPreview(
            $"Synthetic {operation.Label.ToLowerInvariant()} preview ready",
            $"{operation.Summary} {caution}",
            "Unchanged — backend disconnected",
            ProtocolDurableState.Unchanged,
            [
                $"DEMO  Selected folder: {folder.Label} ({folder.Path})",
                $"DEMO  Selected release: {release.Label} [{release.VersionLabel}]",
                $"DEMO  Prepared a synthetic {operation.Label.ToLowerInvariant()} preview.",
                "SAFE  No backend action ran; no files, saves, Mods, or network resources were accessed."
            ]
        );
    }
}
