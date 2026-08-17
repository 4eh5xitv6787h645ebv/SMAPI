using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using StardewModdingAPI.Toolkit.Serialization.Converters;

namespace StardewModdingAPI.Toolkit.Serialization;

#pragma warning disable IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code -- not applicable to our usage.

/// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
public class JsonHelper
{
    /*********
    ** Fields
    *********/
    /// <summary>The options to use when converting a value to a System.Text.Json node.</summary>
    private static readonly System.Text.Json.JsonDocumentOptions ConvertToSystemTextJsonOptions = new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip };

    /// <summary>The character buffer size for streamed JSON files.</summary>
    private const int JsonFileBufferSize = 4096;


    /*********
    ** Accessors
    *********/
    /// <summary>The JSON settings to use when serializing and deserializing files.</summary>
    public JsonSerializerSettings JsonSettings { get; } = JsonHelper.CreateDefaultSettings();


    /*********
    ** Public methods
    *********/
    /// <summary>Create an instance of the default JSON serializer settings.</summary>
    public static JsonSerializerSettings CreateDefaultSettings()
    {
        return new()
        {
            Formatting = Formatting.Indented,
            ObjectCreationHandling = ObjectCreationHandling.Replace, // avoid issue where default ICollection<T> values are duplicated each time the config is loaded
            Converters = new List<JsonConverter>
                {
                    new SemanticVersionConverter(),
                    new StringEnumConverter()
                }
        };
    }

    /// <summary>Convert an arbitrary value to a System.Text.Json node.</summary>
    /// <param name="rawValue">The raw value to convert.</param>
    public static System.Text.Json.Nodes.JsonNode? ConvertToSystemTextJsonNode(object? rawValue)
    {
        // avoid JSON serialization when possible
        return rawValue switch
        {
            null => null,
            bool value => value,
            byte value => value,
            char value => value,
            DateTime value => value,
            DateTimeOffset value => value,
            decimal value => value,
            double value => value,
            float value => value,
            Guid value => value,
            int value => value,
            long value => value,
            sbyte value => value,
            short value => value,
            string value => value,
            uint value => value,
            ulong value => value,
            ushort value => value,
            JToken token => System.Text.Json.Nodes.JsonNode.Parse(JsonConvert.SerializeObject(token), null, JsonHelper.ConvertToSystemTextJsonOptions),
            _ => System.Text.Json.JsonSerializer.SerializeToNode(rawValue)
        };
    }

    /// <summary>Read a JSON file.</summary>
    /// <typeparam name="TModel">The model type.</typeparam>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="result">The parsed content model.</param>
    /// <returns>Returns false if the file doesn't exist, else true.</returns>
    /// <exception cref="ArgumentException">The given <paramref name="fullPath"/> is empty or invalid.</exception>
    /// <exception cref="JsonReaderException">The file contains invalid JSON.</exception>
    public bool ReadJsonFileIfExists<TModel>(string fullPath,
#if NET6_0_OR_GREATER
        [NotNullWhen(true)]
#endif
        out TModel? result
    )
    {
        // validate
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("The file path is empty or invalid.", nameof(fullPath));

        // Stream successful reads so large content-pack files don't need a complete UTF-16 string in memory
        // alongside the deserialized object graph.
        JsonReaderException? readerError = null;
        try
        {
            using FileStream stream = File.OpenRead(fullPath);
            using StreamReader textReader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, JsonHelper.JsonFileBufferSize, leaveOpen: false);
            using JsonTextReader jsonReader = new(textReader);
            result = this.GetSerializer().Deserialize<TModel>(jsonReader);
            return result != null;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            result = default;
            return false;
        }
        catch (JsonReaderException ex)
        {
            // Fall through to the text path. Besides preserving the detailed diagnostics below, Deserialize
            // has a compatibility retry for invalid curly quotes used by some hand-edited mod files.
            readerError = ex;
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw JsonHelper.CreateFileParseException(fullPath, ex, json: null);
        }

        // deserialize through the compatibility path after a syntax error
        string json = File.ReadAllText(fullPath);
        if (!json.Contains("“") && !json.Contains("”"))
            throw JsonHelper.CreateFileParseException(fullPath, readerError!, json);

        try
        {
            result = this.Deserialize<TModel>(json);
            return result != null;
        }
        catch (Exception ex)
        {
            throw JsonHelper.CreateFileParseException(fullPath, ex, json);
        }
    }

    /// <summary>Save to a JSON file.</summary>
    /// <typeparam name="TModel">The model type.</typeparam>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="model">The model to save.</param>
    /// <exception cref="InvalidOperationException">The given path is empty or invalid.</exception>
    public void WriteJsonFile<TModel>(string fullPath, TModel model)
        where TModel : class
    {
        // validate
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("The file path is empty or invalid.", nameof(fullPath));

        // create directory if needed
        string dir = Path.GetDirectoryName(fullPath)!;
        if (dir == null)
            throw new ArgumentException("The file path is invalid.", nameof(fullPath));
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // write file
        string json = this.Serialize(model);
        File.WriteAllText(fullPath, json);
    }

    /// <summary>Deserialize JSON text if possible.</summary>
    /// <typeparam name="TModel">The model type.</typeparam>
    /// <param name="json">The raw JSON text.</param>
    public TModel? Deserialize<TModel>(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<TModel>(json, this.JsonSettings);
        }
        catch (JsonReaderException)
        {
            // try replacing curly quotes
            if (json.Contains("“") || json.Contains("”"))
            {
                try
                {
                    return JsonConvert.DeserializeObject<TModel>(json.Replace('“', '"').Replace('”', '"'), this.JsonSettings)
                        ?? throw new InvalidOperationException($"Couldn't deserialize model type '{typeof(TModel)}' from empty or null JSON.");
                }
                catch { /* rethrow original error */ }
            }

            throw;
        }
    }

    /// <summary>Serialize a model to JSON text.</summary>
    /// <typeparam name="TModel">The model type.</typeparam>
    /// <param name="model">The model to serialize.</param>
    /// <param name="formatting">The formatting to apply.</param>
    public string Serialize<TModel>(TModel model, Formatting formatting = Formatting.Indented)
    {
        return JsonConvert.SerializeObject(model, formatting, this.JsonSettings);
    }

    /// <summary>Get a low-level JSON serializer matching the <see cref="JsonSettings"/>.</summary>
    public JsonSerializer GetSerializer()
    {
        return JsonSerializer.CreateDefault(this.JsonSettings);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Create the public error for a JSON file which couldn't be parsed.</summary>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="exception">The underlying parse error.</param>
    /// <param name="json">The raw JSON text, if it was materialized for compatibility handling.</param>
    private static JsonReaderException CreateFileParseException(string fullPath, Exception exception, string? json)
    {
        string error = $"Can't parse JSON file at {fullPath}.";

        if (exception is JsonReaderException)
        {
            error += " This doesn't seem to be valid JSON.";
            if (json?.Contains("“") is true || json?.Contains("”") is true)
                error += " Found curly quotes in the text; note that only straight quotes are allowed in JSON.";
        }
        error += $"\nTechnical details: {exception.Message}";
        return new JsonReaderException(error);
    }
}
