using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class LinuxModHealthEnvironmentTests
{
    [Test]
    public void ParseDistribution_UsesOnlyAllowlistedIdAndNumericVersion()
    {
        const string source = "ID=ubuntu\nVERSION_ID=\"24.04.3\"\nPRETTY_NAME=\"Private administrator banner\"\nHOME_URL=\"https://private.invalid\"\n";

        LinuxModHealthEnvironment.ParseDistribution(source).Should().Be("ubuntu 24.04.3");
        LinuxModHealthEnvironment.ParseDistribution("ID=private-admin-os\nVERSION_ID=1\n").Should().BeNull();
        LinuxModHealthEnvironment.ParseDistribution("ID=ubuntu\nVERSION_ID=24.04-private\n").Should().Be("ubuntu");
    }

    [TestCase("Linux 6.12.9-private-hostname", "6.12.9")]
    [TestCase("6.8.0-1014-aws", "6.8.0")]
    [TestCase("Unix private-host 6.8", null)]
    public void NormalizeKernel_RetainsOnlyLeadingNumericRelease(string source, string? expected)
    {
        LinuxModHealthEnvironment.NormalizeKernel(source).Should().Be(expected);
    }
}
