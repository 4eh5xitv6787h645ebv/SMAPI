using System;
using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Reflection;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="Reflector"/>.</summary>
[TestFixture]
internal class ReflectorTests
{
    [Test(Description = "Assert that cache keys distinguish identically named types from separate assemblies.")]
    public void GetField_DistinguishesTypesAcrossAssemblies()
    {
        Type numberType = CreateTypeWithField($"ReflectorTests.Number.{Guid.NewGuid():N}", typeof(int));
        Type textType = CreateTypeWithField($"ReflectorTests.Text.{Guid.NewGuid():N}", typeof(string));
        object number = Activator.CreateInstance(numberType)!;
        object text = Activator.CreateInstance(textType)!;
        Reflector reflector = new();

        reflector.GetField<int>(number, "Value").SetValue(42);
        reflector.GetField<string>(text, "Value").SetValue("cached separately");

        reflector.GetField<int>(number, "Value").GetValue().Should().Be(42);
        reflector.GetField<string>(text, "Value").GetValue().Should().Be("cached separately");
    }

    [Test(Description = "Assert that a cached reflection lookup and target-bound wrapper don't allocate.")]
    public void GetField_CacheHitAvoidsLookupAllocations()
    {
        ReflectionTarget target = new();
        Reflector reflector = new();

        // Cross the runtime's tiered-compilation threshold before measuring steady-state allocations.
        for (int i = 0; i < 10_000; i++)
            reflector.GetField<int>(target, nameof(ReflectionTarget.Value));

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            reflector.GetField<int>(target, nameof(ReflectionTarget.Value));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        allocatedBytes.Should().Be(0, "cache hits should reuse their key, callback, and target-bound wrapper");
    }

    [Test(Description = "Assert that target-bound wrappers are reused within an interval and released from the cache for a new interval.")]
    public void GetField_CachesWrapperForInterval()
    {
        ReflectionTarget target = new();
        Reflector reflector = new();

        IReflectedField<int> first = reflector.GetField<int>(target, nameof(ReflectionTarget.Value));
        reflector.GetField<int>(target, nameof(ReflectionTarget.Value)).Should().BeSameAs(first);

        reflector.NewCacheInterval();
        reflector.GetField<int>(target, nameof(ReflectionTarget.Value)).Should().NotBeSameAs(first);
    }

    /// <summary>Create a type with the same full name in a distinct dynamic assembly.</summary>
    private static Type CreateTypeWithField(string assemblyName, Type fieldType)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly
            .DefineDynamicModule(assemblyName)
            .DefineType("ReflectorTests.DuplicateType", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.DefineField("Value", fieldType, FieldAttributes.Public);
        return type.CreateType()!;
    }

    /// <summary>A target for repeated cache-hit lookups.</summary>
    private sealed class ReflectionTarget
    {
        public int Value = 42;
    }
}
