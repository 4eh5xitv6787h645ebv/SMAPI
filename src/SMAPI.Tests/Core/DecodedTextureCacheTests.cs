using System;
using System.IO;
using FluentAssertions;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="DecodedTextureCache"/>.</summary>
[TestFixture]
internal class DecodedTextureCacheTests
{
    [Test(Description = "Assert that decoded pixels are admitted on second use and every hit returns isolated mutable data.")]
    public void Track_AdmitsOnSecondUseAndReturnsCopies()
    {
        string path = Path.GetTempFileName();
        try
        {
            FileInfo file = new(path);
            using DecodedTextureCache cache = new(maxBytes: 64, maxEntryBytes: 64);
            var decoded = new RawTextureData(2, 1, [Color.Red, Color.Blue]);

            cache.Track(file, decoded);
            cache.Count.Should().Be(0);
            cache.TryGetCopy(file, out _).Should().BeFalse();

            cache.Track(file, decoded);
            cache.Count.Should().Be(1);
            cache.SizeInBytes.Should().Be(8);

            decoded.Data[0] = Color.Green;
            cache.TryGetCopy(file, out RawTextureData? firstCopy).Should().BeTrue();
            firstCopy!.Data.Should().Equal(Color.Red, Color.Blue);

            firstCopy.Data[1] = Color.Yellow;
            cache.TryGetCopy(file, out RawTextureData? secondCopy).Should().BeTrue();
            secondCopy!.Data.Should().Equal(Color.Red, Color.Blue);
            secondCopy.Data.Should().NotBeSameAs(firstCopy.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test(Description = "Assert that least-recently-used decoded pixels are evicted at the byte budget.")]
    public void Track_EvictsLeastRecentlyUsedEntry()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        try
        {
            FileInfo firstFile = new(firstPath);
            FileInfo secondFile = new(secondPath);
            using DecodedTextureCache cache = new(maxBytes: 8, maxEntryBytes: 8);
            var first = new RawTextureData(2, 1, [Color.Red, Color.Blue]);
            var second = new RawTextureData(2, 1, [Color.Green, Color.Yellow]);

            cache.Track(firstFile, first);
            cache.Track(firstFile, first);
            cache.Track(secondFile, second);
            cache.Track(secondFile, second);

            cache.Count.Should().Be(1);
            cache.SizeInBytes.Should().Be(8);
            cache.TryGetCopy(firstFile, out _).Should().BeFalse();
            cache.TryGetCopy(secondFile, out RawTextureData? retained).Should().BeTrue();
            retained!.Data.Should().Equal(Color.Green, Color.Yellow);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Test(Description = "Assert that file metadata changes invalidate previously decoded pixels.")]
    public void TryGetCopy_InvalidatesChangedFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            FileInfo file = new(path);
            using DecodedTextureCache cache = new(maxBytes: 64, maxEntryBytes: 64);
            var decoded = new RawTextureData(1, 1, [Color.Red]);
            cache.Track(file, decoded);
            cache.Track(file, decoded);
            cache.TryGetCopy(file, out _).Should().BeTrue();

            using (FileStream stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.WriteByte(1);

            cache.TryGetCopy(file, out _).Should().BeFalse();
            cache.Count.Should().Be(0);
            cache.SizeInBytes.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
