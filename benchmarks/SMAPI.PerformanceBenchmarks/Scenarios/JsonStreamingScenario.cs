using System;
using System.IO;
using System.Text;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Toolkit.Serialization;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measures successful streaming deserialization without materializing the whole file as UTF-16 text.</summary>
internal sealed class JsonStreamingScenario : IPerformanceScenario
{
    private string? Path;
    private JsonHelper? JsonHelper;

    /// <inheritdoc />
    public string Id => "json.streaming";

    /// <inheritdoc />
    public string Description => "Streams a synthetic one-megabyte JSON file into a fixed model.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smapi-performance-{Guid.NewGuid():N}.json");
        string json = $"{{\"value\":\"synthetic\",\"count\":42,\"day\":\"Friday\"}}{new string(' ', 1024 * 1024)}";
        File.WriteAllText(this.Path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        this.JsonHelper = new JsonHelper();
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = 14695981039346656037UL;
        for (int index = 0; index < operations; index++)
        {
            if (!this.JsonHelper!.ReadJsonFileIfExists(this.Path!, out JsonModel? model) || model is null)
                throw new InvalidOperationException("The synthetic JSON fixture wasn't deserialized.");

            digest ^= (ulong)model.Value.Length;
            digest *= 1099511628211UL;
            digest ^= (uint)model.Count;
            digest *= 1099511628211UL;
            digest ^= (uint)model.Day;
            digest *= 1099511628211UL;
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        if (this.Path is not null)
            File.Delete(this.Path);
        this.Path = null;
        this.JsonHelper = null;
    }

    /// <summary>The stable synthetic deserialization target.</summary>
    private sealed class JsonModel
    {
        public string Value { get; set; } = "";

        public int Count { get; set; }

        public DayOfWeek Day { get; set; }
    }
}
