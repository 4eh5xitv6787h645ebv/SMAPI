using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Frontend;

/// <summary>
/// The UI-facing boundary for an installer session. A production implementation will adapt
/// <see cref="LinuxInstallerProtocolService"/>; views and view models must not reproduce engine rules.
/// </summary>
public interface IInstallerFrontendSession
{
    bool IsDemoMode { get; }

    IReadOnlyList<FolderChoice> Folders { get; }

    IReadOnlyList<ReleaseChoice> Releases { get; }

    IReadOnlyList<OperationChoice> Operations { get; }

    FrontendPreview CreatePreview(FolderChoice folder, ReleaseChoice release, OperationChoice operation);
}

public sealed record FolderChoice(string Label, string Path, string Detail);

public sealed record ReleaseChoice(string Label, string VersionLabel, string Detail);

public sealed record OperationChoice(InstallerOperation Operation, string Label, string Summary, bool IsDestructive);

public sealed record FrontendPreview(
    string Heading,
    string Summary,
    string StateLabel,
    ProtocolDurableState DurableState,
    IReadOnlyList<string> LogEntries
);
