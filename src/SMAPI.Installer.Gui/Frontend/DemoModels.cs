using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal sealed record FolderChoice(string Label, string Path, string Detail);

internal sealed record ReleaseChoice(string Label, string VersionLabel, string Detail);

internal sealed record OperationChoice(InstallerOperation Operation, string Label, string Summary, bool IsDestructive);

internal sealed record FrontendPreview(
    string Heading,
    string Summary,
    string StateLabel,
    ProtocolDurableState DurableState,
    IReadOnlyList<string> LogEntries
);
