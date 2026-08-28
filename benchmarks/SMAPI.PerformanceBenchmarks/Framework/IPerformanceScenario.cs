namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>A deterministic operation whose correctness, allocation, and timing can be measured.</summary>
internal interface IPerformanceScenario
{
    /// <summary>Get the stable machine-readable scenario ID.</summary>
    string Id { get; }

    /// <summary>Get a short human-readable description.</summary>
    string Description { get; }

    /// <summary>Prepare immutable inputs and warmable state outside the measured region.</summary>
    void Setup();

    /// <summary>Run the operation batch and return a deterministic digest of its result.</summary>
    /// <param name="operations">The number of logical operations to perform.</param>
    ulong Execute(int operations);

    /// <summary>Release resources after all samples are complete.</summary>
    void Cleanup();
}
