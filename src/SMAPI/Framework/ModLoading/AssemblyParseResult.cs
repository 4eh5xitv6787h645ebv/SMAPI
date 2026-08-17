using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Mono.Cecil;

namespace StardewModdingAPI.Framework.ModLoading;

/// <summary>Metadata about a parsed assembly definition.</summary>
internal class AssemblyParseResult
{
    /*********
    ** Accessors
    *********/
    /// <summary>The original assembly file.</summary>
    public readonly FileInfo File;

    /// <summary>The assembly definition.</summary>
    public readonly AssemblyDefinition? Definition;

    /// <summary>The result of the assembly load.</summary>
    public AssemblyLoadStatus Status;

    /// <summary>The content-addressed rewrite cache key for this assembly, if available.</summary>
    public readonly string? RewriteCacheKey;

    /// <summary>The cached rewrite result for this assembly, if found.</summary>
    public readonly AssemblyRewriteCacheEntry? CachedRewrite;

    /// <summary>Whether the <see cref="Definition"/> is loaded and ready (i.e. the <see cref="Status"/> is not <see cref="AssemblyLoadStatus.AlreadyLoaded"/> or <see cref="AssemblyLoadStatus.Failed"/>).</summary>
    [MemberNotNullWhen(true, nameof(AssemblyParseResult.Definition))]
    public bool HasDefinition => this.Status == AssemblyLoadStatus.Okay;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="file">The original assembly file.</param>
    /// <param name="assembly">The assembly definition.</param>
    /// <param name="status">The result of the assembly load.</param>
    /// <param name="rewriteCacheKey">The content-addressed rewrite cache key, if available.</param>
    /// <param name="cachedRewrite">The cached rewrite result, if found.</param>
    public AssemblyParseResult(FileInfo file, AssemblyDefinition? assembly, AssemblyLoadStatus status, string? rewriteCacheKey = null, AssemblyRewriteCacheEntry? cachedRewrite = null)
    {
        this.File = file;
        this.Definition = assembly;
        this.Status = status;
        this.RewriteCacheKey = rewriteCacheKey;
        this.CachedRewrite = cachedRewrite;

        if (status == AssemblyLoadStatus.Okay && assembly == null)
            throw new InvalidOperationException($"Invalid assembly parse result: load status {status} with a null assembly.");
    }
}
