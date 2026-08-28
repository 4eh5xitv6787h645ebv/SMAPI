namespace SMAPI.PerformanceBenchmarks;

/// <summary>Build stable allocation-free scenario digests.</summary>
internal static class ScenarioDigest
{
    /// <summary>The FNV-1a 64-bit offset basis.</summary>
    public const ulong Offset = 14695981039346656037UL;

    /// <summary>Add an unsigned value to a digest.</summary>
    public static ulong Add(ulong digest, ulong value)
    {
        unchecked
        {
            digest ^= value;
            return digest * 1099511628211UL;
        }
    }

    /// <summary>Add a nullable string to a digest without allocating.</summary>
    public static ulong Add(ulong digest, string? value)
    {
        digest = ScenarioDigest.Add(digest, value is null ? ulong.MaxValue : (ulong)value.Length);
        if (value is not null)
        {
            foreach (char character in value)
                digest = ScenarioDigest.Add(digest, character);
        }
        return digest;
    }
}
