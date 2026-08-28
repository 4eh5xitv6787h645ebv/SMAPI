using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

[JsonConverter(typeof(ProtocolSessionIdJsonConverter))]
public readonly record struct ProtocolSessionId(string Value)
{
    public static ProtocolSessionId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolSessionId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "session"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolPlanIdJsonConverter))]
public readonly record struct ProtocolPlanId(string Value)
{
    public static ProtocolPlanId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolPlanId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "plan"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolPackageIdJsonConverter))]
public readonly record struct ProtocolPackageId(string Value)
{
    public static ProtocolPackageId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolPackageId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "package"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolRecoveryCatalogIdJsonConverter))]
public readonly record struct ProtocolRecoveryCatalogId(string Value)
{
    public static ProtocolRecoveryCatalogId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolRecoveryCatalogId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "recovery catalog"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolRecoverySelectionIdJsonConverter))]
public readonly record struct ProtocolRecoverySelectionId(string Value)
{
    public static ProtocolRecoverySelectionId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolRecoverySelectionId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "recovery selection"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolCandidateIdJsonConverter))]
public readonly record struct ProtocolCandidateId(string Value)
{
    public static ProtocolCandidateId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolCandidateId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "candidate"));
    public override string ToString() => this.Value;
}

[JsonConverter(typeof(ProtocolPrunePlanIdJsonConverter))]
public readonly record struct ProtocolPrunePlanId(string Value)
{
    public static ProtocolPrunePlanId CreateRandom() => new(ProtocolIdentifier.Create());
    public static ProtocolPrunePlanId Parse(string? value) => new(ProtocolIdentifier.Parse(value, "prune plan"));
    public override string ToString() => this.Value;
}

/// <summary>The SHA-256 of the exact canonical plan and all displayed confirmation data.</summary>
[JsonConverter(typeof(ProtocolPlanDigestJsonConverter))]
public sealed record ProtocolPlanDigest
{
    public string Value { get; }

    private ProtocolPlanDigest(string value) => this.Value = value;

    public static ProtocolPlanDigest Parse(string? value)
    {
        AssertCanonical(value);
        return new(value!);
    }

    public static ProtocolPlanDigest Compute(
        ProtocolPlanDigest executionBindingDigest,
        InstallerOperation operation,
        ProtocolPackageId? packageId,
        ProtocolRecoverySelectionId? recoverySelectionId,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease,
        ObservedInstallState observedState,
        IReadOnlyList<ProtocolPlanOperation> operations,
        IReadOnlyList<ProtocolPlanConflict> conflicts,
        IReadOnlyList<ProtocolPlanCandidate> candidates,
        string summary,
        IReadOnlyList<string> warnings,
        bool requiresConfirmation
    ) => Hash(writer =>
    {
        writer.WriteString("execution_binding_sha256", executionBindingDigest.Value);
        writer.WriteString("action", operation.ToString().ToLowerInvariant());
        WriteOptionalId(writer, "package_id", packageId?.Value);
        WriteOptionalId(writer, "recovery_selection_id", recoverySelectionId?.Value);
        WriteGameRoot(writer, gameRoot);
        WriteRelease(writer, "current_release", currentRelease);
        WriteRelease(writer, "target_release", targetRelease);
        writer.WriteString("observed_state", JsonNamingPolicy.CamelCase.ConvertName(observedState.ToString()));
        writer.WriteStartArray("operations");
        foreach (ProtocolPlanOperation item in operations)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()));
            writer.WriteString("path", item.Path);
            WriteOptional(writer, "expected_current_sha256", item.ExpectedCurrentSha256);
            WriteOptional(writer, "result_sha256", item.ResultSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("conflicts");
        foreach (ProtocolPlanConflict item in conflicts)
        {
            writer.WriteStartObject();
            writer.WriteString("code", ToSnakeCase(item.Code.ToString()));
            WriteOptional(writer, "path", item.Path);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("candidates");
        foreach (ProtocolPlanCandidate item in candidates)
        {
            writer.WriteStartObject();
            writer.WriteString("candidate_id", item.CandidateId.Value);
            writer.WriteString("kind", JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()));
            writer.WriteString("path", item.Path);
            writer.WriteString("observed_sha256", item.ObservedSha256);
            writer.WriteNumber("observed_size_bytes", item.ObservedSizeBytes);
            writer.WriteNumber("observed_unix_mode", item.ObservedUnixMode);
            writer.WriteString("proposed_result_sha256", item.ProposedResultSha256);
            writer.WriteBoolean("selected", item.Selected);
            writer.WriteString("evidence", item.Evidence);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("summary", summary);
        WriteStrings(writer, "warnings", warnings);
        writer.WriteBoolean("requires_confirmation", requiresConfirmation);
    });

    public static ProtocolPlanDigest ComputePrune(
        ProtocolPlanDigest executionBindingDigest,
        ProtocolRecoveryCatalogId catalogId,
        ProtocolGameRootIdentity gameRoot,
        string headSha256,
        int retainNewest,
        IReadOnlyList<ProtocolRecoverySelectionId> retained,
        IReadOnlyList<ProtocolRecoverySelectionId> removed,
        string summary,
        IReadOnlyList<string> warnings,
        bool requiresConfirmation
    ) => Hash(writer =>
    {
        writer.WriteString("execution_binding_sha256", executionBindingDigest.Value);
        writer.WriteString("catalog_id", catalogId.Value);
        WriteGameRoot(writer, gameRoot);
        writer.WriteString("head_sha256", headSha256);
        writer.WriteNumber("retain_newest", retainNewest);
        WriteIds(writer, "retained_selection_ids", retained.Select(p => p.Value));
        WriteIds(writer, "removed_selection_ids", removed.Select(p => p.Value));
        writer.WriteString("summary", summary);
        WriteStrings(writer, "warnings", warnings);
        writer.WriteBoolean("requires_confirmation", requiresConfirmation);
    });

    public override string ToString() => this.Value;

    internal static void AssertCanonical(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolException("The protocol plan digest isn't a canonical lowercase SHA-256 value.");
    }

    private static ProtocolPlanDigest Hash(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return new(Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant());
    }

    private static void WriteGameRoot(Utf8JsonWriter writer, ProtocolGameRootIdentity root)
    {
        writer.WriteStartObject("game_root");
        writer.WriteString("canonical_path", root.CanonicalPath);
        writer.WriteNumber("device_major", root.DeviceMajor);
        writer.WriteNumber("device_minor", root.DeviceMinor);
        writer.WriteNumber("inode", root.Inode);
        writer.WriteNumber("operation_generation", root.OperationGeneration);
        writer.WriteEndObject();
    }

    private static void WriteRelease(Utf8JsonWriter writer, string name, ProtocolReleaseIdentity? release)
    {
        if (release is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name);
        writer.WriteString("repository", release.Repository);
        writer.WriteString("tag", release.Tag);
        writer.WriteString("embedded_version", release.EmbeddedVersion);
        writer.WriteString("package_asset_name", release.PackageAssetName);
        writer.WriteString("source_commit", release.SourceCommit);
        writer.WriteString("source_tree", release.SourceTree);
        writer.WriteString("package_sha256", release.PackageSha256);
        writer.WriteNumber("package_size_bytes", release.PackageSizeBytes);
        writer.WriteString("build_workflow", release.BuildWorkflow);
        writer.WriteString("build_configuration", release.BuildConfiguration);
        writer.WriteString("runtime_identifier", release.RuntimeIdentifier);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void WriteOptionalId(Utf8JsonWriter writer, string name, string? value) => WriteOptional(writer, name, value);
    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values) { writer.WriteStartArray(name); foreach (string value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
    private static void WriteIds(Utf8JsonWriter writer, string name, IEnumerable<string> values) => WriteStrings(writer, name, values);
    private static string ToSnakeCase(string value)
    {
        StringBuilder result = new();
        foreach (char character in value)
        {
            if (result.Length > 0 && char.IsUpper(character)) result.Append('_');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
}

internal static class ProtocolIdentifier
{
    public static string Create() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static string Parse(string? value, string kind)
    {
        AssertCanonical(value, kind);
        return value!;
    }

    public static void AssertCanonical(string? value, string kind)
    {
        if (value is null || value.Length != 32 || value == new string('0', 32) || !Guid.TryParseExact(value, "N", out _) || value.Any(c => c is >= 'A' and <= 'F'))
            throw new ProtocolException($"The protocol {kind} ID isn't a canonical nonempty lowercase 128-bit value.");
    }
}

internal abstract class ProtocolIdJsonConverter<T> : JsonConverter<T>
{
    protected abstract string Kind { get; }
    protected abstract T Parse(string? value);
    protected abstract string GetValue(T value);
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"A protocol {this.Kind} ID must be a string.");
        try { return this.Parse(reader.GetString()); } catch (ProtocolException ex) { throw new JsonException(ex.Message, ex); }
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        string text = this.GetValue(value);
        ProtocolIdentifier.AssertCanonical(text, this.Kind);
        writer.WriteStringValue(text);
    }
}

internal sealed class ProtocolSessionIdJsonConverter : ProtocolIdJsonConverter<ProtocolSessionId> { protected override string Kind => "session"; protected override ProtocolSessionId Parse(string? v) => ProtocolSessionId.Parse(v); protected override string GetValue(ProtocolSessionId v) => v.Value; }
internal sealed class ProtocolPlanIdJsonConverter : ProtocolIdJsonConverter<ProtocolPlanId> { protected override string Kind => "plan"; protected override ProtocolPlanId Parse(string? v) => ProtocolPlanId.Parse(v); protected override string GetValue(ProtocolPlanId v) => v.Value; }
internal sealed class ProtocolPackageIdJsonConverter : ProtocolIdJsonConverter<ProtocolPackageId> { protected override string Kind => "package"; protected override ProtocolPackageId Parse(string? v) => ProtocolPackageId.Parse(v); protected override string GetValue(ProtocolPackageId v) => v.Value; }
internal sealed class ProtocolRecoveryCatalogIdJsonConverter : ProtocolIdJsonConverter<ProtocolRecoveryCatalogId> { protected override string Kind => "recovery catalog"; protected override ProtocolRecoveryCatalogId Parse(string? v) => ProtocolRecoveryCatalogId.Parse(v); protected override string GetValue(ProtocolRecoveryCatalogId v) => v.Value; }
internal sealed class ProtocolRecoverySelectionIdJsonConverter : ProtocolIdJsonConverter<ProtocolRecoverySelectionId> { protected override string Kind => "recovery selection"; protected override ProtocolRecoverySelectionId Parse(string? v) => ProtocolRecoverySelectionId.Parse(v); protected override string GetValue(ProtocolRecoverySelectionId v) => v.Value; }
internal sealed class ProtocolCandidateIdJsonConverter : ProtocolIdJsonConverter<ProtocolCandidateId> { protected override string Kind => "candidate"; protected override ProtocolCandidateId Parse(string? v) => ProtocolCandidateId.Parse(v); protected override string GetValue(ProtocolCandidateId v) => v.Value; }
internal sealed class ProtocolPrunePlanIdJsonConverter : ProtocolIdJsonConverter<ProtocolPrunePlanId> { protected override string Kind => "prune plan"; protected override ProtocolPrunePlanId Parse(string? v) => ProtocolPrunePlanId.Parse(v); protected override string GetValue(ProtocolPrunePlanId v) => v.Value; }

internal sealed class ProtocolPlanDigestJsonConverter : JsonConverter<ProtocolPlanDigest>
{
    public override ProtocolPlanDigest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A protocol plan digest must be a string.");
        try { return ProtocolPlanDigest.Parse(reader.GetString()); } catch (ProtocolException ex) { throw new JsonException(ex.Message, ex); }
    }
    public override void Write(Utf8JsonWriter writer, ProtocolPlanDigest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ProtocolPlanDigest.AssertCanonical(value.Value);
        writer.WriteStringValue(value.Value);
    }
}
