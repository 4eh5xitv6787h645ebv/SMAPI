using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Toolkit.Framework.BundledModData;

namespace StardewModdingAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssemblyRewriteCache"/>.</summary>
[TestFixture]
internal class AssemblyRewriteCacheTests
{
    /*********
    ** Unit tests
    *********/
    [Test]
    public void StoresChangedRewriteWithDiagnostics()
    {
        string root = this.GetTempFolderPath();
        try
        {
            AssemblyRewriteCache cache = new(root, "environment-A");
            string key = cache.GetKey([1, 2, 3], [4, 5]);
            cache.Store(key, new AssemblyRewriteCacheEntry(
                changed: true,
                warnings: ModWarning.PatchesGame | ModWarning.AccessesFilesystem,
                messages: ["first", "Unicode ☃"],
                assemblyBytes: [10, 11, 12],
                symbolBytes: [20, 21]
            ));

            cache.TryGet(key, out AssemblyRewriteCacheEntry? entry).Should().BeTrue();
            entry.Should().NotBeNull();
            entry!.Changed.Should().BeTrue();
            entry.Warnings.Should().Be(ModWarning.PatchesGame | ModWarning.AccessesFilesystem);
            entry.Messages.Should().Equal("first", "Unicode ☃");
            entry.AssemblyBytes.Should().Equal(10, 11, 12);
            entry.SymbolBytes.Should().Equal(20, 21);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StoresUnchangedAnalysisWithoutDuplicatingAssembly()
    {
        string root = this.GetTempFolderPath();
        try
        {
            AssemblyRewriteCache cache = new(root, "environment-A");
            string key = cache.GetKey([1], symbolBytes: null);
            cache.Store(key, new AssemblyRewriteCacheEntry(false, ModWarning.None, [], assemblyBytes: null, symbolBytes: null));

            cache.TryGet(key, out AssemblyRewriteCacheEntry? entry).Should().BeTrue();
            entry.Should().NotBeNull();
            entry!.Changed.Should().BeFalse();
            entry.AssemblyBytes.Should().BeNull();
            entry.SymbolBytes.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ContentKeyIncludesAssemblyAndSymbolBoundaries()
    {
        string root = this.GetTempFolderPath();
        try
        {
            AssemblyRewriteCache cache = new(root, "environment-A");

            cache.GetKey([1, 2], [3]).Should().NotBe(cache.GetKey([1], [2, 3]));
            cache.GetKey([1], symbolBytes: null).Should().NotBe(cache.GetKey([1], []));
            cache.GetKey([1], [2]).Should().NotBe(cache.GetKey([1], [3]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void EnvironmentChangeInvalidatesOldEntries()
    {
        string root = this.GetTempFolderPath();
        try
        {
            AssemblyRewriteCache first = new(root, "environment-A");
            string key = first.GetKey([1], symbolBytes: null);
            first.Store(key, new AssemblyRewriteCacheEntry(false, ModWarning.None, [], assemblyBytes: null, symbolBytes: null));
            first.TryGet(key, out _).Should().BeTrue();

            AssemblyRewriteCache second = new(root, "environment-B");
            second.TryGet(key, out _).Should().BeFalse();
            first.TryGet(key, out _).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CorruptEntryBecomesCacheMiss()
    {
        string root = this.GetTempFolderPath();
        try
        {
            AssemblyRewriteCache cache = new(root, "environment-A");
            string key = cache.GetKey([1], symbolBytes: null);
            cache.Store(key, new AssemblyRewriteCacheEntry(false, ModWarning.None, [], assemblyBytes: null, symbolBytes: null));

            string[] entryPaths = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            entryPaths.Should().ContainSingle();
            string entryPath = entryPaths[0];
            byte[] bytes = File.ReadAllBytes(entryPath);
            bytes[10] ^= 0x40;
            File.WriteAllBytes(entryPath, bytes);

            cache.TryGet(key, out _).Should().BeFalse();
            File.Exists(entryPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get a unique temporary directory path.</summary>
    private string GetTempFolderPath()
    {
        return Path.Combine(Path.GetTempPath(), "smapi-assembly-cache-tests", Guid.NewGuid().ToString("N"));
    }
}
