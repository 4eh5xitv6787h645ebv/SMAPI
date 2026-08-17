using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Framework.Extensions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Metadata;
using StardewModdingAPI.Toolkit.Serialization;
using StardewModdingAPI.Toolkit.Utilities.PathLookups;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData;
using StardewValley.Locations;
using xTile;

namespace StardewModdingAPI.Framework;

/// <summary>The central logic for creating content managers, invalidating caches, and propagating asset changes.</summary>
internal class ContentCoordinator : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>An asset key prefix for assets from SMAPI mod folders.</summary>
    private const string ManagedPrefix = "SMAPI";

    /// <summary>Get a file lookup for the given directory.</summary>
    private readonly Func<string, IFileLookup> GetFileLookup;

    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>Provides metadata for core game assets.</summary>
    private readonly CoreAssetPropagator CoreAssets;

    /// <summary>Simplifies access to private code.</summary>
    private readonly Reflector Reflection;

    /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
    private readonly JsonHelper JsonHelper;

    /// <summary>A callback to invoke the first time *any* game content manager loads an asset.</summary>
    private readonly Action OnLoadingFirstAsset;

    /// <summary>A callback to invoke when an asset is fully loaded.</summary>
    private readonly Action<BaseContentManager, IAssetName> OnAssetLoaded;

    /// <summary>A callback to invoke when any asset names have been invalidated from the cache.</summary>
    private readonly Action<ICollection<IAssetName>> OnAssetsInvalidated;

    /// <summary>Get the load/edit operations to apply to an asset by querying registered <see cref="IContentEvents.AssetRequested"/> event handlers.</summary>
    private readonly Func<IAssetInfo, AssetOperationGroup?> RequestAssetOperations;

    /// <summary>The loaded content managers (including the <see cref="MainContentManager"/>).</summary>
    private readonly List<IContentManager> ContentManagers = [];

    /// <summary>The loaded game content managers which can cache intercepted game assets.</summary>
    private readonly List<IContentManager> GameContentManagers = [];

    /// <summary>The first registered namespaced content manager for each managed asset prefix.</summary>
    private readonly Dictionary<string, IContentManager> NamespacedContentManagers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the content coordinator has been disposed.</summary>
    private bool IsDisposed;

    /// <summary>A lock used to prevent asynchronous changes to the content manager list.</summary>
    /// <remarks>The game may add content managers in asynchronous threads (e.g. when populating the load screen).</remarks>
    private readonly ReaderWriterLockSlim ContentManagerLock = new();

    /// <summary>A cache of ordered tilesheet IDs used by vanilla maps.</summary>
    private readonly Dictionary<string, TilesheetReference[]?> VanillaTilesheets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An unmodified content manager which doesn't intercept assets, used to compare asset data.</summary>
    private readonly LocalizedContentManager VanillaContentManager;

    /// <summary>The language enum values indexed by locale code.</summary>
    private Lazy<Dictionary<string, LocalizedContentManager.LanguageCode>> LocaleCodes;

    /// <summary>Parse a locale suffix in an asset name.</summary>
    private readonly Func<string, LocalizedContentManager.LanguageCode?> ParseLocale;

    /// <summary>The bounded cache of parsed immutable asset names.</summary>
    private readonly ParsedAssetNameCache ParsedAssetNames;

    /// <summary>A shared byte-bounded cache of decoded mod image pixels.</summary>
    private readonly DecodedTextureCache DecodedTextures = new();

    /// <summary>The cached asset load/edit operations to apply, indexed by asset name.</summary>
    private readonly TickCacheDictionary<AssetOperationCacheKey, AssetOperationGroup?> AssetOperationsByKey = new();


    /*********
    ** Accessors
    *********/
    /// <summary>The primary content manager used for most assets.</summary>
    public GameContentManager MainContentManager { get; private set; }

    /// <summary>The current language as a constant.</summary>
    public LocalizedContentManager.LanguageCode Language => this.MainContentManager.Language;

    /// <summary>The absolute path to the <see cref="ContentManager.RootDirectory"/>.</summary>
    public string FullRootDirectory { get; }

    /// <summary>A lookup which tracks whether each given asset name has a localized form.</summary>
    /// <remarks>This is a per-screen equivalent to the base game's <see cref="LocalizedContentManager.localizedAssetNames"/> field, since mods may provide different assets per-screen.</remarks>
    public PerScreen<Dictionary<string, string>> LocalizedAssetNames { get; } = new(static () => new(StringComparer.OrdinalIgnoreCase));


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="serviceProvider">The service provider to use to locate services.</param>
    /// <param name="rootDirectory">The root directory to search for content.</param>
    /// <param name="currentCulture">The current culture for which to localize content.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="multiplayer">The multiplayer instance whose map cache to update during asset propagation.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
    /// <param name="onLoadingFirstAsset">A callback to invoke the first time *any* game content manager loads an asset.</param>
    /// <param name="onAssetLoaded">A callback to invoke when an asset is fully loaded.</param>
    /// <param name="getFileLookup">Get a file lookup for the given directory.</param>
    /// <param name="onAssetsInvalidated">A callback to invoke when any asset names have been invalidated from the cache.</param>
    /// <param name="requestAssetOperations">Get the load/edit operations to apply to an asset by querying registered <see cref="IContentEvents.AssetRequested"/> event handlers.</param>
    public ContentCoordinator(IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, IMonitor monitor, Multiplayer multiplayer, Reflector reflection, JsonHelper jsonHelper, Action onLoadingFirstAsset, Action<BaseContentManager, IAssetName> onAssetLoaded, Func<string, IFileLookup> getFileLookup, Action<ICollection<IAssetName>> onAssetsInvalidated, Func<IAssetInfo, AssetOperationGroup?> requestAssetOperations)
    {
        this.GetFileLookup = getFileLookup;
        this.Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.Reflection = reflection;
        this.JsonHelper = jsonHelper;
        this.OnLoadingFirstAsset = onLoadingFirstAsset;
        this.OnAssetLoaded = onAssetLoaded;
        this.OnAssetsInvalidated = onAssetsInvalidated;
        this.RequestAssetOperations = requestAssetOperations;
        this.FullRootDirectory = Path.Combine(Constants.GamePath, rootDirectory);
        this.LocaleCodes = new Lazy<Dictionary<string, LocalizedContentManager.LanguageCode>>(() => this.GetLocaleCodes(customLanguages: []));
        this.ParseLocale = this.TryParseLocale;
        this.ParsedAssetNames = new ParsedAssetNameCache(this.ParseLocale);
        this.AddContentManager(
            this.MainContentManager = new GameContentManager(
                name: "Game1.content",
                serviceProvider: serviceProvider,
                rootDirectory: rootDirectory,
                currentCulture: currentCulture,
                coordinator: this,
                monitor: monitor,
                reflection: reflection,
                onDisposing: this.OnDisposing,
                onLoadingFirstAsset: onLoadingFirstAsset,
                onAssetLoaded: onAssetLoaded
            )
        );

        var contentManagerForAssetPropagation = new GameContentManagerForAssetPropagation(
            name: nameof(GameContentManagerForAssetPropagation),
            serviceProvider: serviceProvider,
            rootDirectory: rootDirectory,
            currentCulture: currentCulture,
            coordinator: this,
            monitor: monitor,
            reflection: reflection,
            onDisposing: this.OnDisposing,
            onLoadingFirstAsset: onLoadingFirstAsset,
            onAssetLoaded: onAssetLoaded
        );
        this.AddContentManager(contentManagerForAssetPropagation);

        this.VanillaContentManager = new LocalizedContentManager(serviceProvider, rootDirectory);
        this.CoreAssets = new CoreAssetPropagator(this.MainContentManager, contentManagerForAssetPropagation, this.Monitor, multiplayer, reflection, name => this.ParseAssetName(name, allowLocales: true));
    }

    /// <summary>Get a new content manager which handles reading files from the game content folder with support for interception.</summary>
    /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
    public GameContentManager CreateGameContentManager(string name)
    {
        return this.ContentManagerLock.InWriteLock((Coordinator: this, Name: name), static state =>
        {
            ContentCoordinator coordinator = state.Coordinator;
            GameContentManager manager = new(
                name: state.Name,
                serviceProvider: coordinator.MainContentManager.ServiceProvider,
                rootDirectory: coordinator.MainContentManager.RootDirectory,
                currentCulture: coordinator.MainContentManager.CurrentCulture,
                coordinator: coordinator,
                monitor: coordinator.Monitor,
                reflection: coordinator.Reflection,
                onDisposing: coordinator.OnDisposing,
                onLoadingFirstAsset: coordinator.OnLoadingFirstAsset,
                onAssetLoaded: coordinator.OnAssetLoaded
            );
            coordinator.AddContentManager(manager);
            return manager;
        });
    }

    /// <summary>Get a new content manager which handles reading files from a SMAPI mod folder with support for unpacked files.</summary>
    /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
    /// <param name="modName">The mod display name to show in errors.</param>
    /// <param name="rootDirectory">The root directory to search for content (or <c>null</c> for the default).</param>
    /// <param name="gameContentManager">The game content manager used for map tilesheets not provided by the mod.</param>
    public ModContentManager CreateModContentManager(string name, string modName, string rootDirectory, IContentManager gameContentManager)
    {
        return this.ContentManagerLock.InWriteLock(
            (Coordinator: this, Name: name, ModName: modName, RootDirectory: rootDirectory, GameContentManager: gameContentManager),
            static state =>
            {
                ContentCoordinator coordinator = state.Coordinator;
                ModContentManager manager = new(
                    name: state.Name,
                    gameContentManager: state.GameContentManager,
                    serviceProvider: coordinator.MainContentManager.ServiceProvider,
                    rootDirectory: state.RootDirectory,
                    modName: state.ModName,
                    currentCulture: coordinator.MainContentManager.CurrentCulture,
                    coordinator: coordinator,
                    monitor: coordinator.Monitor,
                    reflection: coordinator.Reflection,
                    jsonHelper: coordinator.JsonHelper,
                    onDisposing: coordinator.OnDisposing,
                    fileLookup: coordinator.GetFileLookup(state.RootDirectory),
                    decodedTextures: coordinator.DecodedTextures
                );
                coordinator.AddContentManager(manager);
                return manager;
            }
        );
    }

    /// <summary>Get the current content locale.</summary>
    public string GetLocale()
    {
        return this.MainContentManager.GetLocale(LocalizedContentManager.CurrentLanguageCode);
    }

    /// <summary>Perform any updates needed when the game loads custom languages from <c>Data/AdditionalLanguages</c>.</summary>
    public void OnAdditionalLanguagesInitialized()
    {
        // update locale cache for custom languages, and load it now (since languages added later won't work)
        var customLanguages = DataLoader.AdditionalLanguages(this.MainContentManager);
        this.LocaleCodes = new Lazy<Dictionary<string, LocalizedContentManager.LanguageCode>>(() => this.GetLocaleCodes(customLanguages));
        _ = this.LocaleCodes.Value;
        this.ParsedAssetNames.Clear();
    }

    /// <summary>Perform any updates needed when the locale changes.</summary>
    public void OnLocaleChanged()
    {
        // reset baseline cache
        this.ContentManagerLock.InReadLock(this.VanillaContentManager, static contentManager => contentManager.Unload());

        // forget localized flags (to match the logic in Game1.TranslateFields, which is called on language change)
        this.LocalizedAssetNames.Value.Clear();
    }

    /// <summary>Clean up when the player is returning to the title screen.</summary>
    /// <remarks>This is called after the player returns to the title screen, but before <see cref="Game1.CleanupReturningToTitle"/> runs.</remarks>
    public void OnReturningToTitleScreen()
    {
        // The game clears LocalizedContentManager.localizedAssetNames after returning to the title screen. That
        // causes an inconsistency in the SMAPI asset cache, which leads to an edge case where assets already
        // provided by mods via a load operation when playing in non-English are ignored.
        //
        // For example, let's say a mod provides the 'Data\mail' asset via a load operation when playing in
        // Portuguese. Here's the normal load process after it's loaded:
        //   1. The game requests Data\mail.
        //   2. SMAPI sees that it's already cached, and calls LoadRaw to bypass asset interception.
        //   3. LoadRaw sees that there's a localized key mapping, and gets the mapped key.
        //   4. In this case "Data\mail" is mapped to "Data\mail" since it was loaded by a mod, so it loads that
        //      asset.
        //
        // When the game clears localizedAssetNames, that process goes wrong in step 4:
        //  3. LoadRaw sees that there's no localized key mapping *and* the locale is non-English, so it attempts
        //     to load from the localized key format.
        //  4. In this case that's 'Data\mail.pt-BR', so it successfully loads that asset.
        //  5. Since we've bypassed asset interception at this point, it's loaded directly from the base content
        //     manager without mod changes.
        //
        // To avoid issues, we just remove affected assets from the cache here so they'll be reloaded normally.
        // Note that we *must* propagate changes here, otherwise when mods invalidate the cache later to reapply
        // their changes, the assets won't be found in the cache so no changes will be propagated.
        if (LocalizedContentManager.CurrentLanguageCode != LocalizedContentManager.LanguageCode.en)
            this.InvalidateCache((contentManager, _, _) => contentManager is GameContentManager);

        // clear the localized assets lookup (to match the logic in Game1.CleanupReturningToTitle)
        foreach ((_, Dictionary<string, string> localizedAssets) in this.LocalizedAssetNames.GetActiveValues())
            localizedAssets.Clear();
    }

    /// <summary>Perform any updates needed after the player returns to the title screen.</summary>
    public void OnReturnedToTitleScreen()
    {
        this.CoreAssets.ClearWorldCache();
    }

    /// <summary>Parse a raw asset name.</summary>
    /// <param name="rawName">The raw asset name to parse.</param>
    /// <param name="allowLocales">Whether to parse locales in the <paramref name="rawName"/>. If this is false, any locale codes in the name are treated as if they were part of the base name (e.g. for mod files).</param>
    /// <exception cref="ArgumentException">The <paramref name="rawName"/> is null or empty.</exception>
    public AssetName ParseAssetName(string rawName, bool allowLocales)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            throw new ArgumentException("The asset name can't be null or empty.", nameof(rawName));

        return this.ParsedAssetNames.GetOrAdd(rawName, allowLocales);
    }

    /// <summary>Parse a locale suffix in an asset name.</summary>
    /// <param name="locale">The raw locale code.</param>
    private LocalizedContentManager.LanguageCode? TryParseLocale(string locale)
    {
        return this.LocaleCodes.Value.TryGetValue(locale, out LocalizedContentManager.LanguageCode langCode)
            ? langCode
            : null;
    }

    /// <summary>Get whether this asset is mapped to a mod folder.</summary>
    /// <param name="key">The asset name.</param>
    public bool IsManagedAssetKey(IAssetName key)
    {
        return ContentCoordinator.HasManagedAssetPrefix(key.Name);
    }

    /// <summary>Parse a managed SMAPI asset key which maps to a mod folder.</summary>
    /// <param name="key">The asset key.</param>
    /// <param name="contentManagerId">The unique name for the content manager which should load this asset.</param>
    /// <param name="relativePath">The asset name within the mod folder.</param>
    /// <returns>Returns whether the asset was parsed successfully.</returns>
    public bool TryParseManagedAssetKey(string key, [NotNullWhen(true)] out string? contentManagerId, [NotNullWhen(true)] out IAssetName? relativePath)
    {
        relativePath = null;

        if (!ContentCoordinator.TryParseManagedAssetKeyParts(key, out contentManagerId, out string? rawRelativePath))
            return false;

        relativePath = this.ParseAssetName(rawRelativePath, allowLocales: false);
        return true;
    }

    /// <summary>Parse the manager ID and relative path from a normalized managed asset key.</summary>
    /// <param name="key">The asset key.</param>
    /// <param name="contentManagerId">The platform-normalized content manager ID.</param>
    /// <param name="relativePath">The raw relative asset path.</param>
    internal static bool TryParseManagedAssetKeyParts(string key, [NotNullWhen(true)] out string? contentManagerId, [NotNullWhen(true)] out string? relativePath)
    {
        contentManagerId = null;
        relativePath = null;

        if (!ContentCoordinator.HasManagedAssetPrefix(key))
            return false;

        int modIdStart = ContentCoordinator.ManagedPrefix.Length + 1;
        int relativePathSeparator = -1;
        for (int i = modIdStart; i < key.Length; i++)
        {
            if (key[i] is '/' or '\\')
            {
                relativePathSeparator = i;
                break;
            }
        }

        if (relativePathSeparator <= modIdStart)
            return false;

        bool hasRelativePath = false;
        for (int i = relativePathSeparator + 1; i < key.Length; i++)
        {
            if (key[i] is not ('/' or '\\'))
            {
                hasRelativePath = true;
                break;
            }
        }
        if (!hasRelativePath)
            return false;

        int modIdLength = relativePathSeparator - modIdStart;
        contentManagerId = string.Create(
            ContentCoordinator.ManagedPrefix.Length + 1 + modIdLength,
            (Key: key, ModIdStart: modIdStart, ModIdLength: modIdLength),
            static (result, state) =>
            {
                ContentCoordinator.ManagedPrefix.AsSpan().CopyTo(result);
                result[ContentCoordinator.ManagedPrefix.Length] = Path.DirectorySeparatorChar;
                state.Key.AsSpan(state.ModIdStart, state.ModIdLength).CopyTo(result[(ContentCoordinator.ManagedPrefix.Length + 1)..]);
            }
        );
        relativePath = key[(relativePathSeparator + 1)..];
        return true;
    }

    /// <summary>Get whether an asset key starts with the managed asset path segment.</summary>
    internal static bool HasManagedAssetPrefix(string key)
    {
        return
            key.Length > ContentCoordinator.ManagedPrefix.Length
            && key.StartsWith(ContentCoordinator.ManagedPrefix, StringComparison.OrdinalIgnoreCase)
            && key[ContentCoordinator.ManagedPrefix.Length] is '/' or '\\';
    }

    /// <summary>Get the managed asset key prefix for a mod.</summary>
    /// <param name="modId">The mod's unique ID.</param>
    public string GetManagedAssetPrefix(string modId)
    {
        return Path.Combine(ContentCoordinator.ManagedPrefix, modId.ToLowerInvariant());
    }

    /// <summary>Get whether an asset from a mod folder exists.</summary>
    /// <typeparam name="T">The expected asset type.</typeparam>
    /// <param name="contentManagerId">The unique name for the content manager which should load this asset.</param>
    /// <param name="assetName">The asset name within the mod folder.</param>
    public bool DoesManagedAssetExist<T>(string contentManagerId, IAssetName assetName)
        where T : notnull
    {
        // get content manager
        IContentManager? contentManager = this.ContentManagerLock.InReadLock(
            (Coordinator: this, ContentManagerId: contentManagerId),
            static state => state.Coordinator.NamespacedContentManagers.GetValueOrDefault(state.ContentManagerId)
        );
        if (contentManager == null)
            throw new InvalidOperationException($"The '{contentManagerId}' prefix isn't handled by any mod.");

        // get whether the asset exists
        return contentManager.DoesAssetExist<T>(assetName);
    }

    /// <summary>Get a copy of an asset from a mod folder.</summary>
    /// <typeparam name="T">The asset type.</typeparam>
    /// <param name="contentManagerId">The unique name for the content manager which should load this asset.</param>
    /// <param name="relativePath">The asset name within the mod folder.</param>
    public T LoadManagedAsset<T>(string contentManagerId, IAssetName relativePath)
        where T : notnull
    {
        // get content manager
        IContentManager? contentManager = this.ContentManagerLock.InReadLock(
            (Coordinator: this, ContentManagerId: contentManagerId),
            static state => state.Coordinator.NamespacedContentManagers.GetValueOrDefault(state.ContentManagerId)
        );
        if (contentManager == null)
            throw new InvalidOperationException($"The '{contentManagerId}' prefix isn't handled by any mod.");

        // get fresh asset
        return contentManager.LoadExact<T>(relativePath, useCache: false);
    }

    /// <summary>Purge an exact asset name from the cache.</summary>
    /// <param name="assetName">The asset name to invalidate.</param>
    /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
    /// <returns>Returns the invalidated asset names.</returns>
    public ICollection<IAssetName> InvalidateCache(IAssetName assetName, bool dispose = false)
    {
        if (assetName == null)
            throw new ArgumentException("The asset name list can't contain null values.", "assetNames");

        IAssetName normalizedName = this.ParseAssetName(assetName.Name, allowLocales: true);
        return this.InvalidateExactCache(normalizedName, normalizedNames: null, dispose: dispose);
    }

    /// <summary>Purge exact asset names from the cache in one transaction.</summary>
    /// <param name="assetNames">The asset names to invalidate.</param>
    /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
    /// <returns>Returns the invalidated asset names.</returns>
    public ICollection<IAssetName> InvalidateCache(IEnumerable<IAssetName> assetNames, bool dispose = false)
    {
        if (assetNames == null)
            throw new ArgumentNullException(nameof(assetNames));

        HashSet<IAssetName> normalizedNames = [];
        foreach (IAssetName assetName in assetNames)
        {
            if (assetName == null)
                throw new ArgumentException("The asset name list can't contain null values.", nameof(assetNames));

            normalizedNames.Add(this.ParseAssetName(assetName.Name, allowLocales: true));
        }

        return this.InvalidateExactCache(normalizedName: null, normalizedNames, dispose: dispose);
    }

    /// <summary>Purge one or more normalized exact asset names from the cache.</summary>
    /// <param name="normalizedName">The single normalized name to invalidate, if applicable.</param>
    /// <param name="normalizedNames">The normalized name set to invalidate, if applicable.</param>
    /// <param name="dispose">Whether to dispose invalidated assets.</param>
    /// <returns>Returns the invalidated asset names.</returns>
    private ICollection<IAssetName> InvalidateExactCache(IAssetName? normalizedName, IReadOnlySet<IAssetName>? normalizedNames, bool dispose)
    {
        Dictionary<IAssetName, Type> invalidatedAssets = [];
        Dictionary<IAssetName, List<IContentManager>>? loadedTextureManagers = null;

        this.ContentManagerLock.EnterReadLock();
        try
        {
            // Directly check the exact cache keys in game content managers. Namespaced content managers don't cache assets.
            if (normalizedName is not null)
            {
                foreach (IContentManager contentManager in this.GameContentManagers)
                    ContentCoordinator.InvalidateExactCacheEntry(contentManager, normalizedName, dispose, invalidatedAssets, ref loadedTextureManagers);
            }
            else
            {
                foreach (IContentManager contentManager in this.GameContentManagers)
                {
                    // Probe the smaller side of the manager/name intersection. Large content-pack batches often
                    // contain far more requested names than most mod-owned managers have cached entries.
                    if (normalizedNames!.Count <= contentManager.CachedAssetCount)
                    {
                        foreach (IAssetName name in normalizedNames)
                            ContentCoordinator.InvalidateExactCacheEntry(contentManager, name, dispose, invalidatedAssets, ref loadedTextureManagers);
                    }
                    else
                    {
                        foreach ((string rawName, object asset) in contentManager.GetCachedAssets())
                        {
                            AssetName name = this.ParseAssetName(rawName, allowLocales: true);
                            if (normalizedNames.Contains(name))
                                ContentCoordinator.InvalidateExactCachedAsset(contentManager, name, asset, dispose, invalidatedAssets, ref loadedTextureManagers);
                        }
                    }
                }
            }

            this.ForgetLocalizedAssetNames(invalidatedAssets.Keys);

            // Special case: maps may be loaded through a temporary content manager that's removed while the map is still in use.
            // This notably affects the town and farmhouse maps. If every requested name was found in a cache, propagation
            // already has each name and type it needs, so building the expansion-sized live topology can't add anything.
            bool hasMissingName = normalizedName is not null
                ? !invalidatedAssets.ContainsKey(normalizedName)
                : invalidatedAssets.Count < normalizedNames!.Count;
            if (hasMissingName)
            {
                foreach (WorldLocationUtilities.WorldLocationInfo info in WorldLocationUtilities.GetLocations())
                {
                    GameLocation location = info.Location;
                    if (
                        location is MineShaft or VolcanoDungeon
                        || location.map == null
                        || string.IsNullOrWhiteSpace(location.mapPath.Value)
                    )
                        continue;

                    AssetName mapPath = this.ParseAssetName(this.MainContentManager.AssertAndNormalizeAssetName(location.mapPath.Value), allowLocales: true);
                    bool matches = normalizedName is not null
                        ? normalizedName.Equals(mapPath)
                        : normalizedNames!.Contains(mapPath);
                    if (matches)
                        invalidatedAssets.TryAdd(mapPath, typeof(Map));
                }
            }
        }
        finally
        {
            this.ContentManagerLock.ExitReadLock();
        }

        return this.ProcessInvalidatedAssets(invalidatedAssets, loadedTextureManagers);
    }

    /// <summary>Invalidate one normalized exact asset from a content manager if it's loaded.</summary>
    /// <param name="contentManager">The content manager to check.</param>
    /// <param name="assetName">The normalized asset name.</param>
    /// <param name="dispose">Whether to dispose the invalidated asset.</param>
    /// <param name="invalidatedAssets">The invalidated asset types to update.</param>
    /// <param name="loadedTextureManagers">The loaded texture managers to update.</param>
    private static void InvalidateExactCacheEntry(IContentManager contentManager, IAssetName assetName, bool dispose, Dictionary<IAssetName, Type> invalidatedAssets, ref Dictionary<IAssetName, List<IContentManager>>? loadedTextureManagers)
    {
        if (!contentManager.TryGetCachedAsset(assetName, out object? asset))
            return;

        ContentCoordinator.InvalidateExactCachedAsset(contentManager, assetName, asset, dispose, invalidatedAssets, ref loadedTextureManagers);
    }

    /// <summary>Invalidate a normalized exact asset which is already known to be cached by a content manager.</summary>
    /// <param name="contentManager">The content manager containing the asset.</param>
    /// <param name="assetName">The normalized asset name.</param>
    /// <param name="asset">The cached asset.</param>
    /// <param name="dispose">Whether to dispose the invalidated asset.</param>
    /// <param name="invalidatedAssets">The invalidated asset types to update.</param>
    /// <param name="loadedTextureManagers">The loaded texture managers to update.</param>
    private static void InvalidateExactCachedAsset(IContentManager contentManager, IAssetName assetName, object asset, bool dispose, Dictionary<IAssetName, Type> invalidatedAssets, ref Dictionary<IAssetName, List<IContentManager>>? loadedTextureManagers)
    {
        if (asset is Texture2D) // will edit in place
            ContentCoordinator.TrackLoadedTextureManager(ref loadedTextureManagers, assetName, contentManager);
        else
            contentManager.InvalidateCache(assetName, dispose);

        invalidatedAssets.TryAdd(assetName, asset.GetType());
    }

    /// <summary>Purge matched assets from the cache.</summary>
    /// <param name="predicate">Matches the asset keys to invalidate.</param>
    /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
    /// <returns>Returns the invalidated asset keys.</returns>
    public ICollection<IAssetName> InvalidateCache(Func<IAssetInfo, bool> predicate, bool dispose = false)
    {
        string locale = this.GetLocale();
        var predicateCache = new AssetInfoPredicateCache(locale, this.MainContentManager.AssetNameNormalizer, predicate);
        return this.InvalidateCache((_, rawName, type) =>
        {
            IAssetName assetName = this.ParseAssetName(rawName, allowLocales: true);
            return predicateCache.Matches(assetName, type);
        }, dispose);
    }

    /// <summary>Purge matched assets from the cache.</summary>
    /// <param name="predicate">Matches the asset keys to invalidate.</param>
    /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
    /// <returns>Returns the invalidated asset names.</returns>
    public ICollection<IAssetName> InvalidateCache(Func<IContentManager, string, Type, bool> predicate, bool dispose = false)
    {
        // invalidate cache & track removed assets
        Dictionary<IAssetName, Type> invalidatedAssets = new();
        Dictionary<IAssetName, List<IContentManager>>? loadedTextureManagers = null;
        this.ContentManagerLock.EnterReadLock();
        try
        {
            // cached assets
            foreach (IContentManager contentManager in this.GameContentManagers)
            {
                foreach ((string key, object asset) in contentManager.GetCachedAssets())
                {
                    if (!predicate(contentManager, key, asset.GetType()))
                        continue;

                    AssetName assetName = this.ParseAssetName(key, allowLocales: true);

                    if (asset is Texture2D) // will edit in place
                        ContentCoordinator.TrackLoadedTextureManager(ref loadedTextureManagers, assetName, contentManager);
                    else
                        contentManager.InvalidateCache(assetName, dispose);

                    invalidatedAssets.TryAdd(assetName, asset.GetType());
                }
            }

            // forget localized flags
            // A mod might provide a localized variant of a normally non-localized asset (like
            // `Maps/MovieTheater.fr-FR`). When the asset is invalidated, we need to recheck
            // whether the asset is localized in case it stops providing it.
            this.ForgetLocalizedAssetNames(invalidatedAssets.Keys);

            // special case: maps may be loaded through a temporary content manager that's removed while the map is still in use.
            // This notably affects the town and farmhouse maps.
            foreach (WorldLocationUtilities.WorldLocationInfo info in WorldLocationUtilities.GetLocations())
            {
                GameLocation location = info.Location;
                if (
                    location is MineShaft or VolcanoDungeon
                    || location.map == null
                    || string.IsNullOrWhiteSpace(location.mapPath.Value)
                )
                    continue;

                // get map path
                AssetName mapPath = this.ParseAssetName(this.MainContentManager.AssertAndNormalizeAssetName(location.mapPath.Value), allowLocales: true);
                if (!invalidatedAssets.ContainsKey(mapPath) && predicate(this.MainContentManager, mapPath.Name, typeof(Map)))
                    invalidatedAssets[mapPath] = typeof(Map);
            }
        }
        finally
        {
            this.ContentManagerLock.ExitReadLock();
        }

        return this.ProcessInvalidatedAssets(invalidatedAssets, loadedTextureManagers);
    }

    /// <summary>Record a content manager which has a given texture loaded.</summary>
    /// <param name="loadedTextureManagers">The loaded-manager lookup to update.</param>
    /// <param name="assetName">The loaded texture name.</param>
    /// <param name="contentManager">The content manager which has the texture loaded.</param>
    private static void TrackLoadedTextureManager(ref Dictionary<IAssetName, List<IContentManager>>? loadedTextureManagers, IAssetName assetName, IContentManager contentManager)
    {
        loadedTextureManagers ??= [];
        if (!loadedTextureManagers.TryGetValue(assetName, out List<IContentManager>? managers))
            loadedTextureManagers[assetName] = managers = [];

        managers.Add(contentManager);
    }

    /// <summary>Apply the common event, propagation, and logging steps for invalidated assets.</summary>
    /// <param name="invalidatedAssets">The invalidated asset names and their data types.</param>
    /// <param name="loadedTextureManagers">The content managers which were found to have each invalidated texture loaded.</param>
    /// <returns>Returns the invalidated asset names.</returns>
    private ICollection<IAssetName> ProcessInvalidatedAssets(Dictionary<IAssetName, Type> invalidatedAssets, IReadOnlyDictionary<IAssetName, List<IContentManager>>? loadedTextureManagers)
    {
        if (invalidatedAssets.Count > 0)
        {
            // clear cached editor checks
            this.AssetOperationsByKey.RemoveWhere(
                invalidatedAssets,
                static (key, invalidated) => invalidated.ContainsKey(key.Name)
            );

            // raise event
            this.OnAssetsInvalidated(invalidatedAssets.Keys);

            // propagate changes to the game
            this.CoreAssets.Propagate(
                contentManagers: this.GameContentManagers,
                assets: invalidatedAssets,
                loadedTextureManagers: loadedTextureManagers,
                ignoreWorld: Context.IsWorldFullyUnloaded,
                out Dictionary<IAssetName, bool> propagated,
                out bool updatedWarpRoutes
            );

            // Build the sorted diagnostic report on the log-writer thread when trace output isn't visible.
            // These dictionaries are privately owned and aren't mutated after this point.
            this.Monitor.LogDeferred(
                (Invalidated: invalidatedAssets, Propagated: propagated, UpdatedWarpRoutes: updatedWarpRoutes),
                static state => ContentCoordinator.FormatInvalidationReport(state.Invalidated, state.Propagated, state.UpdatedWarpRoutes)
            );
        }
        else
            this.Monitor.Log("Invalidated 0 cache entries.");

        return invalidatedAssets.Keys;
    }

    /// <summary>Format a diagnostic report for one completed invalidation transaction.</summary>
    internal static string FormatInvalidationReport(IReadOnlyDictionary<IAssetName, Type> invalidatedAssets, IReadOnlyDictionary<IAssetName, bool> propagatedAssets, bool updatedWarpRoutes)
    {
        static string FormatKeyList(IEnumerable<IAssetName> keys)
        {
            return string.Join(", ", keys.Select(p => p.Name).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }

        IAssetName[] propagatedKeys = propagatedAssets.Where(p => p.Value).Select(p => p.Key).ToArray();
        StringBuilder report = new();
        report.AppendLine($"Invalidated {invalidatedAssets.Count} asset names ({FormatKeyList(invalidatedAssets.Keys)}).");
        report.AppendLine(propagatedAssets.Count > 0
            ? $"Propagated {propagatedKeys.Length} core assets ({FormatKeyList(propagatedKeys)})."
            : "Propagated 0 core assets."
        );
        if (updatedWarpRoutes)
            report.AppendLine("Updated NPC warp route cache.");
        return report.ToString().TrimEnd();
    }

    /// <summary>Get the asset load and edit operations to apply to a given asset if it's (re)loaded now.</summary>
    /// <param name="info">The asset info to load or edit.</param>
    public AssetOperationGroup? GetAssetOperations(IAssetInfo info)
    {
        return this.AssetOperationsByKey.GetOrSet(
            new AssetOperationCacheKey(info.Name, info.DataType),
            (Coordinator: this, Info: info),
            static state => state.Coordinator.RequestAssetOperations(state.Info)
        );
    }

    /// <summary>Get all loaded instances of an asset name.</summary>
    /// <param name="assetName">The asset name.</param>
    [SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "This method is provided for Content Patcher.")]
    public IReadOnlyList<object> GetLoadedValues(IAssetName assetName)
    {
        return this.ContentManagerLock.InReadLock((Coordinator: this, AssetName: assetName), static state =>
        {
            List<object> values = [];
            foreach (IContentManager content in state.Coordinator.GameContentManagers)
            {
                if (content.TryGetCachedAsset(state.AssetName, out object? value))
                    values.Add(value);
            }
            return values;
        });
    }

    /// <summary>Get the tilesheet ID order used by the unmodified version of a map asset.</summary>
    /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
    public TilesheetReference[] GetVanillaTilesheetIds(string assetName)
    {
        if (!this.VanillaTilesheets.TryGetValue(assetName, out TilesheetReference[]? tilesheets))
        {
            tilesheets = this.TryLoadVanillaAsset(assetName, out Map? map)
                ? map.TileSheets.Select((sheet, index) => new TilesheetReference(index, sheet.Id, sheet.ImageSource, sheet.SheetSize, sheet.TileSize)).ToArray()
                : null;

            this.VanillaTilesheets[assetName] = tilesheets;
            this.VanillaContentManager.Unload();
        }

        return tilesheets ?? [];
    }

    /// <summary>Get the locale code which corresponds to a language enum (e.g. <c>fr-FR</c> given <see cref="LocalizedContentManager.LanguageCode.fr"/>).</summary>
    /// <param name="language">The language enum to search.</param>
    public string? GetLocaleCode(LocalizedContentManager.LanguageCode language)
    {
        if (language == LocalizedContentManager.LanguageCode.mod && LocalizedContentManager.CurrentModLanguage == null)
            return null;

        return this.MainContentManager.GetLocale(language);
    }

    /// <summary>Dispose held resources.</summary>
    public void Dispose()
    {
        if (this.IsDisposed)
            return;
        this.IsDisposed = true;

        this.Monitor.Log("Disposing the content coordinator. Content managers will no longer be usable after this point.");
        foreach (IContentManager contentManager in this.ContentManagers)
            contentManager.Dispose();
        this.ContentManagers.Clear();
        this.GameContentManagers.Clear();
        this.NamespacedContentManagers.Clear();
        this.DecodedTextures.Dispose();
        this.MainContentManager = null!; // instance no longer usable

        this.ContentManagerLock.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Forget cached localized-name mappings for invalidated assets.</summary>
    /// <param name="assetNames">The invalidated asset names.</param>
    private void ForgetLocalizedAssetNames(IEnumerable<IAssetName> assetNames)
    {
        // A mod might provide a localized variant of a normally non-localized asset (like
        // `Maps/MovieTheater.fr-FR`). When the asset is invalidated, we need to recheck
        // whether the asset is localized in case it stops providing it.
        Dictionary<string, string> localizedAssetNames = this.LocalizedAssetNames.Value;
        foreach (IAssetName assetName in assetNames)
        {
            localizedAssetNames.Remove(assetName.Name);

            if (localizedAssetNames.TryGetValue(assetName.BaseName, out string? targetForBaseKey) && string.Equals(targetForBaseKey, assetName.Name, StringComparison.OrdinalIgnoreCase))
                localizedAssetNames.Remove(assetName.BaseName);
        }
    }

    /// <summary>Register a content manager.</summary>
    /// <remarks>The caller must hold a write lock if the coordinator is already available to other threads.</remarks>
    private void AddContentManager(IContentManager contentManager)
    {
        this.ContentManagers.Add(contentManager);

        // Preserve the existing first-match behavior if multiple managers use the same name.
        if (contentManager.IsNamespaced)
            this.NamespacedContentManagers.TryAdd(contentManager.Name, contentManager);
        else
            this.GameContentManagers.Add(contentManager);
    }

    /// <summary>A callback invoked when a content manager is disposed.</summary>
    /// <param name="contentManager">The content manager being disposed.</param>
    private void OnDisposing(IContentManager contentManager)
    {
        if (this.IsDisposed)
            return;

        this.ContentManagerLock.InWriteLock((Coordinator: this, ContentManager: contentManager), static state =>
        {
            ContentCoordinator coordinator = state.Coordinator;
            IContentManager contentManager = state.ContentManager;
            coordinator.ContentManagers.Remove(contentManager);

            if (!contentManager.IsNamespaced)
                coordinator.GameContentManagers.Remove(contentManager);
            else if (coordinator.NamespacedContentManagers.GetValueOrDefault(contentManager.Name) == contentManager)
            {
                coordinator.NamespacedContentManagers.Remove(contentManager.Name);

                IContentManager? next = null;
                foreach (IContentManager candidate in coordinator.ContentManagers)
                {
                    if (candidate.IsNamespaced && AreManagedContentManagerNamesEqual(candidate.Name, contentManager.Name))
                    {
                        next = candidate;
                        break;
                    }
                }
                if (next != null)
                    coordinator.NamespacedContentManagers[contentManager.Name] = next;
            }
        });
    }

    /// <summary>Get whether two managed content manager names identify the same routing prefix.</summary>
    internal static bool AreManagedContentManagerNamesEqual(string left, string right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    /// <summary>Get a vanilla asset without interception.</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
    /// <param name="asset">The loaded asset data.</param>
    private bool TryLoadVanillaAsset<T>(string assetName, [NotNullWhen(true)] out T? asset)
        where T : notnull
    {
        try
        {
            asset = this.VanillaContentManager.Load<T>(assetName);
            return true;
        }
        catch
        {
            // handled below
        }

        asset = default;
        return false;
    }

    /// <summary>Get the language enums (like <see cref="LocalizedContentManager.LanguageCode.ja"/>) indexed by locale code (like <c>ja-JP</c>).</summary>
    /// <param name="customLanguages">The custom languages to add to the lookup.</param>
    private Dictionary<string, LocalizedContentManager.LanguageCode> GetLocaleCodes(IReadOnlyList<ModLanguage?> customLanguages)
    {
        var map = new Dictionary<string, LocalizedContentManager.LanguageCode>(StringComparer.OrdinalIgnoreCase);

        // custom languages
        foreach (ModLanguage? language in customLanguages)
        {
            if (!string.IsNullOrWhiteSpace(language?.LanguageCode))
                map[language.LanguageCode] = LocalizedContentManager.LanguageCode.mod;
        }

        // vanilla languages (override custom language if they conflict)
        foreach (LocalizedContentManager.LanguageCode code in Enum.GetValues<LocalizedContentManager.LanguageCode>())
        {
            string? locale = this.GetLocaleCode(code);
            if (locale != null)
                map[locale] = code;
        }

        return map;
    }
}

/// <summary>A type-sensitive key for cached asset operations.</summary>
/// <param name="Name">The requested asset name.</param>
/// <param name="DataType">The requested data type exposed to asset handlers.</param>
internal readonly record struct AssetOperationCacheKey(IAssetName Name, Type DataType);

/// <summary>Caches a public invalidation predicate's result for each distinct asset name and data type in one transaction.</summary>
/// <remarks>The public predicate can't observe the content manager, so equivalent cached copies must have the same result.</remarks>
internal sealed class AssetInfoPredicateCache
{
    /*********
    ** Fields
    *********/
    /// <summary>The locale to expose through the asset info.</summary>
    private readonly string Locale;

    /// <summary>Normalize an asset name.</summary>
    private readonly Func<string, string> NormalizeAssetName;

    /// <summary>The predicate whose results to cache.</summary>
    private readonly Func<IAssetInfo, bool> Predicate;

    /// <summary>The cached predicate results.</summary>
    private readonly Dictionary<AssetOperationCacheKey, bool> Results = [];


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="locale">The locale to expose through the asset info.</param>
    /// <param name="normalizeAssetName">Normalize an asset name.</param>
    /// <param name="predicate">The predicate whose results to cache.</param>
    public AssetInfoPredicateCache(string locale, Func<string, string> normalizeAssetName, Func<IAssetInfo, bool> predicate)
    {
        this.Locale = locale;
        this.NormalizeAssetName = normalizeAssetName;
        this.Predicate = predicate;
    }

    /// <summary>Get whether an asset matches the predicate.</summary>
    /// <param name="assetName">The normalized asset name.</param>
    /// <param name="dataType">The asset's data type.</param>
    public bool Matches(IAssetName assetName, Type dataType)
    {
        var key = new AssetOperationCacheKey(assetName, dataType);
        if (!this.Results.TryGetValue(key, out bool matches))
        {
            var info = new AssetInfo(this.Locale, assetName, dataType, this.NormalizeAssetName);
            this.Results[key] = matches = this.Predicate(info);
        }

        return matches;
    }
}
