using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>Serializes and strictly validates deterministic version 1 JSONL messages.</summary>
public static class ProtocolJsonSerializer
{
    /// <summary>The only supported wire protocol version.</summary>
    public const int Version = 1;

    /// <summary>The maximum UTF-8 byte length of one line, excluding a line terminator.</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>The maximum JSON nesting depth.</summary>
    public const int MaxDepth = 16;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        MaxDepth = ProtocolJsonSerializer.MaxDepth,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly IReadOnlyDictionary<ProtocolMessageKind, MessageContract> Contracts =
        new Dictionary<ProtocolMessageKind, MessageContract>
        {
            [ProtocolMessageKind.HandshakeRequest] = new("handshake.request", typeof(HandshakeRequest), true, ["clientName", "clientVersion"]),
            [ProtocolMessageKind.InspectPlanRequest] = new("inspect-plan.request", typeof(InspectPlanRequest), true, ["sessionId", "gamePath", "operation", "targetPackageVersion"]),
            [ProtocolMessageKind.ConfirmPlanRequest] = new("confirm-plan.request", typeof(ConfirmPlanRequest), true, ["sessionId", "planId", "planDigest"]),
            [ProtocolMessageKind.ExecutePlanRequest] = new("execute-plan.request", typeof(ExecutePlanRequest), true, ["sessionId", "planId", "planDigest"]),
            [ProtocolMessageKind.CancelPlanRequest] = new("cancel-plan.request", typeof(CancelPlanRequest), true, ["sessionId", "planId", "planDigest"]),
            [ProtocolMessageKind.HandshakeEvent] = new("handshake.event", typeof(HandshakeEvent), false, ["sessionId", "serverVersion", "capabilities"]),
            [ProtocolMessageKind.PlanEvent] = new("plan.event", typeof(PlanEvent), false, ["sessionId", "planId", "planDigest", "executionBindingDigest", "operation", "gameRoot", "currentRelease", "targetRelease", "observedState", "operations", "conflicts", "summary", "warnings", "requiresConfirmation"]),
            [ProtocolMessageKind.ProgressEvent] = new("progress.event", typeof(ProgressEvent), false, ["sessionId", "planId", "planDigest", "sequence", "stage", "completedUnits", "totalUnits", "message"]),
            [ProtocolMessageKind.SuccessEvent] = new("success.event", typeof(SuccessEvent), false, ["sessionId", "planId", "planDigest", "operation", "summary"]),
            [ProtocolMessageKind.RolledBackFailureEvent] = new("rolled-back-failure.event", typeof(RolledBackFailureEvent), false, ["sessionId", "planId", "planDigest", "errorCode", "message", "rollbackSummary"]),
            [ProtocolMessageKind.RecoverableInterruptionEvent] = new("recoverable-interruption.event", typeof(RecoverableInterruptionEvent), false, ["sessionId", "planId", "planDigest", "errorCode", "message", "recoveryAction", "recoverySummary"]),
            [ProtocolMessageKind.CancelledEvent] = new("cancelled.event", typeof(CancelledEvent), false, ["sessionId", "planId", "planDigest", "summary", "safeStateSummary"])
        };

    private static readonly IReadOnlyDictionary<string, KeyValuePair<ProtocolMessageKind, MessageContract>> ContractsByWireName =
        ProtocolJsonSerializer.Contracts.ToDictionary(pair => pair.Value.WireName, StringComparer.Ordinal);

    /// <summary>Serialize one message without a trailing line terminator.</summary>
    public static string SerializeLine(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ProtocolJsonSerializer.ValidateMessage(message);

        if (!ProtocolJsonSerializer.Contracts.TryGetValue(message.Kind, out MessageContract? contract) || contract.Type != message.GetType())
            throw new ProtocolException("The protocol message type doesn't match its discriminator.");

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", ProtocolJsonSerializer.Version);
            writer.WriteString("messageType", contract.WireName);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, message, message.GetType(), ProtocolJsonSerializer.SerializerOptions);
            writer.WriteEndObject();
        }

        if (stream.Length > ProtocolJsonSerializer.MaxLineBytes)
            throw new ProtocolException($"The serialized protocol line exceeds the {ProtocolJsonSerializer.MaxLineBytes}-byte limit.");

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Deserialize one strict client-to-backend request line.</summary>
    public static ProtocolRequest DeserializeRequestLine(string line)
    {
        return (ProtocolRequest)ProtocolJsonSerializer.DeserializeLine(line, expectRequest: true);
    }

    /// <summary>Deserialize one strict backend-to-client event line.</summary>
    public static ProtocolEvent DeserializeEventLine(string line)
    {
        return (ProtocolEvent)ProtocolJsonSerializer.DeserializeLine(line, expectRequest: false);
    }

    private static ProtocolMessage DeserializeLine(string line, bool expectRequest)
    {
        if (line is null)
            throw new ArgumentNullException(nameof(line));
        if (line.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolException("A protocol line must not contain a line terminator.");
        if (Encoding.UTF8.GetByteCount(line) > ProtocolJsonSerializer.MaxLineBytes)
            throw new ProtocolException($"The protocol line exceeds the {ProtocolJsonSerializer.MaxLineBytes}-byte limit.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ProtocolJsonSerializer.MaxDepth
                }
            );
            JsonElement root = document.RootElement;
            ProtocolJsonSerializer.AssertExactObject(root, ["protocolVersion", "messageType", "payload"], "envelope");

            JsonElement versionElement = root.GetProperty("protocolVersion");
            if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version) || version != ProtocolJsonSerializer.Version)
                throw new ProtocolException($"Unsupported protocol version; only version {ProtocolJsonSerializer.Version} is accepted.");

            JsonElement typeElement = root.GetProperty("messageType");
            if (typeElement.ValueKind != JsonValueKind.String)
                throw new ProtocolException("The protocol message type must be a string.");
            string wireName = typeElement.GetString()!;
            if (!ProtocolJsonSerializer.ContractsByWireName.TryGetValue(wireName, out KeyValuePair<ProtocolMessageKind, MessageContract> pair))
                throw new ProtocolException($"Unknown version {ProtocolJsonSerializer.Version} protocol message type.");

            MessageContract contract = pair.Value;
            if (contract.IsRequest != expectRequest)
                throw new ProtocolException(expectRequest ? "An event can't be accepted as a request." : "A request can't be accepted as an event.");

            JsonElement payload = root.GetProperty("payload");
            ProtocolJsonSerializer.AssertExactObject(payload, contract.Properties, "payload");
            ProtocolJsonSerializer.AssertNestedContracts(pair.Key, payload);
            ProtocolMessage? message = (ProtocolMessage?)JsonSerializer.Deserialize(payload.GetRawText(), contract.Type, ProtocolJsonSerializer.SerializerOptions);
            if (message is null || message.Kind != pair.Key)
                throw new ProtocolException("The protocol payload couldn't be created as its declared message type.");

            ProtocolJsonSerializer.ValidateMessage(message);
            return message;
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            throw new ProtocolException("The protocol line isn't valid strict version 1 JSON.", ex);
        }
    }

    private static void AssertExactObject(JsonElement element, IReadOnlyCollection<string> expectedProperties, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ProtocolException($"The protocol {description} must be a JSON object.");

        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                throw new ProtocolException($"The protocol {description} contains a duplicate '{property.Name}' property.");
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal))
                throw new ProtocolException($"The protocol {description} contains an unknown '{property.Name}' property.");
        }

        string? missing = expectedProperties.FirstOrDefault(property => !actual.Contains(property));
        if (missing is not null)
            throw new ProtocolException($"The protocol {description} is missing the required '{missing}' property.");
    }

    private static void AssertNestedContracts(ProtocolMessageKind kind, JsonElement payload)
    {
        if (kind != ProtocolMessageKind.PlanEvent)
            return;

        ProtocolJsonSerializer.AssertExactObject(
            payload.GetProperty("gameRoot"),
            ["canonicalPath", "deviceMajor", "deviceMinor", "inode", "operationGeneration"],
            "plan game-root identity"
        );
        ProtocolJsonSerializer.AssertOptionalReleaseObject(payload.GetProperty("currentRelease"), "current release identity");
        ProtocolJsonSerializer.AssertOptionalReleaseObject(payload.GetProperty("targetRelease"), "target release identity");
        ProtocolJsonSerializer.AssertObjectArray(
            payload.GetProperty("operations"),
            ["kind", "path", "expectedCurrentSha256", "resultSha256"],
            "plan operation",
            2048
        );
        ProtocolJsonSerializer.AssertObjectArray(
            payload.GetProperty("conflicts"),
            ["code", "path"],
            "plan conflict",
            256
        );
    }

    private static void AssertOptionalReleaseObject(JsonElement element, string description)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return;
        ProtocolJsonSerializer.AssertExactObject(
            element,
            [
                "repository",
                "tag",
                "embeddedVersion",
                "packageAssetName",
                "sourceCommit",
                "sourceTree",
                "packageSha256",
                "packageSizeBytes",
                "buildWorkflow",
                "buildConfiguration",
                "runtimeIdentifier"
            ],
            description
        );
    }

    private static void AssertObjectArray(
        JsonElement element,
        IReadOnlyCollection<string> expectedProperties,
        string description,
        int maximumCount
    )
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new ProtocolException($"The protocol {description} collection must be a JSON array.");

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (index >= maximumCount)
                throw new ProtocolException($"The protocol {description} collection is too large.");
            ProtocolJsonSerializer.AssertExactObject(item, expectedProperties, $"{description} at index {index}");
            index++;
        }
    }

    private static void ValidateMessage(ProtocolMessage message)
    {
        static void RequireText(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ProtocolException($"The protocol '{field}' value can't be empty.");
            if (value.Length > 4096)
                throw new ProtocolException($"The protocol '{field}' value is too long.");
        }

        static void RequireStrings(string[]? values, string field)
        {
            if (values is null || values.Length > 256)
                throw new ProtocolException($"The protocol '{field}' collection is missing or too large.");
            foreach (string value in values)
                RequireText(value, field);
        }

        static void RequireDefined<TEnum>(TEnum value, string field) where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new ProtocolException($"The protocol '{field}' value isn't defined by version 1.");
        }

        switch (message)
        {
            case HandshakeRequest value:
                RequireText(value.ClientName, "clientName");
                RequireText(value.ClientVersion, "clientVersion");
                break;
            case InspectPlanRequest value:
                ProtocolIdentifier.AssertCanonical(value.SessionId.Value, "session");
                RequireText(value.GamePath, "gamePath");
                RequireDefined(value.Operation, "operation");
                ProtocolJsonSerializer.ValidateRequestedTarget(value.Operation, value.TargetPackageVersion);
                break;
            case ConfirmPlanRequest value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                break;
            case ExecutePlanRequest value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                break;
            case CancelPlanRequest value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                break;
            case HandshakeEvent value:
                ProtocolIdentifier.AssertCanonical(value.SessionId.Value, "session");
                RequireText(value.ServerVersion, "serverVersion");
                RequireStrings(value.Capabilities, "capabilities");
                break;
            case PlanEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                if (value.ExecutionBindingDigest is null)
                    throw new ProtocolException("The protocol 'executionBindingDigest' value can't be null.");
                ProtocolPlanDigest.AssertCanonical(value.ExecutionBindingDigest.Value);
                RequireDefined(value.Operation, "operation");
                ProtocolJsonSerializer.ValidateGameRoot(value.GameRoot);
                ProtocolJsonSerializer.ValidateRelease(value.CurrentRelease, "currentRelease");
                ProtocolJsonSerializer.ValidateRelease(value.TargetRelease, "targetRelease");
                ProtocolJsonSerializer.ValidatePlanReleaseSemantics(value.Operation, value.CurrentRelease, value.TargetRelease);
                RequireDefined(value.ObservedState, "observedState");
                ProtocolJsonSerializer.ValidatePlanCollections(value.Operations, value.Conflicts);
                RequireText(value.Summary, "summary");
                RequireStrings(value.Warnings, "warnings");
                if (!value.RequiresConfirmation)
                    throw new ProtocolException("Every version 1 operation plan must require explicit confirmation.");
                ProtocolPlanDigest computedDigest = ProtocolPlanDigest.Compute(
                    value.ExecutionBindingDigest,
                    value.Operation,
                    value.GameRoot,
                    value.CurrentRelease,
                    value.TargetRelease,
                    value.ObservedState,
                    value.Operations,
                    value.Conflicts
                );
                if (value.PlanDigest != computedDigest)
                    throw new ProtocolException("The protocol plan digest doesn't match the canonical structured execution plan.");
                break;
            case ProgressEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                RequireDefined(value.Stage, "stage");
                RequireText(value.Message, "message");
                if (value.Sequence < 0 || value.CompletedUnits < 0 || value.TotalUnits < 0 || value.CompletedUnits > value.TotalUnits)
                    throw new ProtocolException("The protocol progress counters are inconsistent.");
                break;
            case SuccessEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                RequireDefined(value.Operation, "operation");
                RequireText(value.Summary, "summary");
                break;
            case RolledBackFailureEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                RequireText(value.ErrorCode, "errorCode");
                RequireText(value.Message, "message");
                RequireText(value.RollbackSummary, "rollbackSummary");
                break;
            case RecoverableInterruptionEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                RequireText(value.ErrorCode, "errorCode");
                RequireText(value.Message, "message");
                RequireDefined(value.RecoveryAction, "recoveryAction");
                RequireText(value.RecoverySummary, "recoverySummary");
                break;
            case CancelledEvent value:
                ProtocolJsonSerializer.ValidatePlanBinding(value.SessionId, value.PlanId, value.PlanDigest);
                RequireText(value.Summary, "summary");
                RequireText(value.SafeStateSummary, "safeStateSummary");
                break;
            default:
                throw new ProtocolException("The message isn't part of the version 1 protocol.");
        }
    }

    private static void ValidateIds(ProtocolSessionId sessionId, ProtocolPlanId planId)
    {
        ProtocolIdentifier.AssertCanonical(sessionId.Value, "session");
        ProtocolIdentifier.AssertCanonical(planId.Value, "plan");
    }

    private static void ValidatePlanBinding(ProtocolSessionId sessionId, ProtocolPlanId planId, ProtocolPlanDigest? planDigest)
    {
        ProtocolJsonSerializer.ValidateIds(sessionId, planId);
        if (planDigest is null)
            throw new ProtocolException("The protocol 'planDigest' value can't be null.");
        ProtocolPlanDigest.AssertCanonical(planDigest.Value);
    }

    private static void ValidateRelease(ProtocolReleaseIdentity? release, string field)
    {
        if (release is null)
            return;

        ProtocolJsonSerializer.RequireBoundedText(release.Repository, "release.repository");
        ProtocolJsonSerializer.RequireBoundedText(release.Tag, "release.tag");
        ProtocolJsonSerializer.RequireBoundedText(release.EmbeddedVersion, "release.embeddedVersion");
        ProtocolJsonSerializer.RequireBoundedText(release.PackageAssetName, "release.packageAssetName");
        ProtocolJsonSerializer.RequireLowerHex(release.SourceCommit, 40, "release.sourceCommit");
        ProtocolJsonSerializer.RequireLowerHex(release.SourceTree, 40, "release.sourceTree");
        ProtocolJsonSerializer.RequireLowerHex(release.PackageSha256, 64, "release.packageSha256");
        if (release.PackageSizeBytes <= 0)
            throw new ProtocolException("The protocol release package size must be positive.");
        ProtocolJsonSerializer.RequireBoundedText(release.BuildWorkflow, "release.buildWorkflow");
        ProtocolJsonSerializer.RequireBoundedText(release.BuildConfiguration, "release.buildConfiguration");
        ProtocolJsonSerializer.RequireBoundedText(release.RuntimeIdentifier, "release.runtimeIdentifier");

        if (
            !Uri.TryCreate(release.Repository, UriKind.Absolute, out Uri? repository)
            || repository.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(repository.Host)
            || !string.IsNullOrEmpty(repository.UserInfo)
            || !string.IsNullOrEmpty(repository.Query)
            || !string.IsNullOrEmpty(repository.Fragment)
        )
        {
            throw new ProtocolException("The protocol release repository must be a canonical HTTPS URL without credentials, query, or fragment.");
        }

        if (
            release.PackageAssetName is "." or ".."
            || release.PackageAssetName.IndexOfAny(['/', '\\']) >= 0
            || release.PackageAssetName.Any(char.IsControl)
        )
        {
            throw new ProtocolException("The protocol release package asset name must be one plain filename.");
        }

        try
        {
            _ = new InstallationReleaseIdentity(
                release.Repository,
                release.Tag,
                release.EmbeddedVersion,
                release.PackageAssetName,
                release.SourceCommit,
                release.SourceTree,
                Sha256Digest.Parse(release.PackageSha256),
                release.PackageSizeBytes,
                release.BuildWorkflow,
                release.BuildConfiguration,
                release.RuntimeIdentifier
            );
        }
        catch (ArgumentException ex)
        {
            throw new ProtocolException($"The protocol '{field}' value isn't an exact reviewed fork release identity.", ex);
        }
    }

    private static void ValidateRequestedTarget(InstallerOperation operation, string? targetPackageVersion)
    {
        bool required = operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair;
        bool forbidden = operation is InstallerOperation.Backup or InstallerOperation.Uninstall;
        if (required)
            ProtocolJsonSerializer.RequireBoundedText(targetPackageVersion, "targetPackageVersion");
        else if (forbidden && targetPackageVersion is not null)
            throw new ProtocolException($"The protocol operation '{operation}' must not invent a target package version.");
        else if (targetPackageVersion is not null)
            ProtocolJsonSerializer.RequireBoundedText(targetPackageVersion, "targetPackageVersion");
    }

    private static void ValidatePlanReleaseSemantics(
        InstallerOperation operation,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease
    )
    {
        if (operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair)
        {
            if (targetRelease is null)
                throw new ProtocolException($"The protocol operation '{operation}' requires an exact target release identity.");
        }
        else if (operation is InstallerOperation.Backup or InstallerOperation.Uninstall)
        {
            if (targetRelease is not null)
                throw new ProtocolException($"The protocol operation '{operation}' must not invent a target release identity.");
        }

        if (operation == InstallerOperation.Install && currentRelease is not null)
            throw new ProtocolException("A fresh install plan must not invent a current release identity.");
    }

    private static void ValidateGameRoot(ProtocolGameRootIdentity? gameRoot)
    {
        if (gameRoot is null)
            throw new ProtocolException("The protocol 'gameRoot' value can't be null.");
        ProtocolJsonSerializer.RequireBoundedText(gameRoot.CanonicalPath, "gameRoot.canonicalPath");
        string path = gameRoot.CanonicalPath;
        if (
            path[0] != '/'
            || path.IndexOf('\\') >= 0
            || path.Any(char.IsControl)
            || (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            || path.Split('/').Skip(1).Any(segment => segment.Length == 0 || segment is "." or "..")
        )
        {
            throw new ProtocolException("The protocol game-root path isn't a canonical absolute Linux path.");
        }
        if (gameRoot.Inode == 0)
            throw new ProtocolException("The protocol game-root inode must be nonzero.");
    }

    private static void ValidatePlanCollections(
        ProtocolPlanOperation[]? operations,
        ProtocolPlanConflict[]? conflicts
    )
    {
        if (operations is null || operations.Length > 2048)
            throw new ProtocolException("The protocol 'operations' collection is missing or too large.");
        if (conflicts is null || conflicts.Length > 256)
            throw new ProtocolException("The protocol 'conflicts' collection is missing or too large.");

        HashSet<(PlanOperationKind Kind, string Path, string? Expected, string? Result)> seenOperations = [];
        (string Path, int Kind, string Result)? previousOperationKey = null;
        foreach (ProtocolPlanOperation? operation in operations)
        {
            if (operation is null)
                throw new ProtocolException("The protocol plan operation collection can't contain null entries.");
            if (!Enum.IsDefined(operation.Kind))
                throw new ProtocolException("The protocol plan operation kind isn't defined by version 1.");
            ProtocolJsonSerializer.RequireCanonicalRelativePath(operation.Path, "operation.path");
            if (operation.ExpectedCurrentSha256 is not null)
                ProtocolJsonSerializer.RequireLowerHex(operation.ExpectedCurrentSha256, 64, "operation.expectedCurrentSha256");
            if (operation.ResultSha256 is not null)
                ProtocolJsonSerializer.RequireLowerHex(operation.ResultSha256, 64, "operation.resultSha256");
            ProtocolJsonSerializer.ValidateOperationHashes(operation);

            if (!seenOperations.Add((operation.Kind, operation.Path, operation.ExpectedCurrentSha256, operation.ResultSha256)))
                throw new ProtocolException("The protocol plan operations contain an exact duplicate.");

            (string Path, int Kind, string Result) key = (operation.Path, (int)operation.Kind, operation.ResultSha256 ?? "");
            if (previousOperationKey is { } previous && ProtocolJsonSerializer.CompareOperationKeys(previous, key) > 0)
                throw new ProtocolException("The protocol plan operations aren't in canonical order.");
            previousOperationKey = key;
        }

        HashSet<(PlanConflictCode Code, string? Path)> seenConflicts = [];
        (string Path, int Code)? previousConflictKey = null;
        foreach (ProtocolPlanConflict? conflict in conflicts)
        {
            if (conflict is null)
                throw new ProtocolException("The protocol plan conflict collection can't contain null entries.");
            if (!Enum.IsDefined(conflict.Code))
                throw new ProtocolException("The protocol plan conflict code isn't defined by version 1.");
            if (conflict.Path is not null)
                ProtocolJsonSerializer.RequireCanonicalRelativePath(conflict.Path, "conflict.path");
            if (!seenConflicts.Add((conflict.Code, conflict.Path)))
                throw new ProtocolException("The protocol plan conflicts contain an exact duplicate.");

            (string Path, int Code) key = (conflict.Path ?? "", (int)conflict.Code);
            if (previousConflictKey is { } previous && ProtocolJsonSerializer.CompareConflictKeys(previous, key) > 0)
                throw new ProtocolException("The protocol plan conflicts aren't in canonical order.");
            previousConflictKey = key;
        }
    }

    private static void ValidateOperationHashes(ProtocolPlanOperation operation)
    {
        bool hasExpected = operation.ExpectedCurrentSha256 is not null;
        bool hasResult = operation.ResultSha256 is not null;
        bool hashesEqual = hasExpected && hasResult && operation.ExpectedCurrentSha256 == operation.ResultSha256;
        bool valid = operation.Kind switch
        {
            PlanOperationKind.Backup => hashesEqual,
            PlanOperationKind.Remove => hasExpected && !hasResult,
            PlanOperationKind.Restore => hasResult,
            PlanOperationKind.Create => !hasExpected && hasResult,
            PlanOperationKind.Replace => hasExpected && hasResult,
            PlanOperationKind.Retain => hashesEqual,
            PlanOperationKind.Preserve => hashesEqual,
            _ => false
        };
        if (!valid)
            throw new ProtocolException("The protocol plan operation hashes are inconsistent with its operation kind.");
    }

    private static int CompareOperationKeys((string Path, int Kind, string Result) left, (string Path, int Kind, string Result) right)
    {
        int result = StringComparer.Ordinal.Compare(left.Path, right.Path);
        if (result != 0)
            return result;
        result = left.Kind.CompareTo(right.Kind);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.Result, right.Result);
    }

    private static int CompareConflictKeys((string Path, int Code) left, (string Path, int Code) right)
    {
        int result = StringComparer.Ordinal.Compare(left.Path, right.Path);
        return result != 0 ? result : left.Code.CompareTo(right.Code);
    }

    private static void RequireCanonicalRelativePath(string? value, string field)
    {
        ProtocolJsonSerializer.RequireBoundedText(value, field);
        if (
            value![0] == '/'
            || value.IndexOf('\\') >= 0
            || value.Any(char.IsControl)
            || value.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
        )
        {
            throw new ProtocolException($"The protocol '{field}' value isn't a canonical relative path.");
        }
    }

    private static void RequireBoundedText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ProtocolException($"The protocol '{field}' value can't be empty.");
        if (value.Length > 4096)
            throw new ProtocolException($"The protocol '{field}' value is too long.");
    }

    private static void RequireLowerHex(string? value, int length, string field)
    {
        if (value is null || value.Length != length || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolException($"The protocol '{field}' value isn't canonical lowercase hexadecimal.");
    }

    private sealed record MessageContract(string WireName, Type Type, bool IsRequest, string[] Properties);
}
