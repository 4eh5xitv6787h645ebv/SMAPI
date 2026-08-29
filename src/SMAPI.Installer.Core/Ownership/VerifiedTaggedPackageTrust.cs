using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>An exact artifact subject observed while verifying a GitHub artifact attestation.</summary>
public sealed class VerifiedAttestedSubject : IEquatable<VerifiedAttestedSubject>
{
    private const int MaximumSubjectNameLength = 240;

    private static readonly Regex SafeSubjectNamePattern = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z",
        RegexOptions.CultureInvariant
    );

    /// <summary>The exact safe artifact filename in the attestation statement.</summary>
    public string Name { get; }

    /// <summary>The verified SHA-256 digest of the retained artifact bytes.</summary>
    public Sha256Digest Sha256 { get; }

    /// <summary>The positive bounded byte length observed from the retained artifact.</summary>
    public long ObservedSizeBytes { get; }

    internal VerifiedAttestedSubject(string name, Sha256Digest sha256, long observedSizeBytes)
    {
        if (
            name is null
            || name.Length > VerifiedAttestedSubject.MaximumSubjectNameLength
            || !VerifiedAttestedSubject.SafeSubjectNamePattern.IsMatch(name)
        )
        {
            throw new ArgumentException("The attested subject name must be a bounded safe filename.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(sha256);
        if (observedSizeBytes is <= 0 or > InstallationPackageIdentity.MaximumPackageSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedSizeBytes),
                "The observed artifact size must be positive and within the installer package limit."
            );
        }

        this.Name = name;
        this.Sha256 = sha256;
        this.ObservedSizeBytes = observedSizeBytes;
    }

    /// <inheritdoc />
    public bool Equals(VerifiedAttestedSubject? other)
    {
        return other is not null
            && this.Name == other.Name
            && this.Sha256 == other.Sha256
            && this.ObservedSizeBytes == other.ObservedSizeBytes;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is VerifiedAttestedSubject other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(this.Name), this.Sha256, this.ObservedSizeBytes);
    }
}

/// <summary>
/// Curated evidence retained after one two-subject artifact attestation passes cryptographic verification and exact workflow policy.
/// Raw bundles, certificates, signed URLs, and verifier output are intentionally excluded.
/// </summary>
public sealed class VerifiedGitHubWorkflowEvidence : IEquatable<VerifiedGitHubWorkflowEvidence>
{
    /// <summary>The pinned numeric GitHub repository ID for the reviewed fork.</summary>
    public const string ReviewedRepositoryIdentifier = "1336010508";

    /// <summary>The pinned numeric GitHub owner ID for the reviewed fork.</summary>
    public const string ReviewedRepositoryOwnerIdentifier = "45441845";

    /// <summary>The only accepted hosted-runner claim.</summary>
    public const string RequiredRunnerEnvironment = "github-hosted";

    /// <summary>The only accepted release workflow trigger.</summary>
    public const string RequiredTrigger = "push";

    private const int MaximumInvocationUriLength = 256;

    private static readonly Regex InvocationUriPattern = new(
        @"\Ahttps://github\.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/[1-9][0-9]*/attempts/[1-9][0-9]*\z",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex InvocationIdentityPattern = new(
        @"\Ahttps://github\.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/(?<run>[1-9][0-9]*)/attempts/(?<attempt>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant
    );

    /// <summary>The exact reviewed repository URL verified in the two-subject attestation.</summary>
    public string Repository { get; }

    /// <summary>The exact immutable tag reference verified in the two-subject attestation.</summary>
    public string SourceReference { get; }

    /// <summary>The exact source commit verified in the two-subject attestation.</summary>
    public string SourceCommit { get; }

    /// <summary>The exact tagged workflow reference verified in the two-subject attestation.</summary>
    public string BuildWorkflow { get; }

    /// <summary>The exact certificate subject alternative name verified in the two-subject attestation.</summary>
    public string SubjectAlternativeName { get; }

    /// <summary>The canonical GitHub Actions run and attempt URI verified in the two-subject attestation.</summary>
    public string RunInvocationUri { get; }

    /// <summary>The verified runner environment.</summary>
    public string RunnerEnvironment { get; }

    /// <summary>The verified workflow trigger.</summary>
    public string Trigger { get; }

    /// <summary>The pinned canonical decimal GitHub repository ID.</summary>
    public string RepositoryIdentifier { get; }

    /// <summary>The pinned canonical decimal GitHub repository-owner ID.</summary>
    public string RepositoryOwnerIdentifier { get; }

    /// <summary>The two-subject attestation's verified transparency-log timestamp, normalized to UTC.</summary>
    public DateTimeOffset TransparencyLogTimestampUtc { get; }

    internal VerifiedGitHubWorkflowEvidence(
        InstallationReleaseIdentity identity,
        string repository,
        string sourceReference,
        string sourceCommit,
        string buildWorkflow,
        string subjectAlternativeName,
        string runInvocationUri,
        string runnerEnvironment,
        string trigger,
        string repositoryIdentifier,
        string repositoryOwnerIdentifier,
        DateTimeOffset transparencyLogTimestampUtc
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        string expectedReference = $"refs/tags/{identity.Tag}";
        string expectedSan = $"https://github.com/{identity.BuildWorkflow}";

        VerifiedGitHubWorkflowEvidence.RequireExact(repository, identity.Repository, nameof(repository));
        VerifiedGitHubWorkflowEvidence.RequireExact(sourceReference, expectedReference, nameof(sourceReference));
        VerifiedGitHubWorkflowEvidence.RequireExact(sourceCommit, identity.SourceCommit, nameof(sourceCommit));
        VerifiedGitHubWorkflowEvidence.RequireExact(buildWorkflow, identity.BuildWorkflow, nameof(buildWorkflow));
        VerifiedGitHubWorkflowEvidence.RequireExact(subjectAlternativeName, expectedSan, nameof(subjectAlternativeName));
        VerifiedGitHubWorkflowEvidence.RequireInvocationUri(runInvocationUri, nameof(runInvocationUri));
        VerifiedGitHubWorkflowEvidence.RequireExact(
            runnerEnvironment,
            VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment,
            nameof(runnerEnvironment)
        );
        VerifiedGitHubWorkflowEvidence.RequireExact(trigger, VerifiedGitHubWorkflowEvidence.RequiredTrigger, nameof(trigger));
        VerifiedGitHubWorkflowEvidence.RequireExact(
            repositoryIdentifier,
            VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier,
            nameof(repositoryIdentifier)
        );
        VerifiedGitHubWorkflowEvidence.RequireExact(
            repositoryOwnerIdentifier,
            VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier,
            nameof(repositoryOwnerIdentifier)
        );
        VerifiedGitHubWorkflowEvidence.RequireUtc(transparencyLogTimestampUtc, nameof(transparencyLogTimestampUtc));

        this.Repository = repository;
        this.SourceReference = sourceReference;
        this.SourceCommit = sourceCommit;
        this.BuildWorkflow = buildWorkflow;
        this.SubjectAlternativeName = subjectAlternativeName;
        this.RunInvocationUri = runInvocationUri;
        this.RunnerEnvironment = runnerEnvironment;
        this.Trigger = trigger;
        this.RepositoryIdentifier = repositoryIdentifier;
        this.RepositoryOwnerIdentifier = repositoryOwnerIdentifier;
        this.TransparencyLogTimestampUtc = transparencyLogTimestampUtc;
    }

    /// <inheritdoc />
    public bool Equals(VerifiedGitHubWorkflowEvidence? other)
    {
        return other is not null
            && this.Repository == other.Repository
            && this.SourceReference == other.SourceReference
            && this.SourceCommit == other.SourceCommit
            && this.BuildWorkflow == other.BuildWorkflow
            && this.SubjectAlternativeName == other.SubjectAlternativeName
            && this.RunInvocationUri == other.RunInvocationUri
            && this.RunnerEnvironment == other.RunnerEnvironment
            && this.Trigger == other.Trigger
            && this.RepositoryIdentifier == other.RepositoryIdentifier
            && this.RepositoryOwnerIdentifier == other.RepositoryOwnerIdentifier
            && this.TransparencyLogTimestampUtc == other.TransparencyLogTimestampUtc;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is VerifiedGitHubWorkflowEvidence other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode result = new();
        result.Add(this.Repository, StringComparer.Ordinal);
        result.Add(this.SourceReference, StringComparer.Ordinal);
        result.Add(this.SourceCommit, StringComparer.Ordinal);
        result.Add(this.BuildWorkflow, StringComparer.Ordinal);
        result.Add(this.SubjectAlternativeName, StringComparer.Ordinal);
        result.Add(this.RunInvocationUri, StringComparer.Ordinal);
        result.Add(this.RunnerEnvironment, StringComparer.Ordinal);
        result.Add(this.Trigger, StringComparer.Ordinal);
        result.Add(this.RepositoryIdentifier, StringComparer.Ordinal);
        result.Add(this.RepositoryOwnerIdentifier, StringComparer.Ordinal);
        result.Add(this.TransparencyLogTimestampUtc);
        return result.ToHashCode();
    }

    private static void RequireExact(string actual, string expected, string parameterName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new ArgumentException("The verified workflow evidence doesn't match the exact reviewed release identity.", parameterName);
    }

    private static void RequireInvocationUri(string value, string parameterName)
    {
        if (
            value is null
            || value.Length > VerifiedGitHubWorkflowEvidence.MaximumInvocationUriLength
            || !VerifiedGitHubWorkflowEvidence.InvocationUriPattern.IsMatch(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("The run invocation URI isn't a bounded canonical reviewed GitHub Actions URI.", parameterName);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("The verified transparency-log timestamp must be normalized to UTC.", parameterName);
    }

    internal (ulong RunId, int RunAttempt) GetRunIdentity()
    {
        Match match = InvocationIdentityPattern.Match(this.RunInvocationUri);
        if (
            !match.Success
            || !ulong.TryParse(match.Groups["run"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong runId)
            || !int.TryParse(match.Groups["attempt"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int attempt)
            || runId == 0
            || attempt == 0
        )
        {
            throw new InvalidOperationException("Verified workflow evidence has no canonical bounded run identity.");
        }
        return (runId, attempt);
    }
}

/// <summary>
/// Immutable authority that one attestation with exactly the tagged package and install-manifest subjects passed verification.
/// The paired identity's <see cref="InstallationReleaseIdentity.SourceTree"/> remains release-quartet metadata and is not
/// represented as an attested claim by this type.
/// </summary>
public sealed class VerifiedTaggedPackageTrust : IEquatable<VerifiedTaggedPackageTrust>
{
    /// <summary>The exact tagged release identity paired with this verified evidence.</summary>
    public InstallationReleaseIdentity Identity { get; }

    /// <summary>The one verified package attestation subject.</summary>
    public VerifiedAttestedSubject PackageSubject { get; }

    /// <summary>The one verified canonical install-manifest attestation subject.</summary>
    public VerifiedAttestedSubject ManifestSubject { get; }

    /// <summary>The shared exact workflow evidence verified for both subjects.</summary>
    public VerifiedGitHubWorkflowEvidence Evidence { get; }

    internal VerifiedTaggedPackageTrust(
        InstallationReleaseIdentity identity,
        VerifiedAttestedSubject packageSubject,
        VerifiedAttestedSubject manifestSubject,
        Sha256Digest retainedManifestSha256,
        long retainedManifestSizeBytes,
        VerifiedGitHubWorkflowEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(packageSubject);
        ArgumentNullException.ThrowIfNull(manifestSubject);
        ArgumentNullException.ThrowIfNull(retainedManifestSha256);
        if (retainedManifestSizeBytes is <= 0 or > InstallationPackageIdentity.MaximumPackageSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(retainedManifestSizeBytes));
        ArgumentNullException.ThrowIfNull(evidence);

        VerifiedTaggedPackageTrust.RequireSubject(
            packageSubject,
            identity.PackageAssetName,
            identity.PackageSha256,
            identity.PackageSizeBytes,
            nameof(packageSubject)
        );
        string expectedManifestName = $"SMAPI-{identity.EmbeddedVersion}-linux-x64-install-manifest.json";
        VerifiedTaggedPackageTrust.RequireSubject(
            manifestSubject,
            expectedManifestName,
            retainedManifestSha256,
            retainedManifestSizeBytes,
            nameof(manifestSubject)
        );

        VerifiedTaggedPackageTrust.RequireEvidence(identity, evidence);
        _ = evidence.GetRunIdentity();

        this.Identity = identity;
        this.PackageSubject = packageSubject;
        this.ManifestSubject = manifestSubject;
        this.Evidence = evidence;
    }

    /// <inheritdoc />
    public bool Equals(VerifiedTaggedPackageTrust? other)
    {
        return other is not null
            && this.Identity.Equals(other.Identity)
            && this.PackageSubject.Equals(other.PackageSubject)
            && this.ManifestSubject.Equals(other.ManifestSubject)
            && this.Evidence.Equals(other.Evidence);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is VerifiedTaggedPackageTrust other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(this.Identity, this.PackageSubject, this.ManifestSubject, this.Evidence);
    }

    private static void RequireSubject(
        VerifiedAttestedSubject subject,
        string expectedName,
        Sha256Digest expectedSha256,
        long expectedSizeBytes,
        string parameterName
    )
    {
        if (
            !string.Equals(subject.Name, expectedName, StringComparison.Ordinal)
            || subject.Sha256 != expectedSha256
            || subject.ObservedSizeBytes != expectedSizeBytes
        )
        {
            throw new ArgumentException("The attested subject doesn't match the exact retained release artifact.", parameterName);
        }
    }

    private static void RequireEvidence(InstallationReleaseIdentity identity, VerifiedGitHubWorkflowEvidence evidence)
    {
        string expectedReference = $"refs/tags/{identity.Tag}";
        string expectedSan = $"https://github.com/{identity.BuildWorkflow}";
        if (
            !string.Equals(evidence.Repository, identity.Repository, StringComparison.Ordinal)
            || !string.Equals(evidence.SourceReference, expectedReference, StringComparison.Ordinal)
            || !string.Equals(evidence.SourceCommit, identity.SourceCommit, StringComparison.Ordinal)
            || !string.Equals(evidence.BuildWorkflow, identity.BuildWorkflow, StringComparison.Ordinal)
            || !string.Equals(evidence.SubjectAlternativeName, expectedSan, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("The workflow evidence is bound to a different tagged release identity.", nameof(evidence));
        }
    }
}
