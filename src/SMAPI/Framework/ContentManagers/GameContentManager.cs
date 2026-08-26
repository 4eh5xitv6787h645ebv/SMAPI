using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Extensions;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Internal;
using StardewValley;
using xTile;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ContentManagers;

/// <summary>A content manager which handles reading files from the game content folder with support for interception.</summary>
internal class GameContentManager : BaseContentManager
{
    /*********
    ** Delegates
    *********/
    /// <summary>Apply editors using the asset's concrete type.</summary>
    private delegate IAssetData ApplyEditorsDelegate(GameContentManager manager, IAssetInfo info, IAssetData asset, List<AssetEditOperation>? editOperations);


    /*********
    ** Fields
    *********/
    /// <summary>The editor methods specialized for concrete asset types loaded through a more general type like <see cref="object"/>.</summary>
    private static readonly ConcurrentDictionary<Type, ApplyEditorsDelegate> ApplyEditorsByType = new();

    /// <summary>The assets currently being intercepted by <see cref="AssetLoadOperation"/> instances. This is used to prevent infinite loops when a loader loads a new asset.</summary>
    private readonly ContextHash<string> AssetsBeingLoaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the next load is the first for any game content manager.</summary>
    private static bool IsFirstLoad = true;

    /// <summary>A callback to invoke the first time *any* game content manager loads an asset.</summary>
    private readonly Action OnLoadingFirstAsset;

    /// <summary>A callback to invoke when an asset is fully loaded.</summary>
    private readonly Action<BaseContentManager, IAssetName> OnAssetLoaded;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
    /// <param name="serviceProvider">The service provider to use to locate services.</param>
    /// <param name="rootDirectory">The root directory to search for content.</param>
    /// <param name="currentCulture">The current culture for which to localize content.</param>
    /// <param name="coordinator">The central coordinator which manages content managers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
    /// <param name="onLoadingFirstAsset">A callback to invoke the first time *any* game content manager loads an asset.</param>
    /// <param name="onAssetLoaded">A callback to invoke when an asset is fully loaded.</param>
    public GameContentManager(string name, IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, Action<BaseContentManager> onDisposing, Action onLoadingFirstAsset, Action<BaseContentManager, IAssetName> onAssetLoaded)
        : base(name, serviceProvider, rootDirectory, currentCulture, coordinator, monitor, reflection, onDisposing, isNamespaced: false)
    {
        this.OnLoadingFirstAsset = onLoadingFirstAsset;
        this.OnAssetLoaded = onAssetLoaded;

        this.CheckGameFolderForAssetExists = true;
    }

    /// <inheritdoc />
    public override bool DoesAssetExist<T>(IAssetName assetName)
    {
        // Cached values remain available even if their source has since disappeared.
        if (this.Cache.ContainsKey(assetName.Name))
            return true;

        // Managed keys belong to their mod provider, so don't probe the vanilla filesystem first.
        if (this.Coordinator.TryParseManagedAssetKey(assetName.Name, out string? contentManagerId, out IAssetName? relativePath))
            return this.Coordinator.DoesManagedAssetExist<T>(contentManagerId, relativePath);

        // vanilla asset
        if (this.DoesGameAssetExist<T>(assetName))
            return true;

        // custom asset from a loader
        string locale = this.GetLocale();
        IAssetInfo info = new AssetInfo(locale, assetName, typeof(T), this.AssetNameNormalizer);
        AssetOperationGroup? operations = this.Coordinator.GetAssetOperations(info);
        if (operations?.LoadOperations is { Count: > 0 } loadOperations)
        {
            if (!this.AssertMaxOneRequiredLoader(info, loadOperations, out string? error))
            {
                this.Monitor.Log(error, LogLevel.Warn);
                return false;
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override T LoadExact<T>(IAssetName assetName, bool useCache)
    {
        if (typeof(IRawTextureData).IsAssignableFrom(typeof(T)))
            throw new SContentLoadException(ContentLoadErrorType.Other, $"Can't load {nameof(IRawTextureData)} assets from the game content pipeline. This asset type is only available for mod files.");

        // raise first-load callback
        if (GameContentManager.IsFirstLoad)
        {
            GameContentManager.IsFirstLoad = false;
            this.OnLoadingFirstAsset();
        }

        // get from cache
        if (useCache && this.TryGetCachedAsset(assetName, out object? cachedAsset))
        {
            if (cachedAsset is T typedAsset)
                return typedAsset;

            // Preserve MonoGame's incompatible-type exception behavior for an existing cache entry.
            return this.RawLoad<T>(assetName, useCache: true);
        }

        // get managed asset
        if (this.Coordinator.TryParseManagedAssetKey(assetName.Name, out string? contentManagerId, out IAssetName? relativePath))
        {
            T managedAsset = this.Coordinator.LoadManagedAsset<T>(contentManagerId, relativePath);
            this.TrackAsset(assetName, managedAsset, useCache);
            return managedAsset;
        }

        // load asset
        T data;
        if (this.AssetsBeingLoaded.Contains(assetName.Name))
        {
            this.Monitor.Log($"Broke loop while loading asset '{assetName}'.", LogLevel.Warn);
            this.Monitor.Log($"Bypassing mod loaders for this asset. Stack trace:\n{Environment.StackTrace}");
            data = this.RawLoad<T>(assetName, useCache);
        }
        else
        {
            data = this.AssetsBeingLoaded.Track(
                assetName.Name,
                (Manager: this, AssetName: assetName, UseCache: useCache),
                static state =>
                {
                    GameContentManager manager = state.Manager;
                    IAssetName assetName = state.AssetName;
                    IAssetInfo info = new AssetInfo(assetName.LocaleCode, assetName, typeof(T), manager.AssetNameNormalizer);
                    AssetOperationGroup? operations = manager.Coordinator.GetAssetOperations(info);
                    if (operations is null)
                        return manager.RawLoad<T>(assetName, state.UseCache);

                    AssetOperationGroup operationGroup = operations.Value;
                    T data = manager.TryApplyLoader<T>(info, operationGroup.LoadOperations, out T? loadedData)
                        ? loadedData
                        : manager.RawLoad<T>(assetName, state.UseCache);
                    if (operationGroup.EditOperations?.Count is not > 0)
                        return data;

                    IAssetData asset = new AssetDataForObject(info, data, manager.AssetNameNormalizer, manager.Reflection);
                    asset = manager.ApplyEditors<T>(info, asset, operationGroup.EditOperations);
                    return (T)asset.Data;
                }
            );
        }

        // update cache
        this.TrackAsset(assetName, data, useCache);

        // raise event & return data
        this.OnAssetLoaded(this, assetName);
        return data;
    }

    /// <inheritdoc />
    public override LocalizedContentManager CreateTemporary()
    {
        return this.Coordinator.CreateGameContentManager("(temporary)");
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Load the initial asset from the registered loaders.</summary>
    /// <param name="info">The basic asset metadata.</param>
    /// <param name="loadOperations">The load operations to apply to the asset.</param>
    /// <param name="data">The loaded asset data, if a loader matched and returned a valid value.</param>
    /// <returns>Returns whether a loader matched and returned a valid value.</returns>
    private bool TryApplyLoader<T>(IAssetInfo info, List<AssetLoadOperation>? loadOperations, [NotNullWhen(true)] out T? data)
        where T : notnull
    {
        data = default;

        // find matching loader
        AssetLoadOperation? loader = null;
        if (loadOperations?.Count > 0)
        {
            if (!this.AssertMaxOneRequiredLoader(info, loadOperations, out string? error))
            {
                this.Monitor.Log(error, LogLevel.Warn);
                return false;
            }

            foreach (AssetLoadOperation candidate in loadOperations)
            {
                if (loader == null || candidate.Priority > loader.Priority)
                    loader = candidate;
            }
        }
        if (loader == null)
            return false;

        // fetch asset from loader
        IModMetadata mod = loader.Mod;
        Context.HeuristicModsRunningCode.Push(loader.Mod);
        bool profile = this.Coordinator.PerformanceManager.IsTracking;
        HandlerTimingToken timing = profile
            ? this.Coordinator.PerformanceManager.BeginHandler(
                mod,
                "Content.Load",
                $"{loader.GetType().FullName}.{nameof(AssetLoadOperation.GetData)}",
                this.GetExecutionPhase(),
                ModHealthOperationKind.ContentLoad,
                GetSafeModId(loader.OnBehalfOf)
            )
            : default;
        bool failed = false;
        try
        {
            data = (T)loader.GetData();
            this.Monitor.LogDeferred(
                (ModName: mod.DisplayName, AssetName: info.Name.Name, ContentPackName: loader.OnBehalfOf?.Manifest.Name),
                static state => $"{state.ModName} loaded asset '{state.AssetName}'{FormatContentPackLabel(state.ContentPackName)}."
            );
        }
        catch (Exception ex)
        {
            failed = true;
            this.Coordinator.HealthObserver?.ObserveCallbackFailure(
                mod,
                this.GetExecutionPhase(),
                ModHealthOperationKind.ContentLoad,
                $"{loader.GetType().FullName}.{nameof(AssetLoadOperation.GetData)}",
                ex,
                loader.OnBehalfOf
            );
            mod.LogAsMod($"Mod crashed when loading asset '{info.Name}'{this.GetOnBehalfOfLabel(loader.OnBehalfOf)}. SMAPI will use the default asset instead. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
            data = default;
            return false;
        }
        finally
        {
            if (profile)
                this.Coordinator.PerformanceManager.EndHandler(timing, failed);
            Context.HeuristicModsRunningCode.TryPop(out _);
        }

        return this.TryFixAndValidateLoadedAsset(info, data, loader);
    }

    /// <summary>Apply any editors to a loaded asset.</summary>
    /// <typeparam name="T">The asset type.</typeparam>
    /// <param name="info">The basic asset metadata.</param>
    /// <param name="asset">The loaded asset.</param>
    /// <param name="editOperations">The edit operations to apply to the asset.</param>
    private IAssetData ApplyEditors<T>(IAssetInfo info, IAssetData asset, List<AssetEditOperation>? editOperations)
        where T : notnull
    {
        if (editOperations?.Count is not > 0)
            return asset;

        // special case: if the asset was loaded with a more general type like 'object', call editors with the actual type instead.
        {
            Type actualType = asset.Data.GetType();
            Type? actualOpenType = actualType.IsGenericType ? actualType.GetGenericTypeDefinition() : null;

            if (typeof(T) != actualType && (actualOpenType == typeof(Dictionary<,>) || actualOpenType == typeof(List<>) || actualType == typeof(Texture2D) || actualType == typeof(Map)))
                return GameContentManager.GetApplyEditorsDelegate(actualType)(this, info, asset, editOperations);
        }

        // edit asset
        foreach (AssetEditOperation editor in editOperations)
        {
            IModMetadata mod = editor.Mod;

            // try edit
            object prevAsset = asset.Data;
            Context.HeuristicModsRunningCode.Push(editor.Mod);
            bool profile = this.Coordinator.PerformanceManager.IsTracking;
            HandlerTimingToken timing = profile
                ? this.Coordinator.PerformanceManager.BeginHandler(
                    editor.Mod,
                    "Content.Edit",
                    $"{editor.ApplyEdit.Method.DeclaringType?.FullName}.{editor.ApplyEdit.Method.Name}",
                    this.GetExecutionPhase(),
                    ModHealthOperationKind.ContentEdit,
                    GetSafeModId(editor.OnBehalfOf)
                )
                : default;
            bool failed = false;
            try
            {
                editor.ApplyEdit(asset);
                this.Monitor.LogDeferred(
                    (ModName: mod.DisplayName, AssetName: info.Name.Name, ContentPackName: editor.OnBehalfOf?.Manifest.Name),
                    static state => $"{state.ModName} edited {state.AssetName}{FormatContentPackLabel(state.ContentPackName)}."
                );
            }
            catch (Exception ex)
            {
                failed = true;
                this.Coordinator.HealthObserver?.ObserveCallbackFailure(
                    mod,
                    this.GetExecutionPhase(),
                    ModHealthOperationKind.ContentEdit,
                    $"{editor.ApplyEdit.Method.DeclaringType?.FullName}.{editor.ApplyEdit.Method.Name}",
                    ex,
                    editor.OnBehalfOf
                );
                mod.LogAsMod($"Mod crashed when editing asset '{info.Name}'{this.GetOnBehalfOfLabel(editor.OnBehalfOf)}, which may cause errors in-game. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
            }
            finally
            {
                if (profile)
                    this.Coordinator.PerformanceManager.EndHandler(timing, failed);
                Context.HeuristicModsRunningCode.TryPop(out _);
            }

            // validate edit
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- it's only guaranteed non-null after this method
            if (asset.Data == null)
            {
                mod.LogAsMod($"Mod incorrectly set asset '{info.Name}'{this.GetOnBehalfOfLabel(editor.OnBehalfOf)} to a null value; ignoring override.", LogLevel.Warn);
                asset = new AssetDataForObject(info, prevAsset, this.AssetNameNormalizer, this.Reflection);
            }
            else if (asset.Data is not T)
            {
                mod.LogAsMod($"Mod incorrectly set asset '{asset.Name}'{this.GetOnBehalfOfLabel(editor.OnBehalfOf)} to incompatible type '{asset.Data.GetType()}', expected '{typeof(T)}'; ignoring override.", LogLevel.Warn);
                asset = new AssetDataForObject(info, prevAsset, this.AssetNameNormalizer, this.Reflection);
            }
        }

        // return result
        return asset;
    }

    /// <summary>Get a cached editor method specialized for a concrete asset type.</summary>
    /// <param name="assetType">The concrete asset type.</param>
    private static ApplyEditorsDelegate GetApplyEditorsDelegate(Type assetType)
    {
        return GameContentManager.ApplyEditorsByType.GetOrAdd(
            assetType,
            static type =>
            {
                MethodInfo method = typeof(GameContentManager)
                    .GetMethod(nameof(GameContentManager.ApplyEditors), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(type);

                return (ApplyEditorsDelegate)method.CreateDelegate(typeof(ApplyEditorsDelegate));
            }
        );
    }

    /// <summary>Get the broad execution phase without retaining game or asset state.</summary>
    private ModHealthExecutionPhase GetExecutionPhase()
    {
        if (this.Coordinator.PerformanceManager.GetActiveExecutionPhase() is { } inheritedPhase)
            return inheritedPhase;
        return Game1.IsOnMainThread()
            ? ModHealthExecutionPhase.Update
            : ModHealthExecutionPhase.Background;
    }

    /// <summary>Get a content pack's safe manifest identity, if available.</summary>
    private static string? GetSafeModId(IModMetadata? mod)
    {
        return mod?.HasId() is true ? mod.Manifest.UniqueID : null;
    }

    /// <summary>Assert that at most one loader will be applied to an asset.</summary>
    /// <param name="info">The basic asset metadata.</param>
    /// <param name="loaders">The asset loaders to apply.</param>
    /// <param name="error">The error message to show to the user, if the method returns false.</param>
    /// <returns>Returns true if only one loader will apply, else false.</returns>
    private bool AssertMaxOneRequiredLoader(IAssetInfo info, List<AssetLoadOperation> loaders, [NotNullWhen(false)] out string? error)
    {
        int requiredCount = 0;
        foreach (AssetLoadOperation loader in loaders)
        {
            if (loader.Priority == AssetLoadPriority.Exclusive && ++requiredCount > 1)
                break;
        }

        if (requiredCount <= 1)
        {
            error = null;
            return true;
        }

        string[] loaderNames = loaders
            .Where(p => p.Priority == AssetLoadPriority.Exclusive)
            .Select(p => p.Mod.DisplayName + this.GetOnBehalfOfLabel(p.OnBehalfOf))
            .OrderBy(p => p)
            .Distinct()
            .ToArray();
        string errorPhrase = loaderNames.Length > 1
            ? $"Multiple mods want to provide the '{info.Name}' asset: {string.Join(", ", loaderNames)}"
            : $"The '{loaderNames[0]}' mod wants to provide the '{info.Name}' asset multiple times";

        error = $"{errorPhrase}. An asset can't be loaded multiple times, so SMAPI will use the default asset instead. Uninstall one of the mods to fix this. (Message for modders: you should avoid {nameof(AssetLoadPriority)}.{nameof(AssetLoadPriority.Exclusive)} if possible to avoid conflicts.)";
        return false;
    }

    /// <summary>Get a parenthetical label for log messages for the content pack on whose behalf the action is being performed, if any.</summary>
    /// <param name="onBehalfOf">The content pack on whose behalf the action is being performed.</param>
    /// <param name="parenthetical">whether to format the label as a parenthetical shown after the mod name like <c> (for the 'X' content pack)</c>, instead of a standalone label like <c>the 'X' content pack</c>.</param>
    /// <returns>Returns the on-behalf-of label if applicable, else <c>null</c>.</returns>
    [return: NotNullIfNotNull("onBehalfOf")]
    private string? GetOnBehalfOfLabel(IModMetadata? onBehalfOf, bool parenthetical = true)
    {
        if (onBehalfOf == null)
            return null;

        return parenthetical
            ? $" (for the '{onBehalfOf.Manifest.Name}' content pack)"
            : $"the '{onBehalfOf.Manifest.Name}' content pack";
    }

    /// <summary>Get a parenthetical label for a content pack in a deferred trace message.</summary>
    /// <param name="contentPackName">The content pack name, if applicable.</param>
    private static string? FormatContentPackLabel(string? contentPackName)
    {
        return contentPackName is null
            ? null
            : $" (for the '{contentPackName}' content pack)";
    }

    /// <summary>Validate that an asset loaded by a mod is valid and won't cause issues, and fix issues if possible.</summary>
    /// <typeparam name="T">The asset type.</typeparam>
    /// <param name="info">The basic asset metadata.</param>
    /// <param name="data">The loaded asset data.</param>
    /// <param name="loader">The loader which loaded the asset.</param>
    /// <returns>Returns whether the asset passed validation checks (after any fixes were applied).</returns>
    private bool TryFixAndValidateLoadedAsset<T>(IAssetInfo info, [NotNullWhen(true)] T? data, AssetLoadOperation loader)
        where T : notnull
    {
        IModMetadata mod = loader.Mod;

        // can't load a null asset
        if (data == null)
        {
            mod.LogAsMod($"SMAPI blocked asset replacement for '{info.Name}': {this.GetOnBehalfOfLabel(loader.OnBehalfOf, parenthetical: false) ?? "mod"} incorrectly set asset to a null value.", LogLevel.Error);
            return false;
        }

        // when replacing a map, the vanilla tilesheets must have the same order and IDs
        if (data is Map loadedMap)
        {
            TilesheetReference[] vanillaTilesheetRefs = this.Coordinator.GetVanillaTilesheetIds(info.Name.Name);
            foreach (TilesheetReference vanillaSheet in vanillaTilesheetRefs)
            {
                // add missing tilesheet
                if (loadedMap.GetTileSheet(vanillaSheet.Id) == null)
                {
                    mod.Monitor!.LogOnce("SMAPI fixed maps loaded by this mod to prevent errors. See the log file for details.", LogLevel.Warn);
                    this.Monitor.Log($"Fixed broken map replacement: {mod.DisplayName} loaded '{info.Name}' without a required tilesheet (id: {vanillaSheet.Id}, source: {vanillaSheet.ImageSource}).");

                    loadedMap.AddTileSheet(new TileSheet(vanillaSheet.Id, loadedMap, vanillaSheet.ImageSource, vanillaSheet.SheetSize, vanillaSheet.TileSize));
                }
            }
        }

        return true;
    }
}
