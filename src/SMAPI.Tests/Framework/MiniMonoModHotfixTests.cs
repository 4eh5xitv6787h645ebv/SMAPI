using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using HarmonyLib;
using MonoMod.Utils;
using NUnit.Framework;

namespace SMAPI.Tests.Framework;

/// <summary>Regression tests for SMAPI's temporary Harmony and MonoMod compatibility fixes.</summary>
[TestFixture]
internal class MiniMonoModHotfixTests
{
    [Test]
    [NonParallelizable]
    public void PreservesReferenceArgumentsAcrossSharedGenericInstantiations()
    {
        if (!OperatingSystem.IsLinux() || Environment.Version.Major < 10)
            Assert.Ignore("The generic-detour regression is specific to the Linux .NET 10 host.");

        Harmony harmony = new($"SMAPI.Tests.{nameof(PreservesReferenceArgumentsAcrossSharedGenericInstantiations)}");
        try
        {
            MethodInfo createDynamicMethod = typeof(Harmony).Assembly
                .GetType("HarmonyLib.MethodPatcherTools", throwOnError: true)!
                .GetMethod("CreateDynamicMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(
                original: createDynamicMethod,
                postfix: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(MiniMonoModHotfix.CanonicalizeLinuxNet10GenericPatchSignature))
            );

            for (int i = 0; i < 100; i++)
                _ = MiniMonoModHotfixTests.GetGenericDetourSnapshot();

            MethodInfo original = typeof(GenericDetourTarget<string>).GetMethod(nameof(GenericDetourTarget<string>.Describe))!;
            MethodInfo postfix = typeof(MiniMonoModHotfixTests).GetMethod(nameof(AppendPatchMarker), BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(original, postfix: new HarmonyMethod(postfix));

            MiniMonoModHotfixTests.GetGenericDetourSnapshot().Should().Equal(
                "String:String|patched",
                "String:Object|patched",
                "String:ReferenceA|patched",
                "String:ReferenceB|patched"
            );
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    [NonParallelizable]
    public void PreservesReferenceReturnsAcrossSharedGenericInstantiations()
    {
        if (!OperatingSystem.IsLinux() || Environment.Version.Major < 10)
            Assert.Ignore("The generic-detour regression is specific to the Linux .NET 10 host.");

        Harmony harmony = new($"SMAPI.Tests.{nameof(PreservesReferenceReturnsAcrossSharedGenericInstantiations)}");
        try
        {
            MethodInfo createDynamicMethod = typeof(Harmony).Assembly
                .GetType("HarmonyLib.MethodPatcherTools", throwOnError: true)!
                .GetMethod("CreateDynamicMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(
                original: createDynamicMethod,
                postfix: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(MiniMonoModHotfix.CanonicalizeLinuxNet10GenericPatchSignature))
            );

            for (int i = 0; i < 100; i++)
                _ = MiniMonoModHotfixTests.GetGenericReturnSnapshot();

            MethodInfo original = typeof(GenericReturnTarget<string>).GetMethod(nameof(GenericReturnTarget<string>.Echo))!;
            MethodInfo postfix = typeof(MiniMonoModHotfixTests).GetMethod(nameof(NoOpPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(original, postfix: new HarmonyMethod(postfix));

            MiniMonoModHotfixTests.GetGenericReturnSnapshot().Should().Equal(
                nameof(String),
                nameof(Object),
                nameof(ReferenceA),
                nameof(ReferenceB)
            );
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void CanonicalizesReferenceParametersAndReturnsFromGenericType()
    {
        MethodInfo method = typeof(GenericType<string>).GetMethod(nameof(GenericType<string>.Echo))!;
        using DynamicMethodDefinition wrapper = MiniMonoModHotfixTests.CreateWrapper(method);

        MiniMonoModHotfix.CanonicalizeLinuxNet10GenericPatchSignature(method, wrapper);

        wrapper.Definition.Parameters[0].ParameterType.FullName.Should().NotBe(typeof(object).FullName);
        wrapper.Definition.Parameters[1].ParameterType.FullName.Should().Be(typeof(object).FullName);
        wrapper.Definition.ReturnType.FullName.Should().Be(typeof(object).FullName);
    }

    [Test]
    public void CanonicalizesReferenceParametersFromGenericMethod()
    {
        MethodInfo method = typeof(GenericMethodType)
            .GetMethod(nameof(GenericMethodType.Echo))!
            .MakeGenericMethod(typeof(string));
        using DynamicMethodDefinition wrapper = MiniMonoModHotfixTests.CreateWrapper(method);

        MiniMonoModHotfix.CanonicalizeLinuxNet10GenericPatchSignature(method, wrapper);

        wrapper.Definition.Parameters[0].ParameterType.FullName.Should().Be(typeof(object).FullName);
        wrapper.Definition.ReturnType.FullName.Should().Be(typeof(object).FullName);
    }

    [Test]
    public void PreservesFixedAndValueTypeParameters()
    {
        MethodInfo method = typeof(GenericType<int>).GetMethod(nameof(GenericType<int>.Mixed))!;
        using DynamicMethodDefinition wrapper = MiniMonoModHotfixTests.CreateWrapper(method);

        MiniMonoModHotfix.CanonicalizeLinuxNet10GenericPatchSignature(method, wrapper);

        wrapper.Definition.Parameters[0].ParameterType.FullName.Should().NotBe(typeof(object).FullName);
        wrapper.Definition.Parameters.Skip(1).Select(parameter => parameter.ParameterType.FullName).Should().Equal(
            typeof(int).FullName,
            typeof(string).FullName
        );
        wrapper.Definition.ReturnType.FullName.Should().Be(typeof(string).FullName);
    }

    /// <summary>Create the same initial wrapper signature Harmony creates for a method.</summary>
    private static DynamicMethodDefinition CreateWrapper(MethodInfo method)
    {
        List<Type> parameterTypes = method.GetParameters().Select(parameter => parameter.ParameterType).ToList();
        if (!method.IsStatic)
            parameterTypes.Insert(0, method.DeclaringType!);

        return new DynamicMethodDefinition($"{method.Name}_Patch", method.ReturnType, [.. parameterTypes]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] GetGenericDetourSnapshot() =>
    [
        new GenericDetourTarget<string>().Describe("value"),
        new GenericDetourTarget<object>().Describe(new object()),
        new GenericDetourTarget<ReferenceA>().Describe(new ReferenceA()),
        new GenericDetourTarget<ReferenceB>().Describe(new ReferenceB())
    ];

    private static void AppendPatchMarker(ref string __result)
    {
        __result += "|patched";
    }

    private static void NoOpPostfix() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] GetGenericReturnSnapshot() =>
    [
        new GenericReturnTarget<string>().Echo("value").GetType().Name,
        new GenericReturnTarget<object>().Echo(new object()).GetType().Name,
        new GenericReturnTarget<ReferenceA>().Echo(new ReferenceA()).GetType().Name,
        new GenericReturnTarget<ReferenceB>().Echo(new ReferenceB()).GetType().Name
    ];

    private sealed class GenericType<T>
    {
        public T Echo(T value) => value;

        public string Mixed(T value, string fixedValue) => fixedValue;
    }

    private static class GenericMethodType
    {
        public static T Echo<T>(T value) => value;
    }

    private sealed class GenericDetourTarget<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Describe(T value) => $"{typeof(T).Name}:{value!.GetType().Name}";
    }

    private sealed class GenericReturnTarget<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public T Echo(T value) => value;
    }

    private sealed class ReferenceA;

    private sealed class ReferenceB;
}
