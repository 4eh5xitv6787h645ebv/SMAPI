using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReviewedGitHubTagResolverTests
{
    private const string TagObject = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public void ParseReference_ExactCatalogCandidate_ReturnsOpaqueBoundObservationAndExactNextUri()
    {
        ReviewedReleaseCandidate candidate = Candidate();

        ReviewedGitHubTagReference result = ReviewedGitHubTagResolver.ParseReference(
            ReferenceDocument(candidate.Identity, TagObject, "tag"),
            candidate
        );

        result.ReleaseTag.Should().Be(candidate.Identity.Tag);
        result.AnnotatedTagDocumentUri.Should().Be(ReviewedGitHubReleaseUris.GetTagObjectUri(TagObject));
    }

    [Test]
    public void ResolveAfterRefresh_ExactInitialAnnotatedAndFreshDocuments_MintsFinalCommitAuthority()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);

        ReviewedGitHubResolvedTag result = ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            initial,
            AnnotatedTagDocument(candidate.Identity, TagObject, Commit),
            ReferenceDocument(candidate.Identity, TagObject, "tag")
        );

        result.Release.Should().BeSameAs(candidate);
        result.ReleaseTag.Should().Be(candidate.Identity.Tag);
        result.SourceCommit.Should().Be(Commit);
    }

    [Test]
    public void PublicApi_AuthoritiesAreOpaqueImmutableAndFinalizationIsOneSequencedOperation()
    {
        Type reference = typeof(ReviewedGitHubTagReference);
        Type resolved = typeof(ReviewedGitHubResolvedTag);
        typeof(ReviewedReleaseCandidate).GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should().BeEmpty("raw identities must not be constructible as catalog authorities");
        reference.IsSealed.Should().BeTrue();
        resolved.IsSealed.Should().BeTrue();
        reference.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Should().BeEmpty();
        resolved.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Should().BeEmpty();
        reference.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Should().OnlyContain(property => !property.CanWrite && property.SetMethod == null);
        resolved.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Should().OnlyContain(property => !property.CanWrite && property.SetMethod == null);
        reference.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Should().NotContain(method => method.Name == "Deconstruct" || method.Name == "<Clone>$");
        resolved.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Should().NotContain(method => method.Name == "Deconstruct" || method.Name == "<Clone>$");

        MethodInfo[] methods = typeof(ReviewedGitHubTagResolver).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly
        );
        methods.Select(method => method.Name).Should().Equal("ParseReference", "ResolveAfterRefresh");
        methods.Should().NotContain(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ForkReleaseIdentity)));
        methods.Single(method => method.Name == "ParseReference").GetParameters()
            .Should().Contain(parameter => parameter.ParameterType == typeof(ReviewedReleaseCandidate));
        MethodInfo finalize = methods.Single(method => method.Name == "ResolveAfterRefresh");
        finalize.ReturnType.Should().Be(typeof(ReviewedGitHubResolvedTag));
        finalize.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            typeof(ReviewedReleaseCandidate),
            typeof(ReviewedGitHubTagReference),
            typeof(ReadOnlyMemory<byte>),
            typeof(ReadOnlyMemory<byte>)
        );
        resolved.GetProperty(nameof(ReviewedGitHubResolvedTag.SourceCommit)).Should().NotBeNull();
        reference.GetProperty("SourceCommit").Should().BeNull();
        reference.GetProperty(nameof(ReviewedGitHubTagReference.AnnotatedTagDocumentUri))!.PropertyType
            .Should().Be(typeof(Uri));
    }

    [Test]
    public void ResolveAfterRefresh_InitialAuthorityFromEqualButDifferentCandidateInstance_Rejects()
    {
        ReviewedReleaseCandidate listed = Candidate();
        ReviewedReleaseCandidate separatelyParsed = Candidate();
        listed.Should().NotBeSameAs(separatelyParsed);
        ReviewedGitHubTagReference initial = Initial(listed);

        Action action = () => ReviewedGitHubTagResolver.ResolveAfterRefresh(
            separatelyParsed,
            initial,
            AnnotatedTagDocument(separatelyParsed.Identity, TagObject, Commit),
            ReferenceDocument(separatelyParsed.Identity, TagObject, "tag")
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>()
            .WithMessage("*different catalog release selection*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public void ResolveAfterRefresh_MovedFreshReference_RejectsBeforeExposingCommit()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);

        Action action = () => ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            initial,
            AnnotatedTagDocument(candidate.Identity, TagObject, Commit),
            ReferenceDocument(candidate.Identity, new string('c', 40), "tag")
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>()
            .WithMessage("*tag moved*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public void ResolveAfterRefresh_LightweightFreshReference_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        Uri commitUri = new($"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{Commit}");

        Action action = () => ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            initial,
            AnnotatedTagDocument(candidate.Identity, TagObject, Commit),
            ReferenceDocument(candidate.Identity, Commit, "commit", commitUri)
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>()
            .WithMessage("*lightweight or unsupported Git tag*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public void ParseReference_LightweightInitialTag_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        Uri commitUri = new($"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{Commit}");

        Action action = () => ReviewedGitHubTagResolver.ParseReference(
            ReferenceDocument(candidate.Identity, Commit, "commit", commitUri),
            candidate
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>()
            .WithMessage("*lightweight or unsupported Git tag*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [TestCase("refs/tags/other")]
    [TestCase("refs/heads/develop")]
    public void ParseReference_DifferentReference_Rejects(string reference)
    {
        ReviewedReleaseCandidate candidate = Candidate();
        Dictionary<string, object?> document = Reference(candidate.Identity, TagObject, "tag");
        document["ref"] = reference;

        Action action = () => ReviewedGitHubTagResolver.ParseReference(
            JsonSerializer.SerializeToUtf8Bytes(document), candidate
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>()
            .WithMessage("*different release tag*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public void ParseReference_OffRepositoryMismatchedOrNoncanonicalObject_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        Dictionary<string, object?> offRepository = Reference(candidate.Identity, TagObject, "tag");
        offRepository["url"] =
            $"https://api.github.com/repos/Pathoschild/SMAPI/git/refs/tags/{candidate.Identity.Tag}";
        Dictionary<string, object?> wrongObject = Reference(candidate.Identity, TagObject, "tag");
        ((Dictionary<string, object?>)wrongObject["object"]!)["url"] =
            ReviewedGitHubReleaseUris.GetTagObjectUri(new string('c', 40)).AbsoluteUri;
        Dictionary<string, object?> uppercaseObject = Reference(candidate.Identity, TagObject, "tag");
        ((Dictionary<string, object?>)uppercaseObject["object"]!)["sha"] = TagObject.ToUpperInvariant();

        Action first = () => ReviewedGitHubTagResolver.ParseReference(
            JsonSerializer.SerializeToUtf8Bytes(offRepository), candidate
        );
        Action second = () => ReviewedGitHubTagResolver.ParseReference(
            JsonSerializer.SerializeToUtf8Bytes(wrongObject), candidate
        );
        Action third = () => ReviewedGitHubTagResolver.ParseReference(
            JsonSerializer.SerializeToUtf8Bytes(uppercaseObject), candidate
        );

        first.Should().Throw<PackageSecurityException>();
        second.Should().Throw<PackageSecurityException>();
        third.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ResolveAfterRefresh_AnnotatedTagObjectOrReleaseTagMismatch_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        Dictionary<string, object?> movedObject = AnnotatedTag(candidate.Identity, new string('c', 40), Commit);
        Dictionary<string, object?> differentTag = AnnotatedTag(candidate.Identity, TagObject, Commit);
        differentTag["tag"] = "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.3";

        Action first = () => Finalize(candidate, initial, movedObject);
        Action second = () => Finalize(candidate, initial, differentTag);

        PackageSecurityException firstException = first.Should().Throw<PackageSecurityException>()
            .WithMessage("*doesn't match the selected tag object*")
            .Which;
        PackageSecurityException secondException = second.Should().Throw<PackageSecurityException>()
            .WithMessage("*different release tag*")
            .Which;
        firstException.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
        secondException.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [TestCase("blob")]
    [TestCase("tree")]
    [TestCase("tag")]
    public void ResolveAfterRefresh_NonCommitAnnotatedTarget_Rejects(string type)
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        Dictionary<string, object?> document = AnnotatedTag(candidate.Identity, TagObject, Commit);
        ((Dictionary<string, object?>)document["object"]!)["type"] = type;

        Action action = () => Finalize(candidate, initial, document);

        action.Should().Throw<PackageSecurityException>().WithMessage("*doesn't target a Git commit*");
    }

    [TestCase("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [TestCase("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [TestCase("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [TestCase("gggggggggggggggggggggggggggggggggggggggg")]
    public void ResolveAfterRefresh_InvalidAnnotatedCommit_Rejects(string commit)
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        Dictionary<string, object?> document = AnnotatedTag(candidate.Identity, TagObject, Commit);
        ((Dictionary<string, object?>)document["object"]!)["sha"] = commit;

        Action action = () => Finalize(candidate, initial, document);

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ResolveAfterRefresh_OffRepositoryOrMismatchedCommitUrl_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        Dictionary<string, object?> offRepository = AnnotatedTag(candidate.Identity, TagObject, Commit);
        ((Dictionary<string, object?>)offRepository["object"]!)["url"] =
            $"https://api.github.com/repos/Pathoschild/SMAPI/git/commits/{Commit}";
        Dictionary<string, object?> wrongCommit = AnnotatedTag(candidate.Identity, TagObject, Commit);
        ((Dictionary<string, object?>)wrongCommit["object"]!)["url"] =
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{new string('c', 40)}";

        Action first = () => Finalize(candidate, initial, offRepository);
        Action second = () => Finalize(candidate, initial, wrongCommit);

        first.Should().Throw<PackageSecurityException>();
        second.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void Parsers_DuplicateMalformedOversizedAndDeepJson_Rejects()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        string duplicate = $"{{\"ref\":\"refs/tags/{candidate.Identity.Tag}\",\"ref\":\"refs/tags/{candidate.Identity.Tag}\",\"url\":\"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/refs/tags/{candidate.Identity.Tag}\",\"object\":{{\"type\":\"tag\",\"sha\":\"{TagObject}\",\"url\":\"{ReviewedGitHubReleaseUris.GetTagObjectUri(TagObject)}\"}}}}";
        Action duplicateAction = () => ReviewedGitHubTagResolver.ParseReference(
            Encoding.UTF8.GetBytes(duplicate), candidate
        );
        Action malformed = () => ReviewedGitHubTagResolver.ParseReference(
            Encoding.UTF8.GetBytes("[]"), candidate
        );
        Action oversized = () => ReviewedGitHubTagResolver.ParseReference(
            new byte[ReviewedGitHubReleaseUris.MaximumTagDocumentBytes + 1], candidate
        );
        string deep = new string('[', 18) + "0" + new string(']', 18);
        Action excessiveDepth = () => ReviewedGitHubTagResolver.ParseReference(
            Encoding.UTF8.GetBytes(deep), candidate
        );

        duplicateAction.Should().Throw<PackageSecurityException>().WithMessage("*duplicate JSON properties*");
        malformed.Should().Throw<PackageSecurityException>();
        oversized.Should().Throw<PackageSecurityException>();
        excessiveDepth.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ResolveAfterRefresh_BuildMetadataShapeCannotMintCommitAuthority()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ReviewedGitHubTagReference initial = Initial(candidate);
        byte[] buildMetadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1,
            source = new
            {
                repository = ForkReleaseIdentity.RepositoryUrl,
                commit = Commit,
                tree = new string('c', 40)
            }
        });

        Action action = () => ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            initial,
            buildMetadata,
            ReferenceDocument(candidate.Identity, TagObject, "tag")
        );

        action.Should().Throw<PackageSecurityException>();
    }

    private static ReviewedReleaseCandidate Candidate()
    {
        ForkReleaseIdentity identity = Identity();
        object[] assets = Enum.GetValues<ReviewedReleaseAssetKind>().Select(kind => (object)new
        {
            name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
            size = Math.Min(4096, ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind)),
            state = "uploaded",
            browser_download_url = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
        }).ToArray();
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = identity.Tag,
                draft = false,
                prerelease = true,
                assets
            }
        });
        return ReviewedGitHubReleaseCatalog.Parse(catalog).Single();
    }

    private static ForkReleaseIdentity Identity()
    {
        return ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2");
    }

    private static ReviewedGitHubTagReference Initial(ReviewedReleaseCandidate candidate)
    {
        return ReviewedGitHubTagResolver.ParseReference(
            ReferenceDocument(candidate.Identity, TagObject, "tag"),
            candidate
        );
    }

    private static void Finalize(
        ReviewedReleaseCandidate candidate,
        ReviewedGitHubTagReference initial,
        Dictionary<string, object?> annotatedTag
    )
    {
        _ = ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            initial,
            JsonSerializer.SerializeToUtf8Bytes(annotatedTag),
            ReferenceDocument(candidate.Identity, TagObject, "tag")
        );
    }

    private static byte[] ReferenceDocument(
        ForkReleaseIdentity identity,
        string target,
        string type,
        Uri? targetUrl = null
    )
    {
        return JsonSerializer.SerializeToUtf8Bytes(Reference(identity, target, type, targetUrl));
    }

    private static Dictionary<string, object?> Reference(
        ForkReleaseIdentity identity,
        string target,
        string type,
        Uri? targetUrl = null
    )
    {
        return new Dictionary<string, object?>
        {
            ["ref"] = $"refs/tags/{identity.Tag}",
            ["url"] = $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/refs/tags/{identity.Tag}",
            ["object"] = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["sha"] = target,
                ["url"] = (targetUrl ?? ReviewedGitHubReleaseUris.GetTagObjectUri(target)).AbsoluteUri
            }
        };
    }

    private static byte[] AnnotatedTagDocument(ForkReleaseIdentity identity, string tagObject, string commit)
    {
        return JsonSerializer.SerializeToUtf8Bytes(AnnotatedTag(identity, tagObject, commit));
    }

    private static Dictionary<string, object?> AnnotatedTag(
        ForkReleaseIdentity identity,
        string tagObject,
        string commit
    )
    {
        return new Dictionary<string, object?>
        {
            ["sha"] = tagObject,
            ["tag"] = identity.Tag,
            ["url"] = ReviewedGitHubReleaseUris.GetTagObjectUri(tagObject).AbsoluteUri,
            ["object"] = new Dictionary<string, object?>
            {
                ["type"] = "commit",
                ["sha"] = commit,
                ["url"] = $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{commit}"
            }
        };
    }
}
