namespace StardewModdingAPI.Installer.Core.Ownership.Persistence;

/// <summary>Resource limits applied before an ownership document becomes trusted installer state.</summary>
public sealed class OwnershipPersistenceLimits
{
    /// <summary>Conservative defaults which are large enough for a normal SMAPI package but bounded against hostile input.</summary>
    public static OwnershipPersistenceLimits Default { get; } = new(16 * 1024 * 1024, 16, 20_000);

    public int MaxDocumentBytes { get; }
    public int MaxJsonDepth { get; }
    public int MaxEntries { get; }

    public OwnershipPersistenceLimits(int maxDocumentBytes, int maxJsonDepth, int maxEntries)
    {
        if (maxDocumentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDocumentBytes));
        if (maxJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maxJsonDepth));
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        this.MaxDocumentBytes = maxDocumentBytes;
        this.MaxJsonDepth = maxJsonDepth;
        this.MaxEntries = maxEntries;
    }
}

/// <summary>An ownership document failed strict syntax, canonical encoding, policy, or cross-document validation.</summary>
public sealed class OwnershipDocumentException : Exception
{
    public OwnershipDocumentException(string message)
        : base(message) { }

    public OwnershipDocumentException(string message, Exception innerException)
        : base(message, innerException) { }
}
