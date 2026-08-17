using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BmFont;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Extensions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Toolkit.Serialization;
using StardewModdingAPI.Toolkit.Utilities;
using StardewModdingAPI.Toolkit.Utilities.PathLookups;
using StardewValley;
using xTile;
using xTile.Format;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ContentManagers;

/// <summary>A content manager which handles reading files from a SMAPI mod folder with support for unpacked files.</summary>
internal sealed class ModContentManager : BaseContentManager
{
    /*********
    ** Fields
    *********/
    /// <summary>A path segment which navigates to the parent directory.</summary>
    private const string DirectoryClimbingPathSegment = "..";

    /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
    private readonly JsonHelper JsonHelper;

    /// <summary>The mod display name to show in errors.</summary>
    private readonly string ModName;

    /// <summary>The game content manager used for map tilesheets not provided by the mod.</summary>
    private readonly IContentManager GameContentManager;

    /// <summary>A lookup for files within the <see cref="ContentManager.RootDirectory"/>.</summary>
    private readonly IFileLookup FileLookup;

    /// <summary>A shared byte-bounded cache of decoded image pixels.</summary>
    private readonly DecodedTextureCache DecodedTextures;

    /// <summary>If a map tilesheet's image source has no file extensions, the file extensions to check for in the local mod folder.</summary>
    private static readonly string[] LocalTilesheetExtensions = [".png", ".xnb"];


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
    /// <param name="gameContentManager">The game content manager used for map tilesheets not provided by the mod.</param>
    /// <param name="serviceProvider">The service provider to use to locate services.</param>
    /// <param name="modName">The mod display name to show in errors.</param>
    /// <param name="rootDirectory">The root directory to search for content.</param>
    /// <param name="currentCulture">The current culture for which to localize content.</param>
    /// <param name="coordinator">The central coordinator which manages content managers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
    /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
    /// <param name="fileLookup">A lookup for files within the <paramref name="rootDirectory"/>.</param>
    /// <param name="decodedTextures">A shared byte-bounded cache of decoded image pixels.</param>
    public ModContentManager(string name, IContentManager gameContentManager, IServiceProvider serviceProvider, string modName, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, JsonHelper jsonHelper, Action<BaseContentManager> onDisposing, IFileLookup fileLookup, DecodedTextureCache decodedTextures)
        : base(name, serviceProvider, rootDirectory, currentCulture, coordinator, monitor, reflection, onDisposing, isNamespaced: true)
    {
        this.GameContentManager = gameContentManager;
        this.FileLookup = fileLookup;
        this.JsonHelper = jsonHelper;
        this.ModName = modName;
        this.DecodedTextures = decodedTextures;

        this.TryLocalizeKeys = false;
    }

    /// <inheritdoc />
    public override bool DoesAssetExist<T>(IAssetName assetName)
    {
        // Mod content is deliberately never cached, so resolve the real file directly.
        assetName = this.ResolveAssetName(assetName);
        FileInfo file = this.GetModFile<T>(assetName.Name);
        return file.Exists;
    }

    /// <inheritdoc />
    public override T LoadExact<T>(IAssetName assetName, bool useCache)
    {
        //
        // Note: caching is ignored for mod content.
        // This is necessary to avoid assets being shared between content managers, which can
        // cause changes to an asset through one content manager affecting the same asset in
        // others (or even fresh content managers). See https://www.patreon.com/posts/27247161
        // for more background info.
        //

        // get local asset
        assetName = this.ResolveAssetName(assetName);
        T asset;
        try
        {
            // get file
            FileInfo file = this.GetModFile<T>(assetName.Name);
            if (!file.Exists)
                this.ThrowLoadError(assetName, ContentLoadErrorType.AssetDoesNotExist, "the specified path doesn't exist.");

            // load content
            string extension = file.Extension;
            if (extension.Equals(".fnt", StringComparison.OrdinalIgnoreCase))
                asset = this.LoadFont<T>(assetName, file);
            else if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                asset = this.LoadDataFile<T>(assetName, file);
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                asset = this.LoadImageFile<T>(assetName, file);
            else if (extension.Equals(".tbin", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tmx", StringComparison.OrdinalIgnoreCase))
                asset = this.LoadMapFile<T>(assetName, file);
            else if (extension.Equals(".xnb", StringComparison.OrdinalIgnoreCase))
                asset = this.LoadXnbFile<T>(assetName);
            else
                asset = (T)this.HandleUnknownFileType(assetName, file, typeof(T));
        }
        catch (Exception ex)
        {
            if (ex is SContentLoadException)
                throw;

            this.ThrowLoadError(assetName, ContentLoadErrorType.Other, "an unexpected error occurred.", ex);
            return default;
        }

        // track & return asset
        this.TrackAsset(assetName, asset, useCache: false);
        return asset;
    }

    /// <inheritdoc />
    [Obsolete($"Temporary {nameof(ModContentManager)}s are unsupported")]
    public override LocalizedContentManager CreateTemporary()
    {
        throw new NotSupportedException("Can't create a temporary mod content manager.");
    }

    /// <summary>Get the underlying key in the game's content cache for an asset. This does not validate whether the asset exists.</summary>
    /// <param name="key">The local path to a content file relative to the mod folder.</param>
    /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
    public IAssetName GetInternalAssetKey(string key)
    {
        string internalKey = Path.Combine(this.Name, PathUtilities.NormalizeAssetName(key));

        return this.Coordinator.ParseAssetName(internalKey, allowLocales: false);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get the relative mod file path from an asset name.</summary>
    /// <param name="assetName">The asset name to parse.</param>
    /// <exception cref="SContentLoadException">The <paramref name="assetName"/> matches a different mod's content manager.</exception>
    private IAssetName ResolveAssetName(IAssetName assetName)
    {
        if (this.Coordinator.TryParseManagedAssetKey(assetName.Name, out string? contentManagerId, out IAssetName? relativePath))
        {
            if (!ContentCoordinator.AreManagedContentManagerNamesEqual(contentManagerId, this.Name))
                this.ThrowLoadError(assetName, ContentLoadErrorType.AccessDenied, "can't load a different mod's managed asset key through this mod content manager.");

            assetName = relativePath;
        }

        return assetName;
    }

    /// <summary>Load an unpacked font file (<c>.fnt</c>).</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file to load.</param>
    private T LoadFont<T>(IAssetName assetName, FileInfo file)
    {
        this.AssertValidType<T>(assetName, file, typeof(XmlSource));

        string source = File.ReadAllText(file.FullName);
        return (T)(object)new XmlSource(source);
    }

    /// <summary>Load an unpacked data file (<c>.json</c>).</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file to load.</param>
    private T LoadDataFile<T>(IAssetName assetName, FileInfo file)
    {
        if (!this.JsonHelper.ReadJsonFileIfExists(file.FullName, out T? asset))
            this.ThrowLoadError(assetName, ContentLoadErrorType.InvalidData, "the JSON file is invalid."); // should never happen since we check for file existence before calling this method

        return asset;
    }

    /// <summary>Load an unpacked image file (<c>.png</c>).</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file to load.</param>
    private T LoadImageFile<T>(IAssetName assetName, FileInfo file)
    {
        this.AssertValidType<T>(assetName, file, typeof(Texture2D), typeof(IRawTextureData));
        bool returnRawData = typeof(T).IsAssignableTo(typeof(IRawTextureData));

        IRawTextureData raw = this.LoadRawImageData(file, returnRawData);

        if (returnRawData)
            return (T)raw;
        else
        {
            Texture2D texture = new Texture2D(Game1.graphics.GraphicsDevice, raw.Width, raw.Height).SetName(assetName);
            texture.SetData(raw.Data);
            return (T)(object)texture;
        }
    }

    /// <summary>Load the raw image data from a file on disk.</summary>
    /// <param name="file">The file whose data to load.</param>
    /// <param name="forRawData">Whether the data is being loaded for an <see cref="IRawTextureData"/> (true) or <see cref="Texture2D"/> (false) instance.</param>
    /// <remarks>This is separate to let framework mods intercept the data before it's loaded, if needed.</remarks>
    [SuppressMessage("ReSharper", "UnusedParameter.Local", Justification = "The 'forRawData' parameter is only added for mods which may intercept this method.")]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "The 'forRawData' parameter is only added for mods which may intercept this method.")]
    private IRawTextureData LoadRawImageData(FileInfo file, bool forRawData)
    {
        if (this.DecodedTextures.TryGetCopy(file, out RawTextureData? cached))
            return cached;

        // load raw data
        using FileStream stream = File.OpenRead(file.FullName);
        using SKBitmap bitmap = SKBitmap.Decode(stream);

        if (bitmap is null)
            throw new InvalidDataException($"Failed to load {file.FullName}. This doesn't seem to be a valid PNG image.");

        var data = new RawTextureData(bitmap.Width, bitmap.Height, ModContentManager.ConvertPixels(bitmap));
        this.DecodedTextures.Track(file, data);
        return data;
    }

    /// <summary>Convert Skia pixels to the premultiplied XNA pixel format.</summary>
    /// <param name="bitmap">The Skia bitmap to convert.</param>
    /// <returns>The converted XNA pixels.</returns>
    private static Color[] ConvertPixels(SKBitmap bitmap)
    {
        // PNGs normally decode to premultiplied RGBA/BGRA pixels. Read that native buffer directly
        // so Skia doesn't allocate an unpremultiplied array which SMAPI would immediately convert back.
        if (
            bitmap.AlphaType is SKAlphaType.Premul or SKAlphaType.Opaque
            && bitmap.ColorType is SKColorType.Rgba8888 or SKColorType.Bgra8888
        )
        {
            ReadOnlySpan<byte> rawPixels = bitmap.GetPixelSpan();
            if (
                bitmap.ColorType == SKColorType.Rgba8888
                && BitConverter.IsLittleEndian
                && Unsafe.SizeOf<Color>() == 4
            )
                return ModContentManager.CopyRgbaPixels(rawPixels, bitmap.RowBytes, bitmap.Width, bitmap.Height);

            bool isBgra = bitmap.ColorType == SKColorType.Bgra8888;
            int redOffset = isBgra ? 2 : 0;
            int blueOffset = isBgra ? 0 : 2;
            var pixels = GC.AllocateUninitializedArray<Color>(bitmap.Width * bitmap.Height);

            int outputIndex = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int inputIndex = y * bitmap.RowBytes;
                int inputEnd = inputIndex + bitmap.Width * 4;
                for (; inputIndex < inputEnd; inputIndex += 4)
                {
                    byte alpha = rawPixels[inputIndex + 3];
                    pixels[outputIndex++] = alpha == 0
                        ? Color.Transparent
                        : new Color(
                            r: rawPixels[inputIndex + redOffset],
                            g: rawPixels[inputIndex + 1],
                            b: rawPixels[inputIndex + blueOffset],
                            alpha: alpha
                        );
                }
            }

            return pixels;
        }

        // Retain the prior general conversion for an unexpected decode format.
        SKPMColor[] rawPixelsFallback = SKPMColor.PreMultiply(bitmap.Pixels);
        var pixelsFallback = GC.AllocateUninitializedArray<Color>(rawPixelsFallback.Length);
        for (int i = 0; i < pixelsFallback.Length; i++)
        {
            SKPMColor pixel = rawPixelsFallback[i];
            pixelsFallback[i] = pixel.Alpha == 0
                ? Color.Transparent
                : new Color(r: pixel.Red, g: pixel.Green, b: pixel.Blue, alpha: pixel.Alpha);
        }

        return pixelsFallback;
    }

    /// <summary>Copy premultiplied RGBA bytes into XNA's matching packed color layout.</summary>
    /// <param name="rawPixels">The source RGBA pixel buffer.</param>
    /// <param name="rowBytes">The number of source bytes per row, including padding.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The copied XNA pixels.</returns>
    private static Color[] CopyRgbaPixels(ReadOnlySpan<byte> rawPixels, int rowBytes, int width, int height)
    {
        const int bytesPerPixel = 4;
        if (Unsafe.SizeOf<Color>() != bytesPerPixel)
            throw new InvalidOperationException($"Can't copy RGBA pixels into a {Unsafe.SizeOf<Color>()}-byte {nameof(Color)} value.");

        int outputRowBytes = checked(width * bytesPerPixel);
        var pixels = GC.AllocateUninitializedArray<Color>(checked(width * height));
        Span<byte> output = MemoryMarshal.AsBytes(pixels.AsSpan());

        PixelBufferUtility.CopyRows(rawPixels, rowBytes, output, outputRowBytes, height);
        return pixels;
    }

    /// <summary>Load an unpacked image file (<c>.tbin</c> or <c>.tmx</c>).</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file to load.</param>
    private T LoadMapFile<T>(IAssetName assetName, FileInfo file)
    {
        this.AssertValidType<T>(assetName, file, typeof(Map));

        FormatManager formatManager = FormatManager.Instance;
        Map map = formatManager.LoadMap(file.FullName);
        map.assetPath = assetName.Name;
        this.FixTilesheetPaths(map, relativeMapPath: assetName.Name, fixEagerPathPrefixes: false);
        return (T)(object)map;
    }

    /// <summary>Load a packed file (<c>.xnb</c>).</summary>
    /// <typeparam name="T">The type of asset to load.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    private T LoadXnbFile<T>(IAssetName assetName)
    {
        if (typeof(IRawTextureData).IsAssignableFrom(typeof(T)))
            this.ThrowLoadError(assetName, ContentLoadErrorType.Other, $"can't read XNB file as type {typeof(IRawTextureData)}; that type can only be read from a PNG file.");

        // the underlying content manager adds a .xnb extension implicitly, so
        // we need to strip it here to avoid trying to load a '.xnb.xnb' file.
        IAssetName loadName = assetName.Name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
            ? this.Coordinator.ParseAssetName(assetName.Name[..^".xnb".Length], allowLocales: false)
            : assetName;

        // load asset
        T asset = this.RawLoad<T>(loadName, useCache: false);
        if (asset is Map map)
        {
            map.assetPath = loadName.Name;
            this.FixTilesheetPaths(map, relativeMapPath: loadName.Name, fixEagerPathPrefixes: true);
        }

        return asset;
    }

    /// <summary>Handle a request to load a file type that isn't supported by SMAPI.</summary>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file to load.</param>
    /// <param name="assetType">The expected file type.</param>
    private object HandleUnknownFileType(IAssetName assetName, FileInfo file, Type assetType)
    {
        this.ThrowLoadError(assetName, ContentLoadErrorType.InvalidName, $"unknown file extension '{file.Extension}'; must be one of '.fnt', '.json', '.png', '.tbin', '.tmx', or '.xnb'.");
        return assetType.IsValueType
            ? Activator.CreateInstance(assetType)
            : null;
    }

    /// <summary>Assert that the asset type is compatible with one of the allowed types.</summary>
    /// <typeparam name="TAsset">The actual asset type.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file being loaded.</param>
    /// <param name="validType">The allowed asset type.</param>
    /// <exception cref="SContentLoadException">The <typeparamref name="TAsset"/> is not compatible with the <paramref name="validType"/>.</exception>
    private void AssertValidType<TAsset>(IAssetName assetName, FileInfo file, Type validType)
    {
        if (validType.IsAssignableFrom(typeof(TAsset)))
            return;

        this.ThrowLoadError(assetName, ContentLoadErrorType.InvalidData, $"can't read file with extension '{file.Extension}' as type '{typeof(TAsset)}'; must be type '{validType.FullName}'.");
    }

    /// <summary>Assert that the asset type is compatible with one of two allowed types.</summary>
    /// <typeparam name="TAsset">The actual asset type.</typeparam>
    /// <param name="assetName">The asset name relative to the loader root directory.</param>
    /// <param name="file">The file being loaded.</param>
    /// <param name="firstValidType">The first allowed asset type.</param>
    /// <param name="secondValidType">The second allowed asset type.</param>
    /// <exception cref="SContentLoadException">The <typeparamref name="TAsset"/> is not compatible with either allowed type.</exception>
    private void AssertValidType<TAsset>(IAssetName assetName, FileInfo file, Type firstValidType, Type secondValidType)
    {
        if (firstValidType.IsAssignableFrom(typeof(TAsset)) || secondValidType.IsAssignableFrom(typeof(TAsset)))
            return;

        this.ThrowLoadError(assetName, ContentLoadErrorType.InvalidData, $"can't read file with extension '{file.Extension}' as type '{typeof(TAsset)}'; must be type '{firstValidType.FullName}' or '{secondValidType.FullName}'.");
    }

    /// <summary>Throw an error which indicates that an asset couldn't be loaded.</summary>
    /// <param name="errorType">Why loading an asset through the content pipeline failed.</param>
    /// <param name="assetName">The asset name that failed to load.</param>
    /// <param name="reasonPhrase">The reason the file couldn't be loaded.</param>
    /// <param name="exception">The underlying exception, if applicable.</param>
    /// <exception cref="SContentLoadException" />
    [DoesNotReturn]
    [DebuggerStepThrough, DebuggerHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowLoadError(IAssetName assetName, ContentLoadErrorType errorType, string reasonPhrase, Exception? exception = null)
    {
        throw new SContentLoadException(errorType, $"Failed loading asset '{assetName}' from {this.Name}: {reasonPhrase}", exception);
    }

    /// <summary>Get a file from the mod folder.</summary>
    /// <typeparam name="T">The expected asset type.</typeparam>
    /// <param name="path">The asset path relative to the content folder.</param>
    private FileInfo GetModFile<T>(string path)
    {
        // get exact file
        FileInfo file = this.FileLookup.GetFile(path);

        // try with default image extensions
        if (!file.Exists && typeof(Texture2D).IsAssignableFrom(typeof(T)) && !ModContentManager.LocalTilesheetExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string extension in ModContentManager.LocalTilesheetExtensions)
            {
                FileInfo result = new(file.FullName + extension);
                if (result.Exists)
                {
                    file = result;
                    break;
                }
            }
        }

        return file;
    }

    /// <summary>Fix custom map tilesheet paths so they can be found by the content manager.</summary>
    /// <param name="map">The map whose tilesheets to fix.</param>
    /// <param name="relativeMapPath">The relative map path within the mod folder.</param>
    /// <param name="fixEagerPathPrefixes">Whether to undo the game's eager tilesheet path prefixing for maps loaded from an <c>.xnb</c> file, which incorrectly prefixes tilesheet paths with the map's local asset key folder.</param>
    /// <exception cref="ContentLoadException">A map tilesheet couldn't be resolved.</exception>
    private void FixTilesheetPaths(Map map, string relativeMapPath, bool fixEagerPathPrefixes)
    {
        // get map info
        relativeMapPath = this.AssertAndNormalizeAssetName(relativeMapPath); // Mono's Path.GetDirectoryName doesn't handle Windows dir separators
        string relativeMapFolder = Path.GetDirectoryName(relativeMapPath) ?? ""; // folder path containing the map, relative to the mod folder
        string? eagerPathPrefix = fixEagerPathPrefixes && relativeMapFolder.Length > 0
            ? relativeMapFolder + Path.DirectorySeparatorChar
            : null;

        // fix tilesheets
        this.Monitor.VerboseLog($"Fixing tilesheet paths for map '{relativeMapPath}' from mod '{this.ModName}'...");
        foreach (TileSheet tilesheet in map.TileSheets)
        {
            // get image path
            tilesheet.ImageSource = this.NormalizePathSeparators(tilesheet.ImageSource);
            string imageSource = tilesheet.ImageSource;
            if (eagerPathPrefix != null && imageSource?.StartsWith(eagerPathPrefix, StringComparison.OrdinalIgnoreCase) is true)
                imageSource = imageSource[eagerPathPrefix.Length..];

            // ensure path isn't empty
            if (string.IsNullOrWhiteSpace(imageSource))
                throw new SContentLoadException(ContentLoadErrorType.InvalidData, $"{this.ModName} loaded map '{relativeMapPath}' with invalid tilesheet '{tilesheet.Id}'. This tilesheet has no image source.");

            // ensure path is relative
            if (Path.IsPathRooted(imageSource))
                throw this.GetPathError(relativeMapFolder, imageSource, $"Tilesheet paths must not be an absolute path ({Path.GetPathRoot(imageSource)}).");

            // ensure any directory climbing is valid
            // Tilesheet paths are relative to either the `Content/Maps` folder or the map file. Directory climbing is
            // restricted for safety and simplicity:
            //   1. Can only climb at the start of the path (e.g. `../LooseSprites/Cursors` but not `Mines/../Barn`).
            //   2. Can only climb once (to avoid escaping the `Content` folder).
            //   3. Always relative to the `Content/Maps` folder (not the map file).
            if (ModContentManager.HasInvalidTilesheetDirectoryClimb(imageSource))
                throw this.GetPathError(relativeMapFolder, imageSource, $"Directory climbing ({ModContentManager.DirectoryClimbingPathSegment}/) is only permitted once at the start of the path.");

            // load best match
            try
            {
                if (!this.TryGetTilesheetAssetName(relativeMapFolder, imageSource, out IAssetName? assetName, out string? error))
                    throw this.GetPathError(relativeMapFolder, imageSource, error);

                if (assetName is not null)
                {
                    if (!assetName.IsEquivalentTo(tilesheet.ImageSource))
                        this.Monitor.VerboseLog($"   Mapped tilesheet '{tilesheet.ImageSource}' to '{assetName}'.");

                    tilesheet.ImageSource = assetName.Name;
                }
            }
            catch (Exception ex)
            {
                if (ex is SContentLoadException)
                    throw;

                throw this.GetPathError(relativeMapFolder, imageSource, "The tilesheet couldn't be loaded.", ex);
            }
        }
    }

    /// <summary>Get the actual asset name for a tilesheet.</summary>
    /// <param name="modRelativeMapFolder">The folder path containing the map, relative to the mod folder.</param>
    /// <param name="relativePath">The tilesheet path to load.</param>
    /// <param name="assetName">The found asset name.</param>
    /// <param name="error">A message indicating why the file couldn't be loaded, if applicable.</param>
    /// <returns>Returns whether the asset name was found.</returns>
    /// <remarks>See remarks on <see cref="FixTilesheetPaths"/>.</remarks>
    private bool TryGetTilesheetAssetName(string modRelativeMapFolder, string relativePath, out IAssetName? assetName, [NotNullWhen(false)] out string? error)
    {
        error = null;

        // nothing to do
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            assetName = null;
            return true;
        }

        // special case: local filenames starting with a dot should be ignored
        // For example, this lets mod authors have a '.spring_town.png' file in their map folder so it can be
        // opened in Tiled, while still mapping it to the vanilla 'Maps/spring_town' asset at runtime.
        {
            string filename = Path.GetFileName(relativePath);
            if (filename.StartsWith('.'))
                relativePath = Path.Combine(Path.GetDirectoryName(relativePath) ?? "", filename.TrimStart('.'));
        }

        // get relative to map file unless path has directory climbing
        if (!ModContentManager.HasLeadingTilesheetDirectoryClimb(relativePath)) // directory climbing can only be at the start of the path
        {
            string localKey = Path.Combine(modRelativeMapFolder, relativePath);
            if (this.GetModFile<Texture2D>(localKey).Exists)
            {
                assetName = this.GetInternalAssetKey(localKey);
                return true;
            }
        }

        // get from game assets
        AssetName contentKey = this.Coordinator.ParseAssetName(ModContentManager.GetContentKeyForTilesheetImageSource(relativePath), allowLocales: false);
        try
        {
            this.GameContentManager.LoadLocalized<Texture2D>(contentKey, this.GameContentManager.Language, useCache: true); // no need to bypass cache here, since we're not storing the asset
            assetName = contentKey;
            return true;
        }
        catch
        {
            // ignore file-not-found errors
            // TODO: while it's useful to suppress an asset-not-found error here to avoid
            // confusion, this is a pretty naive approach. Even if the file doesn't exist,
            // the file may have been loaded through a load operation which failed. So even
            // if the content file doesn't exist, that doesn't mean the error here is a
            // content-not-found error. Unfortunately XNA doesn't provide a good way to
            // detect the error type.
            if (this.GetContentFolderFileExists(contentKey.Name))
                throw;
        }

        // not found
        assetName = null;
        error = "The tilesheet couldn't be found relative to either the map file or the game's content folder.";
        return false;
    }

    /// <summary>Get whether a file from the game's content folder exists.</summary>
    /// <param name="key">The asset key.</param>
    private bool GetContentFolderFileExists(string key)
    {
        // get file path
        string path = Path.Combine(this.GameContentManager.FullRootDirectory, key);
        if (!path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            path += ".xnb";

        // get file
        return File.Exists(path);
    }

    /// <summary>Get the asset key for a tilesheet in the game's <c>Maps</c> content folder.</summary>
    /// <param name="relativePath">The tilesheet image source.</param>
    internal static string GetContentKeyForTilesheetImageSource(string relativePath)
    {
        string key = relativePath;

        // make path relative to Content folder
        if (ModContentManager.HasLeadingTilesheetDirectoryClimb(key))
            key = key.Length == ModContentManager.DirectoryClimbingPathSegment.Length ? "" : key[(ModContentManager.DirectoryClimbingPathSegment.Length + 1)..];
        else if (!ModContentManager.StartsWithPathSegment(key, "Maps", StringComparison.OrdinalIgnoreCase))
            key = Path.Combine("Maps", key);

        // remove legacy physical file extensions from the game asset key
        if (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            key = key[..^4];

        return key;
    }

    /// <summary>Get whether a normalized tilesheet path climbs to the parent directory at its first segment.</summary>
    internal static bool HasLeadingTilesheetDirectoryClimb(string path)
    {
        return ModContentManager.StartsWithPathSegment(path, ModContentManager.DirectoryClimbingPathSegment, StringComparison.Ordinal);
    }

    /// <summary>Get whether a normalized tilesheet path climbs after its first segment or more than once.</summary>
    internal static bool HasInvalidTilesheetDirectoryClimb(string path)
    {
        int segmentStart = 0;
        while (segmentStart < path.Length)
        {
            int separatorIndex = path.IndexOf(PathUtilities.PreferredAssetSeparator, segmentStart);
            int segmentEnd = separatorIndex >= 0 ? separatorIndex : path.Length;
            bool isClimb = segmentEnd - segmentStart == ModContentManager.DirectoryClimbingPathSegment.Length
                && path.AsSpan(segmentStart, ModContentManager.DirectoryClimbingPathSegment.Length).SequenceEqual(ModContentManager.DirectoryClimbingPathSegment);
            if (isClimb && segmentStart != 0)
                return true;

            if (separatorIndex < 0)
                break;
            segmentStart = separatorIndex + 1;
        }

        return false;
    }

    /// <summary>Get whether a normalized path starts with the exact segment.</summary>
    private static bool StartsWithPathSegment(string path, string segment, StringComparison comparison)
    {
        return path.StartsWith(segment, comparison)
            && (path.Length == segment.Length || path[segment.Length] == PathUtilities.PreferredAssetSeparator);
    }

    /// <summary>Get an exception indicating that a map asset can't be loaded because one of its tilesheets has an invalid path.</summary>
    /// <param name="relativeMapPath">The relative path to the folder which contains the map asset, relative to the mod folder.</param>
    /// <param name="imageSource">The tilesheet's path to the image file.</param>
    /// <param name="error">The error message indicating why loading it failed. (Basic metadata like the image source is prepended automatically.)</param>
    /// <param name="ex">The inner exception which caused the error, if applicable.</param>
    private SContentLoadException GetPathError(string relativeMapPath, string imageSource, string error, Exception? ex = null)
    {
        return new SContentLoadException(ContentLoadErrorType.InvalidData, $"{this.ModName} loaded map '{relativeMapPath}' with invalid tilesheet path '{imageSource}'. {error}", ex);
    }
}
