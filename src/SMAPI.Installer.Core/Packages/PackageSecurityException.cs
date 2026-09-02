namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A stable, privacy-safe class of package acquisition or verification failure.</summary>
public enum PackageSecurityFailureKind
{
    /// <summary>The failure is intentionally not classified more narrowly.</summary>
    Unclassified,

    /// <summary>The remote release service or transfer was unavailable.</summary>
    NetworkUnavailable,

    /// <summary>A bounded remote operation exceeded its time limit.</summary>
    NetworkTimeout,

    /// <summary>A transfer ended without publishing one complete expected asset.</summary>
    IncompleteDownload,

    /// <summary>The package or one of its checksum authorities did not agree.</summary>
    IntegrityRejected,

    /// <summary>The release or install metadata did not satisfy its strict schema or binding.</summary>
    MetadataRejected,

    /// <summary>The package archive or its verified payload did not satisfy its strict contract.</summary>
    PackageArchiveRejected,

    /// <summary>The GitHub attestation evidence did not satisfy the reviewed provenance policy.</summary>
    ProvenanceRejected,

    /// <summary>The selected tag, repository, source, workflow, or release identity did not agree.</summary>
    ReleaseIdentityRejected
}

/// <summary>An exception raised when a release package or acquisition violates a safety invariant.</summary>
public sealed class PackageSecurityException : Exception
{
    /// <summary>The stable failure class. This never contains remote or private text.</summary>
    public PackageSecurityFailureKind FailureKind { get; }

    /// <summary>Construct an instance.</summary>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    public PackageSecurityException(string message)
        : this(PackageSecurityFailureKind.Unclassified, message)
    {
    }

    /// <summary>Construct an instance.</summary>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    /// <param name="innerException">The underlying exception.</param>
    public PackageSecurityException(string message, Exception innerException)
        : this(PackageSecurityFailureKind.Unclassified, message, innerException)
    {
    }

    /// <summary>Construct a classified instance.</summary>
    /// <param name="failureKind">The stable privacy-safe failure class.</param>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    public PackageSecurityException(PackageSecurityFailureKind failureKind, string message)
        : base(message)
    {
        if (!Enum.IsDefined(failureKind))
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        this.FailureKind = failureKind;
    }

    /// <summary>Construct a classified instance.</summary>
    /// <param name="failureKind">The stable privacy-safe failure class.</param>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    /// <param name="innerException">The underlying exception.</param>
    public PackageSecurityException(
        PackageSecurityFailureKind failureKind,
        string message,
        Exception innerException
    )
        : base(message, innerException)
    {
        if (!Enum.IsDefined(failureKind))
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        this.FailureKind = failureKind;
    }
}
