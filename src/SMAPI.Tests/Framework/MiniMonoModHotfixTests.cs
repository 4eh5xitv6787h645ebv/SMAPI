using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MonoMod.Utils;
using NUnit.Framework;

namespace SMAPI.Tests.Framework;

/// <summary>Regression tests for SMAPI's temporary Harmony and MonoMod compatibility fixes.</summary>
[TestFixture]
internal class MiniMonoModHotfixTests
{
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

    private sealed class GenericType<T>
    {
        public T Echo(T value) => value;

        public string Mixed(T value, string fixedValue) => fixedValue;
    }

    private static class GenericMethodType
    {
        public static T Echo<T>(T value) => value;
    }
}
