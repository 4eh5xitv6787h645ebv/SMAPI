using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>A random identifier for one backend process session.</summary>
[JsonConverter(typeof(ProtocolSessionIdJsonConverter))]
public readonly record struct ProtocolSessionId
{
    /// <summary>The canonical lowercase 128-bit hexadecimal value.</summary>
    public string Value { get; }

    private ProtocolSessionId(string value)
    {
        this.Value = value;
    }

    /// <summary>Create a cryptographically unpredictable nonempty identifier.</summary>
    public static ProtocolSessionId CreateRandom()
    {
        return new ProtocolSessionId(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    /// <summary>Parse an exact canonical identifier.</summary>
    /// <param name="value">The lowercase 32-character hexadecimal representation.</param>
    public static ProtocolSessionId Parse(string? value)
    {
        ProtocolIdentifier.AssertCanonical(value, "session");
        return new ProtocolSessionId(value!);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Value;
    }
}

/// <summary>A random identifier for one immutable operation plan.</summary>
[JsonConverter(typeof(ProtocolPlanIdJsonConverter))]
public readonly record struct ProtocolPlanId
{
    /// <summary>The canonical lowercase 128-bit hexadecimal value.</summary>
    public string Value { get; }

    private ProtocolPlanId(string value)
    {
        this.Value = value;
    }

    /// <summary>Create a cryptographically unpredictable nonempty identifier.</summary>
    public static ProtocolPlanId CreateRandom()
    {
        return new ProtocolPlanId(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    /// <summary>Parse an exact canonical identifier.</summary>
    /// <param name="value">The lowercase 32-character hexadecimal representation.</param>
    public static ProtocolPlanId Parse(string? value)
    {
        ProtocolIdentifier.AssertCanonical(value, "plan");
        return new ProtocolPlanId(value!);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Value;
    }
}

internal static class ProtocolIdentifier
{
    /// <summary>Require a nonempty canonical 128-bit identifier.</summary>
    public static void AssertCanonical(string? value, string kind)
    {
        if (
            value is null
            || value.Length != 32
            || value == "00000000000000000000000000000000"
            || !Guid.TryParseExact(value, "N", out _)
            || value.Any(character => character is >= 'A' and <= 'F')
        )
        {
            throw new ProtocolException($"The protocol {kind} ID isn't a canonical nonempty lowercase 128-bit value.");
        }
    }
}

internal sealed class ProtocolSessionIdJsonConverter : JsonConverter<ProtocolSessionId>
{
    /// <inheritdoc />
    public override ProtocolSessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A protocol session ID must be a string.");

        try
        {
            return ProtocolSessionId.Parse(reader.GetString());
        }
        catch (ProtocolException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProtocolSessionId value, JsonSerializerOptions options)
    {
        ProtocolIdentifier.AssertCanonical(value.Value, "session");
        writer.WriteStringValue(value.Value);
    }
}

internal sealed class ProtocolPlanIdJsonConverter : JsonConverter<ProtocolPlanId>
{
    /// <inheritdoc />
    public override ProtocolPlanId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A protocol plan ID must be a string.");

        try
        {
            return ProtocolPlanId.Parse(reader.GetString());
        }
        catch (ProtocolException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProtocolPlanId value, JsonSerializerOptions options)
    {
        ProtocolIdentifier.AssertCanonical(value.Value, "plan");
        writer.WriteStringValue(value.Value);
    }
}
