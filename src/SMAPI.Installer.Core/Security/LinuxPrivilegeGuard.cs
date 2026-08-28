using System.Runtime.InteropServices;

namespace StardewModdingAPI.Installer.Core.Security;

/// <summary>Refuses privileged Linux execution before installer side effects.</summary>
public static class LinuxPrivilegeGuard
{
    /// <summary>Throw if the current process is running as effective UID 0 on Linux.</summary>
    public static void AssertNotRoot()
    {
        if (OperatingSystem.IsLinux())
            LinuxPrivilegeGuard.AssertNotRoot(geteuid());
    }

    /// <summary>Validate a captured effective UID. This overload is side-effect free for tests and entry-point guards.</summary>
    public static void AssertNotRoot(uint effectiveUserId)
    {
        if (effectiveUserId == 0)
            throw new PrivilegedInstallerException("The SMAPI installer must not run as root or with sudo.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}

/// <summary>A stable refusal raised before privileged installer side effects.</summary>
public sealed class PrivilegedInstallerException : InvalidOperationException
{
    /// <summary>Construct an instance.</summary>
    public PrivilegedInstallerException(string message)
        : base(message) { }
}
