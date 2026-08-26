// This temporary utility fixes an esoteric issue in XNA Framework where deserialization depends on
// the order of fields returned by Type.GetFields, but that order changes after Harmony/MonoMod use
// reflection to access the fields due to an issue in .NET Framework.
// https://twitter.com/0x0ade/status/1414992316964687873
//
// This will be removed when Harmony/MonoMod are updated to incorporate the fix.
//
// Special thanks to 0x0ade for submitting this workaround! Copy/pasted and adapted from MonoMod.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// ReSharper disable once CheckNamespace -- Temporary hotfix submitted by the MonoMod author.
namespace MonoMod.Utils;

[SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Temporary hotfix submitted by the MonoMod author.")]
[SuppressMessage("ReSharper", "PossibleNullReferenceException", Justification = "Temporary hotfix submitted by the MonoMod author.")]
internal static class MiniMonoModHotfix
{
    // .NET Framework can break member ordering if using Module.Resolve* on certain members.

    private static readonly object[] _NoArgs = [];
    private static readonly object?[] _CacheGetterArgs = [/* MemberListType.All */ 0, /* name apparently always null? */ null];

    private static readonly Type? t_RuntimeType =
        typeof(Type).Assembly
            .GetType("System.RuntimeType");

    private static readonly PropertyInfo? p_RuntimeType_Cache =
        typeof(Type).Assembly
            .GetType("System.RuntimeType")
            ?.GetProperty("Cache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? m_RuntimeTypeCache_GetFieldList =
        typeof(Type).Assembly
            .GetType("System.RuntimeType+RuntimeTypeCache")
            ?.GetMethod("GetFieldList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? m_RuntimeTypeCache_GetPropertyList =
        typeof(Type).Assembly
            .GetType("System.RuntimeType+RuntimeTypeCache")
            ?.GetMethod("GetPropertyList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly ConditionalWeakTable<Type, CacheFixEntry> _CacheFixed = new();

    public static void Apply()
    {
        var harmony = new Harmony("MiniMonoModHotfix");

        harmony.Patch(
            original: typeof(Harmony).Assembly
                .GetType("HarmonyLib.MethodBodyReader", throwOnError: true)!
                .GetMethod("ReadOperand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            transpiler: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(ResolveTokenFix))
        );

        harmony.Patch(
            original: typeof(MonoMod.Utils.ReflectionHelper).Assembly
                .GetType("MonoMod.Utils.DynamicMethodDefinition+<>c__DisplayClass3_0", throwOnError: true)!
                .GetMethod("<_CopyMethodToDefinition>g__ResolveTokenAs|1", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            transpiler: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(ResolveTokenFix))
        );

        // Harmony 2.2 allowed insertion at Pos == Length, which some mods use to append instructions.
        // Harmony 2.4 rejects that position, so relax only the insertion methods which allowed it before.
        foreach (MethodInfo method in new[]
        {
            typeof(CodeMatcher).GetMethod(nameof(CodeMatcher.Insert), [typeof(CodeInstruction[])])!,
            typeof(CodeMatcher).GetMethod(nameof(CodeMatcher.Insert), [typeof(IEnumerable<CodeInstruction>)])!,
            typeof(CodeMatcher).GetMethod(nameof(CodeMatcher.InsertBranch), [typeof(OpCode), typeof(int)])!
        })
        {
            harmony.Patch(
                original: method,
                transpiler: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(AllowLegacyCodeMatcherAppendAtEnd))
            );
        }

        // .NET 10 optimizes closed reference types in Harmony's wrappers more aggressively. Since CoreCLR shares
        // generic code between reference-type instantiations, that can make a wrapper created for (for example)
        // T=string reinterpret arguments passed to the same native method for T=object. Use the canonical object ABI
        // for generic-derived reference parameters on this host, matching the behavior mods previously saw on .NET 6.
        if (OperatingSystem.IsLinux() && Environment.Version.Major >= 10)
        {
            harmony.Patch(
                original: typeof(Harmony).Assembly
                    .GetType("HarmonyLib.MethodPatcherTools", throwOnError: true)!
                    .GetMethod("CreateDynamicMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                postfix: new HarmonyMethod(typeof(MiniMonoModHotfix), nameof(CanonicalizeLinuxNet10GenericPatchSignature))
            );
        }

    }

    /// <summary>Use canonical reference types in a generated wrapper when its signature comes from a generic substitution.</summary>
    /// <param name="original">The constructed method Harmony is wrapping.</param>
    /// <param name="__result">The generated wrapper definition.</param>
    internal static void CanonicalizeLinuxNet10GenericPatchSignature(MethodBase original, DynamicMethodDefinition __result)
    {
        MethodBase openMethod = MiniMonoModHotfix.GetOpenGenericMethod(original);
        ParameterInfo[] openParameters = openMethod.GetParameters();
        ParameterInfo[] closedParameters = original.GetParameters();
        int offset = original.IsStatic ? 0 : 1;

        for (int i = 0; i < openParameters.Length; i++)
        {
            if (!openParameters[i].ParameterType.ContainsGenericParameters)
                continue;

            Type? canonicalType = MiniMonoModHotfix.GetCanonicalReferenceType(closedParameters[i].ParameterType);
            if (canonicalType is not null)
                __result.Definition.Parameters[i + offset].ParameterType = __result.Module.ImportReference(canonicalType);
        }

        if (openMethod is MethodInfo openMethodInfo
            && original is MethodInfo closedMethodInfo
            && openMethodInfo.ReturnType.ContainsGenericParameters)
        {
            Type? canonicalType = MiniMonoModHotfix.GetCanonicalReferenceType(closedMethodInfo.ReturnType);
            if (canonicalType is not null)
                __result.Definition.ReturnType = __result.Module.ImportReference(canonicalType);
        }
    }

    /// <summary>Get the open declaration which shows which signature types came from generic substitutions.</summary>
    private static MethodBase GetOpenGenericMethod(MethodBase method)
    {
        MethodBase openMethod = method;
        if (method.DeclaringType?.IsConstructedGenericType is true)
        {
            Type openType = method.DeclaringType.GetGenericTypeDefinition();
            openMethod = openType
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .OfType<MethodBase>()
                .Single(candidate => candidate.MetadataToken == method.MetadataToken);
        }

        if (openMethod is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } methodInfo)
            openMethod = methodInfo.GetGenericMethodDefinition();

        return openMethod;
    }

    /// <summary>Get the shared generic ABI type for a constructed reference type, if it can be canonicalized safely.</summary>
    private static Type? GetCanonicalReferenceType(Type type)
    {
        if (type.IsByRef)
        {
            Type elementType = type.GetElementType()!;
            return !elementType.IsValueType && !elementType.IsPointer
                ? typeof(object).MakeByRefType()
                : null;
        }

        return !type.IsValueType && !type.IsPointer
            ? typeof(object)
            : null;
    }

    private static IEnumerable<CodeInstruction> AllowLegacyCodeMatcherAppendAtEnd(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getIsInvalid = AccessTools.PropertyGetter(typeof(CodeMatcher), nameof(CodeMatcher.IsInvalid));
        MethodInfo isInvalidInsertPosition = AccessTools.Method(typeof(MiniMonoModHotfix), nameof(IsInvalidInsertPosition));
        bool found = false;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(getIsInvalid))
            {
                if (found)
                    throw new InvalidOperationException("Found multiple CodeMatcher.IsInvalid checks in a legacy-compatible insertion method.");

                instruction.opcode = OpCodes.Call;
                instruction.operand = isInvalidInsertPosition;
                found = true;
            }

            yield return instruction;
        }

        if (!found)
            throw new InvalidOperationException("Couldn't find the CodeMatcher.IsInvalid check in a legacy-compatible insertion method.");
    }

    private static bool IsInvalidInsertPosition(CodeMatcher matcher)
    {
        return matcher.Pos < 0 || matcher.Pos > matcher.Length;
    }

    private static IEnumerable<CodeInstruction> ResolveTokenFix(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getRealDeclaringType = typeof(MiniMonoModHotfix).GetMethod(nameof(MiniMonoModHotfix.GetRealDeclaringType)) ?? throw new InvalidOperationException($"Can't get required method {nameof(MiniMonoModHotfix)}.{nameof(GetRealDeclaringType)}");
        MethodInfo fixReflectionCache = typeof(MiniMonoModHotfix).GetMethod(nameof(MiniMonoModHotfix.FixReflectionCache)) ?? throw new InvalidOperationException($"Can't get required method {nameof(MiniMonoModHotfix)}.{nameof(FixReflectionCache)}");

        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;

            if (instruction.operand is MethodInfo called)
            {
                switch (called.Name)
                {
                    case "ResolveType":
                        // type.FixReflectionCache();
                        yield return new CodeInstruction(OpCodes.Dup);
                        yield return new CodeInstruction(OpCodes.Call, fixReflectionCache);
                        break;

                    case "ResolveMember":
                    case "ResolveMethod":
                    case "ResolveField":
                        // member.GetRealDeclaringType().FixReflectionCache();
                        yield return new CodeInstruction(OpCodes.Dup);
                        yield return new CodeInstruction(OpCodes.Call, getRealDeclaringType);
                        yield return new CodeInstruction(OpCodes.Call, fixReflectionCache);
                        break;
                }
            }
        }
    }

    extension(MemberInfo member)
    {
        public Type? GetRealDeclaringType()
        {
            return member.DeclaringType ?? member.Module.GetModuleType();
        }
    }

    extension(Type? type)
    {
        public void FixReflectionCache()
        {
            if (t_RuntimeType == null || p_RuntimeType_Cache == null || m_RuntimeTypeCache_GetFieldList == null || m_RuntimeTypeCache_GetPropertyList == null)
                return;

            for (; type != null; type = type.DeclaringType)
            {
                // All types SHOULD inherit RuntimeType, including those built at runtime.
                // One might never know what awaits us in the depths of reflection hell though.
                if (!t_RuntimeType.IsInstanceOfType(type))
                    continue;

                CacheFixEntry entry = _CacheFixed.GetValue(type, rt =>
                {
                    // All RuntimeTypes MUST have a cache, the getter is non-virtual, it creates on demand and asserts non-null.
                    object cache = MiniMonoModHotfix.p_RuntimeType_Cache.GetValue(rt, MiniMonoModHotfix._NoArgs)!;
                    Array properties = MiniMonoModHotfix._GetArray(cache, MiniMonoModHotfix.m_RuntimeTypeCache_GetPropertyList);
                    Array fields = MiniMonoModHotfix._GetArray(cache, MiniMonoModHotfix.m_RuntimeTypeCache_GetFieldList);

                    _FixReflectionCacheOrder<PropertyInfo>(properties);
                    _FixReflectionCacheOrder<FieldInfo>(fields);

                    return new CacheFixEntry(cache, properties, fields, needsVerify: false);
                });

                if (entry.NeedsVerify && !_Verify(entry, type))
                {
                    lock (entry)
                    {
                        _FixReflectionCacheOrder<PropertyInfo>(entry.Properties);
                        _FixReflectionCacheOrder<FieldInfo>(entry.Fields);
                    }
                }

                entry.NeedsVerify = true;
            }
        }
    }

    private static bool _Verify(CacheFixEntry entry, Type type)
    {
        // The cache can sometimes be invalidated.
        // TODO: Figure out if only the arrays get replaced or if the entire cache object gets replaced!
        object cache = p_RuntimeType_Cache!.GetValue(type, _NoArgs)!;
        if (entry.Cache != cache)
        {
            entry.Cache = cache;
            entry.Properties = _GetArray(cache, m_RuntimeTypeCache_GetPropertyList!);
            entry.Fields = _GetArray(cache, m_RuntimeTypeCache_GetFieldList!);
            return false;

        }

        Array properties = _GetArray(cache, m_RuntimeTypeCache_GetPropertyList!);
        if (entry.Properties != properties)
        {
            entry.Properties = properties;
            entry.Fields = _GetArray(cache, m_RuntimeTypeCache_GetFieldList!);
            return false;
        }

        Array fields = _GetArray(cache, m_RuntimeTypeCache_GetFieldList!);
        if (entry.Fields != fields)
        {
            entry.Fields = fields;
            return false;

        }

        // Cache should still be the same, no re-fix necessary.
        return true;
    }

    private static Array _GetArray(object cache, MethodInfo getter)
    {
        // Get and discard once, otherwise we might not be getting the actual backing array.
        getter.Invoke(cache, _CacheGetterArgs);
        return (Array)getter.Invoke(cache, _CacheGetterArgs)!;
    }

    private static void _FixReflectionCacheOrder<T>(Array orig) where T : MemberInfo
    {
        // Sort using a short-lived list.
        List<T> list = new(orig.Length);
        for (int i = 0; i < orig.Length; i++)
            list.Add((T)orig.GetValue(i)!);

        list.Sort((a, b) => a.MetadataToken - b.MetadataToken);

        for (int i = orig.Length - 1; i >= 0; --i)
            orig.SetValue(list[i], i);
    }

    private class CacheFixEntry
    {
        public object? Cache;
        public Array Properties;
        public Array Fields;
        public bool NeedsVerify;

        public CacheFixEntry(object? cache, Array properties, Array fields, bool needsVerify)
        {
            this.Cache = cache;
            this.Properties = properties;
            this.Fields = fields;
            this.NeedsVerify = needsVerify;
        }
    }
}
