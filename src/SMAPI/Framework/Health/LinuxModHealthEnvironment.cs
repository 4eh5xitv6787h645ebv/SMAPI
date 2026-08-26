using System;
using System.Collections.Generic;
using System.IO;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Reads only strict, non-identifying Linux release values for a health report.</summary>
internal static class LinuxModHealthEnvironment
{
    private const int MaximumOsReleaseBytes = 64 * 1024;

    private static readonly Dictionary<string, string> KnownDistributionIds = new(StringComparer.Ordinal)
    {
        ["alpine"] = "alpine",
        ["arch"] = "arch",
        ["debian"] = "debian",
        ["fedora"] = "fedora",
        ["linuxmint"] = "linux-mint",
        ["manjaro"] = "manjaro",
        ["opensuse-leap"] = "opensuse-leap",
        ["opensuse-tumbleweed"] = "opensuse-tumbleweed",
        ["pop"] = "pop-os",
        ["rhel"] = "rhel",
        ["rocky"] = "rocky",
        ["ubuntu"] = "ubuntu"
    };

    /// <summary>Read the allowlisted distribution ID and numeric version from <c>/etc/os-release</c>.</summary>
    public static string? ReadDistribution(string path = "/etc/os-release")
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > MaximumOsReleaseBytes)
                return null;
            string content = File.ReadAllText(path);
            return content.Length <= MaximumOsReleaseBytes ? ParseDistribution(content) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parse only <c>ID</c> and <c>VERSION_ID</c> from os-release content.</summary>
    internal static string? ParseDistribution(string content)
    {
        string? id = null;
        string? version = null;
        foreach (string rawLine in content.Split('\n'))
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();
            if (line.StartsWith("ID=", StringComparison.Ordinal))
                id = Unquote(line[3..]);
            else if (line.StartsWith("VERSION_ID=", StringComparison.Ordinal))
                version = Unquote(line[11..]);
        }

        return NormalizeDistribution(id, version);
    }

    /// <summary>Validate an already-normalized distribution ID/version pair.</summary>
    public static string? NormalizeDistribution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > 64)
            return null;
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => NormalizeDistribution(parts[0], null),
            2 => NormalizeDistribution(parts[0], parts[1]),
            _ => null
        };
    }

    /// <summary>Reduce a Linux runtime banner to its numeric kernel release.</summary>
    public static string? NormalizeKernel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.StartsWith("Linux ", StringComparison.OrdinalIgnoreCase))
            span = span[6..].TrimStart();
        if (span.IsEmpty || !IsAsciiDigit(span[0]))
            return null;
        return ExtractNumericVersion(span, requireEntireValue: false);
    }

    private static string? NormalizeDistribution(string? id, string? version)
    {
        if (id is null || !KnownDistributionIds.TryGetValue(id.ToLowerInvariant(), out string? normalizedId))
            return null;
        if (string.IsNullOrWhiteSpace(version))
            return normalizedId;
        string? normalizedVersion = ExtractNumericVersion(version.AsSpan().Trim(), requireEntireValue: true);
        return normalizedVersion is null ? normalizedId : $"{normalizedId} {normalizedVersion}";
    }

    private static string? ExtractNumericVersion(ReadOnlySpan<char> value, bool requireEntireValue)
    {
        int length = 0;
        int components = 0;
        bool hasDigit = false;
        while (length < value.Length && length < 32)
        {
            char character = value[length];
            if (IsAsciiDigit(character))
            {
                hasDigit = true;
                length++;
                continue;
            }
            if (character == '.' && hasDigit && components < 3 && length + 1 < value.Length && IsAsciiDigit(value[length + 1]))
            {
                components++;
                hasDigit = false;
                length++;
                continue;
            }
            break;
        }
        return length > 0 && hasDigit && (!requireEntireValue || length == value.Length)
            ? value[..length].ToString()
            : null;
    }

    private static string Unquote(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return value.ToString();
    }

    private static bool IsAsciiDigit(char value)
    {
        return value is >= '0' and <= '9';
    }
}
