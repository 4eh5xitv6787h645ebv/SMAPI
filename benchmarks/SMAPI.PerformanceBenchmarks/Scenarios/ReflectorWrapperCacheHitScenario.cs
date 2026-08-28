using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Reflection;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure warmed reflection metadata and target-wrapper cache hits.</summary>
internal sealed class ReflectorWrapperCacheHitScenario : IPerformanceScenario
{
    /// <summary>The reflector under test.</summary>
    private Reflector? Reflector;

    /// <summary>The stable target used for every lookup.</summary>
    private ReflectionTarget? Target;

    /// <summary>The exact warmed field wrapper.</summary>
    private IReflectedField<int>? ExpectedField;

    /// <summary>The exact warmed property wrapper.</summary>
    private IReflectedProperty<int>? ExpectedProperty;

    /// <summary>The exact warmed method wrapper.</summary>
    private IReflectedMethod? ExpectedMethod;

    /// <inheritdoc />
    public string Id => "reflection.wrapper-cache-hit";

    /// <inheritdoc />
    public string Description => "Reuses warmed field, property, and method metadata and target wrappers.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Target = new ReflectionTarget();
        this.Reflector = new Reflector();
        this.ExpectedField = this.Reflector.GetField<int>(this.Target, nameof(ReflectionTarget.Value));
        this.ExpectedProperty = this.Reflector.GetProperty<int>(this.Target, nameof(ReflectionTarget.Property));
        this.ExpectedMethod = this.Reflector.GetMethod(this.Target, nameof(ReflectionTarget.GetValue));
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        Reflector reflector = this.Reflector!;
        ReflectionTarget target = this.Target!;
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            IReflectedField<int> field = reflector.GetField<int>(target, nameof(ReflectionTarget.Value));
            IReflectedProperty<int> property = reflector.GetProperty<int>(target, nameof(ReflectionTarget.Property));
            IReflectedMethod method = reflector.GetMethod(target, nameof(ReflectionTarget.GetValue));

            digest = ScenarioDigest.Add(digest, ReferenceEquals(field, this.ExpectedField) ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, ReferenceEquals(property, this.ExpectedProperty) ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, ReferenceEquals(method, this.ExpectedMethod) ? 1UL : 0UL);
            // Invocation is intentionally outside this lookup scenario: FieldInfo.GetValue boxes value types.
            // The wrapper identities prove the field/property/method lookup result without contaminating the
            // exact-zero allocation gate with reflection invocation semantics.
            digest = ScenarioDigest.Add(digest, (ulong)target.Value);
            digest = ScenarioDigest.Add(digest, (ulong)target.Property);
            digest = ScenarioDigest.Add(digest, (ulong)target.GetValue());
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.ExpectedField = null;
        this.ExpectedProperty = null;
        this.ExpectedMethod = null;
        this.Reflector = null;
        this.Target = null;
    }

    /// <summary>A representative target with every supported reflected member kind.</summary>
    private sealed class ReflectionTarget
    {
        public int Value = 42;

        public int Property => this.Value;

        public int GetValue()
        {
            return this.Value;
        }
    }
}
