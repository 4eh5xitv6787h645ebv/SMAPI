using System;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Xna.Framework.Graphics;

namespace StardewModdingAPI.Framework.Extensions;

/// <summary>Provides internal extensions for <see cref="SpriteBatch"/>.</summary>
internal static class SpriteBatchExtensions
{
    /// <summary>Get the private <c>SpriteBatch._beginCalled</c> field without allocating or boxing its value.</summary>
    private static readonly Func<SpriteBatch, bool> GetIsOpen = SpriteBatchExtensions.CreateIsOpenAccessor();

    /// <param name="spriteBatch">The sprite batch to extend.</param>
    extension(SpriteBatch spriteBatch)
    {
        /// <summary>Get whether the sprite batch is between a begin and end pair.</summary>
        public bool IsOpen()
        {
            return SpriteBatchExtensions.GetIsOpen(spriteBatch);
        }
    }

    /// <summary>Create a typed accessor for MonoGame's private sprite-batch state field.</summary>
    private static Func<SpriteBatch, bool> CreateIsOpenAccessor()
    {
        FieldInfo field = typeof(SpriteBatch).GetField("_beginCalled", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SpriteBatch).FullName, "_beginCalled");
        if (field.FieldType != typeof(bool))
            throw new InvalidOperationException($"Expected {typeof(SpriteBatch).FullName}._beginCalled to be a Boolean field, but found {field.FieldType.FullName}.");

        DynamicMethod method = new(
            name: "SMAPI_GetSpriteBatchBeginCalled",
            returnType: typeof(bool),
            parameterTypes: [typeof(SpriteBatch)],
            owner: typeof(SpriteBatchExtensions),
            skipVisibility: true
        );
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<SpriteBatch, bool>>();
    }
}
