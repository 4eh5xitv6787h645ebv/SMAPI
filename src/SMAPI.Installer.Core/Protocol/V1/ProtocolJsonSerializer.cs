using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>Serializes and strictly validates bounded, deterministic version 1 JSONL messages.</summary>
public static class ProtocolJsonSerializer
{
    public const int Version = 1;
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxDepth = 16;
    public const int MaxPackages = 16;
    public const int MaxGameCandidates = 64;
    public const int MaxRecoveryCatalogs = 64;
    public const int MaxRecoveryGenerations = 64;
    public const int MaxPlanCandidates = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        MaxDepth = MaxDepth,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly IReadOnlyDictionary<ProtocolMessageKind, MessageContract> Contracts = CreateContracts();
    private static readonly IReadOnlyDictionary<string, KeyValuePair<ProtocolMessageKind, MessageContract>> ContractsByWireName =
        Contracts.ToDictionary(pair => pair.Value.WireName, StringComparer.Ordinal);

    public static string SerializeLine(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMessage(message);
        if (!Contracts.TryGetValue(message.Kind, out MessageContract? contract) || contract.Type != message.GetType())
            throw new ProtocolException("The protocol message type doesn't match its discriminator.");
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", Version);
            writer.WriteString("messageType", contract.WireName);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, message, message.GetType(), SerializerOptions);
            writer.WriteEndObject();
        }
        if (stream.Length > MaxLineBytes)
            throw new ProtocolException($"The serialized protocol line exceeds the {MaxLineBytes}-byte limit.");
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ProtocolRequest DeserializeRequestLine(string line) => (ProtocolRequest)DeserializeLine(line, true);
    public static ProtocolEvent DeserializeEventLine(string line) => (ProtocolEvent)DeserializeLine(line, false);

    private static ProtocolMessage DeserializeLine(string line, bool expectRequest)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.IndexOfAny(['\r', '\n']) >= 0) throw new ProtocolException("A protocol line must not contain a line terminator.");
        if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes) throw new ProtocolException($"The protocol line exceeds the {MaxLineBytes}-byte limit.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow });
            JsonElement root = document.RootElement;
            AssertExactObject(root, ["protocolVersion", "messageType", "payload"], "envelope");
            if (!root.GetProperty("protocolVersion").TryGetInt32(out int version) || version != Version)
                throw new ProtocolException($"Unsupported protocol version; only version {Version} is accepted.");
            JsonElement type = root.GetProperty("messageType");
            if (type.ValueKind != JsonValueKind.String || !ContractsByWireName.TryGetValue(type.GetString()!, out KeyValuePair<ProtocolMessageKind, MessageContract> pair))
                throw new ProtocolException($"Unknown version {Version} protocol message type.");
            if (pair.Value.IsRequest != expectRequest)
                throw new ProtocolException(expectRequest ? "An event can't be accepted as a request." : "A request can't be accepted as an event.");
            JsonElement payload = root.GetProperty("payload");
            AssertExactObject(payload, pair.Value.Properties, "payload");
            AssertNestedContracts(pair.Key, payload);
            ProtocolMessage? message = (ProtocolMessage?)JsonSerializer.Deserialize(payload.GetRawText(), pair.Value.Type, SerializerOptions);
            if (message is null || message.Kind != pair.Key) throw new ProtocolException("The protocol payload couldn't be created as its declared message type.");
            ValidateMessage(message);
            return message;
        }
        catch (ProtocolException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            throw new ProtocolException("The protocol line isn't valid strict version 1 JSON.", ex);
        }
    }

    private static IReadOnlyDictionary<ProtocolMessageKind, MessageContract> CreateContracts() => new Dictionary<ProtocolMessageKind, MessageContract>
    {
        [ProtocolMessageKind.HandshakeRequest] = C("handshake.request", typeof(HandshakeRequest), true, "clientName", "clientVersion"),
        [ProtocolMessageKind.DiscoverGamesRequest] = C("discover-games.request", typeof(DiscoverGamesRequest), true, "sessionId"),
        [ProtocolMessageKind.OpenPackageRequest] = C("open-package.request", typeof(OpenPackageRequest), true, "sessionId", "releaseTag", "expectedSourceCommit", "packagePath", "checksumsPath", "buildMetadataPath", "installManifestPath"),
        [ProtocolMessageKind.ListRecoveriesRequest] = C("list-recoveries.request", typeof(ListRecoveriesRequest), true, "sessionId", "gamePath"),
        [ProtocolMessageKind.InspectPlanRequest] = C("inspect-plan.request", typeof(InspectPlanRequest), true, "sessionId", "gamePath", "operation", "packageId", "recoverySelectionId"),
        [ProtocolMessageKind.SelectPlanCandidatesRequest] = C("select-plan-candidates.request", typeof(SelectPlanCandidatesRequest), true, "sessionId", "planId", "planDigest", "selectedCandidateIds"),
        [ProtocolMessageKind.ConfirmPlanRequest] = C("confirm-plan.request", typeof(ConfirmPlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.ExecutePlanRequest] = C("execute-plan.request", typeof(ExecutePlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.CancelPlanRequest] = C("cancel-plan.request", typeof(CancelPlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.InspectPruneRequest] = C("inspect-prune.request", typeof(InspectPruneRequest), true, "sessionId", "catalogId", "retainNewest"),
        [ProtocolMessageKind.ConfirmPruneRequest] = C("confirm-prune.request", typeof(ConfirmPruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.ExecutePruneRequest] = C("execute-prune.request", typeof(ExecutePruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.CancelPruneRequest] = C("cancel-prune.request", typeof(CancelPruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.HandshakeEvent] = C("handshake.event", typeof(HandshakeEvent), false, "sessionId", "serverVersion", "capabilities"),
        [ProtocolMessageKind.GameDiscoveryEvent] = C("game-discovery.event", typeof(GameDiscoveryEvent), false, "sessionId", "candidates"),
        [ProtocolMessageKind.PackageOpenedEvent] = C("package-opened.event", typeof(PackageOpenedEvent), false, "sessionId", "packageId", "release"),
        [ProtocolMessageKind.RecoveryCatalogEvent] = C("recovery-catalog.event", typeof(RecoveryCatalogEvent), false, "sessionId", "catalogId", "gameRoot", "headSha256", "generations"),
        [ProtocolMessageKind.PlanEvent] = C("plan.event", typeof(PlanEvent), false, "sessionId", "planId", "planDigest", "executionBindingDigest", "operation", "packageId", "recoveryAuthority", "gameRoot", "currentRelease", "targetRelease", "observedState", "operations", "conflicts", "candidates", "summary", "warnings", "requiresConfirmation"),
        [ProtocolMessageKind.PrunePlanEvent] = C("prune-plan.event", typeof(PrunePlanEvent), false, "sessionId", "prunePlanId", "pruneDigest", "executionBindingDigest", "catalogId", "gameRoot", "headSha256", "retainNewest", "retainedSelectionIds", "removedSelectionIds", "summary", "warnings", "requiresConfirmation"),
        [ProtocolMessageKind.ProgressEvent] = C("progress.event", typeof(ProgressEvent), false, "sessionId", "planId", "planDigest", "sequence", "stage", "completedUnits", "totalUnits", "message"),
        [ProtocolMessageKind.PruneProgressEvent] = C("prune-progress.event", typeof(PruneProgressEvent), false, "sessionId", "prunePlanId", "pruneDigest", "sequence", "stage", "completedUnits", "totalUnits", "message"),
        [ProtocolMessageKind.SuccessEvent] = C("success.event", typeof(SuccessEvent), false, "sessionId", "planId", "planDigest", "operation", "summary", "filesChanged", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.RolledBackFailureEvent] = C("rolled-back-failure.event", typeof(RolledBackFailureEvent), false, "sessionId", "planId", "planDigest", "errorCode", "message", "rollbackSummary", "filesChanged", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.RecoverableInterruptionEvent] = C("recoverable-interruption.event", typeof(RecoverableInterruptionEvent), false, "sessionId", "planId", "planDigest", "errorCode", "message", "recoveryAction", "recoverySummary", "filesChanged", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.CancelledEvent] = C("cancelled.event", typeof(CancelledEvent), false, "sessionId", "planId", "planDigest", "summary", "safeStateSummary", "filesChanged", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneSuccessEvent] = C("prune-success.event", typeof(PruneSuccessEvent), false, "sessionId", "prunePlanId", "pruneDigest", "removedGenerationCount", "summary", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneFailureEvent] = C("prune-failure.event", typeof(PruneFailureEvent), false, "sessionId", "prunePlanId", "pruneDigest", "errorCode", "message", "removedGenerationCount", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneInterruptionEvent] = C("prune-interruption.event", typeof(PruneInterruptionEvent), false, "sessionId", "prunePlanId", "pruneDigest", "errorCode", "message", "recoveryAction", "removedGenerationCount", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneCancelledEvent] = C("prune-cancelled.event", typeof(PruneCancelledEvent), false, "sessionId", "prunePlanId", "pruneDigest", "summary", "safeStateSummary", "removedGenerationCount", "recoveryResult", "safeNextStep", "sanitizedLogPath"),
        [ProtocolMessageKind.PrePlanErrorEvent] = C("pre-plan-error.event", typeof(PrePlanErrorEvent), false, "sessionId", "errorCode", "message", "safeNextStep", "isTerminal", "sanitizedLogPath")
    };

    private static MessageContract C(string name, Type type, bool request, params string[] properties) => new(name, type, request, properties);

    private static void AssertNestedContracts(ProtocolMessageKind kind, JsonElement payload)
    {
        switch (kind)
        {
            case ProtocolMessageKind.GameDiscoveryEvent:
                AssertObjectArray(payload.GetProperty("candidates"), ["canonicalPath", "state", "displayName"], "game candidate", MaxGameCandidates); break;
            case ProtocolMessageKind.PackageOpenedEvent:
                AssertReleaseObject(payload.GetProperty("release"), "release identity"); break;
            case ProtocolMessageKind.RecoveryCatalogEvent:
                AssertGameRoot(payload.GetProperty("gameRoot"));
                AssertObjectArray(payload.GetProperty("generations"), ["selectionId", "generationId", "originOperation", "isCurrent", "isUserCheckpoint"], "recovery generation", MaxRecoveryGenerations); break;
            case ProtocolMessageKind.PlanEvent:
                AssertGameRoot(payload.GetProperty("gameRoot"));
                AssertOptionalRecoveryAuthority(payload.GetProperty("recoveryAuthority"));
                AssertOptionalReleaseObject(payload.GetProperty("currentRelease"), "current release identity");
                AssertOptionalReleaseObject(payload.GetProperty("targetRelease"), "target release identity");
                AssertObjectArray(payload.GetProperty("operations"), ["kind", "path", "expectedCurrentSha256", "resultSha256"], "plan operation", 2048);
                AssertObjectArray(payload.GetProperty("conflicts"), ["code", "path"], "plan conflict", 256);
                AssertObjectArray(payload.GetProperty("candidates"), ["candidateId", "kind", "path", "observedSha256", "observedSizeBytes", "observedUnixMode", "proposedResultSha256", "selected", "evidence"], "plan candidate", MaxPlanCandidates); break;
            case ProtocolMessageKind.PrunePlanEvent:
                AssertGameRoot(payload.GetProperty("gameRoot")); break;
        }
        AssertCanonicalEnums(kind, payload);
    }

    private static void ValidateMessage(ProtocolMessage message)
    {
        switch (message)
        {
            case HandshakeRequest v: Text(v.ClientName, "clientName"); Text(v.ClientVersion, "clientVersion"); break;
            case DiscoverGamesRequest v: Session(v.SessionId); break;
            case OpenPackageRequest v:
                Session(v.SessionId); Text(v.ReleaseTag, "releaseTag"); Hex(v.ExpectedSourceCommit, 40, "expectedSourceCommit");
                AbsolutePath(v.PackagePath, "packagePath"); AbsolutePath(v.ChecksumsPath, "checksumsPath"); AbsolutePath(v.BuildMetadataPath, "buildMetadataPath"); AbsolutePath(v.InstallManifestPath, "installManifestPath"); break;
            case ListRecoveriesRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); break;
            case InspectPlanRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); Defined(v.Operation, "operation"); ValidateInspectAuthorities(v); break;
            case SelectPlanCandidatesRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); IdArray(v.SelectedCandidateIds, "selectedCandidateIds", MaxPlanCandidates); break;
            case ConfirmPlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case ExecutePlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case CancelPlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case InspectPruneRequest v: Session(v.SessionId); ProtocolIdentifier.AssertCanonical(v.CatalogId.Value, "recovery catalog"); if (v.RetainNewest is < 1 or > MaxRecoveryGenerations) throw new ProtocolException("The protocol 'retainNewest' value must be between 1 and 64."); break;
            case ConfirmPruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case ExecutePruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case CancelPruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case HandshakeEvent v: Session(v.SessionId); Text(v.ServerVersion, "serverVersion"); Strings(v.Capabilities, "capabilities", 256); break;
            case GameDiscoveryEvent v: Session(v.SessionId); Objects(v.Candidates, "candidates", MaxGameCandidates); foreach (ProtocolGameCandidate c in v.Candidates) { AbsolutePath(c.CanonicalPath, "candidate.canonicalPath"); Defined(c.State, "candidate.state"); Text(c.DisplayName, "candidate.displayName"); } NoDuplicates(v.Candidates.Select(c => c.CanonicalPath), "game candidate path"); break;
            case PackageOpenedEvent v: Session(v.SessionId); ProtocolIdentifier.AssertCanonical(v.PackageId.Value, "package"); ValidateRelease(v.Release, "release"); break;
            case RecoveryCatalogEvent v: ValidateCatalog(v); break;
            case PlanEvent v: ValidatePlan(v); break;
            case PrunePlanEvent v: ValidatePrune(v); break;
            case ProgressEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Defined(v.Stage, "stage"); Text(v.Message, "message"); if (v.Sequence < 0 || v.CompletedUnits < 0 || v.TotalUnits < 0 || v.CompletedUnits > v.TotalUnits) throw new ProtocolException("The protocol progress counters are inconsistent."); break;
            case PruneProgressEvent v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); Defined(v.Stage, "stage"); Text(v.Message, "message"); if (v.Sequence < 0 || v.CompletedUnits < 0 || v.TotalUnits < 0 || v.CompletedUnits > v.TotalUnits) throw new ProtocolException("The protocol prune progress counters are inconsistent."); break;
            case SuccessEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Defined(v.Operation, "operation"); Terminal(v.Summary, v.FilesChanged, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case RolledBackFailureEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Text(v.ErrorCode, "errorCode"); Text(v.Message, "message"); Text(v.RollbackSummary, "rollbackSummary"); Terminal(v.RollbackSummary, v.FilesChanged, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case RecoverableInterruptionEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Text(v.ErrorCode, "errorCode"); Text(v.Message, "message"); Defined(v.RecoveryAction, "recoveryAction"); Text(v.RecoverySummary, "recoverySummary"); Terminal(v.RecoverySummary, v.FilesChanged, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case CancelledEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Text(v.Summary, "summary"); Text(v.SafeStateSummary, "safeStateSummary"); Terminal(v.Summary, v.FilesChanged, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case PruneSuccessEvent v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); if (v.RemovedGenerationCount < 0 || v.RemovedGenerationCount > MaxRecoveryGenerations) throw new ProtocolException("The removed-generation count is outside its bound."); Text(v.Summary, "summary"); Text(v.SafeNextStep, "safeNextStep"); OptionalLog(v.SanitizedLogPath); break;
            case PruneFailureEvent v: PruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.ErrorCode, v.Message, v.RemovedGenerationCount, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case PruneInterruptionEvent v: PruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.ErrorCode, v.Message, v.RemovedGenerationCount, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); Defined(v.RecoveryAction, "recoveryAction"); break;
            case PruneCancelledEvent v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); Text(v.Summary, "summary"); Text(v.SafeStateSummary, "safeStateSummary"); ValidatePruneTerminalDetails(v.RemovedGenerationCount, v.RecoveryResult, v.SafeNextStep, v.SanitizedLogPath); break;
            case PrePlanErrorEvent v: Session(v.SessionId); Text(v.ErrorCode, "errorCode"); Text(v.Message, "message"); Text(v.SafeNextStep, "safeNextStep"); OptionalLog(v.SanitizedLogPath); break;
            default: throw new ProtocolException("The message isn't part of the version 1 protocol.");
        }
    }

    private static void ValidateCatalog(RecoveryCatalogEvent v)
    {
        Session(v.SessionId); ProtocolIdentifier.AssertCanonical(v.CatalogId.Value, "recovery catalog"); GameRoot(v.GameRoot); Hex(v.HeadSha256, 64, "headSha256"); Objects(v.Generations, "generations", MaxRecoveryGenerations);
        if (v.Generations.Length == 0)
            throw new ProtocolException("A recovery catalog must contain at least one authenticated generation.");
        HashSet<string> generations = new(StringComparer.Ordinal); HashSet<ProtocolRecoverySelectionId> selections = [];
        for (int index = 0; index < v.Generations.Length; index++)
        {
            ProtocolRecoveryGeneration item = v.Generations[index];
            ProtocolIdentifier.AssertCanonical(item.SelectionId.Value, "recovery selection");
            if (!selections.Add(item.SelectionId)) throw new ProtocolException("The recovery catalog contains a duplicate selection ID.");
            if (item.GenerationId.Length != 32 || item.GenerationId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || !generations.Add(item.GenerationId)) throw new ProtocolException("The recovery catalog contains an invalid or duplicate generation ID.");
            Defined(item.OriginOperation, "originOperation");
            if (item.IsCurrent != (index == 0))
                throw new ProtocolException("The recovery catalog must mark exactly its first generation as current.");
            if (item.IsUserCheckpoint != (item.OriginOperation == InstallerOperation.Backup))
                throw new ProtocolException("The recovery generation checkpoint flag doesn't match its core-derived origin operation.");
        }
    }

    private static void ValidatePlan(PlanEvent v)
    {
        PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); ProtocolPlanDigest.AssertCanonical(v.ExecutionBindingDigest.Value); Defined(v.Operation, "operation");
        if (v.PackageId is { } package) ProtocolIdentifier.AssertCanonical(package.Value, "package");
        ValidateRecoveryAuthority(v.RecoveryAuthority);
        ValidatePlanAuthorities(v.Operation, v.PackageId, v.RecoveryAuthority?.SelectionId);
        GameRoot(v.GameRoot); ValidateRelease(v.CurrentRelease, "currentRelease", true); ValidateRelease(v.TargetRelease, "targetRelease", true); ValidateReleaseSemantics(v.Operation, v.CurrentRelease, v.TargetRelease);
        Defined(v.ObservedState, "observedState"); ValidatePlanCollections(v.Operations, v.Conflicts, v.Candidates); Text(v.Summary, "summary"); Strings(v.Warnings, "warnings", 256);
        if (!v.RequiresConfirmation) throw new ProtocolException("Every version 1 operation plan must require explicit confirmation.");
        ProtocolPlanDigest expected = ProtocolPlanDigest.Compute(v.ExecutionBindingDigest, v.Operation, v.PackageId, v.RecoveryAuthority, v.GameRoot, v.CurrentRelease, v.TargetRelease, v.ObservedState, v.Operations, v.Conflicts, v.Candidates, v.Summary, v.Warnings, true);
        if (v.PlanDigest != expected) throw new ProtocolException("The protocol plan digest doesn't match the canonical structured execution plan and display data.");
    }

    private static void ValidatePrune(PrunePlanEvent v)
    {
        PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); ProtocolPlanDigest.AssertCanonical(v.ExecutionBindingDigest.Value); ProtocolIdentifier.AssertCanonical(v.CatalogId.Value, "recovery catalog"); GameRoot(v.GameRoot); Hex(v.HeadSha256, 64, "headSha256");
        if (v.RetainNewest is < 1 or > MaxRecoveryGenerations) throw new ProtocolException("The protocol 'retainNewest' value must be between 1 and 64.");
        IdArray(v.RetainedSelectionIds, "retainedSelectionIds", MaxRecoveryGenerations); IdArray(v.RemovedSelectionIds, "removedSelectionIds", MaxRecoveryGenerations);
        if (v.RetainedSelectionIds.Intersect(v.RemovedSelectionIds).Any()) throw new ProtocolException("A recovery selection can't be both retained and removed.");
        int catalogCount = v.RetainedSelectionIds.Length + v.RemovedSelectionIds.Length;
        if (v.RemovedSelectionIds.Length == 0 || v.RetainedSelectionIds.Length != Math.Min(v.RetainNewest, catalogCount))
            throw new ProtocolException("The prune plan is a no-op or isn't the exact ordered catalog partition.");
        Text(v.Summary, "summary"); Strings(v.Warnings, "warnings", 256); if (!v.RequiresConfirmation) throw new ProtocolException("Every prune plan must require explicit confirmation.");
        ProtocolPlanDigest expected = ProtocolPlanDigest.ComputePrune(v.ExecutionBindingDigest, v.CatalogId, v.GameRoot, v.HeadSha256, v.RetainNewest, v.RetainedSelectionIds, v.RemovedSelectionIds, v.Summary, v.Warnings, true);
        if (v.PruneDigest != expected) throw new ProtocolException("The protocol prune digest doesn't match the exact catalog selection and display data.");
    }

    private static void ValidatePlanCollections(ProtocolPlanOperation[] operations, ProtocolPlanConflict[] conflicts, ProtocolPlanCandidate[] candidates)
    {
        Objects(operations, "operations", 2048); Objects(conflicts, "conflicts", 256); Objects(candidates, "candidates", MaxPlanCandidates);
        HashSet<string> operationKeys = new(StringComparer.Ordinal); string? previous = null;
        foreach (ProtocolPlanOperation item in operations)
        {
            Defined(item.Kind, "operation.kind"); RelativePath(item.Path, "operation.path"); OptionalHex(item.ExpectedCurrentSha256, 64, "operation.expectedCurrentSha256"); OptionalHex(item.ResultSha256, 64, "operation.resultSha256"); ValidateOperationHashes(item);
            string key = $"{item.Path}\0{(int)item.Kind:D3}\0{item.ResultSha256}"; if (!operationKeys.Add(key)) throw new ProtocolException("The plan operations contain a duplicate."); if (previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0) throw new ProtocolException("The plan operations aren't in canonical order."); previous = key;
        }
        HashSet<string> conflictKeys = new(StringComparer.Ordinal); previous = null;
        foreach (ProtocolPlanConflict item in conflicts)
        {
            Defined(item.Code, "conflict.code"); if (item.Path is not null) RelativePath(item.Path, "conflict.path"); string key = $"{item.Path}\0{(int)item.Code:D3}"; if (!conflictKeys.Add(key)) throw new ProtocolException("The plan conflicts contain a duplicate."); if (previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0) throw new ProtocolException("The plan conflicts aren't in canonical order."); previous = key;
        }
        HashSet<ProtocolCandidateId> ids = []; HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (ProtocolPlanCandidate item in candidates)
        {
            ProtocolIdentifier.AssertCanonical(item.CandidateId.Value, "candidate"); if (!ids.Add(item.CandidateId)) throw new ProtocolException("The plan candidates contain a duplicate ID."); Defined(item.Kind, "candidate.kind"); RelativePath(item.Path, "candidate.path"); if (!paths.Add(item.Path)) throw new ProtocolException("The plan candidates contain a duplicate path."); Hex(item.ObservedSha256, 64, "candidate.observedSha256"); if (item.ObservedSizeBytes < 0 || item.ObservedUnixMode < 0 || item.ObservedUnixMode > 4095) throw new ProtocolException("The candidate observed metadata is invalid."); Hex(item.ProposedResultSha256, 64, "candidate.proposedResultSha256"); Text(item.Evidence, "candidate.evidence");
        }
    }

    private static void ValidateInspectAuthorities(InspectPlanRequest v) => ValidatePlanAuthorities(v.Operation, v.PackageId, v.RecoverySelectionId);

    private static void ValidateRecoveryAuthority(ProtocolRecoveryAuthority? authority)
    {
        if (authority is null)
            return;
        ProtocolIdentifier.AssertCanonical(authority.CatalogId.Value, "recovery catalog");
        ProtocolIdentifier.AssertCanonical(authority.SelectionId.Value, "recovery selection");
        GameRoot(authority.GameRoot);
        Hex(authority.HeadSha256, 64, "recoveryAuthority.headSha256");
        ProtocolIdentifier.AssertCanonical(authority.Generation.SelectionId.Value, "recovery selection");
        if (authority.Generation.SelectionId != authority.SelectionId)
            throw new ProtocolException("The recovery authority generation doesn't match its selection ID.");
        if (authority.Generation.GenerationId.Length != 32 || !Guid.TryParseExact(authority.Generation.GenerationId, "N", out Guid generation) || generation == Guid.Empty || authority.Generation.GenerationId.Any(c => c is >= 'A' and <= 'F'))
            throw new ProtocolException("The recovery authority generation ID isn't canonical.");
        Defined(authority.Generation.OriginOperation, "recoveryAuthority.generation.originOperation");
    }
    private static void ValidatePlanAuthorities(InstallerOperation operation, ProtocolPackageId? packageId, ProtocolRecoverySelectionId? recoveryId)
    {
        bool packageRequired = operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair;
        bool recoveryRequired = operation == InstallerOperation.Rollback;
        if (packageRequired != packageId.HasValue || recoveryRequired != recoveryId.HasValue) throw new ProtocolException("The operation doesn't have the exact required opaque package or recovery authority.");
        if (packageId is { } p) ProtocolIdentifier.AssertCanonical(p.Value, "package"); if (recoveryId is { } r) ProtocolIdentifier.AssertCanonical(r.Value, "recovery selection");
    }

    private static void ValidateReleaseSemantics(InstallerOperation operation, ProtocolReleaseIdentity? current, ProtocolReleaseIdentity? target)
    {
        if (operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair) { if (target is null) throw new ProtocolException("This operation requires an exact target release."); }
        else if (operation is InstallerOperation.Backup or InstallerOperation.Uninstall && target is not null) throw new ProtocolException("This operation must not invent a target release.");
        if (operation == InstallerOperation.Install && current is not null) throw new ProtocolException("A fresh install must not invent a current release.");
    }

    private static void ValidateRelease(ProtocolReleaseIdentity? release, string field, bool optional = false)
    {
        if (release is null) { if (optional) return; throw new ProtocolException($"The protocol '{field}' value can't be null."); }
        Text(release.Repository, field); Text(release.Tag, field); Text(release.EmbeddedVersion, field); Text(release.PackageAssetName, field); Hex(release.SourceCommit, 40, field); Hex(release.SourceTree, 40, field); Hex(release.PackageSha256, 64, field); if (release.PackageSizeBytes <= 0) throw new ProtocolException("The release package size must be positive."); Text(release.BuildWorkflow, field); Text(release.BuildConfiguration, field); Text(release.RuntimeIdentifier, field);
        if (!Uri.TryCreate(release.Repository, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo + uri.Query + uri.Fragment)) throw new ProtocolException("The release repository must be a canonical HTTPS URL.");
        if (release.PackageAssetName is "." or ".." || release.PackageAssetName.IndexOfAny(['/', '\\']) >= 0 || release.PackageAssetName.Any(char.IsControl)) throw new ProtocolException("The release package asset name must be one filename.");
        try { _ = new InstallationReleaseIdentity(release.Repository, release.Tag, release.EmbeddedVersion, release.PackageAssetName, release.SourceCommit, release.SourceTree, Sha256Digest.Parse(release.PackageSha256), release.PackageSizeBytes, release.BuildWorkflow, release.BuildConfiguration, release.RuntimeIdentifier); }
        catch (ArgumentException ex) { throw new ProtocolException("The release isn't an exact reviewed fork release identity.", ex); }
    }

    private static void ValidateOperationHashes(ProtocolPlanOperation item)
    {
        bool e = item.ExpectedCurrentSha256 is not null, r = item.ResultSha256 is not null, same = e && r && item.ExpectedCurrentSha256 == item.ResultSha256;
        bool valid = item.Kind switch { PlanOperationKind.Backup => same, PlanOperationKind.Remove => e && !r, PlanOperationKind.Restore => r, PlanOperationKind.Create => !e && r, PlanOperationKind.Replace => e && r, PlanOperationKind.Retain or PlanOperationKind.Preserve => same, _ => false };
        if (!valid) throw new ProtocolException("The plan operation hashes are inconsistent with its kind.");
    }

    private static void Terminal(string summary, int changed, ProtocolRecoveryResult recovery, string next, string? log) { Text(summary, "summary"); if (changed < 0) throw new ProtocolException("The files-changed count can't be negative."); Defined(recovery, "recoveryResult"); Text(next, "safeNextStep"); OptionalLog(log); }
    private static void PruneTerminal(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest, string code, string message, int removed, ProtocolRecoveryResult recovery, string next, string? log) { PruneBinding(session, plan, digest); Text(code, "errorCode"); Text(message, "message"); ValidatePruneTerminalDetails(removed, recovery, next, log); }
    private static void ValidatePruneTerminalDetails(int removed, ProtocolRecoveryResult recovery, string next, string? log) { if (removed < 0 || removed > MaxRecoveryGenerations) throw new ProtocolException("The removed-generation count is outside its bound."); Defined(recovery, "recoveryResult"); Text(next, "safeNextStep"); OptionalLog(log); }
    private static void OptionalLog(string? value) { if (value is not null) AbsolutePath(value, "sanitizedLogPath"); }
    private static void Session(ProtocolSessionId id) => ProtocolIdentifier.AssertCanonical(id.Value, "session");
    private static void PlanBinding(ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest) { Session(session); ProtocolIdentifier.AssertCanonical(plan.Value, "plan"); ArgumentNullException.ThrowIfNull(digest); ProtocolPlanDigest.AssertCanonical(digest.Value); }
    private static void PruneBinding(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest) { Session(session); ProtocolIdentifier.AssertCanonical(plan.Value, "prune plan"); ArgumentNullException.ThrowIfNull(digest); ProtocolPlanDigest.AssertCanonical(digest.Value); }
    private static void GameRoot(ProtocolGameRootIdentity root) { ArgumentNullException.ThrowIfNull(root); AbsolutePath(root.CanonicalPath, "gameRoot.canonicalPath"); if (root.Inode == 0) throw new ProtocolException("The game-root inode must be nonzero."); }
    private static void Text(string? value, string field) { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new ProtocolException($"The protocol '{field}' text is empty, too long, or contains control characters."); }
    private static void Strings(string[] values, string field, int max) { Objects(values, field, max); foreach (string value in values) Text(value, field); if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ProtocolException($"The protocol '{field}' collection contains duplicates."); }
    private static void Objects<T>(T[]? values, string field, int max) { if (values is null || values.Length > max || values.Any(v => v is null)) throw new ProtocolException($"The protocol '{field}' collection is missing, contains null, or is too large."); }
    private static void IdArray<T>(T[] values, string field, int max) where T : struct { Objects(values, field, max); if (values.Distinct().Count() != values.Length) throw new ProtocolException($"The protocol '{field}' collection contains duplicate IDs."); foreach (T value in values) { string? text = value.GetType().GetProperty("Value")?.GetValue(value) as string; ProtocolIdentifier.AssertCanonical(text, field); } }
    private static void NoDuplicates(IEnumerable<string> values, string field) { if (values.Distinct(StringComparer.Ordinal).Count() != values.Count()) throw new ProtocolException($"The protocol {field} collection contains duplicates."); }
    private static void Defined<T>(T value, string field) where T : struct, Enum { if (!Enum.IsDefined(value)) throw new ProtocolException($"The protocol '{field}' value isn't defined by version 1."); }
    private static void Hex(string? value, int length, string field) { if (value is null || value.Length != length || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ProtocolException($"The protocol '{field}' value isn't canonical lowercase hexadecimal."); }
    private static void OptionalHex(string? value, int length, string field) { if (value is not null) Hex(value, length, field); }
    private static void AbsolutePath(string? value, string field) { Text(value, field); if (value![0] != '/' || value.IndexOf('\\') >= 0 || (value.Length > 1 && value.EndsWith('/')) || value.Split('/').Skip(1).Any(s => s.Length == 0 || s is "." or "..")) throw new ProtocolException($"The protocol '{field}' value isn't a canonical absolute Linux path."); }
    private static void RelativePath(string? value, string field) { Text(value, field); if (value![0] == '/' || value.IndexOf('\\') >= 0 || value.Split('/').Any(s => s.Length == 0 || s is "." or "..")) throw new ProtocolException($"The protocol '{field}' value isn't a canonical relative path."); }

    private static void AssertExactObject(JsonElement element, IReadOnlyCollection<string> expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ProtocolException($"The protocol {description} must be a JSON object.");
        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) { if (!actual.Add(property.Name)) throw new ProtocolException($"The protocol {description} contains a duplicate '{property.Name}' property."); if (!expected.Contains(property.Name, StringComparer.Ordinal)) throw new ProtocolException($"The protocol {description} contains an unknown '{property.Name}' property."); }
        string? missing = expected.FirstOrDefault(p => !actual.Contains(p)); if (missing is not null) throw new ProtocolException($"The protocol {description} is missing the required '{missing}' property.");
    }
    private static void AssertObjectArray(JsonElement element, IReadOnlyCollection<string> properties, string description, int max) { if (element.ValueKind != JsonValueKind.Array) throw new ProtocolException($"The protocol {description} collection must be an array."); int index = 0; foreach (JsonElement item in element.EnumerateArray()) { if (index >= max) throw new ProtocolException($"The protocol {description} collection is too large."); AssertExactObject(item, properties, $"{description} at index {index++}"); } }
    private static void AssertGameRoot(JsonElement e) => AssertExactObject(e, ["canonicalPath", "deviceMajor", "deviceMinor", "inode", "operationGeneration"], "game-root identity");
    private static void AssertOptionalRecoveryAuthority(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return;
        AssertExactObject(element, ["catalogId", "selectionId", "gameRoot", "headSha256", "generation"], "recovery authority");
        AssertGameRoot(element.GetProperty("gameRoot"));
        AssertExactObject(element.GetProperty("generation"), ["selectionId", "generationId", "originOperation", "isCurrent", "isUserCheckpoint"], "recovery authority generation");
    }

    private static void AssertCanonicalEnums(ProtocolMessageKind kind, JsonElement payload)
    {
        switch (kind)
        {
            case ProtocolMessageKind.InspectPlanRequest: AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation"); break;
            case ProtocolMessageKind.GameDiscoveryEvent:
                foreach (JsonElement item in payload.GetProperty("candidates").EnumerateArray()) AssertCanonicalEnum<ProtocolGameCandidateState>(item.GetProperty("state"), "candidate.state");
                break;
            case ProtocolMessageKind.RecoveryCatalogEvent:
                foreach (JsonElement item in payload.GetProperty("generations").EnumerateArray()) AssertCanonicalEnum<InstallerOperation>(item.GetProperty("originOperation"), "generation.originOperation");
                break;
            case ProtocolMessageKind.PlanEvent:
                AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation");
                AssertCanonicalEnum<ObservedInstallState>(payload.GetProperty("observedState"), "observedState");
                foreach (JsonElement item in payload.GetProperty("operations").EnumerateArray()) AssertCanonicalEnum<PlanOperationKind>(item.GetProperty("kind"), "operation.kind");
                foreach (JsonElement item in payload.GetProperty("conflicts").EnumerateArray()) AssertCanonicalEnum<PlanConflictCode>(item.GetProperty("code"), "conflict.code");
                foreach (JsonElement item in payload.GetProperty("candidates").EnumerateArray()) AssertCanonicalEnum<ProtocolCandidateKind>(item.GetProperty("kind"), "candidate.kind");
                if (payload.GetProperty("recoveryAuthority").ValueKind != JsonValueKind.Null) AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("recoveryAuthority").GetProperty("generation").GetProperty("originOperation"), "recoveryAuthority.generation.originOperation");
                break;
            case ProtocolMessageKind.ProgressEvent: AssertCanonicalEnum<InstallerProgressStage>(payload.GetProperty("stage"), "stage"); break;
            case ProtocolMessageKind.PruneProgressEvent: AssertCanonicalEnum<ProtocolPruneProgressStage>(payload.GetProperty("stage"), "stage"); break;
            case ProtocolMessageKind.SuccessEvent: AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation"); AssertCanonicalEnum<ProtocolRecoveryResult>(payload.GetProperty("recoveryResult"), "recoveryResult"); break;
            case ProtocolMessageKind.RolledBackFailureEvent or ProtocolMessageKind.CancelledEvent or ProtocolMessageKind.PruneFailureEvent or ProtocolMessageKind.PruneCancelledEvent:
                AssertCanonicalEnum<ProtocolRecoveryResult>(payload.GetProperty("recoveryResult"), "recoveryResult"); break;
            case ProtocolMessageKind.RecoverableInterruptionEvent:
                AssertCanonicalEnum<InstallerRecoveryAction>(payload.GetProperty("recoveryAction"), "recoveryAction"); AssertCanonicalEnum<ProtocolRecoveryResult>(payload.GetProperty("recoveryResult"), "recoveryResult"); break;
            case ProtocolMessageKind.PruneInterruptionEvent:
                AssertCanonicalEnum<InstallerRecoveryAction>(payload.GetProperty("recoveryAction"), "recoveryAction"); AssertCanonicalEnum<ProtocolRecoveryResult>(payload.GetProperty("recoveryResult"), "recoveryResult"); break;
        }
    }

    private static void AssertCanonicalEnum<T>(JsonElement element, string field) where T : struct, Enum
    {
        string? token = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (token is null || !Enum.GetValues<T>().Any(value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()) == token))
            throw new ProtocolException($"The protocol '{field}' enum token isn't canonical camel case.");
    }
    private static void AssertOptionalReleaseObject(JsonElement e, string d) { if (e.ValueKind != JsonValueKind.Null) AssertReleaseObject(e, d); }
    private static void AssertReleaseObject(JsonElement e, string d) => AssertExactObject(e, ["repository", "tag", "embeddedVersion", "packageAssetName", "sourceCommit", "sourceTree", "packageSha256", "packageSizeBytes", "buildWorkflow", "buildConfiguration", "runtimeIdentifier"], d);
    private sealed record MessageContract(string WireName, Type Type, bool IsRequest, string[] Properties);
}
