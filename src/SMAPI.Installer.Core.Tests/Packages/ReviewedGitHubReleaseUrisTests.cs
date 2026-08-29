using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReviewedGitHubReleaseUrisTests
{
    private static readonly ForkReleaseIdentity Identity = ForkReleaseIdentity.Parse(
        "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
    );
    private readonly ReviewedGitHubReleaseApiPolicy ApiPolicy = new();
    private readonly ReviewedGitHubReleaseAssetPolicy AssetPolicy = new();

    [Test]
    public void Builders_DeriveExactRoutesNamesAndBounds()
    {
        ReviewedGitHubReleaseUris.GetCatalogUri().AbsoluteUri.Should().Be(
            "https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=1"
        );
        ReviewedGitHubReleaseUris.GetTagReferenceUri(Identity).AbsoluteUri.Should().Be(
            $"https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/ref/tags/{Identity.Tag}"
        );
        ReviewedGitHubReleaseUris.GetTagObjectUri(new string('a', 40)).AbsoluteUri.Should().Be(
            $"https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/tags/{new string('a', 40)}"
        );

        ReviewedReleaseAssetKind[] kinds = Enum.GetValues<ReviewedReleaseAssetKind>();
        kinds.Should().HaveCount(6);
        kinds.Select(kind => ReviewedGitHubReleaseUris.GetAssetName(Identity, kind))
            .Should().OnlyHaveUniqueItems();
        kinds.Should().OnlyContain(kind => ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind) > 0);
        ReviewedGitHubReleaseUris.GetMaximumAssetSetBytes().Should().Be(
            kinds.Sum(ReviewedGitHubReleaseUris.GetMaximumAssetBytes)
        );
        kinds.Should().OnlyContain(kind => ReviewedGitHubReleaseUris.GetAssetUri(Identity, kind).AbsoluteUri.StartsWith(
            $"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{Identity.Tag}/",
            StringComparison.Ordinal
        ));
    }

    [Test]
    public void ApiPolicy_ExactBuiltUris_AllowsOnlyAsInitialRequests()
    {
        Uri[] accepted =
        [
            ReviewedGitHubReleaseUris.GetCatalogUri(),
            ReviewedGitHubReleaseUris.GetTagReferenceUri(Identity),
            ReviewedGitHubReleaseUris.GetTagObjectUri(new string('a', 40))
        ];

        foreach (Uri uri in accepted)
        {
            this.ApiPolicy.Invoking(policy => policy.AssertAllowed(uri, isInitial: true)).Should().NotThrow();
            this.ApiPolicy.Invoking(policy => policy.AssertAllowed(uri, isInitial: false)).Should().Throw<PackageSecurityException>();
        }
    }

    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases?page=1&per_page=20")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=2")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases/evil?per_page=20&page=1")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/ref/tags-evil/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/ref/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/extra")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/ref/tags/fork%2D4eh5xitv6787h645ebv%2Dlinux%2Dv4.5.4%2Dalpha.2")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/ref/tags/fork%252D4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/tags/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/extra")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/git/tags/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("https://api.github.com/repos/Pathoschild/SMAPI/releases?per_page=20&page=1")]
    [TestCase("https://api.github.com:443/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=1")]
    [TestCase("https://api.github.com:444/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=1")]
    [TestCase("http://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=1")]
    public void ApiPolicy_ConfusableOrUnreviewedRoutes_Rejects(string raw)
    {
        Action action = () => this.ApiPolicy.AssertAllowed(new Uri(raw), isInitial: true);

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void ApiPolicy_CredentialsFragmentAndUnexpectedQueries_RejectWithoutEchoingSecrets()
    {
        string[] values =
        [
            "https://user:secret@api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases?per_page=20&page=1",
            $"{ReviewedGitHubReleaseUris.GetTagReferenceUri(Identity)}?token=secret",
            $"{ReviewedGitHubReleaseUris.GetTagObjectUri(new string('a', 40))}#secret"
        ];

        foreach (string raw in values)
        {
            PackageSecurityException exception = this.ApiPolicy.Invoking(policy => policy.AssertAllowed(new Uri(raw), true))
                .Should().Throw<PackageSecurityException>().Which;
            exception.Message.Should().NotContain("secret");
        }
    }

    [Test]
    public void AssetPolicy_ExactSixInitialUrisAndReviewedRedirects_Allows()
    {
        foreach (ReviewedReleaseAssetKind kind in Enum.GetValues<ReviewedReleaseAssetKind>())
            this.AssetPolicy.Invoking(policy => policy.AssertAllowed(ReviewedGitHubReleaseUris.GetAssetUri(Identity, kind), true)).Should().NotThrow();
        this.AssetPolicy.Invoking(policy => policy.AssertAllowed(
            new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/value?sig=secret"),
            false
        )).Should().NotThrow();
        this.AssetPolicy.Invoking(policy => policy.AssertAllowed(
            new Uri("https://objects.githubusercontent.com/github-production-release-asset/value?sig=secret"),
            false
        )).Should().NotThrow();
    }

    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/not-required.txt")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/SHA256SUMS/extra")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/fork%2D4eh5xitv6787h645ebv%2Dlinux%2Dv4.5.4%2Dalpha.2/SHA256SUMS")]
    [TestCase("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/SHA256SUMS?token=secret")]
    [TestCase("https://github.com/other/repository/releases/download/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/SHA256SUMS")]
    [TestCase("https://example.com/4eh5xitv6787h645ebv/SMAPI/releases/download/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2/SHA256SUMS")]
    public void AssetPolicy_NoncanonicalOrUnexpectedInitialUri_Rejects(string raw)
    {
        this.AssetPolicy.Invoking(policy => policy.AssertAllowed(new Uri(raw), true))
            .Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void AssetPolicy_CrossRepositoryOrInitialGithubRedirect_Rejects()
    {
        this.AssetPolicy.Invoking(policy => policy.AssertAllowed(
            ReviewedGitHubReleaseUris.GetAssetUri(Identity, ReviewedReleaseAssetKind.Checksums),
            false
        )).Should().Throw<PackageSecurityException>();
        this.AssetPolicy.Invoking(policy => policy.AssertAllowed(new Uri("https://evil.example/file"), false))
            .Should().Throw<PackageSecurityException>();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TagObjectBuilder_InvalidObjectId_Rejects(string? value)
    {
        Action action = () => ReviewedGitHubReleaseUris.GetTagObjectUri(value!);

        action.Should().Throw<PackageSecurityException>();
    }
}
