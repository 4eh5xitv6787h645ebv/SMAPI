using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReviewedGitHubDownloadPolicyTests
{
    private readonly ReviewedGitHubDownloadPolicy Policy = new();

    [Test]
    public void AssertAllowed_ReviewedInitialAndRedirectUris_Allows()
    {
        this.Policy.Invoking(p => p.AssertAllowed(
            new Uri(
                "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/"
                + "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1/package.zip"
            ),
            isInitial: true
        )).Should().NotThrow();
        this.Policy.Invoking(p => p.AssertAllowed(
            new Uri("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases/tags/test"),
            isInitial: true
        )).Should().NotThrow();
        this.Policy.Invoking(p => p.AssertAllowed(
            new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/file?sig=secret"),
            isInitial: false
        )).Should().NotThrow();
    }

    [TestCase("http://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/tag/file")]
    [TestCase("https://user:secret@github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/tag/file")]
    [TestCase("https://github.com:444/4eh5xitv6787h645ebv/SMAPI/releases/download/tag/file")]
    [TestCase("https://github.com/Pathoschild/SMAPI/releases/download/tag/file")]
    [TestCase("https://example.com/4eh5xitv6787h645ebv/SMAPI/releases/download/tag/file")]
    [TestCase("https://api.github.com/repos/4eh5xitv6787h645ebv/SMAPI/releases-evil")]
    public void AssertAllowed_UnreviewedInitialUri_RejectsWithoutEchoingCredentials(string rawUri)
    {
        Action action = () => this.Policy.AssertAllowed(new Uri(rawUri), isInitial: true);

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>().Which;
        exception.Message.Should().NotContain("secret");
        exception.Message.Should().NotContain("user:");
    }

    [Test]
    public void AssertAllowed_UnreviewedRedirect_Rejects()
    {
        Action action = () => this.Policy.AssertAllowed(
            new Uri("https://evil.example/package?token=secret"),
            isInitial: false
        );

        PackageSecurityException exception = action.Should().Throw<PackageSecurityException>().Which;
        exception.Message.Should().NotContain("secret");
    }

    [Test]
    public void AssertAllowed_CrossRepositoryGitHubRedirect_Rejects()
    {
        Action action = () => this.Policy.AssertAllowed(
            new Uri("https://github.com/other/repository/releases/download/tag/file"),
            isInitial: false
        );

        action.Should().Throw<PackageSecurityException>();
    }
}
