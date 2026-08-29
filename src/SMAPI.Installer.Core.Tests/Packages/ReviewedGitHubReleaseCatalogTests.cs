using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReviewedGitHubReleaseCatalogTests
{
    [Test]
    public void Parse_CompatibleReleases_ReturnsExactAssetsInDeterministicVersionOrder()
    {
        ForkReleaseIdentity older = Identity("4.5.4", 12);
        ForkReleaseIdentity newest = Identity("10.0.0", 1);
        ForkReleaseIdentity middle = Identity("4.10.0", 2);
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            Release(older),
            Release(newest),
            Release(middle)
        });

        IReadOnlyList<ReviewedReleaseCandidate> result = ReviewedGitHubReleaseCatalog.Parse(document);

        result.Select(candidate => candidate.Identity.Tag).Should().Equal(newest.Tag, middle.Tag, older.Tag);
        result[0].DisplayLabel.Should().Be("SMAPI 10.0.0 — fork Linux alpha 1 (experimental)");
        result.Should().OnlyContain(candidate => candidate.Assets.Length == 6);
        result.Should().OnlyContain(candidate => candidate.Assets.Select(asset => asset.Kind)
            .SequenceEqual(Enum.GetValues<ReviewedReleaseAssetKind>()));
        result.SelectMany(candidate => candidate.Assets).Should().OnlyContain(asset =>
            asset.Name == ReviewedGitHubReleaseUris.GetAssetName(
                result.Single(candidate => candidate.Identity.Tag == GetTagFromAssetUri(asset.DownloadUri)).Identity,
                asset.Kind
            )
        );
    }

    [Test]
    public void Parse_AlphaSequenceOrdersNumericallyDescending()
    {
        ForkReleaseIdentity alpha2 = Identity("4.5.4", 2);
        ForkReleaseIdentity alpha12 = Identity("4.5.4", 12);
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new[] { Release(alpha2), Release(alpha12) });

        ReviewedGitHubReleaseCatalog.Parse(document).Select(candidate => candidate.Identity.Tag)
            .Should().Equal(alpha12.Tag, alpha2.Tag);
    }

    [Test]
    public void Parse_RemoteNamesAndOrdering_DoNotAffectLocalLabelsOrOrder()
    {
        ForkReleaseIdentity older = Identity("4.5.4", 1);
        ForkReleaseIdentity newer = Identity("4.5.5", 1);
        object first = With(Release(older), ("name", "\u202eRemote misleading name"), ("body", "private-looking remote body"));
        object second = With(Release(newer), ("name", "Different remote label"));

        IReadOnlyList<ReviewedReleaseCandidate> result = ReviewedGitHubReleaseCatalog.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new[] { first, second })
        );

        result.Select(candidate => candidate.Identity.Tag).Should().Equal(newer.Tag, older.Tag);
        result.Should().OnlyContain(candidate => !candidate.DisplayLabel.Contains("Remote", StringComparison.Ordinal));
        result.Should().OnlyContain(candidate => !candidate.DisplayLabel.Contains("private", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_DraftsStableInvalidAndIncompleteReleases_Filters()
    {
        ForkReleaseIdentity valid = Identity("4.5.4", 1);
        object draft = With(Release(Identity("4.5.4", 2)), ("draft", true));
        object stable = With(Release(Identity("4.5.4", 3)), ("prerelease", false));
        object invalidTag = With(Release(valid), ("tag_name", "official-looking-v4.5.4"));
        Dictionary<string, object?> incomplete = Release(Identity("4.5.4", 4));
        incomplete["assets"] = ((object[])incomplete["assets"]!).Take(5).ToArray();

        IReadOnlyList<ReviewedReleaseCandidate> result = ReviewedGitHubReleaseCatalog.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new object[] { draft, stable, invalidTag, incomplete, Release(valid) })
        );

        result.Should().ContainSingle().Which.Identity.Should().Be(valid);
    }

    [Test]
    public void Parse_CompatibleReleaseWithExtraAsset_FiltersInsteadOfOpeningPartialContract()
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        Dictionary<string, object?> release = Release(identity);
        object extra = new Dictionary<string, object?>
        {
            ["name"] = "unreviewed-extra.txt",
            ["size"] = 1,
            ["state"] = "uploaded",
            ["browser_download_url"] =
                $"https://github.com/{ForkReleaseIdentity.Repository}/releases/download/{identity.Tag}/unreviewed-extra.txt"
        };
        release["assets"] = ((object[])release["assets"]!).Append(extra).ToArray();

        ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }))
            .Should().BeEmpty();
    }

    [Test]
    public void Parse_AssetsCollectionIsDefensive()
    {
        ReviewedReleaseCandidate candidate = ReviewedGitHubReleaseCatalog.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new[] { Release(Identity("4.5.4", 1)) })
        ).Single();
        ReviewedReleaseAsset[] first = candidate.Assets;

        first[0] = first[1];

        candidate.Assets.Select(asset => asset.Kind).Should().Equal(Enum.GetValues<ReviewedReleaseAssetKind>());
    }

    [Test]
    public void Parse_DuplicateCompatibleTag_Rejects()
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new[] { Release(identity), Release(identity) });

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(document);

        action.Should().Throw<PackageSecurityException>().WithMessage("*duplicate compatible release tag*");
    }

    [Test]
    public void Parse_DuplicateOrCaseCollidingAsset_Rejects()
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        Dictionary<string, object?> release = Release(identity);
        object[] assets = (object[])release["assets"]!;
        Dictionary<string, object?> duplicate = new((Dictionary<string, object?>)assets[0]);
        duplicate["name"] = ((string)duplicate["name"]!).ToUpperInvariant();
        release["assets"] = assets.Append(duplicate).ToArray();

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }));

        action.Should().Throw<PackageSecurityException>().WithMessage("*case-colliding asset names*");
    }

    [TestCase("https://github.com/Pathoschild/SMAPI/releases/download/{tag}/{name}")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/other-tag/{name}")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{tag}/{name}?token=secret")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{tag}/{name}/extra")]
    public void Parse_RequiredAssetWithUnexpectedUri_Rejects(string format)
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        Dictionary<string, object?> release = Release(identity);
        Dictionary<string, object?> asset = (Dictionary<string, object?>)((object[])release["assets"]!)[0];
        string name = (string)asset["name"]!;
        asset["browser_download_url"] = format.Replace("{tag}", identity.Tag, StringComparison.Ordinal)
            .Replace("{name}", name, StringComparison.Ordinal);

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }));

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>().Which;
        exception.Message.Should().NotContain("secret");
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(536870913L)]
    public void Parse_RequiredPackageWithInvalidAdvertisedSize_Rejects(long size)
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        Dictionary<string, object?> release = Release(identity);
        ((Dictionary<string, object?>)((object[])release["assets"]!)[0])["size"] = size;

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }));

        action.Should().Throw<PackageSecurityException>().WithMessage("*invalid or excessive advertised size*");
    }

    [Test]
    public void Parse_EveryRequiredAssetEnforcesItsExactMaximum()
    {
        foreach (ReviewedReleaseAssetKind kind in Enum.GetValues<ReviewedReleaseAssetKind>())
        {
            ForkReleaseIdentity identity = Identity("4.5.4", 1);
            Dictionary<string, object?> release = Release(identity);
            Dictionary<string, object?> asset = ((object[])release["assets"]!)
                .Cast<Dictionary<string, object?>>()
                .Single(value => string.Equals(
                    value["name"] as string,
                    ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
                    StringComparison.Ordinal
                ));
            asset["size"] = ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind) + 1;

            Action action = () => ReviewedGitHubReleaseCatalog.Parse(
                JsonSerializer.SerializeToUtf8Bytes(new[] { release })
            );
            action.Should().Throw<PackageSecurityException>(because: $"{kind} must use its own bound");
        }
    }

    [Test]
    public void Parse_RequiredAssetNotUploaded_Rejects()
    {
        ForkReleaseIdentity identity = Identity("4.5.4", 1);
        Dictionary<string, object?> release = Release(identity);
        ((Dictionary<string, object?>)((object[])release["assets"]!)[1])["state"] = "open";

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }));

        action.Should().Throw<PackageSecurityException>().WithMessage("*isn't in GitHub's uploaded state*");
    }

    [Test]
    public void Parse_DuplicateJsonProperty_Rejects()
    {
        string raw = "[{\"tag_name\":\"x\",\"tag_name\":\"y\",\"draft\":false,\"prerelease\":true,\"assets\":[]}]";

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(Encoding.UTF8.GetBytes(raw));

        action.Should().Throw<PackageSecurityException>().WithMessage("*duplicate JSON properties*");
    }

    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("[1]")]
    [TestCase("[{\"tag_name\":1,\"draft\":false,\"prerelease\":true,\"assets\":[]}]")]
    [TestCase("[{\"tag_name\":\"tag\",\"draft\":\"false\",\"prerelease\":true,\"assets\":[]}]")]
    [TestCase("[{\"tag_name\":\"tag\",\"draft\":false,\"prerelease\":true,\"assets\":{}}]")]
    [TestCase("[/*comment*/]")]
    [TestCase("[],")]
    public void Parse_MalformedOrWrongTypedDocument_Rejects(string raw)
    {
        Action action = () => ReviewedGitHubReleaseCatalog.Parse(Encoding.UTF8.GetBytes(raw));

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void Parse_EmptyOversizedAndExcessReleaseCatalog_Rejects()
    {
        Action empty = () => ReviewedGitHubReleaseCatalog.Parse(ReadOnlyMemory<byte>.Empty);
        Action oversized = () => ReviewedGitHubReleaseCatalog.Parse(
            new byte[ReviewedGitHubReleaseUris.MaximumCatalogBytes + 1]
        );
        object[] tooMany = Enumerable.Range(0, ReviewedGitHubReleaseUris.MaximumCatalogReleases + 1)
            .Select(index => (object)With(Release(Identity("4.5.4", 1)), ("tag_name", $"ignored-{index}")))
            .ToArray();
        Action excessive = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(tooMany));

        empty.Should().Throw<PackageSecurityException>();
        oversized.Should().Throw<PackageSecurityException>();
        excessive.Should().Throw<PackageSecurityException>().WithMessage("*too many releases*");
    }

    [Test]
    public void Parse_ExcessAssetCount_RejectsBeforeFiltering()
    {
        Dictionary<string, object?> release = Release(Identity("4.5.4", 1));
        object sample = ((object[])release["assets"]!)[0];
        release["assets"] = Enumerable.Range(0, ReviewedGitHubReleaseUris.MaximumAssetsPerRelease + 1)
            .Select(_ => sample)
            .ToArray();

        Action action = () => ReviewedGitHubReleaseCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new[] { release }));

        action.Should().Throw<PackageSecurityException>().WithMessage("*too many uploaded assets*");
    }

    private static ForkReleaseIdentity Identity(string version, int alpha)
    {
        return ForkReleaseIdentity.Parse($"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}");
    }

    private static Dictionary<string, object?> Release(ForkReleaseIdentity identity)
    {
        object[] assets = Enum.GetValues<ReviewedReleaseAssetKind>().Select(kind =>
        {
            string name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind);
            return (object)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["size"] = Math.Min(4096, ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind)),
                ["state"] = "uploaded",
                ["browser_download_url"] = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
            };
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["tag_name"] = identity.Tag,
            ["draft"] = false,
            ["prerelease"] = true,
            ["assets"] = assets
        };
    }

    private static Dictionary<string, object?> With(
        Dictionary<string, object?> source,
        params (string Name, object? Value)[] values
    )
    {
        Dictionary<string, object?> result = new(source);
        foreach ((string name, object? value) in values)
            result[name] = value;
        return result;
    }

    private static string GetTagFromAssetUri(Uri uri)
    {
        string[] segments = uri.AbsolutePath.Split('/');
        return segments[^2];
    }
}
