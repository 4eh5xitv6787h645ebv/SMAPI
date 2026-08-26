using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Serializes the stable mod health schema independently of SMAPI's general JSON settings.</summary>
internal sealed class ModHealthReportJsonSerializer
{
    private readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Culture = CultureInfo.InvariantCulture,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        FloatFormatHandling = FloatFormatHandling.Symbol,
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        TypeNameHandling = TypeNameHandling.None,
        Converters = { new FiniteDoubleJsonConverter(), new StringEnumConverter() }
    });

    /// <summary>Serialize a report as deterministic indented UTF-8 JSON text with LF newlines.</summary>
    public string Serialize(ModHealthReport report)
    {
        StringBuilder result = new();
        using StringWriter textWriter = new(result, CultureInfo.InvariantCulture) { NewLine = "\n" };
        using (JsonTextWriter jsonWriter = new(textWriter)
        {
            CloseOutput = false,
            Culture = CultureInfo.InvariantCulture,
            Formatting = Formatting.Indented,
            Indentation = 2,
            IndentChar = ' '
        })
        {
            this.Serializer.Serialize(jsonWriter, report);
            jsonWriter.Flush();
        }

        result.Append('\n');
        return result.ToString();
    }
}

/// <summary>Prevents non-finite numbers from becoming invalid or misleading JSON values.</summary>
internal sealed class FiniteDoubleJsonConverter : JsonConverter<double>
{
    public override void WriteJson(JsonWriter writer, double value, JsonSerializer serializer)
    {
        if (!double.IsFinite(value))
            throw new JsonSerializationException("Mod health reports cannot contain a non-finite numeric value.");
        writer.WriteValue(value);
    }

    public override double ReadJson(JsonReader reader, Type objectType, double existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        throw new NotSupportedException("The mod health serializer does not deserialize reports.");
    }
}
