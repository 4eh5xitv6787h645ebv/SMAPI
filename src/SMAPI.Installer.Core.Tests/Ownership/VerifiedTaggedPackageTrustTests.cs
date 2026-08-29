using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
internal sealed class VerifiedTaggedPackageTrustTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string EmbeddedVersion = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1";
    private const string PackageName = "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip";
    private const string ManifestName = "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-install-manifest.json";
    private const string SourceCommit = "1111111111111111111111111111111111111111";
    private const string SourceTree = "2222222222222222222222222222222222222222";
    private const string Workflow = "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string SourceReference = "refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string San = "https://github.com/4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string Invocation = "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353/attempts/1";
    private static readonly Sha256Digest PackageSha256 = Sha256Digest.Parse(new string('a', 64));
    private static readonly Sha256Digest ManifestSha256 = Sha256Digest.Parse(new string('b', 64));
    private static readonly DateTimeOffset TlogUtc = new(2026, 8, 28, 13, 51, 36, TimeSpan.Zero);

    [Test]
    public void ValidTrustPreservesOnlyClosedCuratedTwoSubjectEvidence()
    {
        VerifiedTaggedPackageTrust trust = CreateTrust();

        trust.Identity.Should().Be(CreateIdentity());
        trust.PackageSubject.Name.Should().Be(PackageName);
        trust.PackageSubject.Sha256.Should().Be(PackageSha256);
        trust.PackageSubject.ObservedSizeBytes.Should().Be(123456);
        trust.ManifestSubject.Name.Should().Be(ManifestName);
        trust.ManifestSubject.Sha256.Should().Be(ManifestSha256);
        trust.ManifestSubject.ObservedSizeBytes.Should().Be(6543);
        trust.Evidence.Repository.Should().Be(InstallationReleaseIdentity.ReviewedRepository);
        trust.Evidence.SourceReference.Should().Be(SourceReference);
        trust.Evidence.SourceCommit.Should().Be(SourceCommit);
        trust.Evidence.BuildWorkflow.Should().Be(Workflow);
        trust.Evidence.SubjectAlternativeName.Should().Be(San);
        trust.Evidence.RunInvocationUri.Should().Be(Invocation);
        trust.Evidence.RunnerEnvironment.Should().Be(VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment);
        trust.Evidence.Trigger.Should().Be(VerifiedGitHubWorkflowEvidence.RequiredTrigger);
        trust.Evidence.RepositoryIdentifier.Should().Be(VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier);
        trust.Evidence.RepositoryOwnerIdentifier.Should().Be(VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier);
        trust.Evidence.TransparencyLogTimestampUtc.Should().Be(TlogUtc);

        typeof(VerifiedTaggedPackageTrust).GetProperties().Should().HaveCount(4);
        typeof(VerifiedGitHubWorkflowEvidence).GetProperties().Select(property => property.Name).Should().NotContain(
            ["SourceTree", "Bundle", "Certificate", "SignedUrl", "RawOutput"]
        );
    }

    [Test]
    public void TrustAndEvidenceCanOnlyBeConstructedInsideCore()
    {
        Type[] closedTypes =
        [
            typeof(VerifiedTaggedPackageTrust),
            typeof(VerifiedGitHubWorkflowEvidence),
            typeof(VerifiedAttestedSubject)
        ];

        closedTypes.Should().OnlyContain(type => type.IsSealed);
        closedTypes.SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)).Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("../artifact.zip")]
    [TestCase("directory/artifact.zip")]
    [TestCase("artifact zip")]
    [TestCase("artifact.zip\n")]
    public void SubjectRejectsUnsafeName(string? name)
    {
        Action construct = () => new VerifiedAttestedSubject(name!, PackageSha256, 1);

        construct.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Test]
    public void SubjectRejectsOversizedNameDigestAndNonPositiveSize()
    {
        VerifiedAttestedSubject exactMaximum = new(
            "artifact.zip",
            PackageSha256,
            InstallationPackageIdentity.MaximumPackageSizeBytes
        );
        Action oversized = () => new VerifiedAttestedSubject($"a{new string('b', 240)}", PackageSha256, 1);
        Action missingDigest = () => new VerifiedAttestedSubject("artifact.zip", null!, 1);
        Action zero = () => new VerifiedAttestedSubject("artifact.zip", PackageSha256, 0);
        Action negative = () => new VerifiedAttestedSubject("artifact.zip", PackageSha256, -1);
        Action oversizedBytes = () => new VerifiedAttestedSubject(
            "artifact.zip",
            PackageSha256,
            InstallationPackageIdentity.MaximumPackageSizeBytes + 1
        );

        exactMaximum.ObservedSizeBytes.Should().Be(InstallationPackageIdentity.MaximumPackageSizeBytes);
        oversized.Should().Throw<ArgumentException>().WithParameterName("name");
        missingDigest.Should().Throw<ArgumentNullException>().WithParameterName("sha256");
        zero.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("observedSizeBytes");
        negative.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("observedSizeBytes");
        oversizedBytes.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("observedSizeBytes");
    }

    [Test]
    public void TrustRejectsPackageSubjectThatDoesNotMatchExactRetainedIdentity()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        VerifiedGitHubWorkflowEvidence evidence = CreateEvidence(identity);
        VerifiedAttestedSubject manifest = CreateManifestSubject();
        VerifiedAttestedSubject wrongName = new("other.zip", PackageSha256, identity.PackageSizeBytes);
        VerifiedAttestedSubject wrongDigest = new(PackageName, Sha256Digest.Parse(new string('c', 64)), identity.PackageSizeBytes);
        VerifiedAttestedSubject wrongSize = new(PackageName, PackageSha256, identity.PackageSizeBytes + 1);

        Action name = () => CreateTrust(identity, wrongName, manifest, evidence);
        Action digest = () => CreateTrust(identity, wrongDigest, manifest, evidence);
        Action size = () => CreateTrust(identity, wrongSize, manifest, evidence);

        name.Should().Throw<ArgumentException>().WithParameterName("packageSubject");
        digest.Should().Throw<ArgumentException>().WithParameterName("packageSubject");
        size.Should().Throw<ArgumentException>().WithParameterName("packageSubject");
    }

    [Test]
    public void TrustRejectsAnythingExceptExactManifestSubjectName()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        VerifiedAttestedSubject wrongManifest = new("install-manifest.json", ManifestSha256, 6543);
        Action construct = () => new VerifiedTaggedPackageTrust(
            identity,
            CreatePackageSubject(identity),
            wrongManifest,
            ManifestSha256,
            6543,
            CreateEvidence(identity)
        );

        construct.Should().Throw<ArgumentException>().WithParameterName("manifestSubject");
    }

    [Test]
    public void TrustRejectsSameNameManifestSubjectWithWrongDigestOrSize()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        VerifiedGitHubWorkflowEvidence evidence = CreateEvidence(identity);
        VerifiedAttestedSubject wrongDigest = new(ManifestName, Sha256Digest.Parse(new string('c', 64)), 6543);
        VerifiedAttestedSubject wrongSize = new(ManifestName, ManifestSha256, 6544);
        Action digest = () => new VerifiedTaggedPackageTrust(
            identity,
            CreatePackageSubject(identity),
            wrongDigest,
            ManifestSha256,
            6543,
            evidence
        );
        Action size = () => new VerifiedTaggedPackageTrust(
            identity,
            CreatePackageSubject(identity),
            wrongSize,
            ManifestSha256,
            6543,
            evidence
        );

        digest.Should().Throw<ArgumentException>().WithParameterName("manifestSubject");
        size.Should().Throw<ArgumentException>().WithParameterName("manifestSubject");
    }

    [Test]
    public void TrustRejectsCartesianEvidenceFromAnotherTaggedIdentity()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        InstallationReleaseIdentity other = CreateIdentity(
            tag: "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
            embeddedVersion: "4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
            packageName: "SMAPI-4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip",
            sourceCommit: new string('3', 40),
            sourceTree: new string('4', 40),
            workflow: "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
        );
        VerifiedAttestedSubject package = new(other.PackageAssetName, other.PackageSha256, other.PackageSizeBytes);
        VerifiedAttestedSubject manifest = new(
            $"SMAPI-{other.EmbeddedVersion}-linux-x64-install-manifest.json",
            ManifestSha256,
            6543
        );

        Action construct = () => new VerifiedTaggedPackageTrust(
            other,
            package,
            manifest,
            manifest.Sha256,
            manifest.ObservedSizeBytes,
            CreateEvidence(identity)
        );

        construct.Should().Throw<ArgumentException>().WithParameterName("evidence");
    }

    [Test]
    public void EvidenceRejectsEveryMismatchedReviewedIdentityField()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        Action[] invalid =
        [
            () => CreateEvidence(identity, repository: "https://github.com/Pathoschild/SMAPI"),
            () => CreateEvidence(identity, sourceReference: "refs/heads/develop"),
            () => CreateEvidence(identity, sourceCommit: new string('2', 40)),
            () => CreateEvidence(identity, buildWorkflow: Workflow.Replace("refs/tags/", "refs/heads/", StringComparison.Ordinal)),
            () => CreateEvidence(identity, subjectAlternativeName: San.Replace("refs/tags/", "refs/heads/", StringComparison.Ordinal)),
            () => CreateEvidence(identity, runnerEnvironment: "self-hosted"),
            () => CreateEvidence(identity, trigger: "workflow_dispatch"),
            () => CreateEvidence(identity, repositoryIdentifier: "01336010508"),
            () => CreateEvidence(identity, repositoryOwnerIdentifier: "045441845")
        ];

        foreach (Action construct in invalid)
            construct.Should().Throw<ArgumentException>();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/0/attempts/1")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/01/attempts/1")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/1/attempts/0")]
    [TestCase("https://github.com/other/SMAPI/actions/runs/1/attempts/1")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/1/attempts/1?x=1")]
    public void EvidenceRejectsNonCanonicalInvocationUri(string? invocation)
    {
        Action construct = () => CreateEvidence(CreateIdentity(), runInvocationUri: invocation!);

        construct.Should().Throw<ArgumentException>().WithParameterName("runInvocationUri");
    }

    [Test]
    public void EvidenceRejectsOversizedInvocationAndNonUtcTimestamps()
    {
        string oversized = $"https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/{new string('1', 220)}/attempts/1";
        DateTimeOffset nonUtc = new(2026, 8, 28, 21, 51, 36, TimeSpan.FromHours(8));
        Action uri = () => CreateEvidence(CreateIdentity(), runInvocationUri: oversized);
        Action timestamp = () => CreateEvidence(CreateIdentity(), tlogUtc: nonUtc);

        uri.Should().Throw<ArgumentException>().WithParameterName("runInvocationUri");
        timestamp.Should().Throw<ArgumentException>().WithParameterName("transparencyLogTimestampUtc");
    }

    [Test]
    public void StructuralEqualityAndHashingCoverSubjectsIdentityAndEvidence()
    {
        VerifiedTaggedPackageTrust first = CreateTrust();
        VerifiedTaggedPackageTrust equal = CreateTrust();
        VerifiedTaggedPackageTrust otherManifest = CreateTrust(manifestSha256: Sha256Digest.Parse(new string('c', 64)));
        VerifiedTaggedPackageTrust otherInvocation = CreateTrust(
            invocation: "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353/attempts/2"
        );
        VerifiedTaggedPackageTrust otherTimestamp = CreateTrust(tlogUtc: TlogUtc.AddSeconds(1));

        first.Should().Be(equal);
        first.GetHashCode().Should().Be(equal.GetHashCode());
        first.Should().NotBe(otherManifest);
        first.Should().NotBe(otherInvocation);
        first.Should().NotBe(otherTimestamp);
        new HashSet<VerifiedTaggedPackageTrust> { first, equal, otherManifest, otherInvocation, otherTimestamp }.Should().HaveCount(4);
    }

    private static VerifiedTaggedPackageTrust CreateTrust(
        Sha256Digest? manifestSha256 = null,
        string invocation = Invocation,
        DateTimeOffset? tlogUtc = null
    )
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        return new VerifiedTaggedPackageTrust(
            identity,
            CreatePackageSubject(identity),
            CreateManifestSubject(manifestSha256),
            manifestSha256 ?? ManifestSha256,
            6543,
            CreateEvidence(identity, runInvocationUri: invocation, tlogUtc: tlogUtc)
        );
    }

    private static VerifiedTaggedPackageTrust CreateTrust(
        InstallationReleaseIdentity identity,
        VerifiedAttestedSubject package,
        VerifiedAttestedSubject manifest,
        VerifiedGitHubWorkflowEvidence evidence
    )
    {
        return new VerifiedTaggedPackageTrust(
            identity,
            package,
            manifest,
            ManifestSha256,
            6543,
            evidence
        );
    }

    private static VerifiedAttestedSubject CreatePackageSubject(InstallationReleaseIdentity identity)
    {
        return new VerifiedAttestedSubject(identity.PackageAssetName, identity.PackageSha256, identity.PackageSizeBytes);
    }

    private static VerifiedAttestedSubject CreateManifestSubject(Sha256Digest? sha256 = null)
    {
        return new VerifiedAttestedSubject(ManifestName, sha256 ?? ManifestSha256, 6543);
    }

    private static VerifiedGitHubWorkflowEvidence CreateEvidence(
        InstallationReleaseIdentity identity,
        string repository = InstallationReleaseIdentity.ReviewedRepository,
        string sourceReference = SourceReference,
        string sourceCommit = SourceCommit,
        string buildWorkflow = Workflow,
        string subjectAlternativeName = San,
        string runInvocationUri = Invocation,
        string runnerEnvironment = VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment,
        string trigger = VerifiedGitHubWorkflowEvidence.RequiredTrigger,
        string repositoryIdentifier = VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier,
        string repositoryOwnerIdentifier = VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier,
        DateTimeOffset? tlogUtc = null
    )
    {
        return new VerifiedGitHubWorkflowEvidence(
            identity,
            repository,
            sourceReference,
            sourceCommit,
            buildWorkflow,
            subjectAlternativeName,
            runInvocationUri,
            runnerEnvironment,
            trigger,
            repositoryIdentifier,
            repositoryOwnerIdentifier,
            tlogUtc ?? TlogUtc
        );
    }

    private static InstallationReleaseIdentity CreateIdentity(
        string tag = Tag,
        string embeddedVersion = EmbeddedVersion,
        string packageName = PackageName,
        string sourceCommit = SourceCommit,
        string sourceTree = SourceTree,
        string workflow = Workflow
    )
    {
        return new InstallationReleaseIdentity(
            InstallationReleaseIdentity.ReviewedRepository,
            tag,
            embeddedVersion,
            packageName,
            sourceCommit,
            sourceTree,
            PackageSha256,
            123456,
            workflow,
            "Release",
            "linux-x64"
        );
    }
}
