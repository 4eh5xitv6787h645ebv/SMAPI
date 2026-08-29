namespace StardewModdingAPI.Installer.Core.Security;

/// <summary>An I/O failure which preserves the exact Linux errno without requiring callers to parse prose.</summary>
internal sealed class LinuxNativeIOException : IOException
{
    /// <summary>The positive Linux errno captured immediately after the failed syscall.</summary>
    public int ErrorNumber { get; }

    public LinuxNativeIOException(string message, int errorNumber)
        : base($"{message} (errno {errorNumber}).")
    {
        if (errorNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(errorNumber));
        this.ErrorNumber = errorNumber;
    }
}
