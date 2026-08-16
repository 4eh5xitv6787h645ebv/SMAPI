using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace StardewModdingAPI.Toolkit.Utilities;

/// <summary>Provides utilities for normalizing file paths.</summary>
public static class PathUtilities
{
    /*********
    ** Fields
    *********/
    /// <summary>The root prefix for a Windows UNC path.</summary>
    private const string WindowsUncRoot = @"\\";


    /*********
    ** Accessors
    *********/
    /// <summary>The possible directory separator characters in a file path.</summary>
    public static readonly char[] PossiblePathSeparators = new[] { '/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }.Distinct().ToArray();

    /// <summary>The preferred directory separator character in a file path.</summary>
    public static readonly char PreferredPathSeparator = Path.DirectorySeparatorChar;

    /// <summary>The preferred directory separator character in an asset key.</summary>
    public static readonly char PreferredAssetSeparator = '/';


    /*********
    ** Public methods
    *********/
    /// <summary>Get the segments from a path (e.g. <c>/usr/bin/example</c> => <c>usr</c>, <c>bin</c>, and <c>example</c>).</summary>
    /// <param name="path">The path to split.</param>
    /// <param name="limit">The number of segments to match. Any additional segments will be merged into the last returned part.</param>
    [Pure]
    public static string[] GetSegments(string? path, int? limit = null)
    {
        if (path == null)
            return [];

        return limit.HasValue
            ? path.Split(PathUtilities.PossiblePathSeparators, limit.Value, StringSplitOptions.RemoveEmptyEntries)
            : path.Split(PathUtilities.PossiblePathSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Normalize an asset name to match how MonoGame's content APIs would normalize and cache it.</summary>
    /// <param name="assetName">The asset name to normalize.</param>
    [Pure]
#if NET6_0_OR_GREATER
    [return: NotNullIfNotNull("assetName")]
#endif
    public static string? NormalizeAssetName(string? assetName)
    {
#if NET6_0_OR_GREATER
        if (assetName is null)
            return null;

        // trim outer whitespace without allocating
        int startIndex = 0;
        int endIndex = assetName.Length - 1;
        while (startIndex <= endIndex && char.IsWhiteSpace(assetName[startIndex]))
            startIndex++;
        while (endIndex >= startIndex && char.IsWhiteSpace(assetName[endIndex]))
            endIndex--;

        if (startIndex > endIndex)
            return string.Empty;

        // calculate the normalized length and detect the common already-canonical case
        int segmentCount = 0;
        int segmentCharacterCount = 0;
        bool isInSegment = false;
        bool hasNonPreferredSeparator = false;
        for (int i = startIndex; i <= endIndex; i++)
        {
            char character = assetName[i];
            if (PathUtilities.IsAssetSeparator(character))
            {
                hasNonPreferredSeparator |= character != PathUtilities.PreferredAssetSeparator;
                if (isInSegment)
                {
                    segmentCount++;
                    isInSegment = false;
                }
            }
            else
            {
                segmentCharacterCount++;
                isInSegment = true;
            }
        }
        if (isInSegment)
            segmentCount++;

        if (segmentCount == 0)
            return string.Empty;

        int normalizedLength = segmentCharacterCount + segmentCount - 1;
        int trimmedLength = endIndex - startIndex + 1;
        if (normalizedLength == trimmedLength && !hasNonPreferredSeparator)
        {
            return trimmedLength == assetName.Length
                ? assetName
                : assetName.Substring(startIndex, trimmedLength);
        }

        // write directly into the result string instead of allocating an array and string for every segment
        return string.Create(
            normalizedLength,
            (AssetName: assetName, StartIndex: startIndex, EndIndex: endIndex),
            static (output, state) =>
            {
                int outputIndex = 0;
                bool startOfSegment = true;

                for (int i = state.StartIndex; i <= state.EndIndex; i++)
                {
                    char character = state.AssetName[i];
                    if (PathUtilities.IsAssetSeparator(character))
                    {
                        startOfSegment = true;
                        continue;
                    }

                    if (startOfSegment && outputIndex > 0)
                        output[outputIndex++] = PathUtilities.PreferredAssetSeparator;

                    output[outputIndex++] = character;
                    startOfSegment = false;
                }
            }
        );
#else
        assetName = assetName?.Trim();
        if (string.IsNullOrEmpty(assetName))
            return assetName;

        return string.Join(PathUtilities.PreferredAssetSeparator.ToString(), PathUtilities.GetSegments(assetName)); // based on MonoGame's ContentManager.Load<T> logic
#endif
    }

    /// <summary>Normalize separators in a file path for the current platform.</summary>
    /// <param name="path">The file path to normalize.</param>
    /// <remarks>This should only be used for file paths. For asset names, use <see cref="NormalizeAssetName"/> instead.</remarks>
    [Pure]
#if NET6_0_OR_GREATER
    [return: NotNullIfNotNull("path")]
#endif
    public static string? NormalizePath(string? path)
    {
        path = path?.Trim();
        if (string.IsNullOrEmpty(path))
            return path;

        // get basic path format (e.g. /some/asset\\path/ => some\asset\path)
        string[] segments = PathUtilities.GetSegments(path);
        string newPath = string.Join(PathUtilities.PreferredPathSeparator.ToString(), segments);

        // keep root prefix
        bool hasRoot = false;
        if (path.StartsWith(PathUtilities.WindowsUncRoot))
        {
            newPath = PathUtilities.WindowsUncRoot + newPath;
            hasRoot = true;
        }
        else if (PathUtilities.PossiblePathSeparators.Contains(path[0]))
        {
            newPath = PathUtilities.PreferredPathSeparator + newPath;
            hasRoot = true;
        }

        // keep trailing separator
        if ((!hasRoot || segments.Length > 0) && PathUtilities.PossiblePathSeparators.Contains(path[path.Length - 1]))
            newPath += PathUtilities.PreferredPathSeparator;

        return newPath;
    }

    /// <summary>Get whether a character is a separator recognized in an asset name.</summary>
    private static bool IsAssetSeparator(char character)
    {
        return character is '/' or '\\';
    }

    /// <summary>Get a path with the home directory path replaced with <c>~</c> (like <c>C:\Users\Admin\Game</c> to <c>~\Game</c>), if applicable.</summary>
    /// <param name="path">The path to anonymize.</param>
    [Pure]
    public static string AnonymizePathForDisplay(string path)
    {
        string? homePath = PathUtilities.NormalizePath(Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE"));
        path = PathUtilities.NormalizePath(path);

        if (homePath != null)
        {
            if (path.Equals(homePath, StringComparison.OrdinalIgnoreCase))
                path = homePath;
            else if (path.StartsWith(homePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                path = "~" + path.Substring(homePath.Length);
        }

        return path;
    }

    /// <summary>Get a directory or file path relative to a given source path. If no relative path is possible (e.g. the paths are on different drives), an absolute path is returned.</summary>
    /// <param name="sourceDir">The source folder path.</param>
    /// <param name="targetPath">The target folder or file path.</param>
    [Pure]
    public static string GetRelativePath(string sourceDir, string targetPath)
    {
#if NET6_0_OR_GREATER
        return Path.GetRelativePath(sourceDir, targetPath);
#else
        // NOTE:
        // this is a heuristic implementation that works in the cases SMAPI needs it for, but it
        // doesn't handle all edge cases (e.g. case-sensitivity on Linux, or traversing between
        // UNC paths on Windows). SMAPI and mods will use the more robust .NET 5 version anyway
        // though, this is only for compatibility with the mod build package.

        // convert to URIs
        Uri from = new(sourceDir.TrimEnd(PathUtilities.PossiblePathSeparators) + "/");
        Uri to = new(targetPath.TrimEnd(PathUtilities.PossiblePathSeparators) + "/");
        if (from.Scheme != to.Scheme)
            throw new InvalidOperationException($"Can't get path for '{targetPath}' relative to '{sourceDir}'.");

        // get relative path
        string rawUrl = Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
        if (rawUrl.StartsWith("file://"))
            rawUrl = PathUtilities.WindowsUncRoot + rawUrl.Substring("file://".Length);
        string relative = PathUtilities.NormalizePath(rawUrl);

        // normalize
        if (relative == "")
            relative = ".";
        else
        {
            // trim trailing slash from URL
            if (relative.EndsWith(PathUtilities.PreferredPathSeparator.ToString()))
                relative = relative.Substring(0, relative.Length - 1);

            // fix root
            if (relative.StartsWith("file:") && !targetPath.Contains("file:"))
                relative = relative.Substring("file:".Length);
        }

        return relative;
#endif
    }

    /// <summary>Get whether a path is relative and doesn't try to climb out of its containing folder (e.g. doesn't contain <c>../</c>).</summary>
    /// <param name="path">The path to check.</param>
    [Pure]
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        return
            !Path.IsPathRooted(path)
            && PathUtilities.GetSegments(path).All(segment => segment.Trim() != "..");
    }

    /// <summary>Create a 'slug' containing only basic characters that are safe in all contexts like filenames, URLs, etc.</summary>
    /// <param name="input">The string to represent.</param>
    /// <remarks>The behavior of this method isn't guaranteed to remain unchanged. You should only use this method is cases where you can use it consistently and the values aren't stored across different versions of SMAPI.</remarks>
    [Pure]
#if NET6_0_OR_GREATER
    [return: NotNullIfNotNull("input")]
#endif
    public static string? CreateSlug(string? input)
    {
        //
        // This pattern is synced with IsSlug below.
        //

        return string.IsNullOrWhiteSpace(input)
            ? input
            : Regex.Replace(input, @"[^\p{L}\d_\.]+", "-").TrimStart('-');
    }

    /// <summary>Get whether a string is a valid 'slug', containing only basic characters that are safe in all contexts like filenames, URLs, etc.</summary>
    /// <param name="str">The string to check.</param>
    /// <remarks>The behavior of this method isn't guaranteed to remain unchanged. You should only use this method is cases where you can use it consistently and the values aren't stored across different versions of SMAPI.</remarks>
    [Pure]
    public static bool IsSlug(string? str)
    {
        //
        // This uses the same pattern as CreateSlug, with the addition of '-'.
        //

        return
            string.IsNullOrEmpty(str)
            || !Regex.IsMatch(str, @"[^\p{L}\d_\.\-]", RegexOptions.IgnoreCase);
    }
}
