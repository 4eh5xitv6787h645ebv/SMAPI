using System;
using System.IO;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json;
using NUnit.Framework;
using StardewModdingAPI.Toolkit.Serialization;

namespace SMAPI.Tests.Utilities;

/// <summary>Unit tests for <see cref="JsonHelper"/>.</summary>
[TestFixture]
internal sealed class JsonHelperTests
{
    [Test]
    public void ReadJsonFileIfExists_StreamsValidJsonWithBomCommentsAndConverters()
    {
        string path = WriteTempJson(
            """
            {
                // Newtonsoft comments are supported by SMAPI's settings.
                "Value": "loaded",
                "Day": "Friday"
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
        );
        try
        {
            JsonHelper helper = new();

            bool found = helper.ReadJsonFileIfExists(path, out TestModel? model);

            found.Should().BeTrue();
            model.Should().NotBeNull();
            model!.Value.Should().Be("loaded");
            model.Day.Should().Be(DayOfWeek.Friday);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReadJsonFileIfExists_ReturnsFalseForMissingOrNullFiles()
    {
        JsonHelper helper = new();
        string missingPath = Path.Combine(Path.GetTempPath(), $"smapi-missing-{Guid.NewGuid():N}.json");
        helper.ReadJsonFileIfExists(missingPath, out TestModel? missing).Should().BeFalse();
        missing.Should().BeNull();

        string nullPath = WriteTempJson("null");
        try
        {
            helper.ReadJsonFileIfExists(nullPath, out TestModel? empty).Should().BeFalse();
            empty.Should().BeNull();
        }
        finally
        {
            File.Delete(nullPath);
        }
    }

    [Test]
    public void ReadJsonFileIfExists_RetainsCurlyQuoteCompatibilityFallback()
    {
        string path = WriteTempJson("{ “Value”: “repaired”, “Day”: “Monday” }");
        try
        {
            JsonHelper helper = new();

            helper.ReadJsonFileIfExists(path, out TestModel? model).Should().BeTrue();

            model.Should().NotBeNull();
            model!.Value.Should().Be("repaired");
            model.Day.Should().Be(DayOfWeek.Monday);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReadJsonFileIfExists_RetainsDetailedInvalidJsonError()
    {
        string path = WriteTempJson("{ “Value”: “still broken” ");
        try
        {
            JsonHelper helper = new();

            JsonReaderException? error = Assert.Throws<JsonReaderException>(() => helper.ReadJsonFileIfExists(path, out TestModel? _));

            error.Should().NotBeNull();
            error!.Message.Should().Contain($"Can't parse JSON file at {path}.");
            error.Message.Should().Contain("This doesn't seem to be valid JSON.");
            error.Message.Should().Contain("Found curly quotes in the text");
            error.Message.Should().Contain("Technical details:");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test(Description = "Assert that successful streamed reads don't allocate in proportion to valid JSON file length.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void ReadJsonFileIfExists_ValidJsonAvoidsWholeFileAllocation()
    {
        const int largePaddingLength = 4 * 1024 * 1024;
        string smallPath = WriteTempJson("{ \"Value\": \"loaded\", \"Day\": \"Friday\" }");
        string largePath = WriteTempJson($"{{ \"Value\": \"loaded\", \"Day\": \"Friday\" }}{new string(' ', largePaddingLength)}");
        try
        {
            JsonHelper helper = new();

            // Warm both paths so the comparison covers steady-state reader allocation, not JIT or metadata setup.
            helper.ReadJsonFileIfExists(smallPath, out TestModel? _).Should().BeTrue();
            helper.ReadJsonFileIfExists(largePath, out TestModel? _).Should().BeTrue();

            long smallBefore = GC.GetAllocatedBytesForCurrentThread();
            bool smallFound = helper.ReadJsonFileIfExists(smallPath, out TestModel? small);
            long smallAllocated = GC.GetAllocatedBytesForCurrentThread() - smallBefore;

            long largeBefore = GC.GetAllocatedBytesForCurrentThread();
            bool largeFound = helper.ReadJsonFileIfExists(largePath, out TestModel? large);
            long largeAllocated = GC.GetAllocatedBytesForCurrentThread() - largeBefore;

            smallFound.Should().BeTrue();
            largeFound.Should().BeTrue();
            small.Should().BeEquivalentTo(large);
            largeAllocated.Should().BeLessThanOrEqualTo(
                smallAllocated + (64 * 1024),
                "streaming may allocate fixed reader buffers, but must not materialize a multi-megabyte UTF-16 copy"
            );
        }
        finally
        {
            File.Delete(smallPath);
            File.Delete(largePath);
        }
    }

    /// <summary>Write a temporary JSON file.</summary>
    private static string WriteTempJson(string json, Encoding? encoding = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-json-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private sealed class TestModel
    {
        public string? Value { get; set; }

        public DayOfWeek Day { get; set; }
    }
}
