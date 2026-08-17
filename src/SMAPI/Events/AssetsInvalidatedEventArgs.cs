using System;
using System.Collections.Generic;
using System.Threading;

namespace StardewModdingAPI.Events;

/// <summary>Event arguments for an <see cref="IContentEvents.AssetsInvalidated"/> event.</summary>
public class AssetsInvalidatedEventArgs : EventArgs
{
    /*********
    ** Fields
    *********/
    /// <summary>The lazily created asset names without locale codes.</summary>
    private IReadOnlySet<IAssetName>? NamesWithoutLocaleImpl;


    /*********
    ** Accessors
    *********/
    /// <summary>The asset names that were invalidated.</summary>
    public IReadOnlySet<IAssetName> Names { get; }

    /// <summary>The <see cref="Names"/> with any locale codes stripped.</summary>
    /// <remarks>For example, if <see cref="Names"/> contains a locale like <c>Data/Bundles.fr-FR</c>, this will have the name without locale like <c>Data/Bundles</c>. If the name has no locale, this field is equivalent.</remarks>
    public IReadOnlySet<IAssetName> NamesWithoutLocale
    {
        get
        {
            IReadOnlySet<IAssetName>? namesWithoutLocale = Volatile.Read(ref this.NamesWithoutLocaleImpl);
            if (namesWithoutLocale is not null)
                return namesWithoutLocale;

            IReadOnlySet<IAssetName> created = this.CreateNamesWithoutLocale();
            return Interlocked.CompareExchange(ref this.NamesWithoutLocaleImpl, created, null) ?? created;
        }
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="assetNames">The asset names that were invalidated.</param>
    internal AssetsInvalidatedEventArgs(ICollection<IAssetName> assetNames)
    {
        this.Names = new HashSet<IAssetName>(assetNames);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Create the asset-name set without locale codes, reusing <see cref="Names"/> if no names are localized.</summary>
    private IReadOnlySet<IAssetName> CreateNamesWithoutLocale()
    {
        bool hasLocale = false;
        foreach (IAssetName name in this.Names)
        {
            if (name.LocaleCode is not null)
            {
                hasLocale = true;
                break;
            }
        }
        if (!hasLocale)
            return this.Names;

        HashSet<IAssetName> namesWithoutLocale = [];
        foreach (IAssetName name in this.Names)
            namesWithoutLocale.Add(name.GetBaseAssetName());
        return namesWithoutLocale;
    }
}
