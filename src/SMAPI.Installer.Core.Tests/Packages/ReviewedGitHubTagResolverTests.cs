using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReviewedGitHubTagResolverTests
{
    private static readonly ForkReleaseIdentity Identity = ForkReleaseIdentity.Parse(
        "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
    );
    private const string TagObject = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public void ParseReference_ExactAnnotatedTag_ReturnsTagObject()
    {
        byte[] document = ReferenceDocument(Identity, TagObject, "tag");

        ReviewedGitHubTagReference result = ReviewedGitHubTagResolver.ParseReference(document, Identity);

        result.Should().Be(new ReviewedGitHubTagReference(Identity.Tag, TagObject));
    }

    [Test]
    public void ParseReference_LightweightTag_Rejects()
    {
        byte[] document = ReferenceDocument(
            Identity,
            Commit,
            "commit",
            targetUrl: new Uri($"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{Commit}")
        );

        Action action = () => ReviewedGitHubTagResolver.ParseReference(document, Identity);

        action.Should().Throw<PackageSecurityException>().WithMessage("*lightweight or unsupported Git tag*");
    }

    [TestCase("refs/tags/other")]
    [TestCase("refs/heads/develop")]
    public void ParseReference_DifferentReference_Rejects(string reference)
    {
        Dictionary<string, object?> document = Reference(Identity, TagObject, "tag");
        document["ref"] = reference;

        Action action = () => ReviewedGitHubTagResolver.ParseReference(JsonSerializer.SerializeToUtf8Bytes(document), Identity);

        action.Should().Throw<PackageSecurityException>().WithMessage("*different release tag*");
    }

    [Test]
    public void ParseReference_OffRepositoryOrMismatchedUrls_Rejects()
    {
        Dictionary<string, object?> offRepository = Reference(Identity, TagObject, "tag");
        offRepository["url"] = "https://api.github.com/repos/Pathoschild/SMAPI/git/refs/tags/" + Identity.Tag;
        Dictionary<string, object?> wrongObject = Reference(Identity, TagObject, "tag");
        ((Dictionary<string, object?>)wrongObject["object"]!)["url"] = ReviewedGitHubReleaseUris.GetTagObjectUri(new string('c', 40)).AbsoluteUri;

        Action first = () => ReviewedGitHubTagResolver.ParseReference(JsonSerializer.SerializeToUtf8Bytes(offRepository), Identity);
        Action second = () => ReviewedGitHubTagResolver.ParseReference(JsonSerializer.SerializeToUtf8Bytes(wrongObject), Identity);

        first.Should().Throw<PackageSecurityException>();
        second.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ParseReference_NoncanonicalTagObjectId_Rejects()
    {
        Dictionary<string, object?> document = Reference(Identity, TagObject, "tag");
        ((Dictionary<string, object?>)document["object"]!)["sha"] = TagObject.ToUpperInvariant();

        Action action = () => ReviewedGitHubTagResolver.ParseReference(
            JsonSerializer.SerializeToUtf8Bytes(document), Identity
        );

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ParseAnnotatedTag_ExactSelectedObject_ReturnsIndependentSourceCommit()
    {
        ReviewedGitHubTagReference reference = new(Identity.Tag, TagObject);

        ReviewedGitHubResolvedTag result = ReviewedGitHubTagResolver.ParseAnnotatedTag(
            AnnotatedTagDocument(Identity, TagObject, Commit),
            Identity,
            reference
        );

        result.Should().Be(new ReviewedGitHubResolvedTag(Identity.Tag, TagObject, Commit));
    }

    [Test]
    public void AssertReferenceUnchanged_ExactRefreshAllowsMovedOrDifferentRefreshRejects()
    {
        ReviewedGitHubTagReference selected = new(Identity.Tag, TagObject);
        ReviewedGitHubTagReference exact = new(Identity.Tag, TagObject);
        ReviewedGitHubTagReference moved = new(Identity.Tag, new string('c', 40));
        ReviewedGitHubTagReference differentTag = new(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.3",
            TagObject
        );

        Action unchanged = () => ReviewedGitHubTagResolver.AssertReferenceUnchanged(selected, exact);
        Action movedAction = () => ReviewedGitHubTagResolver.AssertReferenceUnchanged(selected, moved);
        Action differentAction = () => ReviewedGitHubTagResolver.AssertReferenceUnchanged(selected, differentTag);

        unchanged.Should().NotThrow();
        movedAction.Should().Throw<PackageSecurityException>().WithMessage("*tag moved*");
        differentAction.Should().Throw<PackageSecurityException>().WithMessage("*tag moved*");
    }

    [Test]
    public void ParseAnnotatedTag_MovedTagObject_Rejects()
    {
        ReviewedGitHubTagReference reference = new(Identity.Tag, TagObject);
        byte[] moved = AnnotatedTagDocument(Identity, new string('c', 40), Commit);

        Action action = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(moved, Identity, reference);

        action.Should().Throw<PackageSecurityException>().WithMessage("*doesn't match the selected tag object*");
    }

    [Test]
    public void ParseAnnotatedTag_DifferentTagOrRetainedSelection_Rejects()
    {
        Dictionary<string, object?> differentTag = AnnotatedTag(Identity, TagObject, Commit);
        differentTag["tag"] = "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.3";
        ReviewedGitHubTagReference wrongReference = new("fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.3", TagObject);

        Action documentMismatch = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            JsonSerializer.SerializeToUtf8Bytes(differentTag),
            Identity,
            new(Identity.Tag, TagObject)
        );
        Action selectionMismatch = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            AnnotatedTagDocument(Identity, TagObject, Commit),
            Identity,
            wrongReference
        );

        documentMismatch.Should().Throw<PackageSecurityException>().WithMessage("*different release tag*");
        selectionMismatch.Should().Throw<PackageSecurityException>().WithMessage("*selection doesn't match*");
    }

    [TestCase("blob")]
    [TestCase("tree")]
    [TestCase("tag")]
    public void ParseAnnotatedTag_NonCommitTarget_Rejects(string type)
    {
        Dictionary<string, object?> document = AnnotatedTag(Identity, TagObject, Commit);
        ((Dictionary<string, object?>)document["object"]!)["type"] = type;

        Action action = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            JsonSerializer.SerializeToUtf8Bytes(document),
            Identity,
            new(Identity.Tag, TagObject)
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*doesn't target a Git commit*");
    }

    [TestCase("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [TestCase("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [TestCase("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [TestCase("gggggggggggggggggggggggggggggggggggggggg")]
    public void ParseAnnotatedTag_InvalidCommit_Rejects(string commit)
    {
        Dictionary<string, object?> document = AnnotatedTag(Identity, TagObject, Commit);
        ((Dictionary<string, object?>)document["object"]!)["sha"] = commit;

        Action action = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            JsonSerializer.SerializeToUtf8Bytes(document),
            Identity,
            new(Identity.Tag, TagObject)
        );

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ParseAnnotatedTag_OffRepositoryOrMismatchedCommitUrl_Rejects()
    {
        Dictionary<string, object?> offRepository = AnnotatedTag(Identity, TagObject, Commit);
        ((Dictionary<string, object?>)offRepository["object"]!)["url"] =
            $"https://api.github.com/repos/Pathoschild/SMAPI/git/commits/{Commit}";
        Dictionary<string, object?> wrongCommit = AnnotatedTag(Identity, TagObject, Commit);
        ((Dictionary<string, object?>)wrongCommit["object"]!)["url"] =
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{new string('c', 40)}";

        Action first = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            JsonSerializer.SerializeToUtf8Bytes(offRepository), Identity, new(Identity.Tag, TagObject)
        );
        Action second = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            JsonSerializer.SerializeToUtf8Bytes(wrongCommit), Identity, new(Identity.Tag, TagObject)
        );

        first.Should().Throw<PackageSecurityException>();
        second.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void Parsers_DuplicateMalformedOversizedAndDeepJson_Rejects()
    {
        string duplicate = $"{{\"ref\":\"refs/tags/{Identity.Tag}\",\"ref\":\"refs/tags/{Identity.Tag}\",\"url\":\"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/refs/tags/{Identity.Tag}\",\"object\":{{\"type\":\"tag\",\"sha\":\"{TagObject}\",\"url\":\"{ReviewedGitHubReleaseUris.GetTagObjectUri(TagObject)}\"}}}}";
        Action duplicateAction = () => ReviewedGitHubTagResolver.ParseReference(Encoding.UTF8.GetBytes(duplicate), Identity);
        Action malformed = () => ReviewedGitHubTagResolver.ParseReference(Encoding.UTF8.GetBytes("[]"), Identity);
        Action oversized = () => ReviewedGitHubTagResolver.ParseReference(
            new byte[ReviewedGitHubReleaseUris.MaximumTagDocumentBytes + 1], Identity
        );
        string deep = new string('[', 18) + "0" + new string(']', 18);
        Action excessiveDepth = () => ReviewedGitHubTagResolver.ParseReference(Encoding.UTF8.GetBytes(deep), Identity);

        duplicateAction.Should().Throw<PackageSecurityException>().WithMessage("*duplicate JSON properties*");
        malformed.Should().Throw<PackageSecurityException>();
        oversized.Should().Throw<PackageSecurityException>();
        excessiveDepth.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ParseAnnotatedTag_DoesNotAcceptBuildMetadataShapeAsCommitAuthority()
    {
        byte[] buildMetadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1,
            source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = Commit, tree = new string('c', 40) }
        });

        Action action = () => ReviewedGitHubTagResolver.ParseAnnotatedTag(
            buildMetadata,
            Identity,
            new(Identity.Tag, TagObject)
        );

        action.Should().Throw<PackageSecurityException>();
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
