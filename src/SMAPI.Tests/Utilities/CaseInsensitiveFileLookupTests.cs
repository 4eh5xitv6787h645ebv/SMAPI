using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Toolkit.Utilities.PathLookups;

namespace SMAPI.Tests.Utilities;

/// <summary>Unit tests for <see cref="CaseInsensitiveFileLookup"/>.</summary>
[TestFixture]
internal sealed class CaseInsensitiveFileLookupTests
{
    [Test]
    public void GetCachedFor_KeepsCaseDistinctUnixRootsSeparate()
    {
        if (Path.DirectorySeparatorChar != '/')
            Assert.Ignore("This behavior applies to Unix filesystems which support case-distinct roots.");

        string parent = Path.Combine(Path.GetTempPath(), $"smapi-case-roots-{Guid.NewGuid():N}");
        string upperRoot = Path.Combine(parent, "Pack");
        string lowerRoot = Path.Combine(parent, "pack");
        try
        {
            Directory.CreateDirectory(upperRoot);
            Directory.CreateDirectory(lowerRoot);
            File.WriteAllText(Path.Combine(upperRoot, "sentinel.txt"), "upper");
            File.WriteAllText(Path.Combine(lowerRoot, "sentinel.txt"), "lower");

            CaseInsensitiveFileLookup upper = CaseInsensitiveFileLookup.GetCachedFor(upperRoot);
            CaseInsensitiveFileLookup lower = CaseInsensitiveFileLookup.GetCachedFor(lowerRoot);

            lower.Should().NotBeSameAs(upper);
            File.ReadAllText(upper.GetFile("SENTINEL.TXT").FullName).Should().Be("upper");
            File.ReadAllText(lower.GetFile("SENTINEL.TXT").FullName).Should().Be("lower");
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }
}
