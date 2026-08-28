using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>A SHA-256 digest in canonical lowercase hexadecimal form.</summary>
public readonly record struct Sha256Digest
{
    private static readonly Regex Pattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    /// <summary>The canonical lowercase hexadecimal digest.</summary>
    public string Value { get; }

    private Sha256Digest(string value)
    {
        this.Value = value;
    }

    /// <summary>Parse a canonical SHA-256 digest.</summary>
    public static Sha256Digest Parse(string value)
    {
        if (value == null || !Sha256Digest.Pattern.IsMatch(value))
            throw new ArgumentException("A SHA-256 digest must be 64 lowercase hexadecimal characters.", nameof(value));
        return new Sha256Digest(value);
    }

    /// <summary>Hash the given bytes.</summary>
    public static Sha256Digest Hash(ReadOnlySpan<byte> bytes)
    {
        return Sha256Digest.Parse(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Value;
    }
}
