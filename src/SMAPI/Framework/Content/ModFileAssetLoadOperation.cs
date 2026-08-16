using StardewModdingAPI.Events;

namespace StardewModdingAPI.Framework.Content;

/// <summary>An asset load operation which reads a file from the requesting mod's folder.</summary>
/// <typeparam name="TAsset">The asset type to load.</typeparam>
internal sealed class ModFileAssetLoadOperation<TAsset> : AssetLoadOperation
    where TAsset : notnull
{
    /// <summary>The path relative to the mod folder.</summary>
    private readonly string RelativePath;

    /// <summary>Construct an instance.</summary>
    public ModFileAssetLoadOperation(IModMetadata mod, AssetLoadPriority priority, string relativePath)
        : base(mod, onBehalfOf: null, priority)
    {
        this.RelativePath = relativePath;
    }

    /// <inheritdoc />
    public override object GetData()
    {
        return this.Mod.Mod!.Helper.ModContent.Load<TAsset>(this.RelativePath);
    }
}
