namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>An exception raised when a protocol message or transition violates the version 1 contract.</summary>
public sealed class ProtocolException : Exception
{
    /// <summary>Construct an instance.</summary>
    /// <param name="message">The safe description of the violated invariant.</param>
    public ProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Construct an instance.</summary>
    /// <param name="message">The safe description of the violated invariant.</param>
    /// <param name="innerException">The underlying parser exception.</param>
    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
