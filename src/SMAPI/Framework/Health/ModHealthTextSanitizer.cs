using System;
using System.Text;
using System.Text.RegularExpressions;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Structurally sanitizes allowlisted mod-controlled identity fields before reporting them.</summary>
internal static class ModHealthTextSanitizer
{
    private static readonly Regex AnsiEscape = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixAbsolutePath = new(@"(?<![A-Za-z0-9_.-])/(?:[^\s/]+/)+[^\s]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsAbsolutePath = new(@"(?<![A-Za-z0-9_.-])[A-Za-z]:\\(?:[^\s\\]+\\)+[^\s]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Sanitize and cap an allowlisted identity value.</summary>
    public static string SanitizeIdentity(string? value, int maximumLength = ModHealthReportLimits.MaxIdentityLength)
    {
        if (maximumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        value = ModHealthTextSanitizer.AnsiEscape.Replace(value ?? "", "");
        value = ModHealthTextSanitizer.UnixAbsolutePath.Replace(value, "[path]");
        value = ModHealthTextSanitizer.WindowsAbsolutePath.Replace(value, "[path]");

        StringBuilder result = new(Math.Min(value.Length, maximumLength));
        bool previousWasSpace = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool isSpace = character is '\r' or '\n' or '\t' || char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (result.Length > 0 && !previousWasSpace)
                    result.Append(' ');
                previousWasSpace = true;
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                if (result.Length + 2 > maximumLength)
                    break;
                result.Append(character).Append(value[++index]);
                previousWasSpace = false;
                continue;
            }

            if (char.IsControl(character) || char.IsSurrogate(character))
                continue;

            result.Append(character);
            previousWasSpace = false;
            if (result.Length >= maximumLength)
                break;
        }

        return result.ToString().Trim();
    }
}
