using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Transactions;

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
        [ProtocolMessageKind.ValidateGameRequest] = C("validate-game.request", typeof(ValidateGameRequest), true, "sessionId", "gamePath"),
        [ProtocolMessageKind.RecoverInterruptedRequest] = C("recover-interrupted.request", typeof(RecoverInterruptedRequest), true, "sessionId", "gamePath"),
        [ProtocolMessageKind.OpenPackageRequest] = C("open-package.request", typeof(OpenPackageRequest), true, "sessionId", "releaseTag", "expectedSourceCommit", "packagePath", "checksumsPath", "buildMetadataPath", "installManifestPath", "attestationBundlePath", "attestationBundleChecksumPath", "procWorkspaceIdentity"),
        [ProtocolMessageKind.ListRecoveriesRequest] = C("list-recoveries.request", typeof(ListRecoveriesRequest), true, "sessionId", "gamePath"),
        [ProtocolMessageKind.InspectPlanRequest] = C("inspect-plan.request", typeof(InspectPlanRequest), true, "sessionId", "gamePath", "operation", "packageId", "recoverySelectionId"),
        [ProtocolMessageKind.SelectPlanCandidatesRequest] = C("select-plan-candidates.request", typeof(SelectPlanCandidatesRequest), true, "sessionId", "planId", "planDigest", "selectedCandidateIds"),
        [ProtocolMessageKind.GetPlanPageRequest] = C("get-plan-page.request", typeof(GetPlanPageRequest), true, "sessionId", "planId", "planDigest", "pageKind", "offset"),
        [ProtocolMessageKind.ConfirmPlanRequest] = C("confirm-plan.request", typeof(ConfirmPlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.ExecutePlanRequest] = C("execute-plan.request", typeof(ExecutePlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.CancelPlanRequest] = C("cancel-plan.request", typeof(CancelPlanRequest), true, "sessionId", "planId", "planDigest"),
        [ProtocolMessageKind.InspectPruneRequest] = C("inspect-prune.request", typeof(InspectPruneRequest), true, "sessionId", "catalogId", "retainNewest"),
        [ProtocolMessageKind.ConfirmPruneRequest] = C("confirm-prune.request", typeof(ConfirmPruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.ExecutePruneRequest] = C("execute-prune.request", typeof(ExecutePruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.CancelPruneRequest] = C("cancel-prune.request", typeof(CancelPruneRequest), true, "sessionId", "prunePlanId", "pruneDigest"),
        [ProtocolMessageKind.CommandAcknowledgedEvent] = C("command-acknowledged.event", typeof(CommandAcknowledgedEvent), false, "sessionId", "acknowledgement", "planId", "prunePlanId"),
        [ProtocolMessageKind.HandshakeEvent] = C("handshake.event", typeof(HandshakeEvent), false, "sessionId", "serverVersion", "capabilities"),
        [ProtocolMessageKind.GameDiscoveryEvent] = C("game-discovery.event", typeof(GameDiscoveryEvent), false, "sessionId", "candidates"),
        [ProtocolMessageKind.GameValidationEvent] = C("game-validation.event", typeof(GameValidationEvent), false, "sessionId", "candidate"),
        [ProtocolMessageKind.RecoveryProgressEvent] = C("recovery-progress.event", typeof(RecoveryProgressEvent), false, "sessionId", "sequence", "stage", "completedUnits", "totalUnits", "message"),
        [ProtocolMessageKind.RecoveryCompletedEvent] = C("recovery-completed.event", typeof(RecoveryCompletedEvent), false, "sessionId", "outcome", "terminalState", "attempt", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.RecoveryFailureEvent] = C("recovery-failure.event", typeof(RecoveryFailureEvent), false, "sessionId", "outcome", "terminalState", "message", "sanitizedLogPath", "attempt"),
        [ProtocolMessageKind.PackageOpenedEvent] = C("package-opened.event", typeof(PackageOpenedEvent), false, "sessionId", "packageId", "release"),
        [ProtocolMessageKind.RecoveryCatalogEvent] = C("recovery-catalog.event", typeof(RecoveryCatalogEvent), false, "sessionId", "catalogId", "gameRoot", "headSha256", "generations"),
        [ProtocolMessageKind.NoRecoveryHistoryEvent] = C("no-recovery-history.event", typeof(NoRecoveryHistoryEvent), false, "sessionId"),
        [ProtocolMessageKind.PlanEvent] = C("plan.event", typeof(PlanEvent), false, "sessionId", "planId", "planDigest", "executionBindingDigest", "operation", "packageId", "recoveryAuthority", "gameRoot", "currentRelease", "targetRelease", "observedState", "recoveryUsedGenerationCount", "recoveryMaximumGenerationCount", "operationCount", "conflictCount", "candidateCount", "warningCount", "canExecute", "risks", "recommendedDefault", "summary", "requiresConfirmation"),
        [ProtocolMessageKind.PlanPageEvent] = C("plan-page.event", typeof(PlanPageEvent), false, "sessionId", "planId", "planDigest", "pageKind", "offset", "totalCount", "nextOffset", "operations", "conflicts", "candidates", "warnings"),
        [ProtocolMessageKind.PrunePlanEvent] = C("prune-plan.event", typeof(PrunePlanEvent), false, "sessionId", "prunePlanId", "pruneDigest", "executionBindingDigest", "catalogId", "gameRoot", "headSha256", "retainNewest", "retainedSelectionIds", "removedSelectionIds", "cleanupGenerationIds", "auxiliaryCleanupPlanned", "summary", "warnings", "risks", "recommendedDefault", "requiresConfirmation"),
        [ProtocolMessageKind.ProgressEvent] = C("progress.event", typeof(ProgressEvent), false, "sessionId", "planId", "planDigest", "sequence", "stage", "completedUnits", "totalUnits", "message"),
        [ProtocolMessageKind.PruneProgressEvent] = C("prune-progress.event", typeof(PruneProgressEvent), false, "sessionId", "prunePlanId", "pruneDigest", "sequence", "stage", "completedUnits", "totalUnits", "message"),
        [ProtocolMessageKind.SuccessEvent] = C("success.event", typeof(SuccessEvent), false, "sessionId", "planId", "planDigest", "operation", "outcome", "terminalState", "executionSummary", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.RolledBackFailureEvent] = C("rolled-back-failure.event", typeof(RolledBackFailureEvent), false, "sessionId", "planId", "planDigest", "outcome", "terminalState", "executionSummary", "message", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.RecoverableInterruptionEvent] = C("recoverable-interruption.event", typeof(RecoverableInterruptionEvent), false, "sessionId", "planId", "planDigest", "outcome", "terminalState", "executionSummary", "message", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.CancelledEvent] = C("cancelled.event", typeof(CancelledEvent), false, "sessionId", "planId", "planDigest", "outcome", "terminalState", "executionSummary", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneSuccessEvent] = C("prune-success.event", typeof(PruneSuccessEvent), false, "sessionId", "prunePlanId", "pruneDigest", "outcome", "terminalState", "pruneSummary", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneFailureEvent] = C("prune-failure.event", typeof(PruneFailureEvent), false, "sessionId", "prunePlanId", "pruneDigest", "outcome", "terminalState", "pruneSummary", "message", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneInterruptionEvent] = C("prune-interruption.event", typeof(PruneInterruptionEvent), false, "sessionId", "prunePlanId", "pruneDigest", "outcome", "terminalState", "pruneSummary", "message", "sanitizedLogPath"),
        [ProtocolMessageKind.PruneCancelledEvent] = C("prune-cancelled.event", typeof(PruneCancelledEvent), false, "sessionId", "prunePlanId", "pruneDigest", "outcome", "terminalState", "pruneSummary", "summary", "sanitizedLogPath"),
        [ProtocolMessageKind.PrePlanRejectedEvent] = C("pre-plan-rejected.event", typeof(PrePlanRejectedEvent), false, "sessionId", "errorCode", "message", "nextAction", "isTerminal", "sanitizedLogPath")
    };

    private static MessageContract C(string name, Type type, bool request, params string[] properties) => new(name, type, request, ["commandId", .. properties]);

    private static void AssertNestedContracts(ProtocolMessageKind kind, JsonElement payload)
    {
        switch (kind)
        {
            case ProtocolMessageKind.OpenPackageRequest:
                AssertOptionalObject(payload.GetProperty("procWorkspaceIdentity"), ["deviceMajor", "deviceMinor", "inode", "changeSeconds", "changeNanoseconds"], "proc workspace identity"); break;
            case ProtocolMessageKind.GameDiscoveryEvent:
                AssertObjectArray(payload.GetProperty("candidates"), ["canonicalPath", "state", "displayName"], "game candidate", MaxGameCandidates); break;
            case ProtocolMessageKind.GameValidationEvent:
                AssertExactObject(payload.GetProperty("candidate"), ["canonicalPath", "state", "displayName"], "game candidate"); break;
            case ProtocolMessageKind.PackageOpenedEvent:
                AssertReleaseObject(payload.GetProperty("release"), "release identity"); break;
            case ProtocolMessageKind.RecoveryCatalogEvent:
                AssertGameRoot(payload.GetProperty("gameRoot"));
                AssertObjectArray(payload.GetProperty("generations"), ["selectionId", "generationId", "originOperation", "isCurrent", "isUserCheckpoint", "restoreRelease", "restoresUninstalledState"], "recovery generation", MaxRecoveryGenerations);
                foreach (JsonElement generation in payload.GetProperty("generations").EnumerateArray()) AssertOptionalReleaseObject(generation.GetProperty("restoreRelease"), "recovery restore release");
                break;
            case ProtocolMessageKind.RecoveryCompletedEvent:
                AssertTerminalState(payload.GetProperty("terminalState"));
                AssertRecoveryAttempt(payload.GetProperty("attempt")); break;
            case ProtocolMessageKind.RecoveryFailureEvent:
                AssertTerminalState(payload.GetProperty("terminalState"));
                if (payload.GetProperty("attempt").ValueKind != JsonValueKind.Null) AssertRecoveryAttempt(payload.GetProperty("attempt"));
                break;
            case ProtocolMessageKind.PlanEvent:
                AssertGameRoot(payload.GetProperty("gameRoot"));
                AssertOptionalRecoveryAuthority(payload.GetProperty("recoveryAuthority"));
                AssertOptionalReleaseObject(payload.GetProperty("currentRelease"), "current release identity");
                AssertOptionalReleaseObject(payload.GetProperty("targetRelease"), "target release identity"); break;
            case ProtocolMessageKind.PlanPageEvent:
                AssertObjectArray(payload.GetProperty("operations"), ["kind", "path", "expectedCurrentSha256", "resultSha256"], "plan operation", 2048);
                AssertObjectArray(payload.GetProperty("conflicts"), ["code", "path"], "plan conflict", 256);
                AssertObjectArray(payload.GetProperty("candidates"), ["candidateId", "reason", "disposition", "path", "observedSha256", "observedSizeBytes", "observedUnixMode", "proposedResultSha256", "selected", "evidence"], "plan candidate", MaxPlanCandidates); break;
            case ProtocolMessageKind.PrunePlanEvent:
                AssertGameRoot(payload.GetProperty("gameRoot")); break;
            case ProtocolMessageKind.SuccessEvent or ProtocolMessageKind.RolledBackFailureEvent or ProtocolMessageKind.RecoverableInterruptionEvent or ProtocolMessageKind.CancelledEvent:
                AssertTerminalState(payload.GetProperty("terminalState"));
                AssertExactObject(payload.GetProperty("executionSummary"), ["managedFileChangeCount", "rolledBackManagedFileCount", "internalStateChangeCount", "rolledBackInternalStateCount", "recoveredTransactionCount", "recoveredPathCount"], "execution summary"); break;
            case ProtocolMessageKind.PruneSuccessEvent or ProtocolMessageKind.PruneFailureEvent or ProtocolMessageKind.PruneInterruptionEvent or ProtocolMessageKind.PruneCancelledEvent:
                AssertTerminalState(payload.GetProperty("terminalState"));
                AssertExactObject(payload.GetProperty("pruneSummary"), ["logicallyRemovedGenerationCount", "physicallyCleanedGenerationCount", "pendingCleanupGenerationCount", "auxiliaryCleanupPending"], "prune summary"); break;
        }
        AssertCanonicalEnums(kind, payload);
    }

    private static void ValidateMessage(ProtocolMessage message)
    {
        ProtocolIdentifier.AssertCanonical(message.CommandId.Value, "command");
        switch (message)
        {
            case HandshakeRequest v: Text(v.ClientName, "clientName"); Text(v.ClientVersion, "clientVersion"); break;
            case DiscoverGamesRequest v: Session(v.SessionId); break;
            case ValidateGameRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); break;
            case RecoverInterruptedRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); break;
            case OpenPackageRequest v:
                Session(v.SessionId); Text(v.ReleaseTag, "releaseTag"); Hex(v.ExpectedSourceCommit, 40, "expectedSourceCommit");
                AbsolutePath(v.PackagePath, "packagePath"); AbsolutePath(v.ChecksumsPath, "checksumsPath"); AbsolutePath(v.BuildMetadataPath, "buildMetadataPath"); AbsolutePath(v.InstallManifestPath, "installManifestPath"); AbsolutePath(v.AttestationBundlePath, "attestationBundlePath"); AbsolutePath(v.AttestationBundleChecksumPath, "attestationBundleChecksumPath");
                if (v.ProcWorkspaceIdentity is { } proc && (proc.Inode == 0 || proc.ChangeSeconds < 0 || proc.ChangeNanoseconds >= 1_000_000_000)) throw new ProtocolException("The proc workspace identity is outside its canonical bounds.");
                break;
            case ListRecoveriesRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); break;
            case InspectPlanRequest v: Session(v.SessionId); AbsolutePath(v.GamePath, "gamePath"); Defined(v.Operation, "operation"); ValidateInspectAuthorities(v); break;
            case SelectPlanCandidatesRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); IdArray(v.SelectedCandidateIds, "selectedCandidateIds", MaxPlanCandidates); break;
            case GetPlanPageRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Defined(v.PageKind, "pageKind"); if (v.Offset < 0 || v.Offset >= TransactionPlan.MaximumOperationCount) throw new ProtocolException("The plan page offset is outside its bound."); break;
            case ConfirmPlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case ExecutePlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case CancelPlanRequest v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); break;
            case InspectPruneRequest v: Session(v.SessionId); ProtocolIdentifier.AssertCanonical(v.CatalogId.Value, "recovery catalog"); if (v.RetainNewest is < 1 or > MaxRecoveryGenerations) throw new ProtocolException("The protocol 'retainNewest' value must be between 1 and 64."); break;
            case ConfirmPruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case ExecutePruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case CancelPruneRequest v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); break;
            case CommandAcknowledgedEvent v: ValidateAcknowledgement(v); break;
            case HandshakeEvent v: Session(v.SessionId); Text(v.ServerVersion, "serverVersion"); Strings(v.Capabilities, "capabilities", 256); break;
            case GameDiscoveryEvent v: Session(v.SessionId); Objects(v.Candidates, "candidates", MaxGameCandidates); foreach (ProtocolGameCandidate c in v.Candidates) ValidateGameCandidate(c); NoDuplicates(v.Candidates.Select(c => c.CanonicalPath), "game candidate path"); break;
            case GameValidationEvent v: Session(v.SessionId); ValidateGameCandidate(v.Candidate); break;
            case RecoveryProgressEvent v: Session(v.SessionId); Progress(v.Sequence, v.Stage, v.CompletedUnits, v.TotalUnits, v.Message); break;
            case RecoveryCompletedEvent v: ValidateRecoveryCompleted(v); break;
            case RecoveryFailureEvent v: ValidateRecoveryFailure(v); break;
            case PackageOpenedEvent v: Session(v.SessionId); ProtocolIdentifier.AssertCanonical(v.PackageId.Value, "package"); ValidateRelease(v.Release, "release"); break;
            case RecoveryCatalogEvent v: ValidateCatalog(v); break;
            case NoRecoveryHistoryEvent v: Session(v.SessionId); break;
            case PlanEvent v: ValidatePlan(v); break;
            case PlanPageEvent v: ValidatePlanPage(v); break;
            case PrunePlanEvent v: ValidatePrune(v); break;
            case ProgressEvent v: PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Defined(v.Stage, "stage"); Text(v.Message, "message"); if (v.Sequence < 0 || v.CompletedUnits < 0 || v.TotalUnits < 0 || v.CompletedUnits > v.TotalUnits) throw new ProtocolException("The protocol progress counters are inconsistent."); break;
            case PruneProgressEvent v: PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); Defined(v.Stage, "stage"); Text(v.Message, "message"); if (v.Sequence < 0 || v.CompletedUnits < 0 || v.TotalUnits < 0 || v.CompletedUnits > v.TotalUnits) throw new ProtocolException("The protocol prune progress counters are inconsistent."); break;
            case SuccessEvent v: ValidateExecutionTerminal(v.SessionId, v.PlanId, v.PlanDigest, v.Outcome, v.TerminalState, v.ExecutionSummary, v.Summary, null, v.SanitizedLogPath, ProtocolExecutionOutcome.Succeeded, ProtocolExecutionOutcome.SucceededWithCleanupWarning); Defined(v.Operation, "operation"); break;
            case RolledBackFailureEvent v: ValidateExecutionTerminal(v.SessionId, v.PlanId, v.PlanDigest, v.Outcome, v.TerminalState, v.ExecutionSummary, v.Summary, v.Message, v.SanitizedLogPath, ProtocolExecutionOutcome.FailedBeforeMutation, ProtocolExecutionOutcome.FailedAndRolledBack, ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired); break;
            case RecoverableInterruptionEvent v: ValidateExecutionTerminal(v.SessionId, v.PlanId, v.PlanDigest, v.Outcome, v.TerminalState, v.ExecutionSummary, v.Summary, v.Message, v.SanitizedLogPath, ProtocolExecutionOutcome.InterruptedRecoveryRequired, ProtocolExecutionOutcome.UnexpectedCoreFailure); break;
            case CancelledEvent v: ValidateExecutionTerminal(v.SessionId, v.PlanId, v.PlanDigest, v.Outcome, v.TerminalState, v.ExecutionSummary, v.Summary, null, v.SanitizedLogPath, ProtocolExecutionOutcome.CancelledBeforeMutation, ProtocolExecutionOutcome.CancelledAndRolledBack); break;
            case PruneSuccessEvent v: ValidatePruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.Outcome, v.TerminalState, v.PruneSummary, v.Summary, null, v.SanitizedLogPath, ProtocolPruneOutcome.Succeeded); break;
            case PruneFailureEvent v: ValidatePruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.Outcome, v.TerminalState, v.PruneSummary, null, v.Message, v.SanitizedLogPath, ProtocolPruneOutcome.FailedBeforePublication, ProtocolPruneOutcome.FailedWithCleanupPending, ProtocolPruneOutcome.FailedAfterApply); break;
            case PruneInterruptionEvent v: ValidatePruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.Outcome, v.TerminalState, v.PruneSummary, null, v.Message, v.SanitizedLogPath, ProtocolPruneOutcome.Interrupted, ProtocolPruneOutcome.UnexpectedCoreFailure); break;
            case PruneCancelledEvent v: ValidatePruneTerminal(v.SessionId, v.PrunePlanId, v.PruneDigest, v.Outcome, v.TerminalState, v.PruneSummary, v.Summary, null, v.SanitizedLogPath, ProtocolPruneOutcome.CancelledBeforePublication, ProtocolPruneOutcome.CancelledWithCleanupPending, ProtocolPruneOutcome.CancelledAfterApply); break;
            case PrePlanRejectedEvent v: ValidatePrePlanRejection(v); break;
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
            ValidateRelease(item.RestoreRelease, "recovery restore release", optional: true);
            if ((item.RestoreRelease is null) != item.RestoresUninstalledState)
                throw new ProtocolException("A recovery generation must identify either one exact restore release or an uninstalled result.");
        }
    }

    private static void ValidateGameCandidate(ProtocolGameCandidate candidate)
    {
        if (candidate is null) throw new ProtocolException("The protocol game candidate can't be null.");
        AbsolutePath(candidate.CanonicalPath, "candidate.canonicalPath"); Defined(candidate.State, "candidate.state"); Text(candidate.DisplayName, "candidate.displayName");
    }

    private static void ValidatePlan(PlanEvent v)
    {
        PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); ProtocolPlanDigest.AssertCanonical(v.ExecutionBindingDigest.Value); Defined(v.Operation, "operation");
        if (v.PackageId is { } package) ProtocolIdentifier.AssertCanonical(package.Value, "package");
        ValidateRecoveryAuthority(v.RecoveryAuthority);
        if (v.RecoveryAuthority is not null && v.RecoveryAuthority.GameRoot != v.GameRoot)
            throw new ProtocolException("The recovery authority game root doesn't match the outer plan game root.");
        ValidatePlanAuthorities(v.Operation, v.PackageId, v.RecoveryAuthority?.SelectionId);
        GameRoot(v.GameRoot); ValidateRelease(v.CurrentRelease, "currentRelease", true); ValidateRelease(v.TargetRelease, "targetRelease", true); ValidateReleaseSemantics(v.Operation, v.CurrentRelease, v.TargetRelease);
        Defined(v.ObservedState, "observedState");
        if (v.RecoveryUsedGenerationCount < 0 || v.RecoveryMaximumGenerationCount != MaxRecoveryGenerations || v.RecoveryUsedGenerationCount > v.RecoveryMaximumGenerationCount)
            throw new ProtocolException("The plan recovery-capacity facts are outside their bounds.");
        if (v.OperationCount is < 0 or > TransactionPlan.MaximumOperationCount || v.ConflictCount is < 0 or > 256 || v.CandidateCount is < 0 or > MaxPlanCandidates || v.WarningCount is < 0 or > 256)
            throw new ProtocolException("The plan summary counts are outside their bounds.");
        if (v.CanExecute != (v.ConflictCount == 0)) throw new ProtocolException("The plan executability doesn't match its conflict count.");
        Objects(v.Risks, "risks", 8); foreach (ProtocolPlanRisk risk in v.Risks) Defined(risk, "risk"); if (v.Risks.Distinct().Count() != v.Risks.Length) throw new ProtocolException("The plan risks contain duplicates.");
        Defined(v.RecommendedDefault, "recommendedDefault"); Text(v.Summary, "summary");
        if (!v.RequiresConfirmation) throw new ProtocolException("Every version 1 operation plan must require explicit confirmation.");
    }

    private static void ValidatePlanPage(PlanPageEvent v)
    {
        PlanBinding(v.SessionId, v.PlanId, v.PlanDigest); Defined(v.PageKind, "pageKind");
        if (v.Offset < 0 || v.TotalCount <= 0 || v.TotalCount > TransactionPlan.MaximumOperationCount || v.Offset >= v.TotalCount)
            throw new ProtocolException("The plan page bounds are inconsistent.");
        int populated = v.Operations.Length + v.Conflicts.Length + v.Candidates.Length + v.Warnings.Length;
        if (populated <= 0 || v.Offset + populated > v.TotalCount || v.NextOffset != (v.Offset + populated < v.TotalCount ? v.Offset + populated : null))
            throw new ProtocolException("The plan page continuation is inconsistent.");
        if ((v.PageKind == ProtocolPlanPageKind.Operations) != (v.Operations.Length > 0) || (v.PageKind == ProtocolPlanPageKind.Conflicts) != (v.Conflicts.Length > 0) || (v.PageKind == ProtocolPlanPageKind.Candidates) != (v.Candidates.Length > 0) || (v.PageKind == ProtocolPlanPageKind.Warnings) != (v.Warnings.Length > 0))
            throw new ProtocolException("A plan page must populate only its selected collection.");
        ValidatePlanCollections(v.Operations, v.Conflicts, v.Candidates);
        Strings(v.Warnings, "warnings", 256);
    }

    private static void ValidateAcknowledgement(CommandAcknowledgedEvent v)
    {
        Session(v.SessionId); Defined(v.Acknowledgement, "acknowledgement");
        bool plan = v.Acknowledgement is ProtocolAcknowledgementKind.PlanConfirmed or ProtocolAcknowledgementKind.PlanCancellationRequested or ProtocolAcknowledgementKind.PlanCancelledBeforeExecution;
        if (plan != v.PlanId.HasValue || plan == v.PrunePlanId.HasValue)
            throw new ProtocolException("The command acknowledgement doesn't carry its exact plan binding.");
        if (v.PlanId is { } planId) ProtocolIdentifier.AssertCanonical(planId.Value, "plan");
        if (v.PrunePlanId is { } pruneId) ProtocolIdentifier.AssertCanonical(pruneId.Value, "prune plan");
    }

    private static void ValidatePrune(PrunePlanEvent v)
    {
        PruneBinding(v.SessionId, v.PrunePlanId, v.PruneDigest); ProtocolPlanDigest.AssertCanonical(v.ExecutionBindingDigest.Value); ProtocolIdentifier.AssertCanonical(v.CatalogId.Value, "recovery catalog"); GameRoot(v.GameRoot); Hex(v.HeadSha256, 64, "headSha256");
        if (v.RetainNewest is < 1 or > MaxRecoveryGenerations) throw new ProtocolException("The protocol 'retainNewest' value must be between 1 and 64.");
        IdArray(v.RetainedSelectionIds, "retainedSelectionIds", MaxRecoveryGenerations); IdArray(v.RemovedSelectionIds, "removedSelectionIds", MaxRecoveryGenerations);
        if (v.RetainedSelectionIds.Intersect(v.RemovedSelectionIds).Any()) throw new ProtocolException("A recovery selection can't be both retained and removed.");
        Strings(v.CleanupGenerationIds, "cleanupGenerationIds", MaxRecoveryGenerations);
        foreach (string generationId in v.CleanupGenerationIds) RequireGenerationId(generationId, "cleanupGenerationIds");
        int catalogCount = v.RetainedSelectionIds.Length + v.RemovedSelectionIds.Length;
        if (
            catalogCount is <= 0 or > MaxRecoveryGenerations
            || v.RetainedSelectionIds.Length != Math.Min(v.RetainNewest, catalogCount)
            || v.CleanupGenerationIds.Length < v.RemovedSelectionIds.Length
            || v.RemovedSelectionIds.Length == 0 && v.CleanupGenerationIds.Length == 0 && !v.AuxiliaryCleanupPlanned
        )
            throw new ProtocolException("The prune plan is a no-op or isn't a sensible bounded exact catalog partition and cleanup set.");
        Text(v.Summary, "summary"); Strings(v.Warnings, "warnings", 256); Objects(v.Risks, "risks", 8); foreach (ProtocolPlanRisk risk in v.Risks) Defined(risk, "risk"); if (!v.Risks.Contains(ProtocolPlanRisk.RecoveryPrune) || v.Risks.Distinct().Count() != v.Risks.Length) throw new ProtocolException("A destructive prune plan must carry its unique typed risk."); Defined(v.RecommendedDefault, "recommendedDefault"); if (!v.RequiresConfirmation) throw new ProtocolException("Every prune plan must require explicit confirmation.");
        ProtocolPlanDigest expected = ProtocolPlanDigest.ComputePrune(v.ExecutionBindingDigest, v.CatalogId, v.GameRoot, v.HeadSha256, v.RetainNewest, v.RetainedSelectionIds, v.RemovedSelectionIds, v.CleanupGenerationIds, v.AuxiliaryCleanupPlanned, v.Summary, v.Warnings, true);
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
            ProtocolIdentifier.AssertCanonical(item.CandidateId.Value, "candidate"); if (!ids.Add(item.CandidateId)) throw new ProtocolException("The plan candidates contain a duplicate ID."); Defined(item.Reason, "candidate.reason"); Defined(item.Disposition, "candidate.disposition"); RelativePath(item.Path, "candidate.path"); if (!paths.Add(item.Path)) throw new ProtocolException("The plan candidates contain a duplicate path."); Hex(item.ObservedSha256, 64, "candidate.observedSha256"); if (item.ObservedSizeBytes < 0 || item.ObservedUnixMode < 0 || item.ObservedUnixMode > 4095) throw new ProtocolException("The candidate observed metadata is invalid."); OptionalHex(item.ProposedResultSha256, 64, "candidate.proposedResultSha256"); ValidateCandidatePresentation(item); Text(item.Evidence, "candidate.evidence");
        }
    }

    private static void ValidateCandidatePresentation(ProtocolPlanCandidate candidate)
    {
        bool validPair = candidate.Reason switch
        {
            FileReplacementCandidateReason.ModifiedReceiptOwned => candidate.Disposition is FileReplacementCandidateDisposition.Replace or FileReplacementCandidateDisposition.Remove,
            FileReplacementCandidateReason.ModifiedInstalledLauncher => candidate.Disposition is FileReplacementCandidateDisposition.Replace or FileReplacementCandidateDisposition.Restore,
            FileReplacementCandidateReason.LegacyInstaller or FileReplacementCandidateReason.UnknownCollision or FileReplacementCandidateReason.OfficialOrLegacyLauncher => candidate.Disposition == FileReplacementCandidateDisposition.Replace,
            FileReplacementCandidateReason.OfficialLauncherBackup => candidate.Disposition == FileReplacementCandidateDisposition.TrustRetained,
            _ => false
        };
        if (!validPair)
            throw new ProtocolException("The candidate reason and disposition aren't a core-defined pair.");
        if (candidate.Reason is FileReplacementCandidateReason.ModifiedInstalledLauncher or FileReplacementCandidateReason.OfficialOrLegacyLauncher
            && !string.Equals(candidate.Path, "StardewValley", StringComparison.Ordinal))
        {
            throw new ProtocolException("An installed-launcher candidate must target the exact launcher path.");
        }
        if (candidate.Reason == FileReplacementCandidateReason.OfficialLauncherBackup
            && !string.Equals(candidate.Path, "StardewValley-original", StringComparison.Ordinal))
        {
            throw new ProtocolException("An official-launcher backup candidate must target the exact backup path.");
        }
        if (string.Equals(candidate.Path, "StardewValley", StringComparison.Ordinal)
            && candidate.Reason is not (FileReplacementCandidateReason.ModifiedInstalledLauncher or FileReplacementCandidateReason.OfficialOrLegacyLauncher))
        {
            throw new ProtocolException("The exact installed-launcher path must use an installed-launcher candidate reason.");
        }
        if (string.Equals(candidate.Path, "StardewValley-original", StringComparison.Ordinal)
            && candidate.Reason != FileReplacementCandidateReason.OfficialLauncherBackup)
        {
            throw new ProtocolException("The exact official-launcher backup path must use the official-launcher backup candidate reason.");
        }
        if (candidate.Disposition == FileReplacementCandidateDisposition.Remove != (candidate.ProposedResultSha256 is null))
            throw new ProtocolException("Only removal candidates may omit the proposed result digest.");
        if (candidate.Disposition == FileReplacementCandidateDisposition.TrustRetained && candidate.ProposedResultSha256 != candidate.ObservedSha256)
            throw new ProtocolException("A trust-retained candidate must preserve the exact observed digest.");
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
        if (authority.Generation.IsUserCheckpoint != (authority.Generation.OriginOperation == InstallerOperation.Backup))
            throw new ProtocolException("The recovery authority checkpoint flag doesn't match its origin operation.");
        ValidateRelease(authority.Generation.RestoreRelease, "recovery authority restore release", optional: true);
        if ((authority.Generation.RestoreRelease is null) != authority.Generation.RestoresUninstalledState)
            throw new ProtocolException("A recovery authority must identify either one exact restore release or an uninstalled result.");
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
        else if (operation == InstallerOperation.Backup && ((current is null) != (target is null) || current is not null && current != target)) throw new ProtocolException("A backup operation's current and target releases must both be absent or exactly equal.");
        else if (operation == InstallerOperation.Uninstall && target is not null) throw new ProtocolException("An uninstall operation must not invent a target release.");
        if (operation == InstallerOperation.Install && current is not null) throw new ProtocolException("A fresh install must not invent a current release.");
    }

    private static void ValidateRecoveryAttempt(ProtocolInterruptedRecoveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt); GameRoot(attempt.GameRoot);
        if (attempt.CurrentOperationGeneration is { } current && current < attempt.PreviousOperationGeneration || attempt.GameRoot.OperationGeneration != (attempt.CurrentOperationGeneration ?? attempt.PreviousOperationGeneration)) throw new ProtocolException("The interrupted-recovery attempt root or generation identity is invalid.");
        ProtocolRecoveredTransactionResult[] recovered = attempt.RecoveredTransactions;
        Objects(recovered, "attempt.recoveredTransactions", InstallerTransactionExecutor.MaximumTransactionStoreEntries);
        if (recovered.Any(result => result.ChangedPathCount is < 0 or > TransactionPlan.MaximumOperationCount) || recovered.Any(result => !Guid.TryParseExact(result.TransactionId, "N", out Guid id) || id == Guid.Empty || result.TransactionId.Any(character => character is >= 'A' and <= 'F')) || recovered.Select(result => result.TransactionId).Distinct(StringComparer.Ordinal).Count() != recovered.Length)
            throw new ProtocolException("The interrupted-recovery attempt has invalid or duplicate transaction results.");
    }

    private static void ValidatePrePlanRejection(PrePlanRejectedEvent value)
    {
        Session(value.SessionId); Defined(value.ErrorCode, "errorCode"); Text(value.Message, "message"); Defined(value.NextAction, "nextAction"); OptionalLog(value.SanitizedLogPath);
        (ProtocolNextAction ExpectedAction, bool ExpectedTerminal) expected = value.ErrorCode switch
        {
            ProtocolPrePlanErrorCode.RequestCancelled => (ProtocolNextAction.RetryRequest, false),
            ProtocolPrePlanErrorCode.InvalidGameFolder => (ProtocolNextAction.SelectGameFolder, false),
            ProtocolPrePlanErrorCode.PackageRejected => (ProtocolNextAction.ReopenVerifiedPackage, false),
            ProtocolPrePlanErrorCode.RecoveryUnavailable => (ProtocolNextAction.ListRecoveries, false),
            ProtocolPrePlanErrorCode.NothingToPrune => (ProtocolNextAction.ListRecoveries, false),
            ProtocolPrePlanErrorCode.InspectionFailed => (ProtocolNextAction.InspectAgain, false),
            ProtocolPrePlanErrorCode.CandidateApprovalFailed => (ProtocolNextAction.InspectAgain, false),
            ProtocolPrePlanErrorCode.PermissionDenied => (ProtocolNextAction.ReviewFilesystem, false),
            ProtocolPrePlanErrorCode.InputOutputFailure => (ProtocolNextAction.RetryRequest, false),
            ProtocolPrePlanErrorCode.UnexpectedFailure => (
                value.SanitizedLogPath is null ? ProtocolNextAction.StartNewSession : ProtocolNextAction.ViewPrivateLog,
                true
            ),
            _ => throw new ProtocolException("The pre-plan rejection error code isn't defined by version 1.")
        };
        if (value.NextAction != expected.ExpectedAction || value.IsTerminal != expected.ExpectedTerminal)
            throw new ProtocolException("The pre-plan rejection action, terminal state, or private-log availability doesn't match its exact error class.");
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

    private static void ValidateRecoveryCompleted(RecoveryCompletedEvent value)
    {
        Session(value.SessionId); Defined(value.Outcome, "outcome"); ValidateRecoveryAttempt(value.Attempt);
        if (value.Outcome != ProtocolInterruptedRecoveryOutcome.RecoveryCompleted || value.Attempt.CurrentOperationGeneration is not { } current || current <= value.Attempt.PreviousOperationGeneration || value.Attempt.NamedRootStillSelected is null)
            throw new ProtocolException("The interrupted-recovery completion has an invalid outcome, generation, or selected root.");
        ValidateTerminalState(value.TerminalState, ProtocolDurableState.RecoveryCompleted, errorRequired: false, ProtocolRecoveryDisposition.Completed, value.Attempt.NamedRootStillSelected.Value ? ProtocolNextAction.InspectAgain : ProtocolNextAction.SelectGameFolder);
        Text(value.Summary, "summary"); OptionalLog(value.SanitizedLogPath);
    }

    private static void ValidateRecoveryFailure(RecoveryFailureEvent value)
    {
        Session(value.SessionId); Defined(value.Outcome, "outcome"); Text(value.Message, "message"); OptionalLog(value.SanitizedLogPath);
        switch (value.Outcome)
        {
            case ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery:
                ValidateTerminalState(value.TerminalState, ProtocolDurableState.Unchanged, false, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted);
                if (value.Attempt is not null) throw new ProtocolException("A pre-recovery cancellation can't contain a recovery attempt."); break;
            case ProtocolInterruptedRecoveryOutcome.PartialFailure:
                ValidateTerminalState(value.TerminalState, ProtocolDurableState.RecoveryRequired, true, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted);
                if (value.Attempt is null) throw new ProtocolException("A partial recovery failure requires an exact attempt."); ValidateRecoveryAttempt(value.Attempt); break;
            case ProtocolInterruptedRecoveryOutcome.UnexpectedFailure:
                ValidateTerminalState(value.TerminalState, ProtocolDurableState.Unknown, false, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted, ProtocolTerminalErrorCode.UnexpectedCoreFailure);
                if (value.Attempt is not null) throw new ProtocolException("An unknown recovery failure can't claim a recovery attempt."); break;
            default: throw new ProtocolException("A recovery failure has an invalid typed outcome.");
        }
    }

    private static void ValidateExecutionTerminal(ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest, ProtocolExecutionOutcome outcome, ProtocolTerminalState state, ProtocolExecutionSummary summary, string? displaySummary, string? message, string? log, params ProtocolExecutionOutcome[] allowed)
    {
        PlanBinding(session, plan, digest); Defined(outcome, "outcome"); if (!allowed.Contains(outcome)) throw new ProtocolException("The execution outcome doesn't match its terminal event family.");
        if (displaySummary is not null) Text(displaySummary, "summary"); if (message is not null) Text(message, "message"); OptionalLog(log);
        (ProtocolDurableState durable, bool error, ProtocolRecoveryDisposition recovery, ProtocolNextAction action, ProtocolTerminalErrorCode? exactError) = outcome switch
        {
            ProtocolExecutionOutcome.Succeeded => (ProtocolDurableState.Committed, false, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain, (ProtocolTerminalErrorCode?)null),
            ProtocolExecutionOutcome.SucceededWithCleanupWarning => (ProtocolDurableState.Committed, true, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.InspectAgain, null),
            ProtocolExecutionOutcome.FailedBeforeMutation => (ProtocolDurableState.Unchanged, true, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain, null),
            ProtocolExecutionOutcome.CancelledBeforeMutation => (ProtocolDurableState.Unchanged, false, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain, null),
            ProtocolExecutionOutcome.CancelledAndRolledBack => (ProtocolDurableState.RolledBack, false, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain, null),
            ProtocolExecutionOutcome.FailedAndRolledBack => (ProtocolDurableState.RolledBack, true, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain, null),
            ProtocolExecutionOutcome.InterruptedRecoveryRequired => (ProtocolDurableState.RecoveryRequired, true, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted, null),
            ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired => (ProtocolDurableState.RecoveryCompleted, false, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain, ProtocolTerminalErrorCode.PathChanged),
            ProtocolExecutionOutcome.UnexpectedCoreFailure => (ProtocolDurableState.Unknown, false, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted, ProtocolTerminalErrorCode.UnexpectedCoreFailure),
            _ => throw new ProtocolException("The execution outcome isn't defined by version 1.")
        };
        ValidateTerminalState(state, durable, error, recovery, action, exactError);
        ValidateExecutionSummary(summary, outcome);
    }

    private static void ValidatePruneTerminal(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest, ProtocolPruneOutcome outcome, ProtocolTerminalState state, ProtocolPruneSummary summary, string? displaySummary, string? message, string? log, params ProtocolPruneOutcome[] allowed)
    {
        PruneBinding(session, plan, digest); Defined(outcome, "outcome"); if (!allowed.Contains(outcome)) throw new ProtocolException("The prune outcome doesn't match its terminal event family.");
        if (displaySummary is not null) Text(displaySummary, "summary"); if (message is not null) Text(message, "message"); OptionalLog(log);
        bool unknown = outcome == ProtocolPruneOutcome.UnexpectedCoreFailure; ValidatePruneSummary(summary, unknown);
        bool pending = !unknown && (summary.PendingCleanupGenerationCount > 0 || summary.AuxiliaryCleanupPending == true);
        ProtocolDurableState observedState = !unknown && (summary.LogicallyRemovedGenerationCount > 0 || summary.PhysicallyCleanedGenerationCount > 0) ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged;
        (ProtocolDurableState durable, bool error, ProtocolRecoveryDisposition recovery, ProtocolTerminalErrorCode? exactError) = outcome switch
        {
            ProtocolPruneOutcome.Succeeded => (ProtocolDurableState.PruneApplied, false, ProtocolRecoveryDisposition.NotRequired, (ProtocolTerminalErrorCode?)null),
            ProtocolPruneOutcome.FailedBeforePublication => (ProtocolDurableState.Unchanged, true, pending ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.NotRequired, null),
            ProtocolPruneOutcome.CancelledBeforePublication => (ProtocolDurableState.Unchanged, false, pending ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.NotRequired, null),
            ProtocolPruneOutcome.Interrupted => (observedState, true, pending ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.StateRefreshRequired, null),
            ProtocolPruneOutcome.CancelledWithCleanupPending => (observedState, false, ProtocolRecoveryDisposition.CleanupPending, null),
            ProtocolPruneOutcome.FailedWithCleanupPending => (observedState, true, ProtocolRecoveryDisposition.CleanupPending, null),
            ProtocolPruneOutcome.CancelledAfterApply => (ProtocolDurableState.PruneApplied, false, ProtocolRecoveryDisposition.StateRefreshRequired, null),
            ProtocolPruneOutcome.FailedAfterApply => (ProtocolDurableState.PruneApplied, true, ProtocolRecoveryDisposition.StateRefreshRequired, null),
            ProtocolPruneOutcome.UnexpectedCoreFailure => (ProtocolDurableState.Unknown, false, ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolTerminalErrorCode.UnexpectedCoreFailure),
            _ => throw new ProtocolException("The prune outcome isn't defined by version 1.")
        };
        ValidateTerminalState(state, durable, error, recovery, ProtocolNextAction.ListRecoveries, exactError);
        if (outcome == ProtocolPruneOutcome.Succeeded && pending) throw new ProtocolException("A successful prune can't report pending cleanup.");
        if (outcome is ProtocolPruneOutcome.FailedBeforePublication or ProtocolPruneOutcome.CancelledBeforePublication && (summary.LogicallyRemovedGenerationCount != 0 || summary.PhysicallyCleanedGenerationCount != 0)) throw new ProtocolException("A prune which stopped before publication can't report applied work.");
        if (outcome is ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply && (pending || observedState != ProtocolDurableState.PruneApplied)) throw new ProtocolException("An after-apply prune outcome requires applied work and no known pending cleanup.");
        if (!pending && (outcome is ProtocolPruneOutcome.CancelledWithCleanupPending or ProtocolPruneOutcome.FailedWithCleanupPending)) throw new ProtocolException("A pending prune outcome must report pending cleanup.");
    }

    private static void ValidateTerminalState(ProtocolTerminalState state, ProtocolDurableState durable, bool errorRequired, ProtocolRecoveryDisposition recovery, ProtocolNextAction action, ProtocolTerminalErrorCode? exactError = null)
    {
        ArgumentNullException.ThrowIfNull(state); Defined(state.DurableState, "terminalState.durableState"); if (state.ErrorCode is { } error) Defined(error, "terminalState.errorCode"); Defined(state.RecoveryDisposition, "terminalState.recoveryDisposition"); Defined(state.NextAction, "terminalState.nextAction");
        bool errorValid = exactError is { } exact ? state.ErrorCode == exact : errorRequired ? state.ErrorCode is not null and not ProtocolTerminalErrorCode.UnexpectedCoreFailure : state.ErrorCode is null;
        if (state.DurableState != durable || !errorValid || state.RecoveryDisposition != recovery || state.NextAction != action) throw new ProtocolException("The terminal state doesn't match the exact typed outcome table.");
    }

    private static void ValidateExecutionSummary(ProtocolExecutionSummary summary, ProtocolExecutionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(summary); bool unknown = outcome == ProtocolExecutionOutcome.UnexpectedCoreFailure; int?[] counts = [summary.ManagedFileChangeCount, summary.RolledBackManagedFileCount, summary.InternalStateChangeCount, summary.RolledBackInternalStateCount, summary.RecoveredTransactionCount, summary.RecoveredPathCount];
        if (unknown ? counts.Any(value => value is not null) : counts.Any(value => value is null or < 0)) throw new ProtocolException("The execution summary doesn't preserve known and unknown bounded counts.");
        if (unknown) return;
        int changedTotal;
        int rolledBackTotal;
        try
        {
            changedTotal = checked(summary.ManagedFileChangeCount!.Value + summary.InternalStateChangeCount!.Value);
            rolledBackTotal = checked(summary.RolledBackManagedFileCount!.Value + summary.RolledBackInternalStateCount!.Value);
        }
        catch (OverflowException exception)
        {
            throw new ProtocolException("The execution summary aggregate counts overflow their bounds.", exception);
        }
        if (summary.ManagedFileChangeCount > TransactionPlan.MaximumOperationCount || summary.InternalStateChangeCount > TransactionPlan.MaximumOperationCount || changedTotal > TransactionPlan.MaximumOperationCount || summary.RolledBackManagedFileCount > summary.ManagedFileChangeCount || summary.RolledBackInternalStateCount > summary.InternalStateChangeCount || rolledBackTotal > changedTotal || summary.RecoveredTransactionCount > InstallerTransactionExecutor.MaximumTransactionStoreEntries || summary.RecoveredPathCount > InstallerTransactionExecutor.MaximumTransactionStoreEntries * TransactionPlan.MaximumOperationCount) throw new ProtocolException("The execution summary counts are outside their bounds.");
        bool noChanges = summary.ManagedFileChangeCount == 0 && summary.RolledBackManagedFileCount == 0 && summary.InternalStateChangeCount == 0 && summary.RolledBackInternalStateCount == 0 && summary.RecoveredTransactionCount == 0 && summary.RecoveredPathCount == 0;
        bool fullRollback = summary.ManagedFileChangeCount == summary.RolledBackManagedFileCount && summary.InternalStateChangeCount == summary.RolledBackInternalStateCount;
        bool valid = outcome switch
        {
            ProtocolExecutionOutcome.Succeeded or ProtocolExecutionOutcome.SucceededWithCleanupWarning => summary.RolledBackManagedFileCount == 0 && summary.RolledBackInternalStateCount == 0 && summary.RecoveredTransactionCount == 0 && summary.RecoveredPathCount == 0,
            ProtocolExecutionOutcome.FailedBeforeMutation or ProtocolExecutionOutcome.CancelledBeforeMutation => noChanges,
            ProtocolExecutionOutcome.FailedAndRolledBack or ProtocolExecutionOutcome.CancelledAndRolledBack => fullRollback && summary.RecoveredTransactionCount == 0 && summary.RecoveredPathCount == 0,
            ProtocolExecutionOutcome.InterruptedRecoveryRequired => summary.RecoveredTransactionCount == 0 && summary.RecoveredPathCount == 0,
            ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired => summary.ManagedFileChangeCount == 0 && summary.RolledBackManagedFileCount == 0 && summary.InternalStateChangeCount == 0 && summary.RolledBackInternalStateCount == 0 && summary.RecoveredTransactionCount > 0,
            _ => false
        };
        if (!valid) throw new ProtocolException("The execution summary doesn't match the exact typed outcome counts.");
    }

    private static void ValidatePruneSummary(ProtocolPruneSummary summary, bool unknown)
    {
        ArgumentNullException.ThrowIfNull(summary); int?[] counts = [summary.LogicallyRemovedGenerationCount, summary.PhysicallyCleanedGenerationCount, summary.PendingCleanupGenerationCount];
        if (unknown ? counts.Any(value => value is not null) || summary.AuxiliaryCleanupPending is not null : counts.Any(value => value is null or < 0 or > MaxRecoveryGenerations) || summary.AuxiliaryCleanupPending is null) throw new ProtocolException("The prune summary doesn't preserve known and unknown bounded state.");
    }

    private static void Progress(long sequence, TransactionStage stage, int completed, int? total, string message) { Defined(stage, "stage"); Text(message, "message"); if (sequence < 0 || completed < 0 || total < 0 || total is { } known && completed > known) throw new ProtocolException("The protocol progress counters are inconsistent."); }
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
    private static void RequireGenerationId(string value, string field) { if (value.Length != 32 || !Guid.TryParseExact(value, "N", out Guid id) || id == Guid.Empty || value.Any(c => c is >= 'A' and <= 'F')) throw new ProtocolException($"The protocol '{field}' value isn't a canonical nonempty generation ID."); }
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
    private static void AssertTerminalState(JsonElement e) => AssertExactObject(e, ["durableState", "errorCode", "recoveryDisposition", "nextAction"], "terminal state");
    private static void AssertRecoveryAttempt(JsonElement e) { AssertExactObject(e, ["gameRoot", "previousOperationGeneration", "currentOperationGeneration", "namedRootStillSelected", "recoveredTransactions"], "interrupted-recovery attempt"); AssertGameRoot(e.GetProperty("gameRoot")); AssertObjectArray(e.GetProperty("recoveredTransactions"), ["transactionId", "changedPathCount"], "recovered transaction result", InstallerTransactionExecutor.MaximumTransactionStoreEntries); }
    private static void AssertOptionalObject(JsonElement element, IReadOnlyCollection<string> properties, string description) { if (element.ValueKind != JsonValueKind.Null) AssertExactObject(element, properties, description); }
    private static void AssertOptionalRecoveryAuthority(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return;
        AssertExactObject(element, ["catalogId", "selectionId", "gameRoot", "headSha256", "generation"], "recovery authority");
        AssertGameRoot(element.GetProperty("gameRoot"));
        JsonElement generation = element.GetProperty("generation");
        AssertExactObject(generation, ["selectionId", "generationId", "originOperation", "isCurrent", "isUserCheckpoint", "restoreRelease", "restoresUninstalledState"], "recovery authority generation");
        AssertOptionalReleaseObject(generation.GetProperty("restoreRelease"), "recovery authority restore release");
    }

    private static void AssertCanonicalEnums(ProtocolMessageKind kind, JsonElement payload)
    {
        switch (kind)
        {
            case ProtocolMessageKind.InspectPlanRequest: AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation"); break;
            case ProtocolMessageKind.GetPlanPageRequest: AssertCanonicalEnum<ProtocolPlanPageKind>(payload.GetProperty("pageKind"), "pageKind"); break;
            case ProtocolMessageKind.CommandAcknowledgedEvent: AssertCanonicalEnum<ProtocolAcknowledgementKind>(payload.GetProperty("acknowledgement"), "acknowledgement"); break;
            case ProtocolMessageKind.GameDiscoveryEvent:
                foreach (JsonElement item in payload.GetProperty("candidates").EnumerateArray()) AssertCanonicalEnum<LinuxGameFolderStatus>(item.GetProperty("state"), "candidate.state");
                break;
            case ProtocolMessageKind.GameValidationEvent:
                AssertCanonicalEnum<LinuxGameFolderStatus>(payload.GetProperty("candidate").GetProperty("state"), "candidate.state"); break;
            case ProtocolMessageKind.RecoveryProgressEvent: AssertCanonicalEnum<TransactionStage>(payload.GetProperty("stage"), "stage"); break;
            case ProtocolMessageKind.RecoveryCompletedEvent or ProtocolMessageKind.RecoveryFailureEvent:
                AssertCanonicalEnum<ProtocolInterruptedRecoveryOutcome>(payload.GetProperty("outcome"), "outcome"); AssertCanonicalTerminalState(payload); break;
            case ProtocolMessageKind.RecoveryCatalogEvent:
                foreach (JsonElement item in payload.GetProperty("generations").EnumerateArray()) AssertCanonicalEnum<InstallerOperation>(item.GetProperty("originOperation"), "generation.originOperation");
                break;
            case ProtocolMessageKind.PlanEvent:
                AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation");
                AssertCanonicalEnum<ObservedInstallState>(payload.GetProperty("observedState"), "observedState");
                if (payload.GetProperty("recoveryAuthority").ValueKind != JsonValueKind.Null) AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("recoveryAuthority").GetProperty("generation").GetProperty("originOperation"), "recoveryAuthority.generation.originOperation");
                foreach (JsonElement risk in payload.GetProperty("risks").EnumerateArray()) AssertCanonicalEnum<ProtocolPlanRisk>(risk, "risk");
                AssertCanonicalEnum<ProtocolRecommendedDefault>(payload.GetProperty("recommendedDefault"), "recommendedDefault");
                break;
            case ProtocolMessageKind.PlanPageEvent:
                AssertCanonicalEnum<ProtocolPlanPageKind>(payload.GetProperty("pageKind"), "pageKind");
                foreach (JsonElement item in payload.GetProperty("operations").EnumerateArray()) AssertCanonicalEnum<PlanOperationKind>(item.GetProperty("kind"), "operation.kind");
                foreach (JsonElement item in payload.GetProperty("conflicts").EnumerateArray()) AssertCanonicalEnum<PlanConflictCode>(item.GetProperty("code"), "conflict.code");
                foreach (JsonElement item in payload.GetProperty("candidates").EnumerateArray()) { AssertCanonicalEnum<FileReplacementCandidateReason>(item.GetProperty("reason"), "candidate.reason"); AssertCanonicalEnum<FileReplacementCandidateDisposition>(item.GetProperty("disposition"), "candidate.disposition"); }
                break;
            case ProtocolMessageKind.PrunePlanEvent:
                foreach (JsonElement risk in payload.GetProperty("risks").EnumerateArray()) AssertCanonicalEnum<ProtocolPlanRisk>(risk, "risk");
                AssertCanonicalEnum<ProtocolRecommendedDefault>(payload.GetProperty("recommendedDefault"), "recommendedDefault");
                break;
            case ProtocolMessageKind.ProgressEvent or ProtocolMessageKind.PruneProgressEvent: AssertCanonicalEnum<TransactionStage>(payload.GetProperty("stage"), "stage"); break;
            case ProtocolMessageKind.SuccessEvent:
                AssertCanonicalEnum<InstallerOperation>(payload.GetProperty("operation"), "operation"); AssertCanonicalEnum<ProtocolExecutionOutcome>(payload.GetProperty("outcome"), "outcome"); AssertCanonicalTerminalState(payload); break;
            case ProtocolMessageKind.RolledBackFailureEvent or ProtocolMessageKind.CancelledEvent or ProtocolMessageKind.RecoverableInterruptionEvent:
                AssertCanonicalEnum<ProtocolExecutionOutcome>(payload.GetProperty("outcome"), "outcome"); AssertCanonicalTerminalState(payload); break;
            case ProtocolMessageKind.PruneSuccessEvent or ProtocolMessageKind.PruneFailureEvent or ProtocolMessageKind.PruneCancelledEvent or ProtocolMessageKind.PruneInterruptionEvent:
                AssertCanonicalEnum<ProtocolPruneOutcome>(payload.GetProperty("outcome"), "outcome"); AssertCanonicalTerminalState(payload); break;
            case ProtocolMessageKind.PrePlanRejectedEvent:
                AssertCanonicalEnum<ProtocolPrePlanErrorCode>(payload.GetProperty("errorCode"), "errorCode"); AssertCanonicalEnum<ProtocolNextAction>(payload.GetProperty("nextAction"), "nextAction"); break;
        }
    }

    private static void AssertCanonicalTerminalState(JsonElement payload)
    {
        JsonElement state = payload.GetProperty("terminalState");
        AssertCanonicalEnum<ProtocolDurableState>(state.GetProperty("durableState"), "terminalState.durableState");
        if (state.GetProperty("errorCode").ValueKind != JsonValueKind.Null) AssertCanonicalEnum<ProtocolTerminalErrorCode>(state.GetProperty("errorCode"), "terminalState.errorCode");
        AssertCanonicalEnum<ProtocolRecoveryDisposition>(state.GetProperty("recoveryDisposition"), "terminalState.recoveryDisposition");
        AssertCanonicalEnum<ProtocolNextAction>(state.GetProperty("nextAction"), "terminalState.nextAction");
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
