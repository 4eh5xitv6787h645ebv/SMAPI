using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>The closed origin categories for an exact Linux installer package.</summary>
public enum InstallationPackageOrigin
{
    /// <summary>A published fork release identified by its exact reviewed tag and build identity.</summary>
    TaggedRelease,

    /// <summary>A caller-selected local package with no repository, tag, source, workflow, or provenance claim.</summary>
    LocalManual
}

/// <summary>The common immutable identity of exact Linux installer package bytes.</summary>
public abstract class InstallationPackageIdentity : IEquatable<InstallationPackageIdentity>
{
    private const int MaximumEmbeddedVersionLength = 160;

    /// <summary>The largest package identity accepted by the Linux installer.</summary>
    public const long MaximumPackageSizeBytes = 512L * 1024 * 1024;

    private static readonly Regex EmbeddedVersionPattern = new(
        @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.(?:[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant
    );

    /// <summary>The truthful package origin.</summary>
    public InstallationPackageOrigin Origin { get; }

    /// <summary>The embedded assembly/package version observed for these exact package bytes.</summary>
    public string EmbeddedVersion { get; }

    /// <summary>The exact safe Linux installer filename.</summary>
    public string PackageAssetName { get; }

    /// <summary>The verified package digest.</summary>
    public Sha256Digest PackageSha256 { get; }

    /// <summary>The verified package byte length.</summary>
    public long PackageSizeBytes { get; }

    private protected InstallationPackageIdentity(
        InstallationPackageOrigin origin,
        string embeddedVersion,
        string packageAssetName,
        Sha256Digest packageSha256,
        long packageSizeBytes
    )
    {
        if (!Enum.IsDefined(typeof(InstallationPackageOrigin), origin))
            throw new ArgumentOutOfRangeException(nameof(origin));
        if (
            embeddedVersion is null
            || embeddedVersion.Length > InstallationPackageIdentity.MaximumEmbeddedVersionLength
            || !InstallationPackageIdentity.EmbeddedVersionPattern.IsMatch(embeddedVersion)
        )
        {
            throw new ArgumentException("The embedded version doesn't match the bounded canonical SMAPI fork package format.", nameof(embeddedVersion));
        }

        string expectedPackageName = $"SMAPI-{embeddedVersion}-linux-x64-installer.zip";
        if (!string.Equals(packageAssetName, expectedPackageName, StringComparison.Ordinal))
            throw new ArgumentException("The package name doesn't match the exact safe Linux installer filename for its embedded version.", nameof(packageAssetName));
        ArgumentNullException.ThrowIfNull(packageSha256);
        if (packageSizeBytes is <= 0 or > InstallationPackageIdentity.MaximumPackageSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(packageSizeBytes), "The verified package size must be positive and within the Linux installer package limit.");

        this.Origin = origin;
        this.EmbeddedVersion = embeddedVersion;
        this.PackageAssetName = packageAssetName;
        this.PackageSha256 = packageSha256;
        this.PackageSizeBytes = packageSizeBytes;
    }

    /// <inheritdoc />
    public bool Equals(InstallationPackageIdentity? other)
    {
        return other is not null
            && this.GetType() == other.GetType()
            && this.Origin == other.Origin
            && this.EmbeddedVersion == other.EmbeddedVersion
            && this.PackageAssetName == other.PackageAssetName
            && this.PackageSha256 == other.PackageSha256
            && this.PackageSizeBytes == other.PackageSizeBytes
            && this.EqualsOrigin(other);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is InstallationPackageIdentity other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode result = new();
        result.Add(this.GetType());
        result.Add(this.Origin);
        result.Add(this.EmbeddedVersion, StringComparer.Ordinal);
        result.Add(this.PackageAssetName, StringComparer.Ordinal);
        result.Add(this.PackageSha256);
        result.Add(this.PackageSizeBytes);
        this.AddOriginHash(ref result);
        return result.ToHashCode();
    }

    private protected abstract bool EqualsOrigin(InstallationPackageIdentity other);

    private protected abstract void AddOriginHash(ref HashCode hash);
}

/// <summary>An exact local/manual Linux installer package with no release or provenance claim.</summary>
public sealed class InstallationLocalPackageIdentity : InstallationPackageIdentity, IEquatable<InstallationLocalPackageIdentity>
{
    /// <summary>Construct an exact local package identity from independently observed bytes.</summary>
    internal InstallationLocalPackageIdentity(
        string embeddedVersion,
        string packageAssetName,
        Sha256Digest packageSha256,
        long packageSizeBytes
    )
        : base(
            InstallationPackageOrigin.LocalManual,
            embeddedVersion,
            packageAssetName,
            packageSha256,
            packageSizeBytes
        )
    {
    }

    /// <inheritdoc />
    public bool Equals(InstallationLocalPackageIdentity? other)
    {
        return base.Equals(other);
    }

    private protected override bool EqualsOrigin(InstallationPackageIdentity other)
    {
        return true;
    }

    private protected override void AddOriginHash(ref HashCode hash)
    {
    }
}
