using System;
using StardewModdingAPI.Events;

namespace StardewModdingAPI.Framework.Content;

/// <summary>An asset load operation backed by a caller-provided delegate.</summary>
internal sealed class DelegateAssetLoadOperation : AssetLoadOperation
{
    /// <summary>Load the initial value for the asset.</summary>
    private readonly Func<object> Load;

    /// <summary>Construct an instance.</summary>
    public DelegateAssetLoadOperation(IModMetadata mod, IModMetadata? onBehalfOf, AssetLoadPriority priority, Func<object> load)
        : base(mod, onBehalfOf, priority)
    {
        this.Load = load;
    }

    /// <inheritdoc />
    public override object GetData()
    {
        return this.Load();
    }
}
