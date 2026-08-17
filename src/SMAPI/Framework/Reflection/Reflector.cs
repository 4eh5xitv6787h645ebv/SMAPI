using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using StardewModdingAPI.Framework.Utilities;

namespace StardewModdingAPI.Framework.Reflection;

/// <summary>Provides helper methods for accessing inaccessible code.</summary>
/// <remarks>This implementation searches up the type hierarchy, and caches the reflected fields and methods with a sliding expiry (to optimize performance without unnecessary memory usage).</remarks>
internal class Reflector
{
    /*********
    ** Fields
    *********/
    /// <summary>The cached fields and methods found via reflection.</summary>
    private readonly IntervalMemoryCache<ReflectionCacheKey, MemberInfo?> Cache = new();

    /// <summary>The target-bound wrappers for cached members, indexed weakly so they don't retain game or mod objects.</summary>
    private ConditionalWeakTable<object, Dictionary<ReflectionWrapperCacheKey, object>> WrapperCache = new();


    /*********
    ** Public methods
    *********/
    /****
    ** Fields
    ****/
    /// <summary>Get a instance field.</summary>
    /// <typeparam name="TValue">The field type.</typeparam>
    /// <param name="obj">The object which has the field.</param>
    /// <param name="name">The field name.</param>
    /// <param name="required">Whether to throw an exception if the field isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the field wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the field doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target field doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedField<TValue> GetField<TValue>(object obj, string name, bool required = true)
    {
        // validate
        if (obj == null)
            throw new ArgumentNullException(nameof(obj), "Can't get a instance field from a null object.");

        // get field from hierarchy
        IReflectedField<TValue>? field = this.GetFieldFromHierarchy<TValue>(obj.GetType(), obj, name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (required && field == null)
            throw new InvalidOperationException($"The {obj.GetType().FullName} object doesn't have a '{name}' instance field.");
        return field!;
    }

    /// <summary>Get a static field.</summary>
    /// <typeparam name="TValue">The field type.</typeparam>
    /// <param name="type">The type which has the field.</param>
    /// <param name="name">The field name.</param>
    /// <param name="required">Whether to throw an exception if the field isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the field wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the field doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target field doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedField<TValue> GetField<TValue>(Type type, string name, bool required = true)
    {
        // get field from hierarchy
        IReflectedField<TValue>? field = this.GetFieldFromHierarchy<TValue>(type, null, name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        if (required && field == null)
            throw new InvalidOperationException($"The {type.FullName} object doesn't have a '{name}' static field.");
        return field!;
    }

    /****
    ** Properties
    ****/
    /// <summary>Get a instance property.</summary>
    /// <typeparam name="TValue">The property type.</typeparam>
    /// <param name="obj">The object which has the property.</param>
    /// <param name="name">The property name.</param>
    /// <param name="required">Whether to throw an exception if the property isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the property wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the property doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target property doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedProperty<TValue> GetProperty<TValue>(object obj, string name, bool required = true)
    {
        // validate
        if (obj == null)
            throw new ArgumentNullException(nameof(obj), "Can't get a instance property from a null object.");

        // get property from hierarchy
        IReflectedProperty<TValue>? property = this.GetPropertyFromHierarchy<TValue>(obj.GetType(), obj, name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (required && property == null)
            throw new InvalidOperationException($"The {obj.GetType().FullName} object doesn't have a '{name}' instance property.");
        return property!;
    }

    /// <summary>Get a static property.</summary>
    /// <typeparam name="TValue">The property type.</typeparam>
    /// <param name="type">The type which has the property.</param>
    /// <param name="name">The property name.</param>
    /// <param name="required">Whether to throw an exception if the property isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the property wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the property doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target property doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedProperty<TValue> GetProperty<TValue>(Type type, string name, bool required = true)
    {
        // get field from hierarchy
        IReflectedProperty<TValue>? property = this.GetPropertyFromHierarchy<TValue>(type, null, name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (required && property == null)
            throw new InvalidOperationException($"The {type.FullName} object doesn't have a '{name}' static property.");
        return property!;
    }

    /****
    ** Methods
    ****/
    /// <summary>Get a instance method.</summary>
    /// <param name="obj">The object which has the method.</param>
    /// <param name="name">The method name.</param>
    /// <param name="required">Whether to throw an exception if the method isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the method wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the method doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target method doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedMethod GetMethod(object obj, string name, bool required = true)
    {
        // validate
        if (obj == null)
            throw new ArgumentNullException(nameof(obj), "Can't get a instance method from a null object.");

        // get method from hierarchy
        IReflectedMethod? method = this.GetMethodFromHierarchy(obj.GetType(), obj, name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (required && method == null)
            throw new InvalidOperationException($"The {obj.GetType().FullName} object doesn't have a '{name}' instance method.");
        return method!;
    }

    /// <summary>Get a static method.</summary>
    /// <param name="type">The type which has the method.</param>
    /// <param name="name">The method name.</param>
    /// <param name="required">Whether to throw an exception if the method isn't found. <strong>Due to limitations with nullable reference types, setting this to <c>false</c> will still mark the value non-nullable.</strong></param>
    /// <returns>Returns the method wrapper, or <c>null</c> if <paramref name="required"/> is <c>false</c> and the method doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">The target method doesn't exist, and <paramref name="required"/> is true.</exception>
    public IReflectedMethod GetMethod(Type type, string name, bool required = true)
    {
        // get method from hierarchy
        IReflectedMethod? method = this.GetMethodFromHierarchy(type, null, name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (required && method == null)
            throw new InvalidOperationException($"The {type.FullName} object doesn't have a '{name}' static method.");
        return method!;
    }

    /****
    ** Management
    ****/
    /// <summary>Start a new cache interval, clearing stale reflection lookups.</summary>
    public void NewCacheInterval()
    {
        this.Cache.StartNewInterval();
        this.WrapperCache = new();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get a field from the type hierarchy.</summary>
    /// <typeparam name="TValue">The expected field type.</typeparam>
    /// <param name="type">The type which has the field.</param>
    /// <param name="obj">The object which has the field, or <c>null</c> for a static field.</param>
    /// <param name="name">The field name.</param>
    /// <param name="bindingFlags">The reflection binding which flags which indicates what type of field to find.</param>
    private IReflectedField<TValue>? GetFieldFromHierarchy<TValue>(Type type, object? obj, string name, BindingFlags bindingFlags)
    {
        bool isStatic = (bindingFlags & BindingFlags.Static) != 0;
        FieldInfo? field = this.GetCached<FieldInfo>(ReflectionMemberType.Field, type, name, isStatic);

        if (field == null)
            return null;

        object target = obj ?? type;
        ReflectionWrapperCacheKey wrapperKey = new(field, typeof(TValue));
        Dictionary<ReflectionWrapperCacheKey, object> wrappers = this.WrapperCache.GetOrCreateValue(target);
        if (!wrappers.TryGetValue(wrapperKey, out object? wrapper))
        {
            wrapper = new ReflectedField<TValue>(field.DeclaringType ?? type, obj, field, isStatic);
            wrappers[wrapperKey] = wrapper;
        }
        return (IReflectedField<TValue>)wrapper;
    }

    /// <summary>Get a property from the type hierarchy.</summary>
    /// <typeparam name="TValue">The expected property type.</typeparam>
    /// <param name="type">The type which has the property.</param>
    /// <param name="obj">The object which has the property, or <c>null</c> for a static property.</param>
    /// <param name="name">The property name.</param>
    /// <param name="bindingFlags">The reflection binding which flags which indicates what type of property to find.</param>
    private IReflectedProperty<TValue>? GetPropertyFromHierarchy<TValue>(Type type, object? obj, string name, BindingFlags bindingFlags)
    {
        bool isStatic = (bindingFlags & BindingFlags.Static) != 0;
        PropertyInfo? property = this.GetCached<PropertyInfo>(ReflectionMemberType.Property, type, name, isStatic);

        if (property == null)
            return null;

        object target = obj ?? type;
        ReflectionWrapperCacheKey wrapperKey = new(property, typeof(TValue));
        Dictionary<ReflectionWrapperCacheKey, object> wrappers = this.WrapperCache.GetOrCreateValue(target);
        if (!wrappers.TryGetValue(wrapperKey, out object? wrapper))
        {
            wrapper = new ReflectedProperty<TValue>(property.DeclaringType ?? type, obj, property, isStatic);
            wrappers[wrapperKey] = wrapper;
        }
        return (IReflectedProperty<TValue>)wrapper;
    }

    /// <summary>Get a method from the type hierarchy.</summary>
    /// <param name="type">The type which has the method.</param>
    /// <param name="obj">The object which has the method, or <c>null</c> for a static method.</param>
    /// <param name="name">The method name.</param>
    /// <param name="bindingFlags">The reflection binding which flags which indicates what type of method to find.</param>
    private IReflectedMethod? GetMethodFromHierarchy(Type type, object? obj, string name, BindingFlags bindingFlags)
    {
        bool isStatic = (bindingFlags & BindingFlags.Static) != 0;
        MethodInfo? method = this.GetCached<MethodInfo>(ReflectionMemberType.Method, type, name, isStatic);

        if (method == null)
            return null;

        object target = obj ?? type;
        ReflectionWrapperCacheKey wrapperKey = new(method, null);
        Dictionary<ReflectionWrapperCacheKey, object> wrappers = this.WrapperCache.GetOrCreateValue(target);
        if (!wrappers.TryGetValue(wrapperKey, out object? wrapper))
        {
            wrapper = new ReflectedMethod(method.DeclaringType ?? type, obj, method, isStatic: isStatic);
            wrappers[wrapperKey] = wrapper;
        }
        return (IReflectedMethod)wrapper;
    }

    /// <summary>Get a method or field through the cache.</summary>
    /// <typeparam name="TMemberInfo">The expected <see cref="MemberInfo"/> type.</typeparam>
    /// <param name="memberType">The type of member to find.</param>
    /// <param name="type">The type whose members are being reflected.</param>
    /// <param name="memberName">The member name.</param>
    /// <param name="isStatic">Whether the member is static.</param>
    private TMemberInfo? GetCached<TMemberInfo>(ReflectionMemberType memberType, Type type, string memberName, bool isStatic)
        where TMemberInfo : MemberInfo
    {
        ReflectionCacheKey key = new(memberType, type, memberName, isStatic);
        return (TMemberInfo?)this.Cache.GetOrSet(
            key,
            key,
            static lookup =>
            {
                BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Public | (lookup.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
                for (Type? curType = lookup.Type; curType != null; curType = curType.BaseType)
                {
                    MemberInfo? member = lookup.MemberType switch
                    {
                        ReflectionMemberType.Field => curType.GetField(lookup.MemberName, bindingFlags),
                        ReflectionMemberType.Property => curType.GetProperty(lookup.MemberName, bindingFlags),
                        ReflectionMemberType.Method => curType.GetMethod(lookup.MemberName, bindingFlags),
                        _ => throw new InvalidOperationException($"Unknown reflection member type '{lookup.MemberType}'.")
                    };
                    if (member != null)
                        return member;
                }

                return null;
            }
        );
    }

    /// <summary>A type of reflected member.</summary>
    private enum ReflectionMemberType
    {
        Field,
        Property,
        Method
    }

    /// <summary>A cache key for a reflected member lookup.</summary>
    private readonly record struct ReflectionCacheKey(ReflectionMemberType MemberType, Type Type, string MemberName, bool IsStatic);

    /// <summary>A cache key for a target-bound reflected member wrapper.</summary>
    /// <param name="Member">The reflected member.</param>
    /// <param name="ValueType">The requested field or property value type, or <c>null</c> for a method.</param>
    private readonly record struct ReflectionWrapperCacheKey(MemberInfo Member, Type? ValueType);
}
