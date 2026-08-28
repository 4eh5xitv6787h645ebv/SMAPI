namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>An exception raised when a release package or acquisition violates a safety invariant.</summary>
public sealed class PackageSecurityException : Exception
{
    /// <summary>Construct an instance.</summary>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    public PackageSecurityException(string message)
        : base(message)
    {
    }

    /// <summary>Construct an instance.</summary>
    /// <param name="message">The credential-safe message describing the failed invariant.</param>
    /// <param name="innerException">The underlying exception.</param>
    public PackageSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
