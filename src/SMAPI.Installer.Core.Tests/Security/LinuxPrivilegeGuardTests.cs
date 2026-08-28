using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Security;

[TestFixture]
public sealed class LinuxPrivilegeGuardTests
{
    [Test]
    public void AssertNotRoot_RootEffectiveUserIdRefusesWithStableError()
    {
        Action action = () => LinuxPrivilegeGuard.AssertNotRoot(0);

        action.Should().Throw<PrivilegedInstallerException>()
            .WithMessage("*must not run as root or with sudo*");
    }

    [TestCase(1u)]
    [TestCase(1000u)]
    [TestCase(uint.MaxValue)]
    public void AssertNotRoot_NonRootEffectiveUserIdAllowsExecution(uint effectiveUserId)
    {
        Action action = () => LinuxPrivilegeGuard.AssertNotRoot(effectiveUserId);

        action.Should().NotThrow();
    }
}
