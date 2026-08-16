using StardewModdingAPI.Events;

namespace StardewModdingAPI.Framework.Content;

/// <summary>An operation which provides the initial instance of an asset when it's requested from the content pipeline.</summary>
internal abstract class AssetLoadOperation
{
    /// <summary>The mod applying the load.</summary>
    public IModMetadata Mod { get; }

    /// <summary>The content pack on whose behalf the asset is being loaded, if any.</summary>
    public IModMetadata? OnBehalfOf { get; }

    /// <summary>If there are multiple loads that apply to the same asset, the priority with which this one should be applied.</summary>
    public AssetLoadPriority Priority { get; }

    /// <summary>Construct an instance.</summary>
    /// <param name="mod">The mod applying the load.</param>
    /// <param name="onBehalfOf">The content pack on whose behalf the asset is being loaded, if any.</param>
    /// <param name="priority">If there are multiple loads that apply to the same asset, the priority with which this one should be applied.</param>
    protected AssetLoadOperation(IModMetadata mod, IModMetadata? onBehalfOf, AssetLoadPriority priority)
    {
        this.Mod = mod;
        this.OnBehalfOf = onBehalfOf;
        this.Priority = priority;
    }

    /// <summary>Load the initial value for the asset.</summary>
    public abstract object GetData();
}
