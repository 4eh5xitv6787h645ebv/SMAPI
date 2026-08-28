using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Planning;

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

/// <summary>The SHA-256 of the exact canonical execution plan presented to the user.</summary>
[JsonConverter(typeof(ProtocolPlanDigestJsonConverter))]
public sealed record ProtocolPlanDigest
{
    /// <summary>The canonical lowercase 64-character hexadecimal digest.</summary>
    public string Value { get; }

    private ProtocolPlanDigest(string value)
    {
        this.Value = value;
    }

    /// <summary>Parse an exact canonical lowercase SHA-256 digest.</summary>
    public static ProtocolPlanDigest Parse(string? value)
    {
        ProtocolPlanDigest.AssertCanonical(value);
        return new ProtocolPlanDigest(value!);
    }

    /// <summary>
    /// Compute the digest of the canonical confirmation envelope. This binds the
    /// core <see cref="Engine.BoundInstallationPlan"/> digest to the selected game
    /// root, observed release identities/state, and exact displayed plan details.
    /// </summary>
    public static ProtocolPlanDigest Compute(
        ProtocolPlanDigest executionBindingDigest,
        InstallerOperation operation,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease,
        ObservedInstallState observedState,
        IReadOnlyList<ProtocolPlanOperation> operations,
        IReadOnlyList<ProtocolPlanConflict> conflicts
    )
    {
        ArgumentNullException.ThrowIfNull(executionBindingDigest);
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(conflicts);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("execution_binding_sha256", executionBindingDigest.Value);
            writer.WriteString("action", operation.ToString().ToLowerInvariant());
            writer.WriteStartObject("game_root");
            writer.WriteString("canonical_path", gameRoot.CanonicalPath);
            writer.WriteNumber("device_id", gameRoot.DeviceId);
            writer.WriteNumber("inode", gameRoot.Inode);
            writer.WriteEndObject();
            ProtocolPlanDigest.WriteRelease(writer, "current_release", currentRelease);
            ProtocolPlanDigest.WriteRelease(writer, "target_release", targetRelease);
            writer.WriteString("observed_state", JsonNamingPolicy.CamelCase.ConvertName(observedState.ToString()));
            writer.WriteStartArray("operations");
            foreach (ProtocolPlanOperation planOperation in operations)
            {
                ArgumentNullException.ThrowIfNull(planOperation);
                writer.WriteStartObject();
                writer.WriteString("kind", planOperation.Kind.ToString().ToLowerInvariant());
                writer.WriteString("path", planOperation.Path);
                if (planOperation.ExpectedCurrentSha256 is null)
                    writer.WriteNull("expected_current_sha256");
                else
                    writer.WriteString("expected_current_sha256", planOperation.ExpectedCurrentSha256);
                if (planOperation.ResultSha256 is null)
                    writer.WriteNull("result_sha256");
                else
                    writer.WriteString("result_sha256", planOperation.ResultSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("conflicts");
            foreach (ProtocolPlanConflict conflict in conflicts)
            {
                ArgumentNullException.ThrowIfNull(conflict);
                writer.WriteStartObject();
                writer.WriteString("code", ProtocolPlanDigest.GetConflictName(conflict.Code));
                if (conflict.Path is null)
                    writer.WriteNull("path");
                else
                    writer.WriteString("path", conflict.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new ProtocolPlanDigest(Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant());
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Value;
    }

    internal static void AssertCanonical(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolException("The protocol plan digest isn't a canonical lowercase SHA-256 value.");
    }

    private static string GetConflictName(PlanConflictCode code)
    {
        StringBuilder result = new();
        string value = code.ToString();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
                result.Append('_');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private static void WriteRelease(Utf8JsonWriter writer, string propertyName, ProtocolReleaseIdentity? release)
    {
        if (release is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteString("repository", release.Repository);
        writer.WriteString("tag", release.Tag);
        writer.WriteString("embedded_version", release.EmbeddedVersion);
        writer.WriteString("package_asset_name", release.PackageAssetName);
        writer.WriteString("source_commit", release.SourceCommit);
        writer.WriteString("source_tree", release.SourceTree);
        writer.WriteString("package_sha256", release.PackageSha256);
        writer.WriteEndObject();
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

internal sealed class ProtocolPlanDigestJsonConverter : JsonConverter<ProtocolPlanDigest>
{
    /// <inheritdoc />
    public override ProtocolPlanDigest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A protocol plan digest must be a string.");

        try
        {
            return ProtocolPlanDigest.Parse(reader.GetString());
        }
        catch (ProtocolException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProtocolPlanDigest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ProtocolPlanDigest.AssertCanonical(value.Value);
        writer.WriteStringValue(value.Value);
    }
}
