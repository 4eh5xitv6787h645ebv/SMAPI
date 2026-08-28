using System;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the locale-aware parsing core used by production asset names.</summary>
internal sealed class AssetNameParsingScenario : IPerformanceScenario
{
    private string[] Inputs = Array.Empty<string>();

    /// <inheritdoc />
    public string Id => "asset.name-parsing";

    /// <inheritdoc />
    public string Description => "Parses localized and unlocalized synthetic asset keys through the production parser.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Inputs =
        [
            "Maps/Farm",
            "Characters/Dialogue/Abigail.fr-FR",
            "Data/Objects.de-DE",
            "LooseSprites/Cursors.unknown"
        ];
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            ParsedAssetName<int> parsed = AssetNameParser.Parse(this.Inputs[index & 3], AssetNameParsingScenario.ParseLocale);
            digest = ScenarioDigest.Add(digest, parsed.BaseName);
            digest = ScenarioDigest.Add(digest, parsed.LocaleCode);
            digest = ScenarioDigest.Add(digest, (ulong)(parsed.LanguageCode ?? -1) + 1);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Inputs = Array.Empty<string>();
    }

    /// <summary>Map the synthetic locale set to stable integer codes.</summary>
    private static int? ParseLocale(string locale)
    {
        return locale switch
        {
            "fr-FR" => 1,
            "de-DE" => 2,
            _ => null
        };
    }
}
