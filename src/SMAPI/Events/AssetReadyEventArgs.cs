using System;
using System.Threading;

namespace StardewModdingAPI.Events;

/// <summary>Event arguments for an <see cref="IContentEvents.AssetReady"/> event.</summary>
public class AssetReadyEventArgs : EventArgs
{
    /*********
    ** Fields
    *********/
    /// <summary>The lazily created name without a locale code.</summary>
    private IAssetName? NameWithoutLocaleImpl;


    /*********
    ** Accessors
    *********/
    /// <summary>The name of the asset being requested.</summary>
    public IAssetName Name { get; }

    /// <summary>The <see cref="Name"/> with any locale codes stripped.</summary>
    /// <remarks>For example, if <see cref="Name"/> contains a locale like <c>Data/Bundles.fr-FR</c>, this will be the name without locale like <c>Data/Bundles</c>. If the name has no locale, this field is equivalent.</remarks>
    public IAssetName NameWithoutLocale
    {
        get
        {
            IAssetName? nameWithoutLocale = Volatile.Read(ref this.NameWithoutLocaleImpl);
            if (nameWithoutLocale is not null)
                return nameWithoutLocale;

            IAssetName created = this.Name.GetBaseAssetName();
            return Interlocked.CompareExchange(ref this.NameWithoutLocaleImpl, created, null) ?? created;
        }
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="name">The name of the asset being requested.</param>
    internal AssetReadyEventArgs(IAssetName name)
    {
        this.Name = name;
    }
}
