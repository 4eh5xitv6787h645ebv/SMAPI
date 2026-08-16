using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Internal;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Characters;
using StardewValley.Locations;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Triggers;
using StardewValley.WorldMaps;
using xTile;
using LocationInfo = StardewModdingAPI.Framework.Utilities.WorldLocationUtilities.WorldLocationInfo;

namespace StardewModdingAPI.Metadata;

/// <summary>Propagates changes to core assets to the game state.</summary>
internal class CoreAssetPropagator
{
    /*********
    ** Fields
    *********/
    /// <summary>The main content manager through which to reload assets.</summary>
    private readonly LocalizedContentManager MainContentManager;

    /// <summary>An internal content manager used only for asset propagation. See remarks on <see cref="GameContentManagerForAssetPropagation"/>.</summary>
    private readonly GameContentManagerForAssetPropagation DisposableContentManager;

    /// <summary>Writes messages to the console.</summary>
    private readonly IMonitor Monitor;

    /// <summary>The multiplayer instance whose map cache to update.</summary>
    private readonly Multiplayer Multiplayer;

    /// <summary>Simplifies access to private game code.</summary>
    private readonly Reflector Reflection;

    /// <summary>Parse a raw asset name.</summary>
    private readonly Func<string, IAssetName> ParseAssetName;

    /// <summary>A cache of world data fetched for the current tick.</summary>
    private readonly TickCacheDictionary<string> WorldCache = new();

    /// <summary>The propagation route for each exact texture asset name.</summary>
    private static readonly Dictionary<string, TextureAssetRoute> TextureAssetRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Characters/Farmer/farmer_base"] = TextureAssetRoute.PlayerSprites,
        ["Characters/Farmer/farmer_base_bald"] = TextureAssetRoute.PlayerSprites,
        ["Characters/Farmer/farmer_girl_base"] = TextureAssetRoute.PlayerSprites,
        ["Characters/Farmer/farmer_girl_base_bald"] = TextureAssetRoute.PlayerSprites,
        ["TileSheets/tools"] = TextureAssetRoute.Tools
    };

    /// <summary>The propagation route for each exact non-map, non-texture base asset name.</summary>
    private static readonly Dictionary<string, OtherAssetRoute> OtherAssetRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data/Achievements"] = OtherAssetRoute.Achievements,
        ["Data/AudioChanges"] = OtherAssetRoute.AudioChanges,
        ["Data/BigCraftables"] = OtherAssetRoute.BigCraftables,
        ["Data/Boots"] = OtherAssetRoute.Boots,
        ["Data/Buildings"] = OtherAssetRoute.Buildings,
        ["Data/ChairTiles"] = OtherAssetRoute.ChairTiles,
        ["Data/Characters"] = OtherAssetRoute.Characters,
        ["Data/Concessions"] = OtherAssetRoute.Concessions,
        ["Data/ConcessionTastes"] = OtherAssetRoute.ConcessionTastes,
        ["Data/CookingRecipes"] = OtherAssetRoute.CookingRecipes,
        ["Data/CraftingRecipes"] = OtherAssetRoute.CraftingRecipes,
        ["Data/Crops"] = OtherAssetRoute.Crops,
        ["Data/FarmAnimals"] = OtherAssetRoute.FarmAnimals,
        ["Data/FloorsAndPaths"] = OtherAssetRoute.FloorsAndPaths,
        ["Data/Furniture"] = OtherAssetRoute.Furniture,
        ["Data/FruitTrees"] = OtherAssetRoute.FruitTrees,
        ["Data/HairData"] = OtherAssetRoute.HairData,
        ["Data/Hats"] = OtherAssetRoute.Hats,
        ["Data/JukeboxTracks"] = OtherAssetRoute.JukeboxTracks,
        ["Data/Locations"] = OtherAssetRoute.Locations,
        ["Data/LocationContexts"] = OtherAssetRoute.LocationContexts,
        ["Data/Movies"] = OtherAssetRoute.Movies,
        ["Data/MoviesReactions"] = OtherAssetRoute.Movies,
        ["Data/NPCGiftTastes"] = OtherAssetRoute.NpcGiftTastes,
        ["Data/Objects"] = OtherAssetRoute.Objects,
        ["Data/Pants"] = OtherAssetRoute.Pants,
        ["Data/Pets"] = OtherAssetRoute.Pets,
        ["Data/Shirts"] = OtherAssetRoute.Shirts,
        ["Data/Tools"] = OtherAssetRoute.Tools,
        ["Data/TriggerActions"] = OtherAssetRoute.TriggerActions,
        ["Data/Weapons"] = OtherAssetRoute.Weapons,
        ["Data/WildTrees"] = OtherAssetRoute.WildTrees,
        ["Data/WorldMap"] = OtherAssetRoute.WorldMap,
        ["Fonts/SpriteFont1"] = OtherAssetRoute.SpriteFont1,
        ["Fonts/SmallFont"] = OtherAssetRoute.SmallFont,
        ["Fonts/TinyFont"] = OtherAssetRoute.TinyFont,
        ["Strings/StringsFromCSFiles"] = OtherAssetRoute.StringsFromCsFiles
    };


    /*********
    ** Public methods
    *********/
    /// <summary>Initialize the core asset data.</summary>
    /// <param name="mainContent">The main content manager through which to reload assets.</param>
    /// <param name="disposableContent">An internal content manager used only for asset propagation.</param>
    /// <param name="monitor">Writes messages to the console.</param>
    /// <param name="multiplayer">The multiplayer instance whose map cache to update.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="parseAssetName">Parse a raw asset name.</param>
    public CoreAssetPropagator(LocalizedContentManager mainContent, GameContentManagerForAssetPropagation disposableContent, IMonitor monitor, Multiplayer multiplayer, Reflector reflection, Func<string, IAssetName> parseAssetName)
    {
        this.MainContentManager = mainContent;
        this.DisposableContentManager = disposableContent;
        this.Monitor = monitor;
        this.Multiplayer = multiplayer;
        this.Reflection = reflection;
        this.ParseAssetName = parseAssetName;
    }

    /// <summary>Get the propagation route for an exact normalized texture asset name.</summary>
    /// <param name="assetName">The normalized asset name.</param>
    internal static TextureAssetRoute GetTextureAssetRoute(string assetName)
    {
        return CoreAssetPropagator.TextureAssetRoutes.TryGetValue(assetName, out TextureAssetRoute route)
            ? route
            : TextureAssetRoute.None;
    }

    /// <summary>Get the propagation route for an exact normalized non-map, non-texture base asset name.</summary>
    /// <param name="baseName">The normalized base asset name.</param>
    internal static OtherAssetRoute GetOtherAssetRoute(string baseName)
    {
        return CoreAssetPropagator.OtherAssetRoutes.TryGetValue(baseName, out OtherAssetRoute route)
            ? route
            : OtherAssetRoute.None;
    }

    /// <summary>Reload one of the game's core assets (if applicable).</summary>
    /// <param name="contentManagers">The content managers whose assets to update.</param>
    /// <param name="assets">The asset keys and types to reload.</param>
    /// <param name="loadedTextureManagers">The content managers which were found to have each invalidated texture loaded.</param>
    /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
    /// <param name="propagatedAssets">A lookup of asset names to whether they've been propagated.</param>
    /// <param name="changedWarpRoutes">Whether the NPC pathfinding warp route cache was reloaded.</param>
    public void Propagate(IList<IContentManager> contentManagers, IReadOnlyDictionary<IAssetName, Type> assets, IReadOnlyDictionary<IAssetName, List<IContentManager>>? loadedTextureManagers, bool ignoreWorld, out Dictionary<IAssetName, bool> propagatedAssets, out bool changedWarpRoutes)
    {
        propagatedAssets = new Dictionary<IAssetName, bool>(assets.Count);

        // propagate each asset
        changedWarpRoutes = false;
        {
            Type imageType = typeof(Texture2D);
            Type mapType = typeof(Map);
            Dictionary<FarmHouse, string?>? spouseRoomMapPathCache = null; // constructed later if needed
            Dictionary<IAssetName, List<LocationInfo>>? locationsByMapName = null;
            Dictionary<string, List<NPC>>? charactersByName = null;
            HashSet<string>? oldWarpTargets = null;
            HashSet<string>? newWarpTargets = null;

            // Build world indexes only when a batch contains multiple matching assets. A single invalidation is
            // cheaper as a direct scan, while a large content update can otherwise scan the world once per asset.
            if (!ignoreWorld)
            {
                int mapAssets = 0;
                int targetedNpcAssets = 0;
                foreach ((IAssetName assetName, Type assetType) in assets)
                {
                    if (assetType == mapType)
                        mapAssets++;
                    else if (
                        !imageType.IsAssignableFrom(assetType)
                        && (
                            assetName.IsDirectlyUnderPath("Characters/Dialogue")
                            || assetName.IsDirectlyUnderPath("Characters/schedules")
                        )
                    )
                        targetedNpcAssets++;
                }

                if (mapAssets > 1)
                    locationsByMapName = this.GetLocationsByMapName(ref spouseRoomMapPathCache);
                if (targetedNpcAssets > 1)
                    charactersByName = this.GetCharactersByName();
            }

            foreach ((IAssetName assetName, Type assetType) in assets)
            {
                bool changed = false;

                try
                {
                    // image
                    if (imageType.IsAssignableFrom(assetType))
                        changed = this.PropagateTexture(assetName, contentManagers, loadedTextureManagers, ignoreWorld);

                    // map
                    else if (assetType == mapType)
                    {
                        changed = this.PropagateMap(
                            assetName,
                            ref spouseRoomMapPathCache,
                            locationsByMapName,
                            ref oldWarpTargets,
                            ref newWarpTargets,
                            checkWarpChanges: !changedWarpRoutes,
                            ignoreWorld: ignoreWorld,
                            out bool curChangedMapRoutes
                        );
                        changedWarpRoutes |= curChangedMapRoutes;
                    }

                    // any other type
                    else
                        changed = this.PropagateOther(assetName, ignoreWorld, charactersByName);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"An error occurred while propagating changes to asset '{assetName.Name}'. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                propagatedAssets[assetName] = changed;
            }
        }

        // reload NPC pathfinding cache if any map routes changed
        if (changedWarpRoutes)
            WarpPathfindingCache.PopulateCache();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Propagate changes to a map asset.</summary>
    /// <param name="assetName">The asset name that changed.</param>
    /// <param name="spouseRoomMapPathCache">A cache of spouse room map path lookups by farmhouse or cabin instance. This will be created the first time it's needed.</param>
    /// <param name="locationsByMapName">The locations indexed by map asset name for a multi-map propagation batch, if applicable.</param>
    /// <param name="oldWarpTargets">A pooled set containing warp targets before a map reload.</param>
    /// <param name="newWarpTargets">A pooled set containing warp targets after a map reload.</param>
    /// <param name="checkWarpChanges">Whether to check for changed warp routes. This can be disabled after an earlier map proves the global cache needs to be rebuilt.</param>
    /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
    /// <param name="changedWarpRoutes">Whether the locations reachable by warps from this location changed as part of this propagation.</param>
    /// <returns>Returns whether any assets were updated.</returns>
    private bool PropagateMap(IAssetName assetName, ref Dictionary<FarmHouse, string?>? spouseRoomMapPathCache, Dictionary<IAssetName, List<LocationInfo>>? locationsByMapName, ref HashSet<string>? oldWarpTargets, ref HashSet<string>? newWarpTargets, bool checkWarpChanges, bool ignoreWorld, out bool changedWarpRoutes)
    {
        bool changed = false;
        changedWarpRoutes = false;

        if (!ignoreWorld)
        {
            IReadOnlyList<LocationInfo> locations;
            if (locationsByMapName is null)
                locations = this.GetLocationsWithInfo();
            else if (!locationsByMapName.TryGetValue(assetName.GetBaseAssetName(), out List<LocationInfo>? indexedLocations))
                locations = Array.Empty<LocationInfo>();
            else
                locations = indexedLocations;

            foreach (LocationInfo info in locations)
            {
                GameLocation location = info.Location;

                bool shouldUpdateMap =
                    locationsByMapName is not null

                    // edited this map
                    || this.IsSameBaseName(assetName, location.mapPath.Value)

                    // edited spouse room for this farmhouse
                    || (
                        location is FarmHouse farmhouse
                        && this.IsSameBaseName(
                            assetName,
                            this.GetDisplayedSpouseRoomPath(farmhouse, ref spouseRoomMapPathCache)
                        )
                    );

                if (shouldUpdateMap)
                {
                    static void FillWarpSet(GameLocation location, HashSet<string> targetNames)
                    {
                        foreach (Warp warp in location.warps)
                            targetNames.Add(warp.TargetName);

                        if (location.doors is not null)
                        {
                            foreach (string targetName in location.doors.Values)
                                targetNames.Add(targetName);
                        }
                    }

                    if (checkWarpChanges && !changedWarpRoutes)
                    {
                        oldWarpTargets ??= [];
                        oldWarpTargets.Clear();
                        FillWarpSet(location, oldWarpTargets);
                    }

                    this.UpdateMap(info);

                    if (checkWarpChanges && !changedWarpRoutes)
                    {
                        newWarpTargets ??= [];
                        newWarpTargets.Clear();
                        FillWarpSet(location, newWarpTargets);

                        changedWarpRoutes = oldWarpTargets!.Count != newWarpTargets.Count;
                        if (!changedWarpRoutes)
                        {
                            foreach (string oldWarp in oldWarpTargets)
                            {
                                if (!newWarpTargets.Contains(oldWarp))
                                {
                                    changedWarpRoutes = true;
                                    break;
                                }
                            }
                        }
                    }
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Propagate changes to a cached texture asset.</summary>
    /// <param name="assetName">The asset name that changed.</param>
    /// <param name="allContentManagers">All content managers whose assets may need to be updated.</param>
    /// <param name="loadedTextureManagers">The content managers which were found to have each invalidated texture loaded.</param>
    /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
    /// <returns>Returns whether any assets were updated.</returns>
    private bool PropagateTexture(IAssetName assetName, IList<IContentManager> allContentManagers, IReadOnlyDictionary<IAssetName, List<IContentManager>>? loadedTextureManagers, bool ignoreWorld)
    {
        bool changed = false;

        // get default language
        // This method replaces the textures that would be loaded if you called `contentManager.Load<Texture2D>(assetName)`,
        // which internally maps to `contentManager.LoadLocalized<Texture2D>(assetName, currentLanguage)` regardless of
        // the asset name's language. If the asset name includes a locale, `LoadLocalized` handles that internally.
        LocalizedContentManager.LanguageCode currentLanguage = LocalizedContentManager.CurrentLanguageCode;

        // update textures in-place (0 = localized asset name, 1 = base asset name)
        for (int i = 0; i < 2; i++)
        {
            bool forLocalizedAsset = i == 0;

            // if the asset name is non-localized, only propagate it once
            if (forLocalizedAsset && assetName.LocaleCode is null)
                continue;

            // get asset name to replace
            // We propagate non-textures by comparing base asset names, to update any localized version like
            // `asset.fr-FR` too. We need to check every content manager for in-place texture edits though, so we
            // should avoid iterating their assets if possible. So here we just check for the current localized name
            // and base name, which should cover normal cases.
            IAssetName name = forLocalizedAsset
                ? assetName
                : assetName.GetBaseAssetName();

            // Apply only to the content managers found during invalidation. If this is the base-name half of a
            // localized invalidation, it may not have been part of that exact cache lookup; retain the full scan
            // for that uncommon compatibility case.
            IEnumerable<IContentManager> contentManagers = loadedTextureManagers?.TryGetValue(name, out List<IContentManager>? knownManagers) is true
                ? knownManagers
                : allContentManagers;

            // Load one temporary replacement only if a matching cached texture still exists. Avoid Lazy here: this
            // is already a delayed branch, and a Lazy plus its capturing factory would be allocated for every
            // invalidated texture. Always dispose the temporary in case a target texture rejects the copy.
            Texture2D? newTexture = null;
            bool triedLoadingNewTexture = false;
            try
            {
                foreach (IContentManager contentManager in contentManagers)
                {
                    if (!contentManager.IsLoaded(name))
                        continue;

                    if (!triedLoadingNewTexture)
                    {
                        triedLoadingNewTexture = true;
                        if (this.DisposableContentManager.DoesAssetExist<Texture2D>(name))
                            newTexture = this.DisposableContentManager.LoadLocalized<Texture2D>(name, currentLanguage, useCache: false);
                        else
                            this.Monitor.Log($"Skipped reload for '{name.Name}' because the underlying asset no longer exists.", LogLevel.Warn);
                    }

                    if (newTexture is null)
                        break;

                    Texture2D texture = contentManager.LoadLocalized<Texture2D>(name, currentLanguage, useCache: true);
                    texture.CopyFromTexture(newTexture);
                    changed = true;
                }
            }
            finally
            {
                newTexture?.Dispose();
            }
        }

        // update game state if needed
        if (changed)
        {
            TextureAssetRoute route = CoreAssetPropagator.GetTextureAssetRoute(assetName.Name);
            switch (route)
            {
                case TextureAssetRoute.PlayerSprites:
                    this.UpdatePlayerSprites(assetName);
                    break;

                case TextureAssetRoute.Tools:
                    Game1.ResetToolSpriteSheet();
                    break;

                case TextureAssetRoute.None:
                    if (!ignoreWorld)
                    {
                        if (assetName.IsDirectlyUnderPath("Buildings") && assetName.BaseName.EndsWith("_PaintMask", StringComparison.OrdinalIgnoreCase))
                            return this.UpdateBuildingPaintMask(assetName);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown texture asset propagation route '{route}'.");
            }
        }

        return changed;
    }

    /// <summary>Propagate changes to an asset which isn't a map (handled by <see cref="PropagateMap"/>) or texture (handled by <see cref="PropagateTexture"/>).</summary>
    /// <param name="assetName">The asset name that changed.</param>
    /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
    /// <param name="charactersByName">The NPCs indexed by exact name for a multi-asset propagation batch, if applicable.</param>
    /// <returns>Returns whether any assets were updated.</returns>
    [SuppressMessage("ReSharper", "StringLiteralTypo", Justification = "These deliberately match the asset names.")]
    private bool PropagateOther(IAssetName assetName, bool ignoreWorld, Dictionary<string, List<NPC>>? charactersByName)
    {
        var content = this.MainContentManager;
        string baseName = assetName.BaseName;
        OtherAssetRoute route = CoreAssetPropagator.GetOtherAssetRoute(baseName);

        switch (route)
        {
            /****
            ** Content/Data
            ****/
            case OtherAssetRoute.Achievements: // Game1.LoadContent
                Game1.achievements = DataLoader.Achievements(content);
                return true;

            case OtherAssetRoute.AudioChanges:
                Game1.CueModification.OnStartup(); // reload file and reapply changes
                return true;

            case OtherAssetRoute.BigCraftables: // Game1.LoadContent
                Game1.bigCraftableData = DataLoader.BigCraftables(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Boots: // BootsDataDefinition
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Buildings: // Game1.LoadContent
                Game1.buildingData = DataLoader.Buildings(content);
                if (!ignoreWorld)
                {
                    Utility.ForEachBuilding(building =>
                    {
                        building.ReloadBuildingData();
                        return true;
                    });
                }
                return true;

            case OtherAssetRoute.ChairTiles: // GameLocation.loadMap
                if (!ignoreWorld)
                {
                    Utility.ForEachLocation(location =>
                    {
                        if (Context.IsMainPlayer || location.IsTemporary)
                            this.Reflection.GetField<bool>(location, "_mapSeatsDirty").SetValue(true);

                        return true;
                    });
                }
                return true;

            case OtherAssetRoute.Characters: // Game1.LoadContent
                Game1.characterData = DataLoader.Characters(content);
                if (!ignoreWorld)
                    this.UpdateCharacterData();
                return true;

            case OtherAssetRoute.Concessions: // MovieTheater.GetConcessions
                MovieTheater.ClearCachedLocalizedData();
                return true;

            case OtherAssetRoute.ConcessionTastes: // MovieTheater.GetConcessionTasteForCharacter
                MovieTheater.ClearCachedConcessionTastes();
                return true;

            case OtherAssetRoute.CookingRecipes: // CraftingRecipe.InitShared
                CraftingRecipe.cookingRecipes = DataLoader.CookingRecipes(content);
                return true;

            case OtherAssetRoute.CraftingRecipes: // CraftingRecipe.InitShared
                CraftingRecipe.craftingRecipes = DataLoader.CraftingRecipes(content);
                return true;

            case OtherAssetRoute.Crops: // Game1.LoadContent
                Game1.cropData = DataLoader.Crops(content);
                return true;

            case OtherAssetRoute.FarmAnimals: // FarmAnimal constructor
                Game1.farmAnimalData = DataLoader.FarmAnimals(content);
                if (!ignoreWorld)
                    this.UpdateFarmAnimalData();
                return true;

            case OtherAssetRoute.FloorsAndPaths: // Game1.LoadContent
                Game1.floorPathData = DataLoader.FloorsAndPaths(content);
                return true;

            case OtherAssetRoute.Furniture: // FurnitureDataDefinition
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.FruitTrees: // Game1.LoadContent
                Game1.fruitTreeData = DataLoader.FruitTrees(content);
                return true;

            case OtherAssetRoute.HairData: // Farmer.GetHairStyleMetadataFile
                return this.UpdateHairData();

            case OtherAssetRoute.Hats: // HatDataDefinition
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.JukeboxTracks: // Game1.LoadContent
                Game1.jukeboxTrackData = DataLoader.JukeboxTracks(content);
                return true;

            case OtherAssetRoute.Locations: // Game1.LoadContent
                Game1.locationData = DataLoader.Locations(content);
                return true;

            case OtherAssetRoute.LocationContexts: // Game1.LoadContent
                Game1.locationContextData = DataLoader.LocationContexts(content);
                return true;

            case OtherAssetRoute.Movies: // MovieTheater.GetMovieData / GetMovieReactions
                MovieTheater.ClearCachedLocalizedData();
                return true;

            case OtherAssetRoute.NpcGiftTastes: // Game1.LoadContent
                Game1.NPCGiftTastes = DataLoader.NpcGiftTastes(content);
                return true;

            case OtherAssetRoute.Objects: // Game1.LoadContent
                Game1.objectData = DataLoader.Objects(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Pants: // Game1.LoadContent
                Game1.pantsData = DataLoader.Pants(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Pets: // Game1.LoadContent
                Game1.petData = DataLoader.Pets(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Shirts: // Game1.LoadContent
                Game1.shirtData = DataLoader.Shirts(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.Tools: // Game1.LoadContent
                Game1.toolData = DataLoader.Tools(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.TriggerActions:
                TriggerActionManager.ResetDataCache();
                return true;

            case OtherAssetRoute.Weapons: // Game1.LoadContent
                Game1.weaponData = DataLoader.Weapons(content);
                ItemRegistry.ResetCache();
                return true;

            case OtherAssetRoute.WildTrees: // Tree
                Tree.ClearCache();
                return true;

            case OtherAssetRoute.WorldMap: // WorldMapManager
                WorldMapManager.ReloadData();
                return true;

            /****
            ** Content/Fonts
            ****/
            case OtherAssetRoute.SpriteFont1: // Game1.LoadContent
                Game1.dialogueFont = content.Load<SpriteFont>(baseName);
                return true;

            case OtherAssetRoute.SmallFont: // Game1.LoadContent
                Game1.smallFont = content.Load<SpriteFont>(baseName);
                return true;

            case OtherAssetRoute.TinyFont: // Game1.LoadContent
                Game1.tinyFont = content.Load<SpriteFont>(baseName);
                return true;

            /****
            ** Content/Strings
            ****/
            case OtherAssetRoute.StringsFromCsFiles:
                return this.UpdateStringsFromCsFiles(content);

            /****
            ** Dynamic keys
            ****/
            case OtherAssetRoute.None:
                if (!ignoreWorld)
                {
                    if (assetName.IsDirectlyUnderPath("Characters/Dialogue"))
                        return this.UpdateNpcDialogue(assetName, charactersByName);

                    if (assetName.IsDirectlyUnderPath("Characters/schedules"))
                        return this.UpdateNpcSchedules(assetName, charactersByName);
                }

                return false;

            default:
                throw new InvalidOperationException($"Unknown core asset propagation route '{route}'.");
        }
    }


    /*********
    ** Private methods
    *********/
    /****
    ** Update texture methods
    ****/
    /// <summary>Update building paint mask textures.</summary>
    /// <param name="assetName">The asset name to update.</param>
    /// <returns>Returns whether any textures were updated.</returns>
    private bool UpdateBuildingPaintMask(IAssetName assetName)
    {
        // remove from paint mask cache
        bool removedFromCache = BuildingPainter.paintMaskLookup.Remove(assetName.BaseName) | BuildingPainter.paintMaskLookup.Remove(assetName.BaseName.Replace('/', '\\'));

        // reload building textures
        bool anyReloaded = false;
        foreach (GameLocation location in this.GetLocations(buildingInteriors: false))
        {
            foreach (Building building in location.buildings)
            {
                if (building.paintedTexture != null && assetName.IsEquivalentTo(building.textureName() + "_PaintMask"))
                {
                    anyReloaded = true;
                    building.resetTexture();
                }
            }
        }

        return removedFromCache || anyReloaded;
    }

    /// <summary>Update the sprites for matching players.</summary>
    /// <param name="assetName">The asset name to update.</param>
    private void UpdatePlayerSprites(IAssetName assetName)
    {
        // reset recolors
        FarmerRenderer.recolorOffsets?.Clear();

        // reset local player
        // This is handled separately since Game1.getOnlineFarmers() doesn't include the local player before the save is loaded
        if (this.IsSameBaseName(assetName, Game1.player?.getTexture()))
            Game1.player.FarmerRenderer.MarkSpriteDirty();

        // reset other player
        foreach (Farmer player in Game1.getOnlineFarmers())
        {
            if (!object.ReferenceEquals(player, Game1.player) && this.IsSameBaseName(assetName, player.getTexture()))
                player.FarmerRenderer.MarkSpriteDirty();
        }
    }

    /****
    ** Update data methods
    ****/
    /// <summary>Update the data for matching farm animals.</summary>
    /// <returns>Returns whether any farm animals were updated.</returns>
    /// <remarks>Derived from the <see cref="FarmAnimal"/> constructor.</remarks>
    private void UpdateFarmAnimalData()
    {
        foreach (FarmAnimal animal in this.GetFarmAnimals())
        {
            var data = animal.GetAnimalData();
            if (data != null)
                animal.buildingTypeILiveIn.Value = data.House;
        }
    }

    /// <summary>Update hair style metadata.</summary>
    /// <returns>Returns whether any data was updated.</returns>
    /// <remarks>Derived from the <see cref="Farmer.GetHairStyleMetadataFile"/> and <see cref="Farmer.GetHairStyleMetadata"/>.</remarks>
    private bool UpdateHairData()
    {
        if (Farmer.hairStyleMetadataFile == null)
            return false;

        Farmer.hairStyleMetadataFile = null;
        Farmer.allHairStyleIndices = null;
        Farmer.hairStyleMetadata.Clear();

        return true;
    }

    /// <summary>Update the dialogue data for matching NPCs.</summary>
    /// <param name="assetName">The asset name to update.</param>
    /// <param name="charactersByName">The NPCs indexed by exact name for a multi-asset propagation batch, if applicable.</param>
    /// <returns>Returns whether any NPCs were updated.</returns>
    private bool UpdateNpcDialogue(IAssetName assetName, Dictionary<string, List<NPC>>? charactersByName)
    {
        string name = Path.GetFileName(assetName.BaseName);
        IReadOnlyList<NPC> characters = this.GetCharacters(name, charactersByName);

        // update dialogue
        // Note that marriage dialogue isn't reloaded after reset, but it doesn't need to be
        // propagated anyway since marriage dialogue keys can't be added/removed and the field
        // doesn't store the text itself.
        bool anyChanged = false;
        foreach (NPC npc in characters)
        {
            if (npc.Name != name || !npc.IsVillager)
                continue;

            bool shouldSayMarriageDialogue = npc.shouldSayMarriageDialogue.Value;
            MarriageDialogueReference[] marriageDialogue = npc.currentMarriageDialogue.ToArray();

            npc.resetSeasonalDialogue(); // doesn't only affect seasonal dialogue
            npc.resetCurrentDialogue();

            npc.shouldSayMarriageDialogue.Set(shouldSayMarriageDialogue);
            npc.currentMarriageDialogue.Set(marriageDialogue);

            anyChanged = true;
        }

        return anyChanged;
    }

    /// <summary>Update the character data for matching NPCs.</summary>
    private void UpdateCharacterData()
    {
        foreach (NPC npc in this.GetCharacters())
        {
            if (npc.IsVillager)
                npc.reloadData();
        }
    }

    /// <summary>Update the schedules for matching NPCs.</summary>
    /// <param name="assetName">The asset name to update.</param>
    /// <param name="charactersByName">The NPCs indexed by exact name for a multi-asset propagation batch, if applicable.</param>
    /// <returns>Returns whether any NPCs were updated.</returns>
    private bool UpdateNpcSchedules(IAssetName assetName, Dictionary<string, List<NPC>>? charactersByName)
    {
        string name = Path.GetFileName(assetName.BaseName);
        IReadOnlyList<NPC> characters = this.GetCharacters(name, charactersByName);

        // update schedules
        bool anyChanged = false;
        foreach (NPC npc in characters)
        {
            if (npc.Name != name || !npc.IsVillager)
                continue;

            // reload schedule
            this.Reflection.GetField<bool>(npc, "_hasLoadedMasterScheduleData").SetValue(false);
            this.Reflection.GetField<Dictionary<string, string>?>(npc, "_masterScheduleData").SetValue(null);
            npc.TryLoadSchedule();

            // switch to new schedule if needed
            if (npc.Schedule != null)
            {
                int lastScheduleTime = 0;
                bool foundScheduleTime = false;
                foreach (int scheduleTime in npc.Schedule.Keys)
                {
                    if (scheduleTime <= Game1.timeOfDay && (!foundScheduleTime || scheduleTime > lastScheduleTime))
                    {
                        lastScheduleTime = scheduleTime;
                        foundScheduleTime = true;
                    }
                }

                if (lastScheduleTime != 0)
                {
                    npc.queuedSchedulePaths.Clear();
                    npc.lastAttemptedSchedule = 0;
                    npc.checkSchedule(lastScheduleTime);
                }
            }

            anyChanged = true;
        }

        return anyChanged;
    }

    /// <summary>Update cached translations from the <c>Strings\StringsFromCSFiles</c> asset.</summary>
    /// <param name="content">The content manager through which to reload the asset.</param>
    /// <returns>Returns whether any data was updated.</returns>
    /// <remarks>Derived from the <see cref="Game1.TranslateFields"/>.</remarks>
    private bool UpdateStringsFromCsFiles(LocalizedContentManager content)
    {
        Game1.samBandName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2156");
        Game1.elliottBookName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2157");

        string[] dayNames = this.Reflection.GetField<string[]>(typeof(Game1), "_shortDayDisplayName").GetValue();
        dayNames[0] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3042");
        dayNames[1] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3043");
        dayNames[2] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3044");
        dayNames[3] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3045");
        dayNames[4] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3046");
        dayNames[5] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3047");
        dayNames[6] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3048");

        return true;
    }

    /****
    ** Update map methods
    ****/
    /// <summary>Update the map for a location.</summary>
    /// <param name="locationInfo">The location whose map to update.</param>
    private void UpdateMap(LocationInfo locationInfo)
    {
        GameLocation location = locationInfo.Location;
        Vector2? playerPos = Game1.player?.Position;

        // remove from multiplayer cache
        this.Multiplayer.cachedMultiplayerMaps.Remove(location.NameOrUniqueName);

        // reload map
        location.interiorDoors.Clear(); // prevent errors when doors try to update tiles which no longer exist
        location.reloadMap();

        // reload interior doors
        location.interiorDoors.Clear();
        location.interiorDoors.ResetSharedState(); // load doors from map properties
        location.interiorDoors.ResetLocalState(); // reapply door tiles

        // update for changes
        location.updateWarps();
        location.updateDoors();
        locationInfo.ParentBuilding?.updateInteriorWarps();
        if (locationInfo.AdditionalParentBuildings != null)
        {
            foreach (Building building in locationInfo.AdditionalParentBuildings)
                building.updateInteriorWarps();
        }

        // reapply map changes
        // This must happen after updating warps (since some map modifications like the community shortcuts add warps)
        // and after resetting interior doors' state (so they apply their modifications too).
        if (location is FarmHouse)
            this.Reflection.GetField<bool>(location, "displayingSpouseRoom").SetValue(false);
        location.MakeMapModifications(force: true);

        // update fridge position
        switch (location)
        {
            case FarmHouse farmhouse:
                farmhouse.fridgePosition = farmhouse.GetFridgePositionFromMap() ?? Point.Zero;
                break;

            case IslandFarmHouse farmhouse:
                farmhouse.fridgePosition = farmhouse.GetFridgePositionFromMap() ?? Point.Zero;
                break;
        }

        // reset player position
        // The game may move the player as part of the map changes, even if they're not in that
        // location. That's not needed in this case, and it can have weird effects like players
        // warping onto the wrong tile (or even off-screen) if a patch changes the farmhouse
        // map on location change.
        if (playerPos.HasValue)
            Game1.player!.Position = playerPos.Value;
    }

    /****
    ** Helpers
    ****/
    /// <summary>Get all NPCs in the game (excluding farm animals).</summary>
    private IReadOnlyList<NPC> GetCharacters()
    {
        return this.WorldCache.GetOrSet(
            nameof(this.GetCharacters),
            this,
            static propagator =>
            {
                List<NPC> characters = [];

                foreach (GameLocation location in propagator.GetLocations())
                {
                    foreach (NPC character in location.characters)
                        characters.Add(character);
                }

                if (Game1.CurrentEvent?.actors != null)
                {
                    foreach (NPC character in Game1.CurrentEvent.actors)
                        characters.Add(character);
                }

                return characters;
            }
        );
    }

    /// <summary>Get all NPCs in the game (excluding farm animals), indexed by their exact name.</summary>
    private Dictionary<string, List<NPC>> GetCharactersByName()
    {
        return this.WorldCache.GetOrSet(
            nameof(this.GetCharactersByName),
            this,
            static propagator =>
            {
                Dictionary<string, List<NPC>> charactersByName = new(StringComparer.Ordinal);

                foreach (NPC character in propagator.GetCharacters())
                {
                    if (!charactersByName.TryGetValue(character.Name, out List<NPC>? matches))
                        charactersByName[character.Name] = matches = [];

                    matches.Add(character);
                }

                return charactersByName;
            }
        );
    }

    /// <summary>Get the NPC candidates for an exact name match.</summary>
    /// <param name="name">The exact NPC name.</param>
    /// <param name="charactersByName">The NPCs indexed by exact name for a multi-asset propagation batch, if applicable.</param>
    private IReadOnlyList<NPC> GetCharacters(string name, Dictionary<string, List<NPC>>? charactersByName)
    {
        if (charactersByName is null)
            return this.GetCharacters();

        return charactersByName.TryGetValue(name, out List<NPC>? matches)
            ? matches
            : Array.Empty<NPC>();
    }

    /// <summary>Get all farm animals in the game.</summary>
    private IReadOnlyList<FarmAnimal> GetFarmAnimals()
    {
        return this.WorldCache.GetOrSet(
            nameof(this.GetFarmAnimals),
            this,
            static propagator =>
            {
                List<FarmAnimal> animals = [];

                foreach (GameLocation location in propagator.GetLocations())
                {
                    if (location.animals.Length > 0)
                    {
                        foreach (FarmAnimal animal in location.animals.Values)
                            animals.Add(animal);
                    }
                }

                return animals;
            }
        );
    }

    /// <summary>Get all locations in the game.</summary>
    /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
    private IReadOnlyList<GameLocation> GetLocations(bool buildingInteriors = true)
    {
        return this.WorldCache.GetOrSet(
            buildingInteriors
                ? nameof(this.GetLocations) + "_True"
                : nameof(this.GetLocations) + "_False",
            (Propagator: this, BuildingInteriors: buildingInteriors),
            static state =>
            {
                IReadOnlyList<LocationInfo> locationsWithInfo = state.Propagator.GetLocationsWithInfo(state.BuildingInteriors);
                GameLocation[] locations = new GameLocation[locationsWithInfo.Count];
                for (int i = 0; i < locations.Length; i++)
                    locations[i] = locationsWithInfo[i].Location;

                return locations;
            }
        );
    }

    /// <summary>Get all locations in the game.</summary>
    /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
    private IReadOnlyList<LocationInfo> GetLocationsWithInfo(bool buildingInteriors = true)
    {
        return this.WorldCache.GetOrSet(
            buildingInteriors
                ? nameof(this.GetLocationsWithInfo) + "_True"
                : nameof(this.GetLocationsWithInfo) + "_False",
            buildingInteriors,
            static includeBuildingInteriors => WorldLocationUtilities.GetLocations(includeBuildingInteriors)
        );
    }

    /// <summary>Get all locations in the game, indexed by the map assets which can update them.</summary>
    /// <param name="spouseRoomMapPathCache">A cache of spouse room map path lookups by farmhouse or cabin instance.</param>
    private Dictionary<IAssetName, List<LocationInfo>> GetLocationsByMapName(ref Dictionary<FarmHouse, string?>? spouseRoomMapPathCache)
    {
        Dictionary<IAssetName, List<LocationInfo>> locationsByMapName = [];

        foreach (LocationInfo info in this.GetLocationsWithInfo())
        {
            this.AddLocationByMapName(locationsByMapName, info, info.Location.mapPath.Value);

            if (info.Location is FarmHouse farmhouse)
            {
                string? spouseRoomMapPath = this.GetDisplayedSpouseRoomPath(farmhouse, ref spouseRoomMapPathCache);
                this.AddLocationByMapName(locationsByMapName, info, spouseRoomMapPath);
            }
        }

        return locationsByMapName;
    }

    /// <summary>Add a location to a map asset index.</summary>
    /// <param name="locationsByMapName">The index to update.</param>
    /// <param name="location">The location to index.</param>
    /// <param name="rawMapName">The raw map asset name.</param>
    private void AddLocationByMapName(Dictionary<IAssetName, List<LocationInfo>> locationsByMapName, LocationInfo location, string? rawMapName)
    {
        IAssetName? mapName = this.ParseAssetNameOrNull(rawMapName)?.GetBaseAssetName();
        if (mapName is null)
            return;

        if (!locationsByMapName.TryGetValue(mapName, out List<LocationInfo>? locations))
            locationsByMapName[mapName] = locations = [];

        // A farmhouse can reference the same asset as both its main map and spouse-room map. The direct scan
        // updates that location only once for an asset, so preserve that behavior in the index.
        if (locations.Count == 0 || !ReferenceEquals(locations[^1].Location, location.Location))
            locations.Add(location);
    }

    /// <summary>Get the asset name for a farmhouse's spouse room, if it's currently displaying one.</summary>
    /// <param name="farmhouse">The farmhouse whose spouse room to get.</param>
    /// <param name="cache">A cache of spouse room map path lookups by farmhouse or cabin instance. This is created if it's null.</param>
    private string? GetDisplayedSpouseRoomPath(FarmHouse farmhouse, ref Dictionary<FarmHouse, string?>? cache)
    {
        // from cache
        if (cache is null)
            cache = [];
        else if (cache.TryGetValue(farmhouse, out string? spouseRoomMapPath))
            return spouseRoomMapPath;

        // no spouse room shown
        Farmer? owner = farmhouse.owner;
        if (owner?.spouse is null || !this.Reflection.GetField<bool>(farmhouse, "displayingSpouseRoom").GetValue())
        {
            cache[farmhouse] = null;
            return null;
        }

        // else get map path
        string mapPath = NPC.TryGetData(owner.spouse, out CharacterData? spouseData) && spouseData?.SpouseRoom?.MapAsset is { } mapName
            ? $"Maps/{mapName}"
            : "Maps/spouseRooms";
        cache[farmhouse] = mapPath;
        return mapPath;
    }

    /// <summary>Get whether two asset names are equivalent if you ignore the locale code.</summary>
    /// <param name="left">The first value to compare.</param>
    /// <param name="right">The second value to compare.</param>
    private bool IsSameBaseName([NotNullWhen(true)] IAssetName? left, [NotNullWhen(true)] string? right)
    {
        if (left is null || right is null)
            return false;

        IAssetName? parsedB = this.ParseAssetNameOrNull(right);
        return this.IsSameBaseName(left, parsedB);
    }

    /// <summary>Get whether two asset names are equivalent if you ignore the locale code.</summary>
    /// <param name="left">The first value to compare.</param>
    /// <param name="right">The second value to compare.</param>
    private bool IsSameBaseName([NotNullWhen(true)] IAssetName? left, [NotNullWhen(true)] IAssetName? right)
    {
        if (left is null || right is null)
            return false;

        return left.IsEquivalentTo(right.BaseName, useBaseName: true);
    }

    /// <summary>Normalize an asset key to match the cache key and assert that it's valid, but don't raise an error for null or empty values.</summary>
    /// <param name="path">The asset key to normalize.</param>
    private IAssetName? ParseAssetNameOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return this.ParseAssetName(path);
    }

    /// <summary>A propagation route for an exact texture asset name.</summary>
    internal enum TextureAssetRoute
    {
        None,
        PlayerSprites,
        Tools
    }

    /// <summary>A propagation route for an exact non-map, non-texture base asset name.</summary>
    internal enum OtherAssetRoute
    {
        None,
        Achievements,
        AudioChanges,
        BigCraftables,
        Boots,
        Buildings,
        ChairTiles,
        Characters,
        Concessions,
        ConcessionTastes,
        CookingRecipes,
        CraftingRecipes,
        Crops,
        FarmAnimals,
        FloorsAndPaths,
        Furniture,
        FruitTrees,
        HairData,
        Hats,
        JukeboxTracks,
        Locations,
        LocationContexts,
        Movies,
        NpcGiftTastes,
        Objects,
        Pants,
        Pets,
        Shirts,
        Tools,
        TriggerActions,
        Weapons,
        WildTrees,
        WorldMap,
        SpriteFont1,
        SmallFont,
        TinyFont,
        StringsFromCsFiles
    }

}
