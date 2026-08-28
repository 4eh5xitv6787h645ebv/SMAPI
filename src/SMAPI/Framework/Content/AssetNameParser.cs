using System;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Parse an asset key and optional locale suffix without depending on game runtime types.</summary>
internal static class AssetNameParser
{
    /// <summary>Parse a raw asset name.</summary>
    public static ParsedAssetName<TLanguage> Parse<TLanguage>(string rawName, Func<string, TLanguage?> parseLocale)
        where TLanguage : struct
    {
        if (string.IsNullOrWhiteSpace(rawName))
            throw new ArgumentException("The asset name can't be null or empty.", nameof(rawName));

        int lastPeriodIndex = rawName.LastIndexOf('.');
        if (lastPeriodIndex > 0 && rawName.Length > lastPeriodIndex + 1)
        {
            string possibleLocaleCode = rawName[(lastPeriodIndex + 1)..];
            TLanguage? possibleLanguageCode = parseLocale(possibleLocaleCode);
            if (possibleLanguageCode is not null)
            {
                return new ParsedAssetName<TLanguage>(
                    BaseName: rawName[..lastPeriodIndex],
                    LocaleCode: possibleLocaleCode,
                    LanguageCode: possibleLanguageCode
                );
            }
        }

        return new ParsedAssetName<TLanguage>(rawName, LocaleCode: null, LanguageCode: null);
    }
}

/// <summary>The locale-independent parts parsed from one asset key.</summary>
internal readonly record struct ParsedAssetName<TLanguage>(string BaseName, string? LocaleCode, TLanguage? LanguageCode)
    where TLanguage : struct;
