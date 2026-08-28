using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace StardewModdingAPI.Toolkit.Utilities;

/// <summary>Provides utilities for dealing with files.</summary>
public static class FileUtilities
{
    /*********
    ** Public methods
    *********/
    /// <summary>Delete a file or folder regardless of file permissions, and block until deletion completes.</summary>
    /// <param name="entry">The file or folder to reset.</param>
    public static void ForceDelete(FileSystemInfo entry)
    {
        // Resolve the entry itself, but never traverse a symbolic link, junction, or other
        // reparse point. In particular, recursively enumerating a directory link here could
        // delete files outside the caller's intended tree.
        entry.Refresh();
        bool isLink = entry.LinkTarget != null;
        if (!entry.Exists && !isLink)
            return;

        if (!isLink)
            isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        // delete children
        if (entry is DirectoryInfo folder && !isLink)
        {
            foreach (FileSystemInfo child in folder.GetFileSystemInfos())
                FileUtilities.ForceDelete(child);
        }

        // reset permissions & delete
        // Don't set attributes through a link, since the platform may apply that to its target.
        if (!isLink)
            entry.Attributes = FileAttributes.Normal;
        entry.Delete();

        // wait for deletion to finish
        for (int i = 0; i < 10; i++)
        {
            entry.Refresh();
            if (entry.Exists)
                Thread.Sleep(500);
        }

        // throw exception if deletion didn't happen before timeout
        entry.Refresh();
        if (entry.Exists)
            throw new IOException($"Timed out trying to delete {entry.FullName}");
    }

    /// <summary>Get the MD5 hash for a file.</summary>
    /// <param name="absolutePath">The absolute file path.</param>
    public static string GetFileHash(string absolutePath)
    {
        using MD5 md5 = MD5.Create();
        return FileUtilities.GetFileHash(md5, absolutePath);
    }

    /// <summary>Get the MD5 hash for a file.</summary>
    /// <param name="md5">The MD5 implementation to use.</param>
    /// <param name="absolutePath">The absolute file path.</param>
    public static string GetFileHash(MD5 md5, string absolutePath)
    {
        using FileStream stream = File.OpenRead(absolutePath);
        byte[] hash = md5.ComputeHash(stream);
        return FileUtilities.GetHashString(hash);
    }

    /// <summary>Get the string representation of MD5 hash bytes.</summary>
    /// <param name="hashBytes">The MD5 hash bytes to represent.</param>
    internal static string GetHashString(byte[] hashBytes)
    {
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
