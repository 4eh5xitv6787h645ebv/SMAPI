using SMAPI.PerformanceBenchmarks.Framework;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>A harmless deterministic scenario which verifies the runner before production scenarios are registered.</summary>
internal sealed class InfrastructureCanaryScenario : IPerformanceScenario
{
    /// <inheritdoc />
    public string Id => "infrastructure.canary";

    /// <inheritdoc />
    public string Description => "Exercises the deterministic scenario contract without game or fixture data.";

    /// <inheritdoc />
    public void Setup()
    {
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = 14695981039346656037UL;
        for (int index = 0; index < operations; index++)
        {
            digest ^= (uint)index;
            digest *= 1099511628211UL;
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
    }
}
