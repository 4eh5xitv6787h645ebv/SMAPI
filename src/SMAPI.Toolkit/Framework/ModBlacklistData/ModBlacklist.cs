using System;
using System.Collections.Generic;
using System.IO;
#if NET6_0_OR_GREATER
using System.IO.Enumeration;
#endif
using StardewModdingAPI.Toolkit.Utilities;

namespace StardewModdingAPI.Toolkit.Framework.ModBlacklistData;

/// <summary>Handles access to SMAPI's internal 'malicious mods' blacklist.</summary>
public class ModBlacklist
{
    /*********
    ** Accessors
    *********/
    /// <summary>The underlying mod blacklist data.</summary>
    public ModBlacklistModel Blacklist { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an empty instance.</summary>
    public ModBlacklist()
        : this(new ModBlacklistModel([], [])) { }

    /// <summary>Construct an instance.</summary>
    /// <param name="data">The underlying mod blacklist data.</param>
    public ModBlacklist(ModBlacklistModel data)
    {
        this.Blacklist = data;
    }

    /// <summary>Get the blacklist entry for a mod, if any.</summary>
    /// <param name="modId">The unique mod ID.</param>
    /// <param name="entryDllPath">The absolute path to the entry DLL, if this is a C# mod.</param>
    public ModBlacklistEntryModel? CheckMod(string modId, string? entryDllPath)
    {
        string? entryDllHash = null;

        foreach (ModBlacklistEntryModel entry in this.Blacklist.Blacklist)
        {
            // check mod ID
            if (entry.Id != null && !string.Equals(modId, entry.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            // check entry DLL hash
            if (entry.EntryDllHash != null)
            {
                if (entryDllPath is null)
                    continue;

                entryDllHash ??= FileUtilities.GetFileHash(entryDllPath);
                if (!string.Equals(entryDllHash, entry.EntryDllHash, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return entry;
        }

        return null;
    }

    /// <summary>Scan a folder to detect any files which match the list of malicious files.</summary>
    /// <param name="rootPath">The root path to scan.</param>
    public IEnumerable<(string FilePath, LooseFileBlacklistEntryModel Match)> CheckLooseFiles(string rootPath)
    {
#if NET6_0_OR_GREATER
        FileSystemEnumerable<string> files = new(
            rootPath,
            static (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                MatchType = MatchType.Win32
            }
        )
        {
            // Filter on the enumerator's filename span so non-candidates don't allocate a full path. The full
            // validation below remains authoritative, including its hash check and first-match behavior.
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && this.CouldMatchLooseFile(entry.FileName)
        };
#else
        IEnumerable<string> files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);
#endif

        foreach (string path in files)
        {
            LooseFileBlacklistEntryModel? blacklistEntry = this.CheckLooseFile(path);

            if (blacklistEntry != null)
                yield return (path, blacklistEntry);
        }
    }

    /// <summary>Get the blacklist entry for a loose file, if any.</summary>
    /// <param name="fullPath">The absolute path to the file to check.</param>
    public LooseFileBlacklistEntryModel? CheckLooseFile(string fullPath)
    {
        string? name = null;
        string? extension = null;
        string? hash = null;

        foreach (LooseFileBlacklistEntryModel entry in this.Blacklist.LooseFileBlacklist)
        {
            // check file name
            if (entry.Name != null)
            {
                name ??= Path.GetFileName(fullPath);
                if (!string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // check file extension
            if (entry.Extension != null)
            {
                extension ??= Path.GetExtension(fullPath);
                if (!string.Equals(extension, entry.Extension, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // check hash
            if (entry.Hash != null)
            {
                hash ??= FileUtilities.GetFileHash(fullPath);
                if (!string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return entry;
        }

        return null;
    }

#if NET6_0_OR_GREATER
    /// <summary>Get whether a filename satisfies the allocation-free fields of any loose-file blacklist entry.</summary>
    /// <param name="fileName">The filename without its parent path.</param>
    private bool CouldMatchLooseFile(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> extension = default;
        bool hasExtension = false;

        foreach (LooseFileBlacklistEntryModel entry in this.Blacklist.LooseFileBlacklist)
        {
            if (entry.Name != null && !fileName.Equals(entry.Name.AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Extension != null)
            {
                if (!hasExtension)
                {
                    extension = Path.GetExtension(fileName);
                    hasExtension = true;
                }

                if (!extension.Equals(entry.Extension.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return true;
        }

        return false;
    }
#endif
}
