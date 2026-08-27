using System.Collections.Immutable;

namespace StardewModdingAPI.Framework.Health.Presentation;

/// <summary>Canonical privacy and attribution wording shared by the in-game report presentation.</summary>
internal static class ModHealthPresentationText
{
    public const string SmapiUpdateDispatchLabel = "SMAPI update dispatch observed outside the base-game update";
    public const string TimingAttributionCaveat = "Timing is elapsed wall-clock correlation, not total SMAPI CPU or proof of cause.";
    public const string SmapiUpdateDispatchCaveat = "The measured SMAPI update-dispatch boundary can include waiting, scheduling, and unobserved nested work.";
    public const string BaseGameCaveat = "Base-game-exclusive time can include Harmony patches and direct mod API work invoked by the game.";
    public const string UnavailableSmapiTimingCaveat = "When SMAPI update-dispatch timing is unavailable, its unseparated time is folded into residual.";
    public const string GcCaveat = "GC collection counts are process-wide correlation, not mod attribution.";
    public const string UpdateTickCaveat = "Completed update ticks are not a complete FPS or presentation-rate measurement.";
    public const string DrawCaveat = "This report does not provide a complete draw, GPU, driver, or presentation measurement.";
    public const string InspectBeforeSharingNotice = "This report contains installed mod names, IDs, versions, and statuses. Inspect it before sharing.";
    public const string NoUploadNotice = "No upload occurred; this report was not uploaded automatically.";
    public const string NormalLogNotice = "The normal SMAPI log is still needed for detailed exceptions.";
    public const string StandaloneParserNotice = "smapi.io/log does not parse standalone Mod Health Reports.";

    public static ImmutableArray<string> PrivacyNotices { get; } = ImmutableArray.Create(
        InspectBeforeSharingNotice,
        NoUploadNotice,
        NormalLogNotice,
        StandaloneParserNotice
    );

    public static ImmutableArray<string> TimingCaveats { get; } = ImmutableArray.Create(
        TimingAttributionCaveat,
        SmapiUpdateDispatchCaveat,
        BaseGameCaveat,
        UnavailableSmapiTimingCaveat,
        GcCaveat,
        UpdateTickCaveat,
        DrawCaveat
    );
}
