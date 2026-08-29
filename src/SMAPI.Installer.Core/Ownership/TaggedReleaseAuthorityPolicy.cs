namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>
/// The deterministic, URL-free tagged-release attestation policy embedded before the release manifest is signed.
/// Observed run evidence and the manifest subject digest are intentionally excluded to avoid a circular manifest.
/// </summary>
public sealed class TaggedReleaseAuthorityPolicy : IEquatable<TaggedReleaseAuthorityPolicy>
{
    public const string GitHubArtifactAttestationV1 = "github_artifact_attestation_v1";
    public const string ReviewedRepository = "4eh5xitv6787h645ebv/SMAPI";

    public string Kind { get; }
    public string Repository { get; }
    public string SourceReference { get; }
    public string SourceCommit { get; }
    public string BuildWorkflow { get; }
    public string RunnerEnvironment { get; }
    public string Trigger { get; }
    public string RepositoryIdentifier { get; }
    public string RepositoryOwnerIdentifier { get; }
    public string PackageSubjectName { get; }
    public string ManifestSubjectName { get; }

    private TaggedReleaseAuthorityPolicy(InstallationReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.Kind = GitHubArtifactAttestationV1;
        this.Repository = ReviewedRepository;
        this.SourceReference = $"refs/tags/{identity.Tag}";
        this.SourceCommit = identity.SourceCommit;
        this.BuildWorkflow = identity.BuildWorkflow;
        this.RunnerEnvironment = VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment;
        this.Trigger = VerifiedGitHubWorkflowEvidence.RequiredTrigger;
        this.RepositoryIdentifier = VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier;
        this.RepositoryOwnerIdentifier = VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier;
        this.PackageSubjectName = identity.PackageAssetName;
        this.ManifestSubjectName = $"SMAPI-{identity.EmbeddedVersion}-linux-x64-install-manifest.json";
    }

    internal static TaggedReleaseAuthorityPolicy Create(InstallationReleaseIdentity identity) => new(identity);

    internal bool Matches(VerifiedTaggedPackageTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        TaggedReleaseAuthorityPolicy expected = Create(trust.Identity);
        return this.Equals(expected)
            && trust.PackageSubject.Name == this.PackageSubjectName
            && trust.ManifestSubject.Name == this.ManifestSubjectName
            && trust.Evidence.SourceReference == this.SourceReference
            && trust.Evidence.SourceCommit == this.SourceCommit
            && trust.Evidence.BuildWorkflow == this.BuildWorkflow
            && trust.Evidence.RunnerEnvironment == this.RunnerEnvironment
            && trust.Evidence.Trigger == this.Trigger
            && trust.Evidence.RepositoryIdentifier == this.RepositoryIdentifier
            && trust.Evidence.RepositoryOwnerIdentifier == this.RepositoryOwnerIdentifier;
    }

    public bool Equals(TaggedReleaseAuthorityPolicy? other)
    {
        return other is not null
            && this.Kind == other.Kind
            && this.Repository == other.Repository
            && this.SourceReference == other.SourceReference
            && this.SourceCommit == other.SourceCommit
            && this.BuildWorkflow == other.BuildWorkflow
            && this.RunnerEnvironment == other.RunnerEnvironment
            && this.Trigger == other.Trigger
            && this.RepositoryIdentifier == other.RepositoryIdentifier
            && this.RepositoryOwnerIdentifier == other.RepositoryOwnerIdentifier
            && this.PackageSubjectName == other.PackageSubjectName
            && this.ManifestSubjectName == other.ManifestSubjectName;
    }

    public override bool Equals(object? obj) => obj is TaggedReleaseAuthorityPolicy other && this.Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Kind, StringComparer.Ordinal);
        hash.Add(this.Repository, StringComparer.Ordinal);
        hash.Add(this.SourceReference, StringComparer.Ordinal);
        hash.Add(this.SourceCommit, StringComparer.Ordinal);
        hash.Add(this.BuildWorkflow, StringComparer.Ordinal);
        hash.Add(this.RunnerEnvironment, StringComparer.Ordinal);
        hash.Add(this.Trigger, StringComparer.Ordinal);
        hash.Add(this.RepositoryIdentifier, StringComparer.Ordinal);
        hash.Add(this.RepositoryOwnerIdentifier, StringComparer.Ordinal);
        hash.Add(this.PackageSubjectName, StringComparer.Ordinal);
        hash.Add(this.ManifestSubjectName, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
