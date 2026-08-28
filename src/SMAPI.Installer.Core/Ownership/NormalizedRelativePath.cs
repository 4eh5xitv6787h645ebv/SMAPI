using System.Text;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>A canonical, platform-independent relative path used by installer manifests and receipts.</summary>
public sealed class NormalizedRelativePath : IComparable<NormalizedRelativePath>, IEquatable<NormalizedRelativePath>
{
    /// <summary>The maximum encoded path length accepted by the installer model.</summary>
    public const int MaxPathBytes = 4096;

    /// <summary>The maximum encoded segment length accepted by the installer model.</summary>
    public const int MaxSegmentBytes = 255;

    /// <summary>The canonical path, using <c>/</c> separators.</summary>
    public string Value { get; }

    private NormalizedRelativePath(string value)
    {
        this.Value = value;
    }

    /// <summary>Parse and strictly validate a canonical relative path.</summary>
    /// <exception cref="ArgumentException">The value isn't already in canonical relative form.</exception>
    public static NormalizedRelativePath Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("An owned path can't be empty.", nameof(value));
        if (value.Length > NormalizedRelativePath.MaxPathBytes || Encoding.UTF8.GetByteCount(value) > NormalizedRelativePath.MaxPathBytes)
            throw new ArgumentException("The owned path exceeds the configured length limit.", nameof(value));
        if (value[0] == '/' || value[^1] == '/' || value.Contains('\\'))
            throw new ArgumentException("An owned path must be relative and use canonical '/' separators.", nameof(value));
        if (!value.IsNormalized(NormalizationForm.FormC))
            throw new ArgumentException("An owned path must use canonical Unicode normalization form C.", nameof(value));

        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw new ArgumentException("An owned path can't contain empty, current, or parent segments.", nameof(value));
            if (segment[^1] is ' ' or '.')
                throw new ArgumentException("An owned path segment can't end in a space or period.", nameof(value));
            if (segment.Contains(':') || segment.Any(char.IsControl))
                throw new ArgumentException("An owned path contains a reserved or control character.", nameof(value));
            if (Encoding.UTF8.GetByteCount(segment) > NormalizedRelativePath.MaxSegmentBytes)
                throw new ArgumentException("An owned path segment exceeds the configured length limit.", nameof(value));
        }

        return new NormalizedRelativePath(value);
    }

    /// <inheritdoc />
    public int CompareTo(NormalizedRelativePath? other)
    {
        return other == null ? 1 : StringComparer.Ordinal.Compare(this.Value, other.Value);
    }

    /// <inheritdoc />
    public bool Equals(NormalizedRelativePath? other)
    {
        return other != null && StringComparer.Ordinal.Equals(this.Value, other.Value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is NormalizedRelativePath other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(this.Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Value;
    }
}
