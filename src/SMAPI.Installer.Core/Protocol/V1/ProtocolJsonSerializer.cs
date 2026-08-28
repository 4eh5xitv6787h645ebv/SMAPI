using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            [ProtocolMessageKind.ConfirmPlanRequest] = new("confirm-plan.request", typeof(ConfirmPlanRequest), true, ["sessionId", "planId"]),
            [ProtocolMessageKind.ExecutePlanRequest] = new("execute-plan.request", typeof(ExecutePlanRequest), true, ["sessionId", "planId"]),
            [ProtocolMessageKind.CancelPlanRequest] = new("cancel-plan.request", typeof(CancelPlanRequest), true, ["sessionId", "planId"]),
            [ProtocolMessageKind.HandshakeEvent] = new("handshake.event", typeof(HandshakeEvent), false, ["sessionId", "serverVersion", "capabilities"]),
            [ProtocolMessageKind.PlanEvent] = new("plan.event", typeof(PlanEvent), false, ["sessionId", "planId", "operation", "gamePath", "observedState", "summary", "warnings", "requiresConfirmation"]),
            [ProtocolMessageKind.ProgressEvent] = new("progress.event", typeof(ProgressEvent), false, ["sessionId", "planId", "sequence", "stage", "completedUnits", "totalUnits", "message"]),
            [ProtocolMessageKind.SuccessEvent] = new("success.event", typeof(SuccessEvent), false, ["sessionId", "planId", "operation", "summary"]),
            [ProtocolMessageKind.RolledBackFailureEvent] = new("rolled-back-failure.event", typeof(RolledBackFailureEvent), false, ["sessionId", "planId", "errorCode", "message", "rollbackSummary"]),
            [ProtocolMessageKind.RecoverableInterruptionEvent] = new("recoverable-interruption.event", typeof(RecoverableInterruptionEvent), false, ["sessionId", "planId", "errorCode", "message", "recoveryAction", "recoverySummary"])
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
                RequireText(value.TargetPackageVersion, "targetPackageVersion");
                break;
            case ConfirmPlanRequest value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                break;
            case ExecutePlanRequest value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                break;
            case CancelPlanRequest value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                break;
            case HandshakeEvent value:
                ProtocolIdentifier.AssertCanonical(value.SessionId.Value, "session");
                RequireText(value.ServerVersion, "serverVersion");
                RequireStrings(value.Capabilities, "capabilities");
                break;
            case PlanEvent value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                RequireDefined(value.Operation, "operation");
                RequireText(value.GamePath, "gamePath");
                RequireDefined(value.ObservedState, "observedState");
                RequireText(value.Summary, "summary");
                RequireStrings(value.Warnings, "warnings");
                if (!value.RequiresConfirmation)
                    throw new ProtocolException("Every version 1 operation plan must require explicit confirmation.");
                break;
            case ProgressEvent value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                RequireDefined(value.Stage, "stage");
                RequireText(value.Message, "message");
                if (value.Sequence < 0 || value.CompletedUnits < 0 || value.TotalUnits < 0 || value.CompletedUnits > value.TotalUnits)
                    throw new ProtocolException("The protocol progress counters are inconsistent.");
                break;
            case SuccessEvent value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                RequireDefined(value.Operation, "operation");
                RequireText(value.Summary, "summary");
                break;
            case RolledBackFailureEvent value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                RequireText(value.ErrorCode, "errorCode");
                RequireText(value.Message, "message");
                RequireText(value.RollbackSummary, "rollbackSummary");
                break;
            case RecoverableInterruptionEvent value:
                ProtocolJsonSerializer.ValidateIds(value.SessionId, value.PlanId);
                RequireText(value.ErrorCode, "errorCode");
                RequireText(value.Message, "message");
                RequireDefined(value.RecoveryAction, "recoveryAction");
                RequireText(value.RecoverySummary, "recoverySummary");
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

    private sealed record MessageContract(string WireName, Type Type, bool IsRequest, string[] Properties);
}
