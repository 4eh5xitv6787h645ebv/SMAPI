using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Extensions;
using StardewModdingAPI.Framework.ModLoading.Framework;
using StardewModdingAPI.Framework.ModLoading.Symbols;
using StardewModdingAPI.Metadata;
using StardewModdingAPI.Toolkit.Framework.BundledModData;
using StardewModdingAPI.Toolkit.Utilities;

namespace StardewModdingAPI.Framework.ModLoading;

/// <summary>Preprocesses and loads mod assemblies.</summary>
internal class AssemblyLoader : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>Metadata for mapping assemblies to the current platform.</summary>
    private readonly PlatformAssemblyMap AssemblyMap;

    /// <summary>A type => assembly lookup for types which should be rewritten.</summary>
    private readonly IDictionary<string, Assembly> TypeAssemblies;

    /// <summary>A minimal assembly definition resolver which resolves references to known loaded assemblies.</summary>
    private readonly AssemblyDefinitionResolver AssemblyDefinitionResolver;

    /// <summary>Provides assembly symbol writers for Mono.Cecil.</summary>
    private readonly SymbolWriterProvider SymbolWriterProvider = new();

    /// <summary>The objects to dispose as part of this instance.</summary>
    private readonly HashSet<IDisposable> Disposables = [];

    /// <summary>The instruction finders and rewriters to apply.</summary>
    private readonly IInstructionHandler[] InstructionHandlers;

    /// <summary>Whether to rewrite mods for compatibility.</summary>
    private readonly bool RewriteMods;

    /// <summary>Whether to include more technical details about broken mods in the TRACE logs. This is mainly useful for creating compatibility rewriters.</summary>
    private readonly bool LogTechnicalDetailsForBrokenMods;

    /// <summary>The simple names of assemblies currently loaded in the application domain.</summary>
    private readonly ConcurrentDictionary<string, byte> LoadedAssemblyNames;

    /// <summary>Compare canonical assembly file paths for the current platform.</summary>
    private readonly StringComparer AssemblyPathComparer;

    /// <summary>Persists content-addressed rewrite results across launches.</summary>
    private readonly AssemblyRewriteCache RewriteCache;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="targetPlatform">The current game platform.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="paranoidMode">Whether to detect paranoid mode issues.</param>
    /// <param name="rewriteMods">Whether to rewrite mods for compatibility.</param>
    /// <param name="logTechnicalDetailsForBrokenMods">Whether to include more technical details about broken mods in the TRACE logs. This is mainly useful for creating compatibility rewriters.</param>
    public AssemblyLoader(Platform targetPlatform, IMonitor monitor, bool paranoidMode, bool rewriteMods, bool logTechnicalDetailsForBrokenMods)
    {
        this.Monitor = monitor;
        this.RewriteMods = rewriteMods;
        this.LogTechnicalDetailsForBrokenMods = logTechnicalDetailsForBrokenMods;
        this.AssemblyMap = this.TrackForDisposal(Constants.GetAssemblyMap(targetPlatform));
        this.LoadedAssemblyNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        this.AssemblyPathComparer = targetPlatform == Platform.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        this.RewriteCache = new AssemblyRewriteCache(
            rootPath: Path.Combine(Constants.DataPath, "Cache", "AssemblyRewrite"),
            environmentKey: this.GetRewriteCacheEnvironmentKey(targetPlatform, paranoidMode, rewriteMods, logTechnicalDetailsForBrokenMods)
        );

        // init resolver
        this.AssemblyDefinitionResolver = this.TrackForDisposal(new AssemblyDefinitionResolver());
        Constants.ConfigureAssemblyResolver(this.AssemblyDefinitionResolver);

        // generate type => assembly lookup for types which should be rewritten
        this.TypeAssemblies = new Dictionary<string, Assembly>();
        foreach (Assembly assembly in this.AssemblyMap.Targets)
        {
            ModuleDefinition module = this.AssemblyMap.TargetModules[assembly];
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (!type.IsPublic)
                    continue; // no need to rewrite
                if (type.Namespace.Contains("<"))
                    continue; // ignore assembly metadata
                this.TypeAssemblies[type.FullName] = assembly;
            }
        }

        // init rewriters
        Stopwatch? timer = null;
        if (logTechnicalDetailsForBrokenMods)
        {
            timer = new();
            timer.Start();
        }
        this.InstructionHandlers = new InstructionMetadata().GetHandlers(paranoidMode, this.RewriteMods, logTechnicalDetailsForBrokenMods).ToArray();
        if (logTechnicalDetailsForBrokenMods)
        {
            timer!.Stop();
            monitor.Log($"[SMAPI] Initialized rewriters in {timer.ElapsedMilliseconds}ms");
        }

        // Keep a live loaded-name index so each mod doesn't need to rescan and reflect every AppDomain assembly.
        // Subscribe before taking the initial snapshot so assemblies loaded concurrently can't fall through a gap.
        AppDomain.CurrentDomain.AssemblyLoad += this.OnAssemblyLoaded;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            this.TrackLoadedAssembly(assembly);
    }

    /// <summary>Preprocess and load an assembly.</summary>
    /// <param name="mod">The mod for which the assembly is being loaded.</param>
    /// <param name="assemblyFile">The assembly file.</param>
    /// <param name="assumeCompatible">Assume the mod is compatible, even if incompatible code is detected.</param>
    /// <returns>Returns the rewrite metadata for the preprocessed assembly.</returns>
    /// <exception cref="IncompatibleInstructionException">An incompatible CIL instruction was found while rewriting the assembly.</exception>
    public Assembly Load(IModMetadata mod, FileInfo assemblyFile, bool assumeCompatible)
    {
        // get referenced local assemblies
        AssemblyParseResult[] assemblies;
        {
            HashSet<string> visitedAssemblyNames = new(StringComparer.Ordinal);
            HashSet<string> visitedAssemblyPaths = new(this.AssemblyPathComparer);
            assemblies = this.GetReferencedLocalAssemblies(assemblyFile, visitedAssemblyNames, visitedAssemblyPaths, this.AssemblyDefinitionResolver, expectedAssemblyName: null).ToArray();
        }

        // validate load
        if (assemblies.Length == 0 || assemblies[0].Status == AssemblyLoadStatus.Failed)
        {
            throw new SAssemblyLoadFailedException(!assemblyFile.Exists
                ? $"Could not load '{assemblyFile.FullName}' because it doesn't exist."
                : $"Could not load '{assemblyFile.FullName}'."
            );
        }
        if (assemblies.Last().Status == AssemblyLoadStatus.AlreadyLoaded) // mod assembly is last in dependency order
            throw new SAssemblyLoadFailedException($"Could not load '{assemblyFile.FullName}' because it was already loaded. Do you have two copies of this mod?");

        // rewrite & load assemblies in leaf-to-root order
        bool oneAssembly = assemblies.Length == 1;
        Assembly? lastAssembly = null;
        HashSet<string> loggedMessages = [];
        foreach (AssemblyParseResult assembly in assemblies)
        {
            if (!assembly.HasDefinition)
                continue;

            // rewrite assembly, or replay the equivalent diagnostics from a content-addressed cache hit
            AssemblyRewriteResult rewriteResult;
            if (assembly.CachedRewrite is not null)
            {
                foreach (string message in assembly.CachedRewrite.Messages)
                    this.Monitor.LogOnce(loggedMessages, message);
                mod.SetWarning(assembly.CachedRewrite.Warnings);
                rewriteResult = new AssemblyRewriteResult(assembly.CachedRewrite.Changed, assembly.CachedRewrite.Warnings, assembly.CachedRewrite.Messages);
            }
            else
                rewriteResult = this.RewriteAssembly(mod, assembly.Definition, loggedMessages, logPrefix: "      ");

            // detect broken assembly reference
            foreach (AssemblyNameReference reference in assembly.Definition.MainModule.AssemblyReferences)
            {
                if (!reference.Name.StartsWith("System.") && !this.IsAssemblyLoaded(reference))
                {
                    this.Monitor.LogOnce(loggedMessages, $"      Broken code in {assembly.File.Name}: reference to missing assembly '{reference.FullName}'.");
                    if (!assumeCompatible)
                        throw new IncompatibleInstructionException($"Found a reference to missing assembly '{reference.FullName}' while loading assembly {assembly.File.Name}.");
                    mod.SetWarning(ModWarning.BrokenCodeLoaded);
                    break;
                }
            }

            // load assembly
            byte[]? rewrittenAssemblyBytes = null;
            byte[]? rewrittenSymbolBytes = null;
            if (rewriteResult.Changed)
            {
                if (!oneAssembly)
                    this.Monitor.Log($"      Loading assembly '{assembly.File.Name}' (rewritten)...");

                if (assembly.CachedRewrite is not null)
                {
                    rewrittenAssemblyBytes = assembly.CachedRewrite.AssemblyBytes!;
                    rewrittenSymbolBytes = assembly.CachedRewrite.SymbolBytes;
                }
                else
                {
                    using MemoryStream outAssemblyStream = new();
                    using MemoryStream outSymbolStream = new();
                    assembly.Definition.Write(outAssemblyStream, new WriterParameters { WriteSymbols = true, SymbolStream = outSymbolStream, SymbolWriterProvider = this.SymbolWriterProvider });
                    rewrittenAssemblyBytes = outAssemblyStream.ToArray();
                    rewrittenSymbolBytes = outSymbolStream.ToArray();
                }

                lastAssembly = Assembly.Load(rewrittenAssemblyBytes, rewrittenSymbolBytes);
            }
            else
            {
                if (!oneAssembly)
                    this.Monitor.Log($"      Loading assembly '{assembly.File.Name}'...");
                lastAssembly = Assembly.UnsafeLoadFrom(assembly.File.FullName);
            }

            // Only publish the cache entry after the rewritten/original assembly has loaded successfully. A bad
            // generated image therefore can't poison every later launch.
            if (assembly.CachedRewrite is null && assembly.RewriteCacheKey is not null)
            {
                this.RewriteCache.Store(
                    assembly.RewriteCacheKey,
                    new AssemblyRewriteCacheEntry(rewriteResult.Changed, rewriteResult.Warnings, rewriteResult.Messages, rewrittenAssemblyBytes, rewrittenSymbolBytes)
                );
            }

            // track loaded assembly for definition resolution
            this.AssemblyDefinitionResolver.Add(assembly.Definition);
        }

        // throw if incompatibilities detected
        if (!assumeCompatible && mod.Warnings.HasFlag(ModWarning.BrokenCodeLoaded))
            throw new IncompatibleInstructionException();

        // last assembly loaded is the root
        return lastAssembly!;
    }

    /// <summary>Get whether an assembly is loaded.</summary>
    /// <param name="reference">The assembly name reference.</param>
    public bool IsAssemblyLoaded(AssemblyNameReference reference)
    {
        try
        {
            _ = this.AssemblyDefinitionResolver.Resolve(reference);
            return true;
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    /// <summary>Resolve an assembly by its name.</summary>
    /// <param name="name">The assembly name.</param>
    /// <remarks>
    /// This implementation returns the first loaded assembly which matches the short form of
    /// the assembly name, to resolve assembly resolution issues when rewriting
    /// assemblies (especially with Mono). Since this is meant to be called on <see cref="AppDomain.AssemblyResolve"/>,
    /// the implicit assumption is that loading the exact assembly failed.
    /// </remarks>
    public static Assembly? ResolveAssembly(string name)
    {
        string shortName = name.Split(',', 2).First(); // get simple name (without version and culture)
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(p => p.GetName().Name == shortName);
    }

    /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= this.OnAssemblyLoaded;

        foreach (IDisposable instance in this.Disposables)
            instance.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Track an object for disposal as part of the assembly loader.</summary>
    /// <typeparam name="T">The instance type.</typeparam>
    /// <param name="instance">The disposable instance.</param>
    private T TrackForDisposal<T>(T instance)
        where T : IDisposable
    {
        this.Disposables.Add(instance);
        return instance;
    }

    /// <summary>Dispose and stop tracking an object which is no longer needed.</summary>
    private void DisposeTracked(IDisposable instance)
    {
        if (this.Disposables.Remove(instance))
            instance.Dispose();
    }

    /// <summary>Track an assembly loaded after this loader was created.</summary>
    private void OnAssemblyLoaded(object? sender, AssemblyLoadEventArgs e)
    {
        this.TrackLoadedAssembly(e.LoadedAssembly);
    }

    /// <summary>Add a loaded assembly's simple name to the live index.</summary>
    private void TrackLoadedAssembly(Assembly assembly)
    {
        string? name = assembly.GetName().Name;
        if (name != null)
            this.LoadedAssemblyNames.TryAdd(name, 0);
    }

    /// <summary>Get a stable identity for everything outside a mod DLL/PDB which can affect rewrite output or diagnostics.</summary>
    private string GetRewriteCacheEnvironmentKey(Platform targetPlatform, bool paranoidMode, bool rewriteMods, bool logTechnicalDetailsForBrokenMods)
    {
        StringBuilder key = new();
        key.Append("assembly-rewrite-v2|")
            .Append(typeof(AssemblyLoader).Module.ModuleVersionId).Append('|')
            .Append(targetPlatform).Append('|')
            .Append(paranoidMode).Append('|')
            .Append(rewriteMods).Append('|')
            .Append(logTechnicalDetailsForBrokenMods);

        // The handlers are compiled into SMAPI (covered by its MVID above), while their mapped target member and
        // assembly identities depend on these live game/framework modules. This also invalidates on game updates
        // even when the same SMAPI build remains installed.
        foreach ((Assembly targetAssembly, ModuleDefinition targetModule) in this.AssemblyMap.TargetModules.OrderBy(p => p.Key.FullName, StringComparer.Ordinal))
        {
            key.Append('|')
                .Append(targetAssembly.FullName)
                .Append('|')
                .Append(targetModule.Mvid);
        }

        return key.ToString();
    }

    /****
    ** Assembly parsing
    ****/
    /// <summary>Get a list of referenced local assemblies starting from the mod assembly, ordered from leaf to root.</summary>
    /// <param name="file">The assembly file to load.</param>
    /// <param name="visitedAssemblyNames">The local assembly identities visited in the current dependency graph.</param>
    /// <param name="visitedAssemblyPaths">The canonical local assembly paths visited in the current dependency graph.</param>
    /// <param name="assemblyResolver">A resolver which resolves references to known assemblies.</param>
    /// <param name="expectedAssemblyName">The simple assembly name from the parent reference, if this is a dependency.</param>
    /// <returns>Returns the rewrite metadata for the preprocessed assembly.</returns>
    private IEnumerable<AssemblyParseResult> GetReferencedLocalAssemblies(FileInfo file, HashSet<string> visitedAssemblyNames, HashSet<string> visitedAssemblyPaths, IAssemblyResolver assemblyResolver, string? expectedAssemblyName)
    {
        // validate
        if (file.Directory == null)
            throw new InvalidOperationException($"Could not get directory from file path '{file.FullName}'.");
        if (!file.Exists)
            yield break; // not a local assembly

        // Skip cycles, repeated local references, and dependencies already loaded by an earlier mod before doing
        // any file I/O, symbol probing, or Cecil parsing. Root assemblies are checked by their parsed identity below
        // so duplicate mods keep the existing diagnostic even when their filename doesn't match the assembly name.
        if (!visitedAssemblyPaths.Add(file.FullName))
        {
            yield return new AssemblyParseResult(file, null, AssemblyLoadStatus.AlreadyLoaded);
            yield break;
        }
        if (
            expectedAssemblyName != null
            && (
                this.LoadedAssemblyNames.ContainsKey(expectedAssemblyName)
                || !visitedAssemblyNames.Add(expectedAssemblyName)
            )
        )
        {
            yield return new AssemblyParseResult(file, null, AssemblyLoadStatus.AlreadyLoaded);
            yield break;
        }

        // add the assembly's directory temporarily if needed
        // this is needed by F# mods which bundle FSharp.Core.dll, for example
        string? temporarySearchDir = null;
        if (this.AssemblyDefinitionResolver.TryAddSearchDirectory(file.DirectoryName))
            temporarySearchDir = file.DirectoryName;

        // read the source assembly and look up a matching persistent rewrite result
        AssemblyDefinition assembly;
        byte[] sourceAssemblyBytes;
        byte[]? sourceSymbolBytes;
        string rewriteCacheKey;
        AssemblyRewriteCacheEntry? cachedRewrite;
        try
        {
            sourceAssemblyBytes = File.ReadAllBytes(file.FullName);
            FileInfo symbolsFile = new(Path.Combine(file.Directory.FullName, Path.GetFileNameWithoutExtension(file.Name)) + ".pdb");
            sourceSymbolBytes = symbolsFile.Exists
                ? File.ReadAllBytes(symbolsFile.FullName)
                : null;
            rewriteCacheKey = this.RewriteCache.GetKey(sourceAssemblyBytes, sourceSymbolBytes);
            this.RewriteCache.TryGet(rewriteCacheKey, out cachedRewrite);

            // Always discover identity and local dependencies from the authoritative source. Rewriters can change
            // assembly references, so traversing a cached output graph could otherwise omit a bundled dependency.
            ReadingMode sourceReadingMode = cachedRewrite?.Changed == false
                ? ReadingMode.Deferred
                : ReadingMode.Immediate;
            assembly = this.ReadAssembly(file, sourceAssemblyBytes, sourceSymbolBytes, assemblyResolver, sourceReadingMode);
        }
        finally
        {
            // clean up temporary search directory
            if (temporarySearchDir is not null)
                this.AssemblyDefinitionResolver.RemoveSearchDirectory(temporarySearchDir);
        }

        // skip if already loaded or visited under a different filename/reference
        string assemblyName = assembly.Name.Name;
        bool expectedNameWasReserved = expectedAssemblyName != null && string.Equals(expectedAssemblyName, assemblyName, StringComparison.Ordinal);
        if (
            this.LoadedAssemblyNames.ContainsKey(assemblyName)
            || (!expectedNameWasReserved && !visitedAssemblyNames.Add(assemblyName))
        )
        {
            yield return new AssemblyParseResult(file, null, AssemblyLoadStatus.AlreadyLoaded);
            yield break;
        }

        // yield referenced assemblies
        foreach (AssemblyNameReference dependency in assembly.MainModule.AssemblyReferences)
        {
            FileInfo dependencyFile = new(Path.Combine(file.Directory.FullName, $"{dependency.Name}.dll"));
            foreach (AssemblyParseResult result in this.GetReferencedLocalAssemblies(dependencyFile, visitedAssemblyNames, visitedAssemblyPaths, assemblyResolver, dependency.Name))
                yield return result;
        }

        // A changed cache entry contains the definition that later dependents must resolve against. Parse it only
        // after traversing the original dependency graph, and fall back to a normal rewrite if it's damaged.
        if (cachedRewrite?.Changed == true)
        {
            try
            {
                AssemblyDefinition cachedAssembly = this.ReadAssembly(file, cachedRewrite.AssemblyBytes!, cachedRewrite.SymbolBytes, assemblyResolver, ReadingMode.Immediate);
                if (!string.Equals(cachedAssembly.Name.Name, assemblyName, StringComparison.Ordinal))
                    throw new InvalidDataException("Cached rewrite has a different assembly identity.");
                this.DisposeTracked(assembly);
                assembly = cachedAssembly;
            }
            catch
            {
                this.RewriteCache.Remove(rewriteCacheKey);
                cachedRewrite = null;
            }
        }

        // yield assembly
        yield return new AssemblyParseResult(file, assembly, AssemblyLoadStatus.Okay, rewriteCacheKey, cachedRewrite);
    }

    /// <summary>Read an assembly definition from in-memory assembly and optional symbol bytes.</summary>
    private AssemblyDefinition ReadAssembly(FileInfo file, byte[] assemblyBytes, byte[]? symbolBytes, IAssemblyResolver assemblyResolver, ReadingMode readingMode)
    {
        Stream readStream = this.TrackForDisposal(new MemoryStream(assemblyBytes, writable: false));
        SymbolReaderProvider symbolReaderProvider = new();
        try
        {
            if (symbolBytes != null)
                symbolReaderProvider.TryAddSymbolData(file.Name, () => this.TrackForDisposal(new MemoryStream(symbolBytes, writable: false)));
            return this.TrackForDisposal(AssemblyDefinition.ReadAssembly(readStream, new ReaderParameters(readingMode) { AssemblyResolver = assemblyResolver, InMemory = true, ReadSymbols = true, SymbolReaderProvider = symbolReaderProvider }));
        }
        catch (SymbolsNotMatchingException ex)
        {
            this.Monitor.Log($"      Failed loading PDB for '{file.Name}'. Technical details:\n{ex}");
            readStream.Position = 0;
            return this.TrackForDisposal(AssemblyDefinition.ReadAssembly(readStream, new ReaderParameters(readingMode) { AssemblyResolver = assemblyResolver, InMemory = true }));
        }
    }

    /****
    ** Assembly rewriting
    ****/
    /// <summary>Rewrite the types referenced by an assembly.</summary>
    /// <param name="mod">The mod for which the assembly is being loaded.</param>
    /// <param name="assembly">The assembly to rewrite.</param>
    /// <param name="loggedMessages">The messages that have already been logged for this mod.</param>
    /// <param name="logPrefix">A string to prefix to log messages.</param>
    /// <returns>Returns the rewrite output and diagnostics.</returns>
    /// <exception cref="IncompatibleInstructionException">An incompatible CIL instruction was found while rewriting the assembly.</exception>
    private AssemblyRewriteResult RewriteAssembly(IModMetadata mod, AssemblyDefinition assembly, HashSet<string> loggedMessages, string logPrefix)
    {
        ModuleDefinition module = assembly.MainModule;
        string filename = $"{assembly.Name.Name}.dll";
        List<string> messages = [];
        ModWarning warnings = ModWarning.None;

        // swap assembly references if needed (e.g. XNA => MonoGame)
        bool platformChanged = false;
        if (this.RewriteMods)
        {
            for (int i = 0; i < module.AssemblyReferences.Count; i++)
            {
                // remove old assembly reference
                if (this.AssemblyMap.RemoveNames.Contains(module.AssemblyReferences[i].Name))
                {
                    platformChanged = true;
                    module.AssemblyReferences.RemoveAt(i);
                    i--;
                    string message = $"{logPrefix}Rewrote {filename} for OS...";
                    if (!messages.Contains(message))
                        messages.Add(message);
                    this.Monitor.LogOnce(loggedMessages, message);
                }
            }
            if (platformChanged)
            {
                // add target assembly references
                foreach (AssemblyNameReference target in this.AssemblyMap.TargetReferences.Values)
                    module.AssemblyReferences.Add(target);

                // rewrite type scopes to use target assemblies
                foreach (TypeReference type in module.GetTypeReferences())
                    this.ChangeTypeScope(type);

                // rewrite types using custom attributes
                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (CustomAttribute attr in type.CustomAttributes)
                    {
                        foreach (CustomAttributeArgument conField in attr.ConstructorArguments)
                        {
                            if (conField.Value is TypeReference typeRef)
                                this.ChangeTypeScope(typeRef);
                        }
                    }
                }
            }
        }

        // reset instruction handlers
        IInstructionHandler[] handlers = this.InstructionHandlers;
        foreach (IInstructionHandler handler in handlers)
            handler.Reset();

        // find or rewrite code
        RecursiveRewriter rewriter = new(
            module: module,
            rewriteModule: curModule =>
            {
                bool rewritten = false;
                foreach (IInstructionHandler handler in handlers)
                    rewritten |= handler.Handle(curModule);
                return rewritten;
            },
            rewriteType: (type, replaceWith) =>
            {
                bool rewritten = false;
                foreach (IInstructionHandler handler in handlers)
                    rewritten |= handler.Handle(module, type, replaceWith);
                return rewritten;
            },
            rewriteInstruction: (ref Instruction instruction, ILProcessor cil) =>
            {
                bool rewritten = false;
                foreach (IInstructionHandler handler in handlers)
                    rewritten |= handler.Handle(module, cil, instruction);
                return rewritten;
            }
        );
        bool anyRewritten = rewriter.RewriteModule();

        // handle rewrite flags
        foreach (IInstructionHandler handler in handlers)
        {
            foreach (var flag in handler.Flags)
                warnings |= this.ProcessInstructionHandleResult(mod, handler, flag, loggedMessages, messages, logPrefix, filename);
        }

        return new AssemblyRewriteResult(platformChanged || anyRewritten, warnings, messages);
    }

    /// <summary>Process the result from an instruction handler.</summary>
    /// <param name="mod">The mod being analyzed.</param>
    /// <param name="handler">The instruction handler.</param>
    /// <param name="result">The result returned by the handler.</param>
    /// <param name="loggedMessages">The messages already logged for the current mod.</param>
    /// <param name="rewriteMessages">The messages captured for this assembly's persistent rewrite result.</param>
    /// <param name="logPrefix">A string to prefix to log messages.</param>
    /// <param name="filename">The assembly filename for log messages.</param>
    /// <returns>The mod warning flag detected by the handler, if any.</returns>
    private ModWarning ProcessInstructionHandleResult(IModMetadata mod, IInstructionHandler handler, InstructionHandleResult result, HashSet<string> loggedMessages, List<string> rewriteMessages, string logPrefix, string filename)
    {
        // get message template
        // ($phrase is replaced with the noun phrase or messages)
        string? template = null;
        ModWarning warning = ModWarning.None;
        switch (result)
        {
            case InstructionHandleResult.Rewritten:
                template = $"{logPrefix}Rewrote {filename} to fix $phrase...";
                break;

            case InstructionHandleResult.NotCompatible:
                template = $"{logPrefix}Broken code in {filename}: $phrase.";
                warning = ModWarning.BrokenCodeLoaded;
                break;

            case InstructionHandleResult.DetectedGamePatch:
                template = $"{logPrefix}Detected game patcher in assembly {filename}."; // no need for phrase, which would confusingly be 'Harmony 1.x' here
                warning = ModWarning.PatchesGame;
                break;

            case InstructionHandleResult.DetectedSaveSerializer:
                template = $"{logPrefix}Detected possible save serializer change ($phrase) in assembly {filename}.";
                warning = ModWarning.ChangesSaveSerializer;
                break;

            case InstructionHandleResult.DetectedUnvalidatedUpdateTick:
                template = $"{logPrefix}Detected reference to $phrase in assembly {filename}.";
                warning = ModWarning.UsesUnvalidatedUpdateTick;
                break;

            case InstructionHandleResult.DetectedConsoleAccess:
                template = $"{logPrefix}Detected direct console access ($phrase) in assembly {filename}.";
                warning = ModWarning.AccessesConsole;
                break;

            case InstructionHandleResult.DetectedFilesystemAccess:
                template = $"{logPrefix}Detected filesystem access ($phrase) in assembly {filename}.";
                warning = ModWarning.AccessesFilesystem;
                break;

            case InstructionHandleResult.DetectedShellAccess:
                template = $"{logPrefix}Detected shell or process access ($phrase) in assembly {filename}.";
                warning = ModWarning.AccessesShell;
                break;

            case InstructionHandleResult.None:
                break;

            default:
                throw new NotSupportedException($"Unrecognized instruction handler result '{result}'.");
        }
        if (template == null)
            return ModWarning.None;

        if (warning != ModWarning.None)
            mod.SetWarning(warning);

        // format messages
        string phrase;
        if (handler.Phrases.Count == 0)
            phrase = handler.DefaultPhrase;
        else if (this.LogTechnicalDetailsForBrokenMods && result == InstructionHandleResult.NotCompatible)
            phrase = "\n - " + string.Join(";\n - ", handler.Phrases.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        else
            phrase = string.Join(", ", handler.Phrases.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        string message = template.Replace("$phrase", phrase);
        if (!rewriteMessages.Contains(message))
            rewriteMessages.Add(message);
        this.Monitor.LogOnce(loggedMessages, message);
        return warning;
    }

    /// <summary>Get the correct reference to use for compatibility with the current platform.</summary>
    /// <param name="type">The type reference to rewrite.</param>
    private void ChangeTypeScope(TypeReference? type)
    {
        // check skip conditions
        if (type == null || type.FullName.StartsWith("System."))
            return;

        // get assembly
        if (!this.TypeAssemblies.TryGetValue(type.FullName, out Assembly? assembly))
            return;

        // replace scope
        AssemblyNameReference assemblyRef = this.AssemblyMap.TargetReferences[assembly];
        type.Scope = assemblyRef;
    }
}
