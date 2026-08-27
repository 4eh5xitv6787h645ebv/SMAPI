namespace StardewModdingAPI.Framework.Health.Viewer.Content;

/// <summary>The semantic emphasis of one health-report display row.</summary>
internal enum ModHealthViewerRowSeverity
{
    Neutral,
    Positive,
    Info,
    Warning,
    Error
}

/// <summary>A renderer-independent icon key for one health-report display row.</summary>
internal enum ModHealthViewerRowIconKey
{
    Report,
    Privacy,
    Finding,
    Capture,
    Mark,
    Mod,
    Timing,
    Callback,
    Episode,
    Update,
    Log,
    Failure,
    Inventory,
    Environment,
    Capacity,
    Omission,
    Limitation
}

/// <summary>One concise, immutable row which the menu can render without consulting live state.</summary>
internal sealed record ModHealthViewerDisplayRow(
    string Title,
    string Detail,
    ModHealthViewerRowSeverity Severity,
    ModHealthViewerRowIconKey IconKey,
    string? StableId = null
);

/// <summary>One label/value line from the bounded details for a selected display row.</summary>
internal sealed record ModHealthViewerDetailRow(
    string Label,
    string Value,
    string? StableId = null
);
