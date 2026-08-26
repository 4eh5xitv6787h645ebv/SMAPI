namespace StardewModdingAPI.Framework.Health;

/// <summary>Stable schema-v1 labels shared by report analysis and presentation.</summary>
internal static class ModHealthReportLabels
{
    public const string Event = "event";
    public const string ContentLoad = "content-load";
    public const string ContentEdit = "content-edit";
    public const string Console = "console";
    public const string Entry = "entry";
    public const string GetApi = "get-api";
    public const string Other = "other";

    public static string GetOperation(ModHealthOperationKind operation) => operation switch
    {
        ModHealthOperationKind.Event => Event,
        ModHealthOperationKind.ContentLoad => ContentLoad,
        ModHealthOperationKind.ContentEdit => ContentEdit,
        ModHealthOperationKind.Console => Console,
        ModHealthOperationKind.Entry => Entry,
        ModHealthOperationKind.GetApi => GetApi,
        _ => Other
    };
}
