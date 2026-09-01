using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class ProcessInstallerProtocolClientTests
{
    private static readonly ProtocolSessionId Session = ProtocolSessionId.Parse("11111111111111111111111111111111");

    [Test]
    public async Task StartsExactAbsoluteSiblingWithoutShellOrArgumentParsing()
    {
        ScriptedProcess process = new(CorrectResponse);
        CapturingFactory factory = new(process);
        string path = "/tmp/installer folder ;$(touch nope)/SMAPI.Installer";
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(path, factory);

        HandshakeEvent response = await client.HandshakeAsync("SMAPI GUI", "1");

        response.SessionId.Should().Be(Session);
        factory.StartInfo.Should().NotBeNull();
        factory.StartInfo!.FileName.Should().Be(path);
        factory.StartInfo.UseShellExecute.Should().BeFalse();
        factory.StartInfo.RedirectStandardInput.Should().BeTrue();
        factory.StartInfo.RedirectStandardOutput.Should().BeTrue();
        factory.StartInfo.RedirectStandardError.Should().BeTrue();
        factory.StartInfo.ArgumentList.Should().Equal(ProcessInstallerProtocolClient.ProtocolFlag);
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task RealProcessIntegrationUsesExactFlagAndHandlesPathMetacharacters()
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi gui process ;$[] {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string gui = Path.Combine(root, "SMAPI.Installer.Gui");
            string installer = Path.Combine(root, SiblingInstallerLocator.InstallerFileName);
            await File.WriteAllTextAsync(gui, "gui");
            await File.WriteAllTextAsync(installer, """
                #!/bin/sh
                test "$#" -eq 1 && test "$1" = "--linux-protocol-v1-jsonl" || exit 42
                IFS= read -r request || exit 43
                command_id=$(printf '%s\n' "$request" | sed -n 's/.*"commandId":"\([0-9a-f]*\)".*/\1/p')
                test "${#command_id}" -eq 32 || exit 44
                printf '%s\n' "{\"protocolVersion\":1,\"messageType\":\"handshake.event\",\"payload\":{\"commandId\":\"$command_id\",\"sessionId\":\"11111111111111111111111111111111\",\"serverVersion\":\"1\",\"capabilities\":[\"verified-local-package\",\"linux-game-discovery\",\"linux-game-validation\",\"install-update-repair-uninstall-backup-rollback\",\"candidate-approval\",\"exact-core-progress\",\"cancellation\",\"interrupted-operation-recovery\"]}}"
                while IFS= read -r ignored; do :; done
                """);
            File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using LinuxExternalExecutableLease lease = SiblingInstallerLocator.OpenSibling(gui);
            await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
                installer,
                new SystemInstallerProtocolProcessFactory(),
                executableLease: lease
            );

            HandshakeEvent response = await client.HandshakeAsync("SMAPI GUI", "1");

            response.SessionId.Should().Be(Session);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task ActualSmapiInstallerProtocolHostKeepsLiveSessionAcrossTwoNormalRejections()
    {
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent?.Name
            ?? throw new AssertionException("The test build configuration couldn't be derived.");
        string installer = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "SMAPI.Installer", "bin", configuration, "SMAPI.Installer"
        ));
        File.Exists(installer).Should().BeTrue("the test project has a build-only reference to the actual installer host");
        using LinuxExternalExecutableLease lease = LinuxExternalExecutableLease.Open(installer);
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
            installer,
            new RollForwardSystemFactory(),
            executableLease: lease
        );

        HandshakeEvent response = await client.HandshakeAsync("SMAPI GUI integration test", "1");
        InstallerPackageOpenResult first = await client.OpenPackageAsync(CreatePackage());
        InstallerPackageOpenResult second = await client.OpenPackageAsync(CreatePackage());

        response.Capabilities.Should().Contain(ProcessInstallerProtocolClient.PackageVerificationCapability);
        first.Should().BeOfType<InstallerPackageOpenRejection>();
        second.Should().BeOfType<InstallerPackageOpenRejection>();
        client.SessionFaulted.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task CorrelatesHandshakeAndPackageOpenAndSendsOnlyThoseTwoRequestKinds()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);

        await client.HandshakeAsync("SMAPI GUI", "1");
        InstallerPackageOpenSuccess result = (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>().Subject;
        ProtocolReleaseIdentity opened = result.Release;

        opened.Tag.Should().Be(CreatePackage().ReleaseTag);
        client.HasRetainedPackageAuthority.Should().BeTrue();
        process.Requests.Select(request => request.Kind).Should().Equal(ProtocolMessageKind.HandshakeRequest, ProtocolMessageKind.OpenPackageRequest);
        OpenPackageRequest sent = process.Requests.OfType<OpenPackageRequest>().Single();
        sent.PackagePath.Should().Be(CreatePackage().PackagePath);
        sent.AttestationBundleChecksumPath.Should().Be(CreatePackage().AttestationBundleChecksumPath);
        typeof(IInstallerProtocolClient).GetMethods().Select(method => method.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .Should().BeEquivalentTo([
                nameof(IInstallerProtocolClient.HandshakeAsync),
                nameof(IInstallerProtocolClient.OpenPackageAsync),
                nameof(IInstallerProtocolClient.DiscoverGamesAsync),
                nameof(IInstallerProtocolClient.ValidateGameAsync),
                nameof(IInstallerProtocolClient.InspectPlanAsync),
                nameof(IInstallerProtocolClient.ApprovePlanCandidatesAsync),
                nameof(IInstallerProtocolClient.ConfirmPlanAsync),
                nameof(IInstallerProtocolClient.ExecutePlanAsync),
                nameof(IInstallerProtocolClient.RecoverInterruptedAsync)
            ]);
    }

    [Test]
    public void ConfirmationAuthoritiesAndConfirmedSessionExposeOnlyTheIntendedOpaqueSurface()
    {
        foreach (Type capability in new[] { typeof(InstallerPlanConfirmation), typeof(InstallerConfirmedPlanAuthority) })
        {
            capability.GetConstructors().Should().BeEmpty();
            capability.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Should().BeEmpty();
            capability.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Should().BeEmpty();
            capability.GetMethod(nameof(object.Equals), [typeof(object)])!.DeclaringType.Should().Be(typeof(object));
        }

        typeof(IConfirmedInstallerSession).GetProperties().Select(property => property.Name).Should().BeEquivalentTo([
            nameof(IConfirmedInstallerSession.Release),
            nameof(IConfirmedInstallerSession.Game),
            nameof(IConfirmedInstallerSession.SessionFaulted)
        ]);
        typeof(IConfirmedInstallerSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().Equal(nameof(IConfirmedInstallerSession.ExecuteAsync));
        MethodInfo execute = typeof(IConfirmedInstallerSession).GetMethod(nameof(IConfirmedInstallerSession.ExecuteAsync))!;
        execute.ReturnType.Should().Be(typeof(Task<InstallerExecutionOperation>));
        execute.GetParameters().Should().ContainSingle().Which.ParameterType.Should().Be(typeof(CancellationToken));
        execute.GetParameters().Single().HasDefaultValue.Should().BeTrue();
        typeof(IConfirmedInstallerSession).GetInterfaces().Should().Equal(typeof(IAsyncDisposable));

        Type[] authorityTypes = [
            typeof(InstallerPlanConfirmation),
            typeof(InstallerConfirmedPlanAuthority),
            typeof(IConfirmedInstallerSession)
        ];
        foreach (Type presentation in new[] { typeof(PlanReviewSnapshot), typeof(PlanReviewResult), typeof(PlanReviewPlan), typeof(PlanReviewRejection) })
        {
            presentation.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(property => property.PropertyType)
                .Should().NotContain(type => authorityTypes.Contains(type));
            presentation.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(field => field.FieldType)
                .Should().NotContain(type => authorityTypes.Contains(type));
        }

        typeof(InstallerExecutionOperation).GetProperties().Select(property => property.Name).Should().BeEquivalentTo([
            nameof(InstallerExecutionOperation.Progress),
            nameof(InstallerExecutionOperation.Completion)
        ]);
        typeof(InstallerExecutionOperation).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().Equal(nameof(InstallerExecutionOperation.RequestCancellationAsync));
        foreach (Type projection in new[]
        {
            typeof(InstallerExecutionProgress),
            typeof(InstallerExecutionTerminalResult),
            typeof(InstallerExecutionStateUnknownResult),
            typeof(InstallerExecutionSummary)
        })
        {
            projection.GetProperties().Should().NotContain(property =>
                property.PropertyType == typeof(string)
                || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || new[] { "Path", "LogPath", "SanitizedLogPath" }.Contains(property.Name));
        }
        typeof(InstallerRecoveryOperation).GetProperties().Select(property => property.Name).Should().BeEquivalentTo([
            nameof(InstallerRecoveryOperation.Progress),
            nameof(InstallerRecoveryOperation.Completion)
        ]);
        typeof(InstallerRecoveryOperation).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Should().BeEmpty("the recovery protocol has no cancellation command");
        foreach (Type projection in new[]
        {
            typeof(InstallerRecoveryProgress),
            typeof(InstallerRecoveryTerminalResult),
            typeof(InstallerRecoveryStateUnknownResult),
            typeof(InstallerRecoveryAttemptSummary)
        })
        {
            projection.GetProperties().Should().NotContain(property =>
                property.PropertyType == typeof(string)
                || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) && property.Name != nameof(InstallerRecoveryAttemptSummary.RecoveredPathCount)
                || property.Name.Contains("Generation", StringComparison.OrdinalIgnoreCase) && property.Name != nameof(InstallerRecoveryAttemptSummary.OperationGenerationAdvanced));
        }
    }

    [Test]
    public async Task CorrelatesDiscoveryAndManualValidationWithinTheAuthenticatedSession()
    {
        const string selectedPath = "/games/Stardew Valley";
        ProtocolGameCandidate discovered = new(selectedPath, LinuxGameFolderStatus.Valid, "Stardew Valley 1.6.15");
        ProtocolGameCandidate validatedCandidate = new("/real-games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley 1.6.15");
        ProtocolGameCandidate missing = new("/games/missing", LinuxGameFolderStatus.MissingDirectory, "Stardew Valley folder (missing)");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            DiscoverGamesRequest => Serialize(new GameDiscoveryEvent(Session, [discovered, missing]) { CommandId = request.CommandId }),
            ValidateGameRequest validate when validate.GamePath == selectedPath => Serialize(new GameValidationEvent(Session, validatedCandidate) { CommandId = request.CommandId }),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        IReadOnlyList<ProtocolGameCandidate> candidates = await client.DiscoverGamesAsync();
        ProtocolGameCandidate validated = await client.ValidateGameAsync(selectedPath);

        candidates.Should().Equal(discovered, missing);
        candidates.Should().BeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<ProtocolGameCandidate>>();
        validated.Should().Be(validatedCandidate, "safe backend anchoring may resolve a selected symlink to another canonical path");
        process.Requests.Select(request => request.Kind).Should().Equal(
            ProtocolMessageKind.HandshakeRequest,
            ProtocolMessageKind.DiscoverGamesRequest,
            ProtocolMessageKind.ValidateGameRequest
        );
    }

    [Test]
    public async Task RecoveryUsesExactCandidateRoutesBoundedProgressAndReturnsSanitizedExactTerminal()
    {
        const string privatePath = "/home/private-user/Stardew Valley";
        ProtocolGameCandidate issued = new(privatePath, LinuxGameFolderStatus.Valid, "hostile private display ;$[]");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, issued) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(
                new RecoveryProgressEvent(Session, 4, TransactionStage.Recovering, 7, 10, "private progress detail") { CommandId = recovery.CommandId },
                CreateRecoveryCompleted(recovery, privatePath, namedRootStillSelected: true)
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(privatePath);

        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate);
        InstallerRecoveryResult result = await operation.Completion;

        InstallerRecoveryTerminalResult terminal = result.Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;
        terminal.Should().BeEquivalentTo(new InstallerRecoveryTerminalResult(
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
            ProtocolDurableState.RecoveryCompleted,
            null,
            ProtocolRecoveryDisposition.Completed,
            ProtocolNextAction.InspectAgain,
            new(true, true, 1, 7),
            InstallerBackendSettlement.ConfirmedClosed
        ));
        List<InstallerRecoveryProgress> observedProgress = [];
        await foreach (InstallerRecoveryProgress progress in operation.Progress.ReadAllAsync())
            observedProgress.Add(progress);
        observedProgress.Should().Equal(new InstallerRecoveryProgress(TransactionStage.Recovering, 7, 10));
        terminal.ToString().Should().NotContain(privatePath).And.NotContain("private progress").And.NotContain("private log");
        process.Requests.Select(request => request.Kind).Should().Equal(
            ProtocolMessageKind.HandshakeRequest,
            ProtocolMessageKind.ValidateGameRequest,
            ProtocolMessageKind.RecoverInterruptedRequest
        );
        await FluentActions.Awaiting(() => client.DiscoverGamesAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task RecoveryRejectsReconstructedStaleForeignAndInvalidCandidatesWithoutConsumingCurrentAuthority()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate firstSource = new(path, LinuxGameFolderStatus.Valid, "first");
        ProtocolGameCandidate secondSource = new(path, LinuxGameFolderStatus.Valid, "second");
        int validations = 0;
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, ++validations == 1 ? firstSource : secondSource) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, namedRootStillSelected: false)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate stale = await client.ValidateGameAsync(path);
        ProtocolGameCandidate current = await client.ValidateGameAsync(path);
        ProtocolGameCandidate reconstructed = current with { };
        ProtocolGameCandidate invalid = new(path, LinuxGameFolderStatus.MissingGameAssembly, "invalid");

        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(stale)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(reconstructed)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(invalid)).Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);

        InstallerRecoveryOperation admitted = await client.RecoverInterruptedAsync(current);
        (await admitted.Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
        process.Requests.OfType<RecoverInterruptedRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task RecoveryRejectsCandidateFromAnotherAuthenticatedClientWithoutWire()
    {
        const string path = "/games/Stardew Valley";
        static byte[] Respond(ProtocolRequest request) => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, new(path, LinuxGameFolderStatus.Valid, "valid")) { CommandId = request.CommandId }),
            _ => throw new AssertionException("Unexpected protocol request.")
        };
        ScriptedProcess firstProcess = new(Respond);
        ScriptedProcess secondProcess = new(Respond);
        await using ProcessInstallerProtocolClient first = Create(firstProcess);
        await using ProcessInstallerProtocolClient second = Create(secondProcess);
        await first.HandshakeAsync("SMAPI GUI", "1");
        await second.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate foreign = await first.ValidateGameAsync(path);
        _ = await second.ValidateGameAsync(path);

        await FluentActions.Awaiting(() => second.RecoverInterruptedAsync(foreign)).Should().ThrowAsync<ArgumentException>();
        secondProcess.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);
    }

    [Test]
    public async Task RecoveryCancellationIsAdmissionOnlyAndNeverSendsProtocolCancellation()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, namedRootStillSelected: true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        using CancellationTokenSource beforeAdmission = new();
        client.BeforeRecoveryAdmissionForTesting = beforeAdmission.Cancel;
        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate, beforeAdmission.Token)).Should().ThrowAsync<OperationCanceledException>();
        process.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);

        using CancellationTokenSource afterAdmission = new();
        client.BeforeRecoveryAdmissionForTesting = null;
        client.BeforeRecoveryWriteForTesting = afterAdmission.Cancel;
        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate, afterAdmission.Token);
        (await operation.Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
        process.Requests.Should().NotContain(request => request is CancelPlanRequest);
    }

    [Test]
    public async Task RecoveryRejectsPackageWorkflowHistoryWithoutWire()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => Serialize(CreateOpened(Session, request.CommandId)),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        _ = await client.OpenPackageAsync(CreatePackage());
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate)).Should().ThrowAsync<InvalidOperationException>();
        process.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);
    }

    [TestCase(ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery, ProtocolDurableState.Unchanged, null)]
    [TestCase(ProtocolInterruptedRecoveryOutcome.PartialFailure, ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.IoFailure)]
    [TestCase(ProtocolInterruptedRecoveryOutcome.UnexpectedFailure, ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure)]
    public async Task RecoveryProjectsEachExactFailureTuple(
        ProtocolInterruptedRecoveryOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolTerminalErrorCode? errorCode
    )
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryFailure(recovery, path, outcome, errorCode)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryTerminalResult terminal = (await (await client.RecoverInterruptedAsync(candidate)).Completion)
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        terminal.Outcome.Should().Be(outcome);
        terminal.DurableState.Should().Be(durableState);
        terminal.ErrorCode.Should().Be(errorCode);
        terminal.RecoveryDisposition.Should().Be(ProtocolRecoveryDisposition.InterruptedRecoveryRequired);
        terminal.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        if (outcome == ProtocolInterruptedRecoveryOutcome.PartialFailure)
            terminal.Attempt.Should().BeEquivalentTo(new InstallerRecoveryAttemptSummary(null, null, 1, 7));
        else
            terminal.Attempt.Should().BeNull("this terminal has no exact attempt");
        terminal.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
    }

    [Test]
    public async Task RecoveryWrongAttemptPathFailsClosedAsUnknown()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, "/games/other", namedRootStillSelected: true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        process.Terminated.Should().BeTrue();
        client.SessionFaulted.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task RecoveryAcceptsIncreasingSequenceGapsButRejectsDuplicateSequence()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        bool duplicate = false;
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery when !duplicate => SerializeMany(
                new RecoveryProgressEvent(Session, 2, TransactionStage.Recovering, 1, 2, "first") { CommandId = recovery.CommandId },
                new RecoveryProgressEvent(Session, 9, TransactionStage.Recovering, 2, 2, "gap") { CommandId = recovery.CommandId },
                CreateRecoveryCompleted(recovery, path, namedRootStillSelected: true)
            ),
            RecoverInterruptedRequest recovery => SerializeMany(
                new RecoveryProgressEvent(Session, 3, TransactionStage.Recovering, 1, 2, "first") { CommandId = recovery.CommandId },
                new RecoveryProgressEvent(Session, 3, TransactionStage.Recovering, 2, 2, "duplicate") { CommandId = recovery.CommandId }
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using (ProcessInstallerProtocolClient client = Create(process))
        {
            await client.HandshakeAsync("SMAPI GUI", "1");
            ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
            (await (await client.RecoverInterruptedAsync(candidate)).Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
        }

        duplicate = true;
        ScriptedProcess duplicateProcess = new(process.Responder);
        await using ProcessInstallerProtocolClient duplicateClient = Create(duplicateProcess);
        await duplicateClient.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate duplicateCandidate = await duplicateClient.ValidateGameAsync(path);
        InstallerRecoveryResult result = await (await duplicateClient.RecoverInterruptedAsync(duplicateCandidate)).Completion;
        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        duplicateProcess.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task RecoveryProgressEventBoundRejectsNPlusOneConservatively()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(
                new RecoveryProgressEvent(Session, 0, TransactionStage.Recovering, 0, 2, "one") { CommandId = recovery.CommandId },
                new RecoveryProgressEvent(Session, 1, TransactionStage.Recovering, 1, 2, "two") { CommandId = recovery.CommandId },
                CreateRecoveryCompleted(recovery, path, namedRootStillSelected: true)
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        client.RecoveryProgressCapacityForTesting = 1;
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task RecoveryProgressEventBoundAcceptsExactN()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(
                new RecoveryProgressEvent(Session, 5, TransactionStage.Recovering, 1, 1, "one") { CommandId = recovery.CommandId },
                CreateRecoveryCompleted(recovery, path, namedRootStillSelected: true)
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        client.RecoveryProgressCapacityForTesting = 1;
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        (await (await client.RecoverInterruptedAsync(candidate)).Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
    }

    [TestCase("wrong-session")]
    [TestCase("wrong-command")]
    [TestCase("wrong-family")]
    public async Task RecoveryRejectsIncorrectlyCorrelatedFramesConservatively(string fault)
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => fault switch
            {
                "wrong-session" => Serialize(CreateRecoveryCompleted(recovery, path, true) with { SessionId = ProtocolSessionId.CreateRandom() }),
                "wrong-command" => Serialize(CreateRecoveryCompleted(recovery, path, true) with { CommandId = ProtocolCommandId.CreateRandom() }),
                "wrong-family" => Serialize(new GameDiscoveryEvent(Session, []) { CommandId = recovery.CommandId }),
                _ => throw new AssertionException("Unknown fault.")
            },
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        client.SessionFaulted.IsCompleted.Should().BeTrue();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RecoveryHardAndIdleDeadlinesReturnConservativeUnknown(bool hardDeadline)
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest => null,
            _ => throw new AssertionException("Unexpected protocol request.")
        }, completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        client.RecoveryHardTimeoutForTesting = hardDeadline ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(1);
        client.RecoveryIdleTimeoutForTesting = hardDeadline ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(20);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion.WaitAsync(TimeSpan.FromSeconds(2));

        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task RecoveryWriteFailureAfterAdmissionReturnsUnknownAndConsumesAuthority()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            _ => throw new AssertionException("Recovery should fail before transport serialization.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        client.BeforeRecoveryWriteForTesting = () => throw new IOException("/home/private/write failure");

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        process.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);
        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task RecoveryFlushFollowedByLocalFailurePreservesAlreadyValidatedTerminalTruth()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        TaskCompletionSource terminalRouted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RecoveryTerminalRoutedForTesting = () => terminalRouted.TrySetResult();
        client.BeforeRecoveryWrittenCommitForTesting = async () =>
        {
            await terminalRouted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new IOException("/home/private/post-flush failure");
        };
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryTerminalResult result = (await (await client.RecoverInterruptedAsync(candidate)).Completion)
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        result.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed, "the exact terminal and clean backend EOF are stronger than a later local test-hook failure");
        process.Requests.OfType<RecoverInterruptedRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task CleanupWinningImmediatelyBeforeRecoveryAdmissionRejectsWithoutRouteOrWire()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            _ => throw new AssertionException("Recovery must not reach the wire.")
        });
        ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        Task? disposal = null;
        client.BeforeRecoveryAdmissionForTesting = () => disposal = client.DisposeAsync().AsTask();

        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate)).Should().ThrowAsync<ObjectDisposedException>();
        await disposal!.WaitAsync(TimeSpan.FromSeconds(2));
        process.Requests.Should().NotContain(request => request is RecoverInterruptedRequest);
    }

    [TestCase(640000, true)]
    [TestCase(640001, false)]
    public async Task RecoveryProgressUnitBoundHasExactNAndNPlusOneBehavior(int units, bool accepted)
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(
                new RecoveryProgressEvent(Session, 0, TransactionStage.Recovering, units, units, "bounded") { CommandId = recovery.CommandId },
                CreateRecoveryCompleted(recovery, path, true)
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        if (accepted)
            result.Should().BeOfType<InstallerRecoveryTerminalResult>();
        else
            result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RecoveryAggregateByteBoundHasExactNAndNPlusOneBehavior(bool addOneFrame)
    {
        const string path = "/games/Stardew Valley";
        const int frameCount = 20;
        string message = new('x', 4000);
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        static ProtocolEvent[] Frames(RecoverInterruptedRequest recovery, string gamePath, string text, int count)
        {
            List<ProtocolEvent> frames = [];
            for (int index = 0; index < count; index++)
                frames.Add(new RecoveryProgressEvent(Session, index, TransactionStage.Recovering, index, count, text) { CommandId = recovery.CommandId });
            frames.Add(CreateRecoveryCompleted(recovery, gamePath, true));
            return frames.ToArray();
        }
        RecoverInterruptedRequest sizingRequest = new(Session, path);
        long exactBytes = SerializeMany(Frames(sizingRequest, path, message, frameCount)).LongLength;
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(Frames(recovery, path, message, frameCount + (addOneFrame ? 1 : 0))),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        client.RecoveryProgressByteCapacityForTesting = exactBytes;
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryResult result = await (await client.RecoverInterruptedAsync(candidate)).Completion;

        if (addOneFrame)
            result.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        else
            result.Should().BeOfType<InstallerRecoveryTerminalResult>();
    }

    [Test]
    public async Task RecoveryAcceptsExactDiscoveryIssuedCandidate()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            DiscoverGamesRequest => Serialize(new GameDiscoveryEvent(Session, [source]) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = (await client.DiscoverGamesAsync()).Single();

        (await (await client.RecoverInterruptedAsync(candidate)).Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
    }

    [Test]
    public async Task RecoveryBufferedTrailingFramePreservesExactTerminalButMarksSettlementUnconfirmed()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => SerializeMany(
                CreateRecoveryCompleted(recovery, path, true),
                new GameDiscoveryEvent(Session, []) { CommandId = ProtocolCommandId.CreateRandom() }
            ),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryTerminalResult result = (await (await client.RecoverInterruptedAsync(candidate)).Completion)
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        result.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        client.SessionFaulted.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task DisposeDuringActiveRecoverySettlesUnknownAndBlocksEveryFurtherCommand()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest => null,
            _ => throw new AssertionException("Unexpected protocol request.")
        }, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate);

        await client.DisposeAsync();

        (await operation.Completion).Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => client.DiscoverGamesAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task ExactRecoveryFailureCanOnlyBeRetriedOnFreshClientWithFreshCandidate()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess firstProcess = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryFailure(recovery, path, ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery, null)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient first = Create(firstProcess);
        await first.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate firstCandidate = await first.ValidateGameAsync(path);
        (await (await first.RecoverInterruptedAsync(firstCandidate)).Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
        await FluentActions.Awaiting(() => first.RecoverInterruptedAsync(firstCandidate)).Should().ThrowAsync<ObjectDisposedException>();

        ScriptedProcess secondProcess = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient second = Create(secondProcess);
        await second.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate secondCandidate = await second.ValidateGameAsync(path);
        (await (await second.RecoverInterruptedAsync(secondCandidate)).Completion).Should().BeOfType<InstallerRecoveryTerminalResult>();
    }

    [Test]
    public async Task LateUnbufferedFramePreservesExactRecoveryTerminalAndMarksSettlementUnconfirmed()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        TaskCompletionSource settlementEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeRecoverySettlementForTesting = async () =>
        {
            settlementEntered.TrySetResult();
            await releaseSettlement.Task;
        };
        client.RecoveryTerminalRoutedForTesting = () => process.Publish(Serialize(
            new GameDiscoveryEvent(Session, []) { CommandId = ProtocolCommandId.CreateRandom() }
        ));
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate);
        await settlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSettlement.TrySetResult();
        InstallerRecoveryTerminalResult terminal = (await operation.Completion)
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        terminal.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted);
        terminal.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        client.SessionFaulted.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task RecoveryConfirmedProcessExitStillDrainsPipeBufferedFramesBeforePublishingSettlement()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        TaskCompletionSource secondChunkRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecondChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int gateNextChunk = 0;
        ScriptedProcess process = new(
            request => request switch
            {
                HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
                ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
                RecoverInterruptedRequest => null,
                _ => throw new AssertionException("Unexpected protocol request.")
            },
            completeWaitInitially: false,
            beforeResponseChunk: async _ =>
            {
                if (Interlocked.Exchange(ref gateNextChunk, 0) != 0)
                {
                    secondChunkRead.TrySetResult();
                    await releaseSecondChunk.Task;
                }
            }
        );
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate);
        RecoverInterruptedRequest recovery = process.Requests.OfType<RecoverInterruptedRequest>().Single();
        TaskCompletionSource routed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RecoveryTerminalRoutedForTesting = () => routed.TrySetResult();
        process.Publish(Serialize(CreateRecoveryCompleted(recovery, path, true)));
        await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref gateNextChunk, 1);
        process.Publish(Serialize(new RecoveryProgressEvent(Session, 1, TransactionStage.Recovering, 1, 1, "pipe buffered") { CommandId = recovery.CommandId }));
        await secondChunkRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.CompleteExit();

        operation.Completion.IsCompleted.Should().BeFalse("stdout must reach EOF before settlement is published");
        releaseSecondChunk.TrySetResult();

        InstallerRecoveryTerminalResult result = (await operation.Completion).Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
    }

    [Test]
    public async Task RecoveryTerminalWithUnconfirmedReapPreservesExactOutcomeAndReportsUnconfirmedSettlement()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        }, completeWaitInitially: false, completeExitOnTerminate: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(30));
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);

        InstallerRecoveryTerminalResult terminal = (await (await client.RecoverInterruptedAsync(candidate)).Completion)
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        terminal.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted);
        terminal.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        client.CleanupConfirmed.Should().BeFalse();
        process.Terminated.Should().BeTrue();
        process.CompleteExit();
        await SpinWaitUntilAsync(() => process.Disposed);
    }

    [Test]
    public async Task ActiveRecoveryRejectsSecondRecoveryAndOrdinaryCommandsWithoutAdditionalWire()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest => null,
            _ => throw new AssertionException("No ordinary or second recovery command may reach the wire.")
        }, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(50));
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        InstallerRecoveryOperation active = await client.RecoverInterruptedAsync(candidate);

        await FluentActions.Awaiting(() => client.RecoverInterruptedAsync(candidate)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => client.DiscoverGamesAsync()).Should().ThrowAsync<InvalidOperationException>();
        process.Requests.OfType<RecoverInterruptedRequest>().Should().ContainSingle();

        await client.DisposeAsync();
        (await active.Completion).Should().BeOfType<InstallerRecoveryStateUnknownResult>();
    }

    [Test]
    public async Task DisposeDuringExactRecoverySettlementPreservesTerminalAndMarksSettlementUnconfirmed()
    {
        const string path = "/games/Stardew Valley";
        ProtocolGameCandidate source = new(path, LinuxGameFolderStatus.Valid, "valid");
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            ValidateGameRequest => Serialize(new GameValidationEvent(Session, source) { CommandId = request.CommandId }),
            RecoverInterruptedRequest recovery => Serialize(CreateRecoveryCompleted(recovery, path, true)),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        ProcessInstallerProtocolClient client = Create(process);
        TaskCompletionSource settlementEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeRecoverySettlementForTesting = async () =>
        {
            settlementEntered.TrySetResult();
            await releaseSettlement.Task;
        };
        await client.HandshakeAsync("SMAPI GUI", "1");
        ProtocolGameCandidate candidate = await client.ValidateGameAsync(path);
        InstallerRecoveryOperation operation = await client.RecoverInterruptedAsync(candidate);
        await settlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposal = client.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSettlement.TrySetResult();
        InstallerRecoveryTerminalResult terminal = (await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2)))
            .Should().BeOfType<InstallerRecoveryTerminalResult>().Subject;

        terminal.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted);
        terminal.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        process.Requests.OfType<RecoverInterruptedRequest>().Should().ContainSingle();
        process.Disposed.Should().BeTrue();
    }

    [TestCase(InstallerOperation.Install, true)]
    [TestCase(InstallerOperation.Update, true)]
    [TestCase(InstallerOperation.Repair, true)]
    [TestCase(InstallerOperation.Uninstall, false)]
    [TestCase(InstallerOperation.Backup, false)]
    public async Task InspectPlanUsesExactAuthorityCompositionAndProjectsAZeroItemPlan(InstallerOperation operation, bool expectsPackage)
    {
        ReadOnlyPlanScript script = new(operation);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanSuccess result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, operation))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        InspectPlanRequest request = process.Requests.OfType<InspectPlanRequest>().Single();
        request.SessionId.Should().Be(Session);
        request.GamePath.Should().Be(ReadOnlyPlanScript.GamePath);
        request.Operation.Should().Be(operation);
        request.PackageId.HasValue.Should().Be(expectsPackage);
        if (expectsPackage)
            request.PackageId.Should().Be(ReadOnlyPlanScript.PackageId);
        request.RecoverySelectionId.Should().BeNull();
        process.Requests.Should().NotContain(item => item is GetPlanPageRequest);
        result.Operation.Should().Be(operation);
        result.OperationCounts.Should().BeEmpty();
        result.ConflictCounts.Should().BeEmpty();
        result.CandidateCounts.Should().BeEmpty();
        result.AdditionalNoticeCount.Should().Be(0);
        result.RecommendedDefault.Should().Be(ProtocolRecommendedDefault.Cancel);
        result.SeparateConfirmationRequired.Should().BeTrue();
        result.HasBlockingConflicts.Should().BeFalse();
        result.Confirmation.Should().NotBeNull();
        if (operation == InstallerOperation.Backup)
            result.TargetRelease.Should().Be(result.CurrentRelease);
    }

    [Test]
    public async Task BackupAcceptsTheExactUninstalledNullReleasePair()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup)
        {
            CurrentRelease = null,
            ObservedState = ObservedInstallState.NotInstalled,
            Conflicts = [new(PlanConflictCode.InstalledReceiptRequired, null)],
            Warnings = [$"{PlanConflictCode.InstalledReceiptRequired}."]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanSuccess result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        result.ObservedState.Should().Be(ObservedInstallState.NotInstalled);
        result.CurrentRelease.Should().BeNull();
        result.TargetRelease.Should().BeNull();
        result.HasBlockingConflicts.Should().BeTrue();
        result.Confirmation.Should().BeNull("blocked plans must never carry confirmation authority");
        result.ConflictCounts.Should().Equal(new InstallerPlanConflictCount(PlanConflictCode.InstalledReceiptRequired, 1));
        result.AdditionalNoticeCount.Should().Be(1);
    }

    [Test]
    public async Task ConfirmPlanConsumesOnlyTheExactCurrentCapabilityAndValidatesTheExactAcknowledgement()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Uninstall);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Uninstall);

        InstallerPlanConfirmation foreign = new();
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(foreign)).Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().NotContain(request => request is ConfirmPlanRequest);

        InstallerConfirmedPlanAuthority confirmed = await client.ConfirmPlanAsync(plan.Confirmation!);

        confirmed.Should().NotBeNull();
        ConfirmPlanRequest request = process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle().Subject;
        request.SessionId.Should().Be(Session);
        request.PlanId.Should().Be(script.PlanId);
        request.PlanDigest.Should().Be(script.PlanDigest);
        request.PlanDigest.Should().NotBe(script.ExecutionDigest, "the private execution binding is never public confirmation authority");
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Uninstall)).Should().ThrowAsync<InvalidOperationException>();
        process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task ConcurrentConfirmationConsumesAuthorityOnceAndSendsOneRequest()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(InstallerConfirmedPlanAuthority? Authority, Exception? Error)> AttemptAsync()
        {
            await start.Task;
            try { return (await client.ConfirmPlanAsync(plan.Confirmation!), null); }
            catch (Exception error) { return (null, error); }
        }

        Task<(InstallerConfirmedPlanAuthority? Authority, Exception? Error)>[] attempts = [AttemptAsync(), AttemptAsync()];
        start.TrySetResult();
        (InstallerConfirmedPlanAuthority? Authority, Exception? Error)[] results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(result => result.Authority != null && result.Error == null);
        results.Should().ContainSingle(result => result.Authority == null && result.Error != null && result.Error.GetType() == typeof(InvalidOperationException));
        process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task FreshInspectionRevokesThePriorConfirmationReferenceWithoutAProtocolCall()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess first = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);
        InstallerReadOnlyPlanSuccess current = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);

        first.Confirmation.Should().NotBeSameAs(current.Confirmation);
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(first.Confirmation!)).Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().NotContain(request => request is ConfirmPlanRequest);

        (await client.ConfirmPlanAsync(current.Confirmation!)).Should().NotBeNull();
        process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task CandidateReplacementRevokesThePriorConfirmationAndConfirmsOnlyTheReplacementBinding()
    {
        ProtocolPlanCandidate candidate = CreateCandidate('4', "mods/reissue.dll", false);
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [candidate],
            ReplacementCandidates = []
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess first = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);
        InstallerReadOnlyPlanSuccess replacement = (InstallerReadOnlyPlanSuccess)await client.ApprovePlanCandidatesAsync([first.Candidates.Single()]);

        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(first.Confirmation!)).Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().NotContain(request => request is ConfirmPlanRequest);
        await client.ConfirmPlanAsync(replacement.Confirmation!);

        ConfirmPlanRequest confirmation = process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle().Subject;
        confirmation.PlanId.Should().Be(script.PlanId);
        confirmation.PlanDigest.Should().Be(script.PlanDigest);
    }

    [TestCase(ConfirmationAcknowledgementFault.WrongSession)]
    [TestCase(ConfirmationAcknowledgementFault.WrongPlan)]
    [TestCase(ConfirmationAcknowledgementFault.WrongKind)]
    [TestCase(ConfirmationAcknowledgementFault.PruneAuthority)]
    [TestCase(ConfirmationAcknowledgementFault.WrongCommand)]
    public async Task InvalidConfirmationAcknowledgementFailStopsWithoutPublishingAuthority(ConfirmationAcknowledgementFault fault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup) { ConfirmationFault = fault };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);

        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<InstallerProtocolClientException>();

        process.Terminated.Should().BeTrue();
        client.CleanupConfirmed.Should().BeTrue();
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task CancellationAtConfirmationPrecommitRevokesAuthorityAndStopsTheBackend()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);
        using CancellationTokenSource cancellation = new();
        client.BeforeConfirmationAuthorityCommitForTesting = cancellation.Cancel;

        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!, cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        process.Terminated.Should().BeTrue();
        process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle();
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FaultOrObserverFailureAtConfirmationPrecommitNeverPublishesAuthority(bool sessionFault)
    {
        const string privateFailure = "/home/private-user/confirmation-hook";
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);
        client.BeforeConfirmationAuthorityCommitForTesting = () =>
        {
            if (!sessionFault)
                throw new InvalidOperationException(privateFailure);
            process.CompleteOutput();
            if (!client.SessionFaulted.Wait(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("The scripted session fault did not arrive.");
        };

        InstallerProtocolClientException failure = (await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!))
            .Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        failure.Message.Should().NotContain(privateFailure);
        process.Terminated.Should().BeTrue();
        process.Requests.OfType<ConfirmPlanRequest>().Should().ContainSingle();
        await FluentActions.Awaiting(() => client.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task ExecuteRoutesExactBoundedProgressAndPublishesOnlyTypedTerminalData()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request switch
        {
            ExecutePlanRequest => null,
            _ => script.Respond(request)
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest request = process.Requests.OfType<ExecutePlanRequest>().Single();
        ProgressEvent progress = new(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Staging, 0, null, "/home/private/progress") { CommandId = request.CommandId };
        SuccessEvent terminal = Success(script, request.CommandId, "/home/private/summary", "/home/private/log");
        process.Publish(SerializeMany(progress, terminal));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        List<InstallerExecutionProgress> observed = [];
        await foreach (InstallerExecutionProgress value in execution.Progress.ReadAllAsync())
            observed.Add(value);

        observed.Should().Equal(new InstallerExecutionProgress(TransactionStage.Staging, 0, null));
        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.DurableState.Should().Be(ProtocolDurableState.Committed);
        result.NextAction.Should().Be(ProtocolNextAction.InspectAgain);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
        string[] publicNames = typeof(InstallerExecutionTerminalResult).GetProperties().Select(property => property.Name).ToArray();
        publicNames.Should().NotContain(name => name.Contains("Message", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Id", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task CancellationUsesOneCorrelatedAckLaneAndLateSuccessRemainsAuthoritative()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request switch
        {
            ExecutePlanRequest => null,
            CancelPlanRequest cancel => Serialize(new CommandAcknowledgedEvent(Session, ProtocolAcknowledgementKind.PlanCancellationRequested, script.PlanId, null) { CommandId = cancel.CommandId }),
            _ => script.Respond(request)
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        Task first = execution.RequestCancellationAsync();
        Task second = execution.RequestCancellationAsync();
        await Task.WhenAll(first, second);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        process.Publish(Serialize(Success(script, execute.CommandId, "late success", null)));

        (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>()
            .Which.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        process.Requests.OfType<CancelPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task DuplicateProgressSequenceFailStopsToConservativeRecoveryRequiredResult()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();

        process.Publish(SerializeMany(
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 1, TransactionStage.Staging, 0, null, "first") { CommandId = execute.CommandId },
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 1, TransactionStage.Staging, 0, null, "duplicate") { CommandId = execute.CommandId }
        ));

        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;
        result.DurableState.Should().Be(ProtocolDurableState.Unknown);
        result.ErrorCode.Should().BeNull("local transport uncertainty isn't a backend-reported core failure");
        result.RecoveryDisposition.Should().Be(ProtocolRecoveryDisposition.InterruptedRecoveryRequired);
        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task ProgressSequenceUsesTheProtocolMonotonicContractAndAllowsGaps()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();

        process.Publish(SerializeMany(
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 4, TransactionStage.Staging, 0, null, "first") { CommandId = execute.CommandId },
            new ProgressEvent(
                Session,
                script.PlanId,
                script.PlanDigest,
                10,
                TransactionStage.Applying,
                ProcessInstallerProtocolClient.MaximumExecutionProgressUnits,
                ProcessInstallerProtocolClient.MaximumExecutionProgressUnits,
                "gap at exact unit bound"
            )
            { CommandId = execute.CommandId },
            Success(script, execute.CommandId, "complete", null)
        ));

        (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>()
            .Which.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
    }

    [Test]
    public async Task ConfirmedAuthorityExecutesOnlyOnceAndForeignReferenceNeverWrites()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        await FluentActions.Awaiting(() => client.ExecutePlanAsync(new InstallerConfirmedPlanAuthority())).Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().NotContain(request => request is ExecutePlanRequest);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        await FluentActions.Awaiting(() => client.ExecutePlanAsync(authority)).Should().ThrowAsync<InvalidOperationException>();
        process.Requests.OfType<ExecutePlanRequest>().Should().ContainSingle();
        process.CompleteOutput();
        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
    }

    [Test]
    public async Task TerminalBeforeCancellationAdmissionSendsNoLateCancelRequest()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource terminalRouted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ExecutionTerminalRoutedForTesting = () => terminalRouted.TrySetResult();
        client.BeforeExecuteWrittenCommitForTesting = async () =>
        {
            cancellation.Cancel();
            ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
            process.Publish(Serialize(Success(script, execute.CommandId, "terminal won", null)));
            await terminalRouted.Task;
        };

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority, cancellation.Token);
        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;

        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        process.Requests.Should().NotContain(request => request is CancelPlanRequest);
    }

    [Test]
    public async Task TerminalMayPrecedeItsExactCancellationAcknowledgement()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest or CancelPlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        Task cancellation = execution.RequestCancellationAsync();
        await SpinWaitUntilAsync(() => process.Requests.OfType<CancelPlanRequest>().Any());
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        CancelPlanRequest cancel = process.Requests.OfType<CancelPlanRequest>().Single();
        TaskCompletionSource terminalRouted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ExecutionTerminalRoutedForTesting = () => terminalRouted.TrySetResult();
        process.Publish(Serialize(Success(script, execute.CommandId, "terminal first", null)));
        await terminalRouted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task repeated = execution.RequestCancellationAsync();
        ReferenceEquals(repeated, cancellation).Should().BeTrue("repeated cancellation must retain the original settlement task");
        process.Publish(Serialize(
            new CommandAcknowledgedEvent(Session, ProtocolAcknowledgementKind.PlanCancellationRequested, script.PlanId, null) { CommandId = cancel.CommandId }
        ));

        await Task.WhenAll(cancellation, repeated);
        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
    }

    [Test]
    public async Task ExactTerminalSurvivesWrongLateCancellationAckButMarksSettlementUnconfirmed()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest or CancelPlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        Task cancellation = execution.RequestCancellationAsync();
        await SpinWaitUntilAsync(() => process.Requests.OfType<CancelPlanRequest>().Any());
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        CancelPlanRequest cancel = process.Requests.OfType<CancelPlanRequest>().Single();
        process.Publish(SerializeMany(
            Success(script, execute.CommandId, "terminal exact", null),
            new CommandAcknowledgedEvent(Session, ProtocolAcknowledgementKind.PlanConfirmed, script.PlanId, null) { CommandId = cancel.CommandId }
        ));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        Exception cancellationError = (await FluentActions.Awaiting(() => cancellation).Should().ThrowAsync<Exception>()).Which;
        (cancellationError is InstallerProtocolClientException or OperationCanceledException).Should().BeTrue();
    }

    [Test]
    public async Task LateFrameAfterExactTerminalCannotEraseTruthAndMarksSettlementUnconfirmed()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request), completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        TaskCompletionSource routed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ExecutionTerminalRoutedForTesting = () => routed.TrySetResult();
        process.Publish(Serialize(Success(script, execute.CommandId, "exact", null)));
        await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.Publish(Serialize(new ProgressEvent(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Completed, 0, 0, "late") { CommandId = execute.CommandId }));
        await client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(2));
        process.CompleteExit();

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
    }

    [Test]
    public async Task ConfirmedProcessExitStillDrainsPipeBufferedFramesBeforePublishingSettlement()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        TaskCompletionSource secondChunkRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecondChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int gateNextChunk = 0;
        ScriptedProcess process = new(
            request => request is ExecutePlanRequest ? null : script.Respond(request),
            completeWaitInitially: false,
            beforeResponseChunk: async _ =>
            {
                if (Interlocked.Exchange(ref gateNextChunk, 0) != 0)
                {
                    secondChunkRead.TrySetResult();
                    await releaseSecondChunk.Task;
                }
            }
        );
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        TaskCompletionSource routed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ExecutionTerminalRoutedForTesting = () => routed.TrySetResult();
        process.Publish(Serialize(Success(script, execute.CommandId, "exact", null)));
        await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref gateNextChunk, 1);
        process.Publish(Serialize(new ProgressEvent(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Completed, 0, 0, "pipe buffered") { CommandId = execute.CommandId }));
        await secondChunkRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.CompleteExit();

        execution.Completion.IsCompleted.Should().BeFalse("stdout must reach EOF before settlement is published");
        releaseSecondChunk.TrySetResult();

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
    }

    [Test]
    public async Task BufferedFrameAfterExactTerminalMarksSettlementBeforePublishingTerminal()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();

        process.Publish(SerializeMany(
            Success(script, execute.CommandId, "exact", null),
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Completed, 0, 0, "buffered late") { CommandId = execute.CommandId }
        ));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PostTerminalExecutionFramesAreRejectedWhileCancellationAcknowledgementIsPending(bool duplicateTerminal)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest or CancelPlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        Task cancellation = execution.RequestCancellationAsync();
        await SpinWaitUntilAsync(() => process.Requests.OfType<CancelPlanRequest>().Any());
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        TaskCompletionSource routed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ExecutionTerminalRoutedForTesting = () => routed.TrySetResult();
        process.Publish(Serialize(Success(script, execute.CommandId, "exact", null)));
        await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ProtocolEvent extra = duplicateTerminal
            ? Success(script, execute.CommandId, "duplicate", null)
            : new ProgressEvent(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Completed, 0, 0, "late") { CommandId = execute.CommandId };
        process.Publish(Serialize(extra));
        await client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(2));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        await FluentActions.Awaiting(() => cancellation).Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task ProgressFloodBoundFailsClosedAndOrdinaryCommandsNeverOverlapExecution()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        client.ExecutionProgressCapacityForTesting = 1;
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        int writes = process.Requests.Count;
        await FluentActions.Awaiting(() => client.DiscoverGamesAsync()).Should().ThrowAsync<InvalidOperationException>();
        process.Requests.Should().HaveCount(writes);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        process.Publish(SerializeMany(
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 0, TransactionStage.Recovering, 0, 1, "first") { CommandId = execute.CommandId },
            new ProgressEvent(Session, script.PlanId, script.PlanDigest, 1, TransactionStage.Recovering, 1, 1, "overflow") { CommandId = execute.CommandId }
        ));

        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExecutionAggregateWireByteBoundAcceptsExactCapacityAndRejectsOneByteLess(bool oneByteLess)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ProtocolEvent[] Transcript(ProtocolCommandId commandId) =>
        [
            .. Enumerable.Range(0, 256).Select(index => (ProtocolEvent)new ProgressEvent(
                Session,
                script.PlanId,
                script.PlanDigest,
                index,
                TransactionStage.Staging,
                0,
                null,
                new string('x', 256)
            ) { CommandId = commandId }),
            Success(script, commandId, "complete", null)
        ];

        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        int exactBytes = Transcript(ProtocolCommandId.CreateRandom()).Sum(frame => Serialize(frame).Length);
        exactBytes.Should().BeGreaterThan(ProtocolJsonSerializer.MaxLineBytes);
        client.ExecutionProgressByteCapacityForTesting = exactBytes - (oneByteLess ? 1 : 0);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();

        process.Publish(SerializeMany(Transcript(execute.CommandId)));

        InstallerExecutionResult result = await execution.Completion;
        if (oneByteLess)
            result.Should().BeOfType<InstallerExecutionStateUnknownResult>();
        else
            result.Should().BeOfType<InstallerExecutionTerminalResult>()
                .Which.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
    }

    [Test]
    public async Task ExecutionHardDeadlineReturnsConservativeUnknown()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        client.ExecutionHardTimeoutForTesting = TimeSpan.Zero;
        client.ExecutionIdleTimeoutForTesting = Timeout.InfiniteTimeSpan;
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task ExecutionIdleDeadlineReturnsConservativeUnknown()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        client.ExecutionHardTimeoutForTesting = Timeout.InfiniteTimeSpan;
        client.ExecutionIdleTimeoutForTesting = TimeSpan.Zero;
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task CancellationAcknowledgementDeadlineIsBoundedAndSanitized()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest or CancelPlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        client.ExecutionCancellationAcknowledgementTimeoutForTesting = TimeSpan.Zero;
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        InstallerProtocolClientException failure = (await FluentActions.Awaiting(() => execution.RequestCancellationAsync())
            .Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        failure.Message.Should().NotContain("/home/");
        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
    }

    [Test]
    public async Task PostCancellationTerminalDeadlineReturnsConservativeUnknownAfterExactAck()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request switch
        {
            ExecutePlanRequest => null,
            CancelPlanRequest cancel => Serialize(new CommandAcknowledgedEvent(Session, ProtocolAcknowledgementKind.PlanCancellationRequested, script.PlanId, null) { CommandId = cancel.CommandId }),
            _ => script.Respond(request)
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        client.ExecutionHardTimeoutForTesting = Timeout.InfiniteTimeSpan;
        client.ExecutionIdleTimeoutForTesting = Timeout.InfiniteTimeSpan;
        client.ExecutionPostCancellationTimeoutForTesting = TimeSpan.Zero;
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDeadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforePostCancellationDeadlineForTesting = async () =>
        {
            cancellationObserved.TrySetResult();
            await releaseDeadline.Task;
        };
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        await execution.RequestCancellationAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseDeadline.TrySetResult();

        (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>();
        process.Requests.OfType<CancelPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task ExecuteWriteFailureReturnsConservativeUnknownAndNeverSuggestsRetry()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest
            ? throw new IOException("/home/private/write-failure")
            : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;

        result.DurableState.Should().Be(ProtocolDurableState.Unknown);
        result.ErrorCode.Should().BeNull();
        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        result.NextAction.Should().NotBe(ProtocolNextAction.RetryRequest);
        process.Terminated.Should().BeTrue();
        process.Requests.OfType<ExecutePlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task CancellationRequestedBeforeExecuteWriteFailureCannotReportSuccessfulAcknowledgement()
    {
        const string privateFailure = "/home/private/execute-write";
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        using CancellationTokenSource cancellation = new();
        client.BeforeExecutionWriteForTesting = () =>
        {
            cancellation.Cancel();
            throw new IOException(privateFailure);
        };

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority, cancellation.Token);
        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;
        InstallerProtocolClientException failure = (await FluentActions.Awaiting(() => execution.RequestCancellationAsync())
            .Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        failure.Message.Should().NotContain(privateFailure);
        process.Requests.Should().NotContain(request => request is ExecutePlanRequest);
        process.Requests.Should().NotContain(request => request is CancelPlanRequest);
    }

    [Test]
    public async Task CancellationTransportFailureIsSanitizedAndExecutionBecomesConservativelyUnknown()
    {
        const string privateFailure = "/home/private/cancel-write";
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request switch
        {
            ExecutePlanRequest => null,
            CancelPlanRequest => throw new IOException(privateFailure),
            _ => script.Respond(request)
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        InstallerProtocolClientException failure = (await FluentActions.Awaiting(() => execution.RequestCancellationAsync())
            .Should().ThrowAsync<InstallerProtocolClientException>()).Which;
        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;

        failure.Message.Should().NotContain(privateFailure);
        result.ErrorCode.Should().BeNull();
        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        process.Requests.OfType<CancelPlanRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task DisposeDuringAdmittedExecutionSettlesCompletionAsConservativeUnknown()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);

        await client.DisposeAsync();

        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;
        result.RecoveryDisposition.Should().Be(ProtocolRecoveryDisposition.InterruptedRecoveryRequired);
        process.InputDisposed.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task CleanupDisposeFailureCannotEraseExactTerminalTruth()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(
            request => request is ExecutePlanRequest ? null : script.Respond(request),
            faultDispose: true
        );
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        process.Publish(Serialize(Success(script, execute.CommandId, "exact", null)));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;

        result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.Unconfirmed);
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task CleanupDisposeFailureCannotEraseConservativeUnknownAfterWriteFailure()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(
            request => request is ExecutePlanRequest ? throw new IOException("/home/private/write") : script.Respond(request),
            faultDispose: true
        );
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);

        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;

        result.ErrorCode.Should().BeNull();
        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ValidRolledBackFailureProjectsTypedCountsWithoutBackendTextOrLog()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        RolledBackFailureEvent terminal = new(
            Session,
            script.PlanId,
            script.PlanDigest,
            ProtocolExecutionOutcome.FailedAndRolledBack,
            new(ProtocolDurableState.RolledBack, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain),
            new(0, 0, 0, 0, 0, 0),
            "/home/private/error",
            "/home/private/summary",
            "/home/private/log"
        )
        {
            CommandId = execute.CommandId
        };
        process.Publish(Serialize(terminal));

        InstallerExecutionTerminalResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionTerminalResult>().Subject;
        result.Outcome.Should().Be(ProtocolExecutionOutcome.FailedAndRolledBack);
        result.DurableState.Should().Be(ProtocolDurableState.RolledBack);
        result.ErrorCode.Should().Be(ProtocolTerminalErrorCode.IoFailure);
        result.BackendSettlement.Should().Be(InstallerBackendSettlement.ConfirmedClosed);
    }

    [TestCase(ExecutionProtocolFault.WrongProgressBinding)]
    [TestCase(ExecutionProtocolFault.ProgressCounterOverBound)]
    [TestCase(ExecutionProtocolFault.WrongSuccessOperation)]
    [TestCase(ExecutionProtocolFault.ManagedCountOverPlan)]
    [TestCase(ExecutionProtocolFault.UnrequestedCancellationTerminal)]
    public async Task ClientSpecificExecutionProtocolFaultsFailClosed(ExecutionProtocolFault fault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup);
        ScriptedProcess process = new(request => request is ExecutePlanRequest ? null : script.Respond(request));
        await using ProcessInstallerProtocolClient client = Create(process);
        InstallerConfirmedPlanAuthority authority = await PrepareConfirmedPlanAsync(client, script);
        InstallerExecutionOperation execution = await client.ExecutePlanAsync(authority);
        ExecutePlanRequest execute = process.Requests.OfType<ExecutePlanRequest>().Single();
        ProtocolEvent response = fault switch
        {
            ExecutionProtocolFault.WrongProgressBinding => new ProgressEvent(
                Session,
                ProtocolPlanId.CreateRandom(),
                script.PlanDigest,
                0,
                TransactionStage.Staging,
                0,
                null,
                "wrong binding"
            )
            { CommandId = execute.CommandId },
            ExecutionProtocolFault.ProgressCounterOverBound => new ProgressEvent(
                Session,
                script.PlanId,
                script.PlanDigest,
                0,
                TransactionStage.Staging,
                ProcessInstallerProtocolClient.MaximumExecutionProgressUnits + 1,
                ProcessInstallerProtocolClient.MaximumExecutionProgressUnits + 1,
                "over bound"
            )
            { CommandId = execute.CommandId },
            ExecutionProtocolFault.WrongSuccessOperation => Success(script, execute.CommandId, "wrong operation", null) with
            {
                Operation = InstallerOperation.Install
            },
            ExecutionProtocolFault.ManagedCountOverPlan => new SuccessEvent(
                Session,
                script.PlanId,
                script.PlanDigest,
                script.Operation,
                ProtocolExecutionOutcome.Succeeded,
                new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
                new(1, 0, 0, 0, 0, 0),
                "over plan",
                null
            )
            { CommandId = execute.CommandId },
            ExecutionProtocolFault.UnrequestedCancellationTerminal => new CancelledEvent(
                Session,
                script.PlanId,
                script.PlanDigest,
                ProtocolExecutionOutcome.CancelledBeforeMutation,
                new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
                new(0, 0, 0, 0, 0, 0),
                "not requested",
                null
            )
            { CommandId = execute.CommandId },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        process.Publish(Serialize(response));

        InstallerExecutionStateUnknownResult result = (await execution.Completion).Should().BeOfType<InstallerExecutionStateUnknownResult>().Subject;

        result.ErrorCode.Should().BeNull();
        result.NextAction.Should().Be(ProtocolNextAction.RecoverInterrupted);
    }

    [Test]
    public async Task BackupWithoutAReceiptAllowsTheIndependentExactRecoveryCapacityConflict()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup)
        {
            CurrentRelease = null,
            ObservedState = ObservedInstallState.Unknown,
            Conflicts =
            [
                new(PlanConflictCode.InstalledReceiptRequired, null),
                new(PlanConflictCode.RecoveryCapacityReached, null)
            ],
            Warnings =
            [
                $"{PlanConflictCode.InstalledReceiptRequired}.",
                $"{PlanConflictCode.RecoveryCapacityReached}."
            ]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanSuccess result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        result.HasBlockingConflicts.Should().BeTrue();
        result.ConflictCounts.Should().Equal(
            new InstallerPlanConflictCount(PlanConflictCode.InstalledReceiptRequired, 1),
            new InstallerPlanConflictCount(PlanConflictCode.RecoveryCapacityReached, 1)
        );
        result.AdditionalNoticeCount.Should().Be(2);
    }

    [TestCase(BackupWithoutReceiptFault.ExecutableWithoutReceipt)]
    [TestCase(BackupWithoutReceiptFault.ContainsOperation)]
    [TestCase(BackupWithoutReceiptFault.ContainsCandidate)]
    [TestCase(BackupWithoutReceiptFault.WrongConflict)]
    [TestCase(BackupWithoutReceiptFault.ConflictHasPath)]
    [TestCase(BackupWithoutReceiptFault.AdditionalConflict)]
    [TestCase(BackupWithoutReceiptFault.MissingExactNotice)]
    public async Task BackupWithoutAReceiptRejectsForgedExecutableOrNoncanonicalBlockedSemantics(BackupWithoutReceiptFault fault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup)
        {
            CurrentRelease = null,
            ObservedState = ObservedInstallState.NotInstalled,
            Conflicts = [new(PlanConflictCode.InstalledReceiptRequired, null)],
            Warnings = [$"{PlanConflictCode.InstalledReceiptRequired}."]
        };
        switch (fault)
        {
            case BackupWithoutReceiptFault.ExecutableWithoutReceipt:
                script.Conflicts = [];
                script.Warnings = [];
                break;
            case BackupWithoutReceiptFault.ContainsOperation:
                script.Operations = [CreateOperation(PlanOperationKind.Backup, "private.dll", 'a', 'a')];
                break;
            case BackupWithoutReceiptFault.ContainsCandidate:
                script.Candidates = [CreateCandidate('4', "private.dll", false)];
                break;
            case BackupWithoutReceiptFault.WrongConflict:
                script.Conflicts = [new(PlanConflictCode.TargetManifestRequired, null)];
                script.Warnings = [$"{PlanConflictCode.TargetManifestRequired}."];
                break;
            case BackupWithoutReceiptFault.ConflictHasPath:
                script.Conflicts = [new(PlanConflictCode.InstalledReceiptRequired, "private.dll")];
                script.Warnings = [$"{PlanConflictCode.InstalledReceiptRequired}: private.dll."];
                break;
            case BackupWithoutReceiptFault.AdditionalConflict:
                script.Conflicts =
                [
                    new(PlanConflictCode.TargetManifestRequired, null),
                    new(PlanConflictCode.InstalledReceiptRequired, null)
                ];
                script.Warnings =
                [
                    $"{PlanConflictCode.TargetManifestRequired}.",
                    $"{PlanConflictCode.InstalledReceiptRequired}."
                ];
                break;
            case BackupWithoutReceiptFault.MissingExactNotice:
                script.Warnings = [];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task InspectPlanAggregatesDynamicPagesAndReturnsOnlySanitizedCandidatePresentation()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Update)
        {
            PageSize = 1,
            CurrentRelease = CreateRelease(3),
            Operations =
            [
                CreateOperation(PlanOperationKind.Create, "a/private-first.dll", null, 'a'),
                CreateOperation(PlanOperationKind.Replace, "b/private-second.dll", 'b', 'c'),
                CreateOperation(PlanOperationKind.Replace, "c/private-third.dll", 'd', 'e')
            ],
            Conflicts =
            [
                new(PlanConflictCode.ModifiedOwnedFile, "d/private-conflict-one.dll"),
                new(PlanConflictCode.UnknownCollision, "e/private-conflict-two.dll")
            ],
            Candidates =
            [
                CreateCandidate('4', "f/private-candidate-one.dll", selected: false),
                CreateCandidate('5', "g/private-candidate-two.dll", selected: true)
            ],
            Warnings = ["private warning one", "private warning two"]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process, operation: TimeSpan.FromSeconds(10));
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanSuccess result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Update))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        result.ObservedState.Should().Be(ObservedInstallState.KnownUnmodified);
        result.CurrentRelease.Should().Be(new InstallerPlanRelease(CreateRelease(3).Tag, CreateRelease(3).EmbeddedVersion));
        result.TargetRelease.Should().Be(new InstallerPlanRelease(CreateRelease(2).Tag, CreateRelease(2).EmbeddedVersion));
        result.HasBlockingConflicts.Should().BeTrue();
        result.Risks.Should().Equal(ProtocolPlanRisk.Downgrade, ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        result.OperationCounts.Should().Equal(
            new InstallerPlanOperationCount(PlanOperationKind.Create, 1),
            new InstallerPlanOperationCount(PlanOperationKind.Replace, 2)
        );
        result.ConflictCounts.Should().Equal(
            new InstallerPlanConflictCount(PlanConflictCode.ModifiedOwnedFile, 1),
            new InstallerPlanConflictCount(PlanConflictCode.UnknownCollision, 1)
        );
        result.CandidateCounts.Should().Equal(
            new InstallerPlanCandidateCount(FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, false, 1),
            new InstallerPlanCandidateCount(FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, true, 1)
        );
        result.Candidates.Select(candidate => candidate.DisplayPath).Should().Equal(
            "f/private-candidate-one.dll",
            "g/private-candidate-two.dll"
        );
        result.Candidates.Select(candidate => candidate.BackendProvisionallyIncluded).Should().Equal(false, true);
        result.AdditionalNoticeCount.Should().Be(2);
        process.Requests.OfType<GetPlanPageRequest>().Select(page => (page.PageKind, page.Offset)).Should().Equal(
            (ProtocolPlanPageKind.Operations, 0),
            (ProtocolPlanPageKind.Operations, 1),
            (ProtocolPlanPageKind.Operations, 2),
            (ProtocolPlanPageKind.Conflicts, 0),
            (ProtocolPlanPageKind.Conflicts, 1),
            (ProtocolPlanPageKind.Candidates, 0),
            (ProtocolPlanPageKind.Candidates, 1),
            (ProtocolPlanPageKind.Warnings, 0),
            (ProtocolPlanPageKind.Warnings, 1)
        );

        string projection = result.ToString();
        projection.Should().NotContain(ReadOnlyPlanScript.GamePath)
            .And.NotContain(ReadOnlyPlanScript.PackageId.Value)
            .And.NotContain(script.PlanId.Value)
            .And.NotContain(script.ExecutionDigest.Value)
            .And.NotContain(new string('a', 64));
        GetProjectionPropertyNames(typeof(InstallerReadOnlyPlanResult)).Should().NotContain(name =>
            name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Evidence", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Confirmation", StringComparison.OrdinalIgnoreCase) && name != nameof(InstallerReadOnlyPlanSuccess.SeparateConfirmationRequired)
        );
    }

    [TestCase("folder/bi\u202Edi.dll", "folder/bi\\u202Edi.dll")]
    [TestCase("folder/line\u2028break.dll", "folder/line\\u2028break.dll")]
    [TestCase("folder/paragraph\u2029break.dll", "folder/paragraph\\u2029break.dll")]
    [TestCase("folder/format\u200Fmark.dll", "folder/format\\u200Fmark.dll")]
    public void CandidatePresentationEscapesHostileDisplayCodeUnits(string source, string expected)
    {
        InstallerReadOnlyPlanCandidate candidate = new(CreateCandidate('4', source, false));

        candidate.DisplayPath.Should().Be(expected);
        typeof(InstallerReadOnlyPlanCandidate).IsAssignableTo(typeof(IEquatable<InstallerReadOnlyPlanCandidate>)).Should().BeFalse();
    }

    [Test]
    public void CandidateProjectionRejectsAnInvalidSurrogatePath()
    {
        string source = string.Concat("folder/bad", '\uD800', "name.dll");

        Action action = () => _ = new InstallerReadOnlyPlanCandidate(CreateCandidate('4', source, false));

        action.Should().Throw<ArgumentException>();
    }

    [TestCase("../outside.dll")]
    [TestCase("/absolute.dll")]
    [TestCase("folder//duplicate.dll")]
    [TestCase("folder/./alias.dll")]
    [TestCase("folder/tab\tname.dll")]
    public async Task InspectPlanFailStopsCandidatePathsWhichAreNotCanonicalRelativePaths(string path)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', path, false)]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task CandidateApprovalUsesExactRetainedBindingAndReplacesEveryCandidateCapability()
    {
        ProtocolPlanCandidate selected = CreateCandidate('4', "mods/private.dll", false);
        ProtocolPlanCandidate remaining = CreateCandidate('5', "mods/remaining.dll", true);
        ReadOnlyPlanScript script = new(InstallerOperation.Update)
        {
            Candidates = [selected, remaining],
            ReplacementCandidates = [CreateCandidate('7', "mods/remaining.dll", true)]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess first = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Update))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        InstallerReadOnlyPlanSuccess replacement = (await client.ApprovePlanCandidatesAsync([first.Candidates[0]]))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        SelectPlanCandidatesRequest request = process.Requests.OfType<SelectPlanCandidatesRequest>().Single();
        request.PlanId.Should().Be(ProtocolPlanId.Parse("33333333333333333333333333333333"));
        request.SelectedCandidateIds.Should().Equal(selected.CandidateId);
        replacement.Candidates.Should().ContainSingle();
        replacement.Candidates.Single().Should().NotBeSameAs(first.Candidates[1]);
        replacement.Candidates.Single().BackendProvisionallyIncluded.Should().BeTrue();

        int writes = process.Requests.Count;
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([]))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([first.Candidates[0]]))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([replacement.Candidates[0], replacement.Candidates[0]]))
            .Should().ThrowAsync<ArgumentException>();
        InstallerReadOnlyPlanCandidate foreign = new(CreateCandidate('9', "mods/remaining.dll", true));
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([foreign]))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([replacement.Candidates[0], foreign]))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync(Enumerable.Repeat(replacement.Candidates[0], ProtocolJsonSerializer.MaxPlanCandidates + 1).ToArray()))
            .Should().ThrowAsync<ArgumentException>();
        process.Requests.Should().HaveCount(writes, "invalid capabilities must be rejected without a wire request");

        script.ApprovalRejection = new(Session, ProtocolPrePlanErrorCode.CandidateApprovalFailed, "Changed safely.", ProtocolNextAction.InspectAgain, false, null);
        (await client.ApprovePlanCandidatesAsync([replacement.Candidates[0]]))
            .Should().BeOfType<InstallerReadOnlyPlanRejection>("pre-wire validation failures must preserve the current exact binding");
    }

    [Test]
    public async Task CandidateApprovalSnapshotsCountAndIndexesWithoutEnumeratingAnUnboundedCallerCollection()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)],
            ReplacementCandidates = []
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);
        IndexedCandidateList oversized = new(ProtocolJsonSerializer.MaxPlanCandidates + 1, _ => throw new AssertionException("an oversized selection must not be indexed"));

        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync(oversized)).Should().ThrowAsync<ArgumentException>();
        (await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync(
            new IndexedCandidateList(1, _ => throw new InvalidOperationException("private indexer detail"))
        )).Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain("private indexer detail");
        InstallerReadOnlyPlanSuccess replacement = (InstallerReadOnlyPlanSuccess)await client.ApprovePlanCandidatesAsync(
            new IndexedCandidateList(1, _ => plan.Candidates.Single())
        );

        replacement.Candidates.Should().BeEmpty();
        process.Requests.OfType<SelectPlanCandidatesRequest>().Should().ContainSingle();
    }

    [Test]
    public async Task CandidateApprovalAllowsExplicitSelectionOfBackendProvisionalCandidate()
    {
        ProtocolPlanCandidate provisional = CreateCandidate('4', "mods/provisional.dll", true);
        ReadOnlyPlanScript script = new(InstallerOperation.Repair)
        {
            Candidates = [provisional],
            ReplacementCandidates = []
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Repair);

        InstallerReadOnlyPlanSuccess replacement = (InstallerReadOnlyPlanSuccess)await client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]);

        replacement.Candidates.Should().BeEmpty();
        process.Requests.OfType<SelectPlanCandidatesRequest>().Single().SelectedCandidateIds.Should().Equal(provisional.CandidateId);
    }

    [Test]
    public async Task CandidateApprovalAcceptsOnlyExactRetryableRejectionAndClearsTheOldBinding()
    {
        const string privateText = "/home/private-user/candidate detail";
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)],
            ApprovalRejection = new(Session, ProtocolPrePlanErrorCode.CandidateApprovalFailed, privateText, ProtocolNextAction.InspectAgain, false, null)
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        InstallerReadOnlyPlanRejection rejection = (await client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]))
            .Should().BeOfType<InstallerReadOnlyPlanRejection>().Subject;

        rejection.Should().Be(new InstallerReadOnlyPlanRejection(ProtocolPrePlanErrorCode.CandidateApprovalFailed, ProtocolNextAction.InspectAgain, false));
        rejection.ToString().Should().NotContain(privateText);
        int writes = process.Requests.Count;
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]))
            .Should().ThrowAsync<InvalidOperationException>();
        process.Requests.Should().HaveCount(writes);
        process.Terminated.Should().BeFalse();
        script.ApprovalRejection = null;
        await FluentActions.Awaiting(() => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install))
            .Should().ThrowAsync<InstallerProtocolClientException>("a nonterminal rejection must not erase session-lifetime ID tombstones");
        process.Terminated.Should().BeTrue();
    }

    [TestCase(ProtocolPrePlanErrorCode.RequestCancelled, ProtocolNextAction.RetryRequest, false)]
    [TestCase(ProtocolPrePlanErrorCode.InvalidGameFolder, ProtocolNextAction.SelectGameFolder, false)]
    [TestCase(ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ReviewFilesystem, false)]
    [TestCase(ProtocolPrePlanErrorCode.UnexpectedFailure, ProtocolNextAction.StartNewSession, true)]
    public async Task CandidateApprovalFailStopsEveryOtherGloballyValidRejection(
        ProtocolPrePlanErrorCode code,
        ProtocolNextAction nextAction,
        bool terminal
    )
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)],
            ApprovalRejection = new(Session, code, "private rejection", nextAction, terminal, null)
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task CandidateApprovalFailStopsAReplacementWhichReusesTheOldPlanBinding(bool reusePlanId, bool reusePlanDigest)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)],
            ReplacementCandidates = reusePlanDigest ? null : [],
            ReusePlanIdOnApproval = reusePlanId,
            ReusePlanDigestOnApproval = reusePlanDigest
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(CandidateReplacementFault.RootGenerationChanged)]
    [TestCase(CandidateReplacementFault.SelectedCandidateRemains)]
    [TestCase(CandidateReplacementFault.RemainingSemanticChanged)]
    [TestCase(CandidateReplacementFault.RemainingObservedIdentityChanged)]
    [TestCase(CandidateReplacementFault.RemainingSizeChanged)]
    [TestCase(CandidateReplacementFault.RemainingModeChanged)]
    [TestCase(CandidateReplacementFault.RemainingProposedIdentityChanged)]
    [TestCase(CandidateReplacementFault.RemainingCandidateIdReused)]
    public async Task CandidateApprovalFailStopsAuthorityOrCandidateDrift(CandidateReplacementFault fault)
    {
        ProtocolPlanCandidate selected = CreateCandidate('4', "mods/selected.dll", false);
        ProtocolPlanCandidate remaining = CreateCandidate('5', "mods/remaining.dll", false);
        ProtocolPlanCandidate refreshed = remaining with { CandidateId = ProtocolCandidateId.Parse(new string('7', 32)) };
        ReadOnlyPlanScript script = new(InstallerOperation.Update)
        {
            Candidates = [selected, remaining],
            ReplacementCandidates = fault switch
            {
                CandidateReplacementFault.SelectedCandidateRemains =>
                [selected with { CandidateId = ProtocolCandidateId.Parse(new string('6', 32)) }, refreshed],
                CandidateReplacementFault.RemainingSemanticChanged =>
                [refreshed with { Reason = FileReplacementCandidateReason.UnknownCollision }],
                CandidateReplacementFault.RemainingObservedIdentityChanged =>
                [refreshed with { ObservedSha256 = new string('9', 64) }],
                CandidateReplacementFault.RemainingSizeChanged =>
                [refreshed with { ObservedSizeBytes = refreshed.ObservedSizeBytes + 1 }],
                CandidateReplacementFault.RemainingModeChanged =>
                [refreshed with { ObservedUnixMode = 493 }],
                CandidateReplacementFault.RemainingProposedIdentityChanged =>
                [refreshed with { ProposedResultSha256 = new string('8', 64) }],
                CandidateReplacementFault.RemainingCandidateIdReused => [remaining],
                _ => [refreshed]
            },
            ReplacementOperationGeneration = fault == CandidateReplacementFault.RootGenerationChanged ? 5UL : null
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Update);

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([plan.Candidates[0]]);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task FreshInspectionFailStopsAProtocolCandidateIdReissuedByAnEarlierPlan()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task CandidateIdLifetimeTombstonesAreBoundedAndFailStopAtCapacity()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            InspectionCandidatesFactory = generation => Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates)
                .Select(index => new ProtocolPlanCandidate(
                    ProtocolCandidateId.Parse((generation * ProtocolJsonSerializer.MaxPlanCandidates + index + 1).ToString("x32")),
                    FileReplacementCandidateReason.ModifiedReceiptOwned,
                    FileReplacementCandidateDisposition.Replace,
                    $"mods/{generation:D2}-{index:D3}.dll",
                    new string('6', 64),
                    123,
                    420,
                    new string('7', 64),
                    false,
                    "private evidence"
                ))
                .ToArray()
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process, operation: TimeSpan.FromSeconds(10));
        client.IssuedCandidateCapacityForTesting = ProtocolJsonSerializer.MaxPlanCandidates * 2;
        await OpenVerifiedSessionAsync(client);
        InstallerCandidateSelection.MaximumIssuedCandidatesPerSession.Should().Be(
            ProtocolJsonSerializer.MaxPlanCandidates * ProtocolJsonSerializer.MaxPlanCandidates
        );
        int acceptedGenerations = client.IssuedCandidateCapacityForTesting / ProtocolJsonSerializer.MaxPlanCandidates;
        for (int generation = 0; generation < acceptedGenerations; generation++)
            (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();

        await FluentActions.Awaiting(() => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install))
            .Should().ThrowAsync<InstallerProtocolClientException>();

        process.Terminated.Should().BeTrue();
        process.Requests.OfType<InspectPlanRequest>().Should().HaveCount(acceptedGenerations + 1);
    }

    [TestCase(CandidateIdReuseFault.SelectedIdReassignedToRemaining)]
    [TestCase(CandidateIdReuseFault.RemainingIdsCrossSwapped)]
    [TestCase(CandidateIdReuseFault.DuplicateReplacementId)]
    public async Task CandidateApprovalFailStopsAnyPriorOrDuplicateIdInAReplacement(CandidateIdReuseFault fault)
    {
        ProtocolPlanCandidate selected = CreateCandidate('4', "mods/selected.dll", false);
        ProtocolPlanCandidate remainingOne = CreateCandidate('5', "mods/remaining-one.dll", false);
        ProtocolPlanCandidate remainingTwo = CreateCandidate('6', "mods/remaining-two.dll", false);
        ProtocolPlanCandidate[] replacement = fault switch
        {
            CandidateIdReuseFault.SelectedIdReassignedToRemaining =>
            [remainingOne with { CandidateId = selected.CandidateId }, remainingTwo with { CandidateId = ProtocolCandidateId.Parse(new string('7', 32)) }],
            CandidateIdReuseFault.RemainingIdsCrossSwapped =>
            [remainingOne with { CandidateId = remainingTwo.CandidateId }, remainingTwo with { CandidateId = remainingOne.CandidateId }],
            CandidateIdReuseFault.DuplicateReplacementId =>
            [remainingOne with { CandidateId = ProtocolCandidateId.Parse(new string('7', 32)) }, remainingTwo with { CandidateId = ProtocolCandidateId.Parse(new string('7', 32)) }],
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        ReadOnlyPlanScript script = new(InstallerOperation.Update)
        {
            Candidates = [selected, remainingOne, remainingTwo],
            ReplacementCandidates = replacement
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Update);

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([plan.Candidates[0]]);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task CandidateApprovalFailStopsAnIdResurrectedFromTwoPlanGenerationsEarlier()
    {
        ProtocolPlanCandidate selected = CreateCandidate('4', "mods/selected.dll", false);
        ProtocolPlanCandidate remainingOne = CreateCandidate('5', "mods/remaining-one.dll", false);
        ProtocolPlanCandidate remainingTwo = CreateCandidate('6', "mods/remaining-two.dll", false);
        ProtocolPlanCandidate refreshedOne = remainingOne with { CandidateId = ProtocolCandidateId.Parse(new string('7', 32)) };
        ProtocolPlanCandidate refreshedTwo = remainingTwo with { CandidateId = ProtocolCandidateId.Parse(new string('8', 32)) };
        ReadOnlyPlanScript script = new(InstallerOperation.Update)
        {
            Candidates = [selected, remainingOne, remainingTwo],
            ReplacementGenerations =
            [
                [refreshedOne, refreshedTwo],
                [remainingTwo with { CandidateId = selected.CandidateId }]
            ]
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess first = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Update);
        InstallerReadOnlyPlanSuccess second = (InstallerReadOnlyPlanSuccess)await client.ApprovePlanCandidatesAsync([first.Candidates[0]]);

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([second.Candidates[0]]);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CandidateApprovalCannotCommitAfterCancellationOrSessionFaultAtThePrecommitBoundary(bool sessionFault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Candidates = [CreateCandidate('4', "mods/private.dll", false)],
            ReplacementCandidates = []
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);
        using CancellationTokenSource cancellation = new();
        client.BeforePlanBindingCommitForTesting = () =>
        {
            if (sessionFault)
            {
                process.CompleteOutput();
                client.SessionFaulted.GetAwaiter().GetResult();
            }
            else
                cancellation.Cancel();
        };

        Func<Task> action = () => client.ApprovePlanCandidatesAsync([plan.Candidates.Single()], cancellation.Token);

        if (sessionFault)
            await action.Should().ThrowAsync<InstallerProtocolClientException>();
        else
            await action.Should().ThrowAsync<OperationCanceledException>();
        process.Terminated.Should().BeTrue();
        await FluentActions.Awaiting(() => client.ApprovePlanCandidatesAsync([plan.Candidates.Single()]))
            .Should().ThrowAsync<Exception>();
    }

    [TestCase(PlanHeaderFault.WrongSession)]
    [TestCase(PlanHeaderFault.WrongGame)]
    [TestCase(PlanHeaderFault.WrongOperation)]
    [TestCase(PlanHeaderFault.WrongPackage)]
    [TestCase(PlanHeaderFault.WrongTargetRelease)]
    [TestCase(PlanHeaderFault.InconsistentCurrentRelease)]
    [TestCase(PlanHeaderFault.WrongRisk)]
    [TestCase(PlanHeaderFault.WrongDigest)]
    [TestCase(PlanHeaderFault.WrongDefault)]
    [TestCase(PlanHeaderFault.NoConfirmation)]
    public async Task InspectPlanRejectsInvalidOrMismatchedPlanHeaders(PlanHeaderFault fault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install) { HeaderFault = fault };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(ObservedInstallState.LegacyOrOfficial)]
    [TestCase(ObservedInstallState.Unknown)]
    public async Task InspectPlanRejectsAReceiptReleaseForReceiptlessObservedStates(ObservedInstallState observedState)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Uninstall) { ObservedState = observedState };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Uninstall);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(PlanPageFault.WrongSession)]
    [TestCase(PlanPageFault.WrongPlan)]
    [TestCase(PlanPageFault.WrongDigest)]
    [TestCase(PlanPageFault.WrongOffset)]
    [TestCase(PlanPageFault.WrongNext)]
    [TestCase(PlanPageFault.WrongTotal)]
    [TestCase(PlanPageFault.WrongKind)]
    public async Task InspectPlanRejectsInvalidOrMismatchedPageBindings(PlanPageFault fault)
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Operations =
            [
                CreateOperation(PlanOperationKind.Create, "a.dll", null, 'a'),
                CreateOperation(PlanOperationKind.Create, "b.dll", null, 'b')
            ],
            PageSize = 1,
            PageFault = fault
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(PlanCollectionFault.DuplicateOperation)]
    [TestCase(PlanCollectionFault.UnorderedOperations)]
    [TestCase(PlanCollectionFault.DuplicateConflict)]
    [TestCase(PlanCollectionFault.UnorderedConflicts)]
    [TestCase(PlanCollectionFault.DuplicateCandidateId)]
    [TestCase(PlanCollectionFault.DuplicateCandidatePath)]
    [TestCase(PlanCollectionFault.DuplicateWarning)]
    public async Task InspectPlanRejectsCrossPageDuplicateOrNoncanonicalCollections(PlanCollectionFault fault)
    {
        ReadOnlyPlanScript script = ReadOnlyPlanScript.ForCollectionFault(fault);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, script.Operation);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [TestCase(ProtocolPrePlanErrorCode.RequestCancelled, ProtocolNextAction.RetryRequest, false)]
    [TestCase(ProtocolPrePlanErrorCode.InvalidGameFolder, ProtocolNextAction.SelectGameFolder, false)]
    [TestCase(ProtocolPrePlanErrorCode.PackageRejected, ProtocolNextAction.ReopenVerifiedPackage, false)]
    [TestCase(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, false)]
    [TestCase(ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ReviewFilesystem, false)]
    [TestCase(ProtocolPrePlanErrorCode.UnexpectedFailure, ProtocolNextAction.ViewPrivateLog, true)]
    public async Task InspectPlanProjectsOnlyRequestReachableRejectionsWithoutPrivateTextOrAuthority(
        ProtocolPrePlanErrorCode errorCode,
        ProtocolNextAction nextAction,
        bool terminal
    )
    {
        const string privateText = "/home/private-user/secret package detail";
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Rejection = new(
                Session,
                errorCode,
                privateText,
                nextAction,
                terminal,
                "/tmp/private-installer.log"
            )
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanRejection result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install))
            .Should().BeOfType<InstallerReadOnlyPlanRejection>().Subject;

        result.Should().Be(new InstallerReadOnlyPlanRejection(errorCode, nextAction, terminal));
        result.ToString().Should().NotContain(privateText).And.NotContain("private-installer.log");
        process.Terminated.Should().Be(terminal);
        if (!terminal)
        {
            script.Rejection = null;
            (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();
        }
    }

    [TestCase(ProtocolPrePlanErrorCode.RecoveryUnavailable, ProtocolNextAction.ListRecoveries)]
    [TestCase(ProtocolPrePlanErrorCode.CandidateApprovalFailed, ProtocolNextAction.InspectAgain)]
    [TestCase(ProtocolPrePlanErrorCode.InputOutputFailure, ProtocolNextAction.RetryRequest)]
    public async Task InspectPlanFailStopsGloballyValidButRequestUnreachableRejections(
        ProtocolPrePlanErrorCode errorCode,
        ProtocolNextAction nextAction
    )
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Rejection = new(Session, errorCode, "Rejected safely.", nextAction, false, null)
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task InspectPlanRejectsRollbackLocallyWithoutSendingAPlanRequest()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install);
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Rollback);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        process.Requests.Any(item => item is InspectPlanRequest or GetPlanPageRequest).Should().BeFalse();
        process.Terminated.Should().BeFalse();
    }

    [Test]
    public async Task InspectPlanRejectsMoreThanTheBoundedPageCount()
    {
        ProtocolPlanOperation[] operations = Enumerable.Range(0, ProcessInstallerProtocolClient.MaximumPlanPageCount + 1)
            .Select(index => CreateOperation(PlanOperationKind.Create, $"items/{index:D4}.dll", null, 'a'))
            .ToArray();
        ReadOnlyPlanScript script = new(InstallerOperation.Install) { Operations = operations, PageSize = 1 };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process, operation: TimeSpan.FromSeconds(30));
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Requests.OfType<GetPlanPageRequest>().Should().HaveCount(ProcessInstallerProtocolClient.MaximumPlanPageCount);
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task InspectPlanRejectsAggregatePresentationBeyondTheUtf8ByteBound()
    {
        string large = new('界', 4000);
        ProtocolPlanOperation[] operations = Enumerable.Range(0, 1500)
            .Select(index => CreateOperation(PlanOperationKind.Create, $"items/{index:D4}-{large}", null, 'a'))
            .ToArray();
        ReadOnlyPlanScript script = new(InstallerOperation.Install) { Operations = operations, PageSize = 4 };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process, operation: TimeSpan.FromSeconds(60));
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Requests.OfType<GetPlanPageRequest>().Should().HaveCountLessThanOrEqualTo(ProcessInstallerProtocolClient.MaximumPlanPageCount);
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task CallerCancellationDuringPlanPagingRevokesAndReapsTheSession()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Operations = [CreateOperation(PlanOperationKind.Create, "one.dll", null, 'a')],
            SuppressPageResponse = true
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);
        using CancellationTokenSource cancellation = new();

        Task<InstallerReadOnlyPlanResult> action = client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install, cancellation.Token);
        await SpinWaitUntilAsync(() => process.Requests.OfType<GetPlanPageRequest>().Any());
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => action).Should().ThrowAsync<OperationCanceledException>();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
    }

    [Test]
    public async Task AggregateDeadlineDuringPlanPagingStopsTheBackendWithSanitizedFailure()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Operations = [CreateOperation(PlanOperationKind.Create, "one.dll", null, 'a')],
            SuppressPageResponse = true
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process, operation: TimeSpan.FromMilliseconds(100));
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;
        exception.Message.Should().Contain("bounded deadline").And.NotContain(ReadOnlyPlanScript.GamePath);
        process.Terminated.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RejectsUncorrelatedDiscoveryOrValidationAndRevokesTheSession(bool discovery)
    {
        const string selectedPath = "/games/Stardew Valley";
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            DiscoverGamesRequest => Serialize(new GameDiscoveryEvent(
                ProtocolSessionId.CreateRandom(),
                [new(selectedPath, LinuxGameFolderStatus.Valid, "Stardew Valley")]
            )
            {
                CommandId = request.CommandId
            }),
            ValidateGameRequest => Serialize(new GameValidationEvent(
                ProtocolSessionId.CreateRandom(),
                new("/games/a-different-root", LinuxGameFolderStatus.Valid, "Stardew Valley")
            )
            {
                CommandId = request.CommandId
            }),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        Func<Task> action = discovery
            ? async () => await client.DiscoverGamesAsync()
            : async () => await client.ValidateGameAsync(selectedPath);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task DuplicateStdoutAfterValidPackageResultRevokesSuccessAndFailStops()
    {
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => [.. Serialize(CreateOpened(Session, request.CommandId)), .. Serialize(CreateOpened(Session, request.CommandId))],
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        Func<Task> action = () => client.OpenPackageAsync(CreatePackage());

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task PartialSecondFrameBufferedWithValidPackageResultRevokesSuccess()
    {
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => [.. Serialize(CreateOpened(Session, request.CommandId)), (byte)'{'],
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        Func<Task> action = () => client.OpenPackageAsync(CreatePackage());

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        client.HasRetainedPackageAuthority.Should().BeFalse();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task ValidPackageResultKeepsSessionAliveAndDelayedUnsolicitedOutputFaultsIt()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        InstallerPackageOpenResult result = await client.OpenPackageAsync(CreatePackage());

        result.Should().BeOfType<InstallerPackageOpenSuccess>();
        process.Terminated.Should().BeFalse();
        process.Disposed.Should().BeFalse();
        client.SessionFaulted.IsCompleted.Should().BeFalse();

        process.Publish(Serialize(CreateOpened(Session, ProtocolCommandId.CreateRandom())));
        InstallerProtocolClientException fault = await client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(2));
        fault.Message.Should().NotContain("commandId").And.NotContain("111111");
        client.HasRetainedPackageAuthority.Should().BeFalse("fault publication follows authority revocation");
        await SpinWaitUntilAsync(() => process.Disposed);
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task DelayedPartialUnsolicitedFrameFaultsWithinBoundedIdleDeadline()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(
            process,
            partialFrame: TimeSpan.FromMilliseconds(50)
        );
        await client.HandshakeAsync("SMAPI GUI", "1");
        (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>();
        client.HasRetainedPackageAuthority.Should().BeTrue();

        process.Publish([(byte)'{']);
        InstallerProtocolClientException fault = await client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(1));

        fault.Message.Should().Contain("bounded deadline").And.NotContain("{");
        client.HasRetainedPackageAuthority.Should().BeFalse("fault publication follows authority revocation");
        await SpinWaitUntilAsync(() => process.Disposed);
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task DribbledPartialUnsolicitedFrameCannotExtendAbsoluteDeadline()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(
            process,
            partialFrame: TimeSpan.FromMilliseconds(100)
        );
        await client.HandshakeAsync("SMAPI GUI", "1");
        (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>();

        using CancellationTokenSource stopDribble = new();
        Task dribble = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    process.Publish([(byte)'{']);
                    await Task.Delay(TimeSpan.FromMilliseconds(30), stopDribble.Token);
                }
            }
            catch (OperationCanceledException) when (stopDribble.IsCancellationRequested) { }
        });
        try
        {
            InstallerProtocolClientException fault = await client.SessionFaulted.WaitAsync(TimeSpan.FromMilliseconds(250));
            fault.Message.Should().Contain("bounded deadline").And.NotContain("{");
            client.HasRetainedPackageAuthority.Should().BeFalse();
        }
        finally
        {
            stopDribble.Cancel();
            await dribble;
        }
    }

    [Test]
    public async Task ValidSplitFrameCompletingWithinAbsoluteDeadlineIsAccepted()
    {
        ResponseStream responses = new();
        StrictJsonLineReader reader = new(responses, TimeSpan.FromMilliseconds(500));
        ValueTask<string?> pending = reader.ReadLineAsync(CancellationToken.None);

        responses.Set([(byte)'a']);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        responses.Set([(byte)'b']);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        responses.Set([(byte)'c', (byte)'\n']);

        (await pending).Should().Be("abc");
        reader.HasBufferedFrameData.Should().BeFalse();
    }

    [Test]
    public async Task FaultBetweenResponseAndAuthorityCommitCannotResurrectPackageAuthority()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        client.BeforePackageAuthorityCommitForTesting = () =>
        {
            process.Publish(Serialize(CreateOpened(Session, ProtocolCommandId.CreateRandom())));
            _ = client.SessionFaulted.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        };

        Func<Task> action = () => client.OpenPackageAsync(CreatePackage());

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        client.HasRetainedPackageAuthority.Should().BeFalse();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task RejectsPackageOpenBeforeHandshakeWithoutStartingProcess()
    {
        ScriptedProcess process = new(CorrectResponse);
        CapturingFactory factory = new(process);
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting("/tmp/SMAPI.Installer", factory);

        Func<Task> action = () => client.OpenPackageAsync(CreatePackage());

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        factory.StartInfo.Should().BeNull();
    }

    [Test]
    public async Task RejectsHandshakeWithoutEveryRequiredCapability()
    {
        foreach (string omitted in RequiredCapabilities)
        {
            ScriptedProcess process = new(request => Serialize(new HandshakeEvent(
                Session,
                "1",
                RequiredCapabilities.Where(value => value != omitted).ToArray()
            )
            {
                CommandId = request.CommandId
            }));
            await using ProcessInstallerProtocolClient client = Create(process);

            Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

            await action.Should().ThrowAsync<InstallerProtocolClientException>();
            process.Terminated.Should().BeTrue();
        }
    }

    [Test]
    public async Task SurfacesNormalCorrelatedPackageRejectionWithoutPrivateLogOrFailStop()
    {
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => Serialize(new PrePlanRejectedEvent(
                Session,
                ProtocolPrePlanErrorCode.PackageRejected,
                "The selected release asset set failed strict package verification.",
                ProtocolNextAction.ReopenVerifiedPackage,
                false,
                "/private/log/which-must-not-cross-the-interface"
            )
            {
                CommandId = request.CommandId
            }),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        InstallerPackageOpenResult result = await client.OpenPackageAsync(CreatePackage());

        InstallerPackageOpenRejection rejection = result.Should().BeOfType<InstallerPackageOpenRejection>().Subject;
        rejection.ErrorCode.Should().Be(ProtocolPrePlanErrorCode.PackageRejected);
        rejection.NextAction.Should().Be(ProtocolNextAction.ReopenVerifiedPackage);
        rejection.Message.Should().Be("The selected release asset set failed strict package verification.");
        rejection.IsTerminal.Should().BeFalse();
        result.ToString().Should().NotContain("/private/log");
        process.Terminated.Should().BeFalse();
        process.Disposed.Should().BeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RejectsWrongCommandOrSessionCorrelationAndTerminates(bool wrongCommand)
    {
        ScriptedProcess process = new(request =>
        {
            ProtocolCommandId command = wrongCommand ? ProtocolCommandId.CreateRandom() : request.CommandId;
            ProtocolSessionId session = wrongCommand ? Session : ProtocolSessionId.CreateRandom();
            return Serialize(new HandshakeEvent(session, "1", RequiredCapabilities) { CommandId = command });
        });
        await using ProcessInstallerProtocolClient client = Create(process);

        if (!wrongCommand)
        {
            // A handshake may establish any valid session, so test mismatched session on package open instead.
            process.Responder = request => request is HandshakeRequest
                ? Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId })
                : Serialize(CreateOpened(ProtocolSessionId.CreateRandom(), request.CommandId));
            await client.HandshakeAsync("SMAPI GUI", "1");
            Func<Task> open = () => client.OpenPackageAsync(CreatePackage());
            await open.Should().ThrowAsync<InstallerProtocolClientException>();
        }
        else
        {
            Func<Task> handshake = () => client.HandshakeAsync("SMAPI GUI", "1");
            await handshake.Should().ThrowAsync<InstallerProtocolClientException>();
        }

        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [TestCase("tag")]
    [TestCase("commit")]
    [TestCase("asset")]
    public async Task RejectsCorrelatedPackageOpenedEventWithWrongReleaseBinding(string mismatch)
    {
        InstallerPackageOpenInput package = mismatch switch
        {
            // Keep the response's asset name and commit bound to the input so this isolates the tag check.
            "tag" => CreatePackage(packageAssetName: PackageName(3)),
            "commit" => CreatePackage(),
            // Keep the response's tag and commit bound to the input so this isolates the basename check.
            "asset" => CreatePackage(packageAssetName: PackageName(3)),
            _ => throw new AssertionException("Unknown mismatch fixture.")
        };
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => mismatch switch
            {
                "tag" => Serialize(CreateOpened(Session, request.CommandId, alpha: 3)),
                "commit" => Serialize(CreateOpened(Session, request.CommandId, sourceCommit: new string('3', 40))),
                "asset" => Serialize(CreateOpened(Session, request.CommandId)),
                _ => throw new AssertionException("Unknown mismatch fixture.")
            },
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        Func<Task> action = () => client.OpenPackageAsync(package);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [TestCase("/tmp/package/")]
    [TestCase("/tmp/package/.")]
    [TestCase("/tmp/package/..")]
    [TestCase("/tmp/package/unsafe\\name.zip")]
    public async Task RejectsUnsafePackageBasenameBeforeSendingOpenRequest(string packagePath)
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        InstallerPackageOpenInput package = CreatePackage() with { PackagePath = packagePath };

        Func<Task> action = () => client.OpenPackageAsync(package);

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*safe absolute Linux filename*");
        process.Requests.Should().ContainSingle().Which.Should().BeOfType<HandshakeRequest>();
    }

    [Test]
    public async Task RejectsEofInvalidUtf8AndOversizedFramesWithoutLeakingTheirContents()
    {
        byte[][] responses =
        [
            [],
            [0xff, (byte)'\n'],
            [.. Enumerable.Repeat((byte)'x', ProtocolJsonSerializer.MaxLineBytes + 1), (byte)'\n']
        ];
        foreach (byte[] response in responses)
        {
            ScriptedProcess process = new(_ => response);
            await using ProcessInstallerProtocolClient client = Create(process);
            Func<Task> action = () => client.HandshakeAsync("private-client-value", "1");

            Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;

            exception.Message.Should().NotContain("private-client-value");
            process.Terminated.Should().BeTrue();
        }
    }

    [Test]
    public async Task DrainsStderrFloodButRetainsOnlyBoundedGenericMetadata()
    {
        string secret = "/private/home/user/token";
        byte[] flood = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(secret, 10000)));
        ScriptedProcess process = new(CorrectResponse, error: new MemoryStream(flood));
        await using ProcessInstallerProtocolClient client = Create(process);

        await client.HandshakeAsync("SMAPI GUI", "1");
        await SpinWaitUntilAsync(() => client.ObservedStderrBytes == ProcessInstallerProtocolClient.MaximumObservedStderrBytes);

        client.ObservedStderrBytes.Should().Be(ProcessInstallerProtocolClient.MaximumObservedStderrBytes);
        client.ToString().Should().NotContain(secret);
    }

    [Test]
    public async Task CancellationStopsAndReapsWithCancellationResistantOutput()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();

        Task running = client.HandshakeAsync("SMAPI GUI", "1", cancellation.Token);
        await process.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task OperationDeadlineStopsABackendWithCancellationResistantIo()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*bounded deadline*");
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
    }

    [Test]
    public async Task PartialProcessInitializationStillTerminatesReapsAndDisposesStartedChild()
    {
        ThrowingSetupProcess process = new();
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
            "/tmp/SMAPI.Installer",
            new SingleProcessFactory(process),
            reapTimeout: TimeSpan.FromMilliseconds(100)
        );

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task RetainedSiblingIdentityExecutesThroughOriginalDescriptorAfterPathSwap()
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi-gui-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string installer = Path.Combine(root, SiblingInstallerLocator.InstallerFileName);
            string gui = Path.Combine(root, "SMAPI.Installer.Gui");
            await File.WriteAllTextAsync(gui, "gui");
            await File.WriteAllTextAsync(installer, "original executable");
            File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using LinuxExternalExecutableLease lease = SiblingInstallerLocator.OpenSibling(gui);
            File.Delete(installer);
            await File.WriteAllTextAsync(installer, "replacement executable");
            File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            ScriptedProcess process = new(CorrectResponse);
            CapturingFactory factory = new(process);
            await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
                installer,
                factory,
                executableLease: lease
            );

            await client.HandshakeAsync("SMAPI GUI", "1");

            factory.StartInfo!.FileName.Should().Be(lease.ProcPath).And.NotBe(installer);
            File.ReadAllText(lease.ProcPath).Should().Be("original executable");
            process.Requests.Should().ContainSingle().Which.Should().BeOfType<HandshakeRequest>();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task UnconfirmedTerminationIsReportedTruthfullyAndRetainedForDeferredReap()
    {
        string executablePath = Path.Combine(Path.GetTempPath(), $"smapi-backend-lease-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(executablePath, "backend");
        File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using LinuxExternalExecutableLease lease = LinuxExternalExecutableLease.Open(executablePath);
        string procPath = lease.ProcPath;
        ScriptedProcess process = new(
            _ => Encoding.UTF8.GetBytes("not-json\n"),
            completeWaitInitially: false,
            completeExitOnTerminate: false
        );
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
            executablePath,
            new CapturingFactory(process),
            reapTimeout: TimeSpan.FromMilliseconds(50),
            executableLease: lease
        );

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*termination could not be confirmed*");
        client.CleanupConfirmed.Should().BeFalse();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeFalse("the process handle must remain owned by the deferred reaper");
        File.Exists(procPath).Should().BeTrue("the exact execution authority must remain retained until deferred reap");

        process.CompleteExit();
        await SpinWaitUntilAsync(() => process.Disposed);
        await SpinWaitUntilAsync(() => !File.Exists(procPath));
        File.Delete(executablePath);
    }

    [Test]
    [SupportedOSPlatform("linux")]
    [NonParallelizable]
    public async Task ProductionGateAllowsOnlyOneClientAndDisablesRelaunchAfterUnconfirmedReap()
    {
        string executablePath = Path.Combine(Path.GetTempPath(), $"smapi-production-gate-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(executablePath, "backend");
        File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        ScriptedProcess process = new(
            _ => Encoding.UTF8.GetBytes("not-json\n"),
            completeWaitInitially: false,
            completeExitOnTerminate: false
        );
        ProcessInstallerProtocolClient? client = null;
        try
        {
            client = ProcessInstallerProtocolClient.CreateProductionForTesting(
                () => LinuxExternalExecutableLease.Open(executablePath),
                new CapturingFactory(process),
                reapTimeout: TimeSpan.FromMilliseconds(50)
            );
            FluentActions.Invoking(() => ProcessInstallerProtocolClient.CreateProductionForTesting(
                () => LinuxExternalExecutableLease.Open(executablePath),
                new CapturingFactory(new ScriptedProcess(CorrectResponse))
            )).Should().Throw<InvalidOperationException>().WithMessage("*already active*");

            await FluentActions.Awaiting(() => client.HandshakeAsync("SMAPI GUI", "1"))
                .Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*termination could not be confirmed*");
            FluentActions.Invoking(() => ProcessInstallerProtocolClient.CreateProductionForTesting(
                () => LinuxExternalExecutableLease.Open(executablePath),
                new CapturingFactory(new ScriptedProcess(CorrectResponse))
            )).Should().Throw<InvalidOperationException>().WithMessage("*disabled until restart*");
        }
        finally
        {
            process.CompleteExit();
            if (client is not null)
                await client.DisposeAsync();
            if (process.WaitObserved)
            {
                await SpinWaitUntilAsync(() => ProcessInstallerProtocolClient.IsProductionQuarantineClearedForTesting);
                process.Disposed.Should().BeTrue("the quarantine clears only after the deferred process disposal");
            }
            ProcessInstallerProtocolClient.ResetProductionGateForTesting();
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task FaultedWaitIsNeverTreatedAsConfirmedReap()
    {
        ScriptedProcess process = new(_ => Encoding.UTF8.GetBytes("not-json\n"), faultWait: true);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(50));

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*termination could not be confirmed*");
        client.CleanupConfirmed.Should().BeFalse();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeFalse();
    }

    [Test]
    public async Task CancellationStopsAndReapsWithCancellationResistantInput()
    {
        CancellationResistantWriteStream input = new();
        ScriptedProcess process = new(CorrectResponse, input: input, completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        input.Disposed.Should().BeTrue();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
    }

    [Test]
    public async Task DisposeClosesInputThenTerminatesAndReapsBackendWhichDoesNotExitOnEof()
    {
        ScriptedProcess process = new(CorrectResponse, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        await client.HandshakeAsync("SMAPI GUI", "1");

        await client.DisposeAsync();

        process.InputDisposed.Should().BeTrue();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ConcurrentDisposeCancelsTransportAndSettlesBeforeDisposingSynchronizationObjects()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        Task running = client.HandshakeAsync("SMAPI GUI", "1");
        await process.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposal = client.DisposeAsync().AsTask();

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await FluentActions.Awaiting(() => running).Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task EveryConcurrentDisposeCallerAwaitsTheSameCleanupAndReap()
    {
        ScriptedProcess process = new(CorrectResponse, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(200));
        await client.HandshakeAsync("SMAPI GUI", "1");

        Task first = client.DisposeAsync().AsTask();
        Task second = client.DisposeAsync().AsTask();
        await Task.Delay(30);

        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task MalformedBackendDiagnosticsAndPackagePathsAreNeverReflected()
    {
        const string privatePath = "/home/private-user/download/token-package.zip";
        ScriptedProcess process = new(_ => Encoding.UTF8.GetBytes("not-json\n"), error: new MemoryStream(Encoding.UTF8.GetBytes(privatePath)));
        await using ProcessInstallerProtocolClient client = Create(process);
        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        exception.Message.Should().NotContain(privatePath).And.NotContain("not-json");
    }

    private static async Task OpenVerifiedSessionAsync(ProcessInstallerProtocolClient client)
    {
        await client.HandshakeAsync("SMAPI GUI", "1");
        (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>();
    }

    private static ProtocolReleaseIdentity CreateRelease(int alpha) => CreateOpened(Session, ProtocolCommandId.CreateRandom(), alpha).Release;

    private static ProtocolPlanOperation CreateOperation(PlanOperationKind kind, string path, char? expected, char result)
    {
        string? expectedHash = expected is { } value ? new string(value, 64) : null;
        string resultHash = new(result, 64);
        return new(kind, path, expectedHash, resultHash);
    }

    private static ProtocolPlanCandidate CreateCandidate(char id, string path, bool selected) => new(
        ProtocolCandidateId.Parse(new string(id, 32)),
        FileReplacementCandidateReason.ModifiedReceiptOwned,
        FileReplacementCandidateDisposition.Replace,
        path,
        new string('6', 64),
        123,
        420,
        new string('7', 64),
        selected,
        "Core observed an exact private fixture identity."
    );

    private static IEnumerable<string> GetProjectionPropertyNames(Type root)
    {
        Type[] types =
        [
            root,
            typeof(InstallerReadOnlyPlanSuccess),
            typeof(InstallerReadOnlyPlanRejection),
            typeof(InstallerPlanRelease),
            typeof(InstallerPlanOperationCount),
            typeof(InstallerPlanConflictCount),
            typeof(InstallerPlanCandidateCount),
            typeof(InstallerReadOnlyPlanCandidate)
        ];
        return types.SelectMany(type => type.GetProperties().Select(property => property.Name)).Distinct(StringComparer.Ordinal);
    }

    public enum PlanHeaderFault
    {
        None,
        WrongSession,
        WrongGame,
        WrongOperation,
        WrongPackage,
        WrongTargetRelease,
        InconsistentCurrentRelease,
        WrongRisk,
        WrongDigest,
        WrongDefault,
        NoConfirmation
    }

    public enum PlanPageFault
    {
        None,
        WrongSession,
        WrongPlan,
        WrongDigest,
        WrongOffset,
        WrongNext,
        WrongTotal,
        WrongKind
    }

    public enum PlanCollectionFault
    {
        DuplicateOperation,
        UnorderedOperations,
        DuplicateConflict,
        UnorderedConflicts,
        DuplicateCandidateId,
        DuplicateCandidatePath,
        DuplicateWarning
    }

    public enum BackupWithoutReceiptFault
    {
        ExecutableWithoutReceipt,
        ContainsOperation,
        ContainsCandidate,
        WrongConflict,
        ConflictHasPath,
        AdditionalConflict,
        MissingExactNotice
    }

    public enum CandidateReplacementFault
    {
        RootGenerationChanged,
        SelectedCandidateRemains,
        RemainingSemanticChanged,
        RemainingObservedIdentityChanged,
        RemainingSizeChanged,
        RemainingModeChanged,
        RemainingProposedIdentityChanged,
        RemainingCandidateIdReused
    }

    public enum CandidateIdReuseFault
    {
        SelectedIdReassignedToRemaining,
        RemainingIdsCrossSwapped,
        DuplicateReplacementId
    }

    public enum ConfirmationAcknowledgementFault
    {
        None,
        WrongSession,
        WrongPlan,
        WrongKind,
        PruneAuthority,
        WrongCommand
    }

    public enum ExecutionProtocolFault
    {
        WrongProgressBinding,
        ProgressCounterOverBound,
        WrongSuccessOperation,
        ManagedCountOverPlan,
        UnrequestedCancellationTerminal
    }

    private sealed class ReadOnlyPlanScript
    {
        public const string GamePath = "/games/private Stardew Valley";
        public static readonly ProtocolPackageId PackageId = ProtocolPackageId.Parse("22222222222222222222222222222222");

        private bool PageFaultReturned;

        public InstallerOperation Operation { get; }
        public ProtocolPlanId PlanId { get; private set; } = ProtocolPlanId.Parse("33333333333333333333333333333333");
        public ProtocolPlanDigest PlanDigest { get; private set; } = null!;
        public ProtocolPlanDigest ExecutionDigest { get; private set; } = ProtocolPlanDigest.Parse(new string('8', 64));
        public ProtocolReleaseIdentity? CurrentRelease { get; set; }
        public ObservedInstallState ObservedState { get; set; }
        public ProtocolPlanOperation[] Operations { get; set; } = [];
        public ProtocolPlanConflict[] Conflicts { get; set; } = [];
        public ProtocolPlanCandidate[] Candidates { get; set; } = [];
        public string[] Warnings { get; set; } = [];
        public int PageSize { get; init; } = 128;
        public PlanHeaderFault HeaderFault { get; init; }
        public PlanPageFault PageFault { get; init; }
        public PrePlanRejectedEvent? Rejection { get; set; }
        public PrePlanRejectedEvent? ApprovalRejection { get; set; }
        public ProtocolPlanCandidate[]? ReplacementCandidates { get; set; }
        public ProtocolPlanCandidate[][]? ReplacementGenerations { get; set; }
        public Func<int, ProtocolPlanCandidate[]>? InspectionCandidatesFactory { get; set; }
        public bool ReusePlanIdOnApproval { get; set; }
        public bool ReusePlanDigestOnApproval { get; set; }
        public ulong? ReplacementOperationGeneration { get; set; }
        private ulong OperationGeneration { get; set; } = 4;
        private int ApprovalGeneration { get; set; }
        private int InspectionGeneration { get; set; }
        public bool SuppressPageResponse { get; init; }
        public ConfirmationAcknowledgementFault ConfirmationFault { get; init; }

        public ReadOnlyPlanScript(InstallerOperation operation)
        {
            this.Operation = operation;
            this.CurrentRelease = operation == InstallerOperation.Install ? null : CreateRelease(1);
            this.ObservedState = operation == InstallerOperation.Install ? ObservedInstallState.NotInstalled : ObservedInstallState.KnownUnmodified;
        }

        public byte[]? Respond(ProtocolRequest request)
        {
            return request switch
            {
                HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
                OpenPackageRequest => Serialize(CreateOpened(Session, request.CommandId)),
                InspectPlanRequest inspect when this.Rejection is not null => Serialize(this.Rejection with { CommandId = inspect.CommandId }),
                InspectPlanRequest inspect when this.InspectionCandidatesFactory is not null => this.CreateGeneratedInspectionResponse(inspect),
                InspectPlanRequest inspect => this.CreatePlanResponse(inspect),
                SelectPlanCandidatesRequest approval when this.ApprovalRejection is not null => Serialize(this.ApprovalRejection with { CommandId = approval.CommandId }),
                SelectPlanCandidatesRequest approval => this.CreateApprovalResponse(approval),
                GetPlanPageRequest when this.SuppressPageResponse => null,
                GetPlanPageRequest page => this.CreatePageResponse(page),
                ConfirmPlanRequest confirm => this.CreateConfirmationResponse(confirm),
                _ => throw new AssertionException("Unexpected protocol request in read-only plan script.")
            };
        }

        private byte[] CreateConfirmationResponse(ConfirmPlanRequest request)
        {
            ProtocolSessionId session = this.ConfirmationFault == ConfirmationAcknowledgementFault.WrongSession
                ? ProtocolSessionId.Parse("99999999999999999999999999999999")
                : Session;
            bool pruneFault = this.ConfirmationFault == ConfirmationAcknowledgementFault.PruneAuthority;
            ProtocolPlanId? plan = pruneFault
                ? null
                : this.ConfirmationFault == ConfirmationAcknowledgementFault.WrongPlan
                    ? ProtocolPlanId.Parse("99999999999999999999999999999999")
                    : this.PlanId;
            ProtocolAcknowledgementKind kind = pruneFault
                ? ProtocolAcknowledgementKind.PrunePlanConfirmed
                : this.ConfirmationFault == ConfirmationAcknowledgementFault.WrongKind
                    ? ProtocolAcknowledgementKind.PlanCancellationRequested
                    : ProtocolAcknowledgementKind.PlanConfirmed;
            ProtocolPrunePlanId? prune = this.ConfirmationFault == ConfirmationAcknowledgementFault.PruneAuthority
                ? ProtocolPrunePlanId.Parse("99999999999999999999999999999999")
                : null;
            ProtocolCommandId command = this.ConfirmationFault == ConfirmationAcknowledgementFault.WrongCommand
                ? ProtocolCommandId.CreateRandom()
                : request.CommandId;
            return Serialize(new CommandAcknowledgedEvent(session, kind, plan, prune) { CommandId = command });
        }

        private byte[] CreateGeneratedInspectionResponse(InspectPlanRequest request)
        {
            this.Candidates = this.InspectionCandidatesFactory!(this.InspectionGeneration);
            this.PlanId = ProtocolPlanId.Parse((this.InspectionGeneration + 1).ToString("x32"));
            this.ExecutionDigest = ProtocolPlanDigest.Parse((this.InspectionGeneration + 1).ToString("x64"));
            this.InspectionGeneration++;
            return this.CreatePlanResponse(request);
        }

        private byte[] CreateApprovalResponse(SelectPlanCandidatesRequest request)
        {
            this.ApprovalGeneration++;
            if (!this.ReusePlanIdOnApproval)
                this.PlanId = ProtocolPlanId.Parse(new string((char)('a' + (this.ApprovalGeneration - 1) * 2), 32));
            if (this.ReplacementGenerations is not null)
                this.Candidates = this.ReplacementGenerations[this.ApprovalGeneration - 1];
            else if (this.ReplacementCandidates is not null)
                this.Candidates = this.ReplacementCandidates;
            if (!this.ReusePlanDigestOnApproval)
                this.ExecutionDigest = ProtocolPlanDigest.Parse(new string((char)('b' + (this.ApprovalGeneration - 1) * 2), 64));
            if (this.ReplacementOperationGeneration is { } generation)
                this.OperationGeneration = generation;
            PlanEvent plan = this.CreatePlan(request.CommandId);
            return Serialize(plan);
        }

        public static ReadOnlyPlanScript ForCollectionFault(PlanCollectionFault fault)
        {
            ReadOnlyPlanScript script = new(InstallerOperation.Install) { PageSize = 1 };
            switch (fault)
            {
                case PlanCollectionFault.DuplicateOperation:
                    ProtocolPlanOperation duplicateOperation = CreateOperation(PlanOperationKind.Create, "same.dll", null, 'a');
                    script.Operations = [duplicateOperation, duplicateOperation];
                    break;
                case PlanCollectionFault.UnorderedOperations:
                    script.Operations =
                    [
                        CreateOperation(PlanOperationKind.Create, "z.dll", null, 'a'),
                        CreateOperation(PlanOperationKind.Create, "a.dll", null, 'b')
                    ];
                    break;
                case PlanCollectionFault.DuplicateConflict:
                    ProtocolPlanConflict duplicateConflict = new(PlanConflictCode.UnknownCollision, "same.dll");
                    script.Conflicts = [duplicateConflict, duplicateConflict];
                    break;
                case PlanCollectionFault.UnorderedConflicts:
                    script.Conflicts =
                    [
                        new(PlanConflictCode.UnknownCollision, "z.dll"),
                        new(PlanConflictCode.UnknownCollision, "a.dll")
                    ];
                    break;
                case PlanCollectionFault.DuplicateCandidateId:
                    script.Candidates =
                    [
                        CreateCandidate('4', "a.dll", false),
                        CreateCandidate('4', "b.dll", false)
                    ];
                    break;
                case PlanCollectionFault.DuplicateCandidatePath:
                    script.Candidates =
                    [
                        CreateCandidate('4', "same.dll", false),
                        CreateCandidate('5', "same.dll", false)
                    ];
                    break;
                case PlanCollectionFault.DuplicateWarning:
                    script.Warnings = ["same warning", "same warning"];
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault));
            }
            return script;
        }

        private byte[] CreatePlanResponse(InspectPlanRequest request)
        {
            PlanEvent plan = this.CreatePlan(request.CommandId);
            string line = ProtocolJsonSerializer.SerializeLine(plan);
            if (this.HeaderFault == PlanHeaderFault.WrongDefault)
                line = line.Replace("\"recommendedDefault\":\"cancel\"", "\"recommendedDefault\":999", StringComparison.Ordinal);
            else if (this.HeaderFault == PlanHeaderFault.NoConfirmation)
                line = line.Replace("\"requiresConfirmation\":true", "\"requiresConfirmation\":false", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(line + "\n");
        }

        private PlanEvent CreatePlan(ProtocolCommandId commandId)
        {
            ProtocolSessionId session = this.HeaderFault == PlanHeaderFault.WrongSession ? ProtocolSessionId.CreateRandom() : Session;
            InstallerOperation responseOperation = this.HeaderFault == PlanHeaderFault.WrongOperation ? InstallerOperation.Update : this.Operation;
            ProtocolPackageId? package = responseOperation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair
                ? this.HeaderFault == PlanHeaderFault.WrongPackage ? ProtocolPackageId.Parse("99999999999999999999999999999999") : PackageId
                : null;
            ProtocolGameRootIdentity root = new(
                this.HeaderFault == PlanHeaderFault.WrongGame ? "/games/a-different-private-root" : GamePath,
                1,
                2,
                3,
                this.OperationGeneration
            );
            ProtocolReleaseIdentity? current = this.HeaderFault == PlanHeaderFault.InconsistentCurrentRelease
                ? CreateRelease(1)
                : this.CurrentRelease;
            ProtocolReleaseIdentity? target = responseOperation switch
            {
                InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair => this.HeaderFault == PlanHeaderFault.WrongTargetRelease ? CreateRelease(3) : CreateRelease(2),
                InstallerOperation.Backup => current,
                _ => null
            };
            ObservedInstallState observed = this.ObservedState;
            ProtocolPlanRisk[] risks = this.GetExpectedRisks(current, target);
            if (this.HeaderFault == PlanHeaderFault.WrongRisk)
                risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval];
            string summary = this.Conflicts.Length == 0 ? "The plan is ready for review." : "The plan is blocked by observed conflicts.";
            ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(
                this.ExecutionDigest,
                responseOperation,
                package,
                null,
                root,
                current,
                target,
                observed,
                this.Operations,
                this.Conflicts,
                this.Candidates,
                summary,
                this.Warnings,
                true
            );
            if (this.HeaderFault == PlanHeaderFault.WrongDigest)
                digest = ProtocolPlanDigest.Parse(new string('9', 64));
            this.PlanDigest = digest;
            return new(
                session,
                this.PlanId,
                digest,
                this.ExecutionDigest,
                responseOperation,
                package,
                null,
                root,
                current,
                target,
                observed,
                this.Operations.Length,
                this.Conflicts.Length,
                this.Candidates.Length,
                this.Warnings.Length,
                this.Conflicts.Length == 0,
                risks,
                ProtocolRecommendedDefault.Cancel,
                summary,
                true
            )
            {
                CommandId = commandId
            };
        }

        private byte[] CreatePageResponse(GetPlanPageRequest request)
        {
            PlanPageFault fault = this.PageFaultReturned ? PlanPageFault.None : this.PageFault;
            this.PageFaultReturned = true;
            int requestedTotal = this.Count(request.PageKind);
            ProtocolSessionId session = fault == PlanPageFault.WrongSession ? ProtocolSessionId.CreateRandom() : Session;
            ProtocolPlanId planId = fault == PlanPageFault.WrongPlan ? ProtocolPlanId.CreateRandom() : this.PlanId;
            ProtocolPlanDigest digest = fault == PlanPageFault.WrongDigest ? ProtocolPlanDigest.Parse(new string('9', 64)) : request.PlanDigest;
            ProtocolPlanPageKind kind = fault == PlanPageFault.WrongKind ? ProtocolPlanPageKind.Warnings : request.PageKind;
            int offset = fault == PlanPageFault.WrongOffset ? request.Offset + 1 : request.Offset;
            int total = fault == PlanPageFault.WrongTotal ? requestedTotal + 1 : requestedTotal;
            int available = Math.Max(0, total - offset);
            int count = Math.Min(this.PageSize, available);
            ProtocolPlanOperation[] operations = kind == ProtocolPlanPageKind.Operations ? this.Operations.Skip(offset).Take(count).ToArray() : [];
            ProtocolPlanConflict[] conflicts = kind == ProtocolPlanPageKind.Conflicts ? this.Conflicts.Skip(offset).Take(count).ToArray() : [];
            ProtocolPlanCandidate[] candidates = kind == ProtocolPlanPageKind.Candidates ? this.Candidates.Skip(offset).Take(count).ToArray() : [];
            string[] warnings = kind == ProtocolPlanPageKind.Warnings
                ? fault == PlanPageFault.WrongKind ? ["wrong page kind"] : this.Warnings.Skip(offset).Take(count).ToArray()
                : [];
            int populated = operations.Length + conflicts.Length + candidates.Length + warnings.Length;
            if (populated == 0 && fault == PlanPageFault.WrongKind)
                warnings = ["wrong page kind"];
            populated = operations.Length + conflicts.Length + candidates.Length + warnings.Length;
            int? next = offset + populated < total ? offset + populated : null;
            PlanPageEvent page = new(session, planId, digest, kind, offset, total, next, operations, conflicts, candidates, warnings)
            {
                CommandId = request.CommandId
            };
            string line = ProtocolJsonSerializer.SerializeLine(page);
            if (fault == PlanPageFault.WrongNext)
                line = line.Replace("\"nextOffset\":1", "\"nextOffset\":null", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(line + "\n");
        }

        private int Count(ProtocolPlanPageKind kind) => kind switch
        {
            ProtocolPlanPageKind.Operations => this.Operations.Length,
            ProtocolPlanPageKind.Conflicts => this.Conflicts.Length,
            ProtocolPlanPageKind.Candidates => this.Candidates.Length,
            ProtocolPlanPageKind.Warnings => this.Warnings.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private ProtocolPlanRisk[] GetExpectedRisks(ProtocolReleaseIdentity? current, ProtocolReleaseIdentity? target)
        {
            List<ProtocolPlanRisk> risks = [];
            if (this.Operation == InstallerOperation.Uninstall)
                risks.Add(ProtocolPlanRisk.Uninstall);
            if (current is not null && target is not null && current.Tag != target.Tag && current.Tag.EndsWith("alpha.3", StringComparison.Ordinal))
                risks.Add(ProtocolPlanRisk.Downgrade);
            if (this.Candidates.Length > 0)
                risks.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
            return risks.ToArray();
        }
    }

    private sealed class IndexedCandidateList(int count, Func<int, InstallerReadOnlyPlanCandidate> indexer) : IReadOnlyList<InstallerReadOnlyPlanCandidate>
    {
        public int Count => count;
        public InstallerReadOnlyPlanCandidate this[int index] => indexer(index);
        public IEnumerator<InstallerReadOnlyPlanCandidate> GetEnumerator() => throw new AssertionException("candidate selection enumeration isn't bounded");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private static ProcessInstallerProtocolClient Create(ScriptedProcess process, TimeSpan? reap = null, TimeSpan? operation = null, TimeSpan? partialFrame = null) =>
        ProcessInstallerProtocolClient.CreateForTesting(
            "/tmp/SMAPI.Installer",
            new CapturingFactory(process),
            operation ?? TimeSpan.FromSeconds(2),
            reap ?? TimeSpan.FromMilliseconds(250),
            partialFrameTimeout: partialFrame
        );

    private static InstallerPackageOpenInput CreatePackage(string? packageAssetName = null)
    {
        string root = "/tmp/package set ;$[]";
        return new(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2",
            new string('1', 40),
            Path.Combine(root, packageAssetName ?? PackageName(2)),
            Path.Combine(root, "SHA256SUMS"),
            Path.Combine(root, "build-metadata.json"),
            Path.Combine(root, "install-manifest.json"),
            Path.Combine(root, "attestation.json"),
            Path.Combine(root, "attestation.sha256")
        );
    }

    private static RecoveryCompletedEvent CreateRecoveryCompleted(
        RecoverInterruptedRequest request,
        string canonicalPath,
        bool namedRootStillSelected
    ) => new(
        request.SessionId,
        ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
        new(
            ProtocolDurableState.RecoveryCompleted,
            null,
            ProtocolRecoveryDisposition.Completed,
            namedRootStillSelected ? ProtocolNextAction.InspectAgain : ProtocolNextAction.SelectGameFolder
        ),
        new(
            new(canonicalPath, 1, 2, 3, 2),
            1,
            2,
            namedRootStillSelected,
            [new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7)]
        ),
        "private recovery summary",
        "/home/private-user/recovery.log"
    )
    {
        CommandId = request.CommandId
    };

    private static RecoveryFailureEvent CreateRecoveryFailure(
        RecoverInterruptedRequest request,
        string canonicalPath,
        ProtocolInterruptedRecoveryOutcome outcome,
        ProtocolTerminalErrorCode? errorCode
    )
    {
        (ProtocolDurableState durable, ProtocolTerminalErrorCode? exactError, ProtocolInterruptedRecoveryAttempt? attempt) = outcome switch
        {
            ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => (ProtocolDurableState.Unchanged, (ProtocolTerminalErrorCode?)null, (ProtocolInterruptedRecoveryAttempt?)null),
            ProtocolInterruptedRecoveryOutcome.PartialFailure => (
                ProtocolDurableState.RecoveryRequired,
                errorCode ?? ProtocolTerminalErrorCode.RecoveryFailed,
                new(
                    new(canonicalPath, 1, 2, 3, 1),
                    1,
                    null,
                    null,
                    [new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7)]
                )
            ),
            ProtocolInterruptedRecoveryOutcome.UnexpectedFailure => (ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, (ProtocolInterruptedRecoveryAttempt?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        return new(
            request.SessionId,
            outcome,
            new(durable, exactError, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted),
            "private recovery failure",
            "/home/private-user/recovery.log",
            attempt
        )
        {
            CommandId = request.CommandId
        };
    }

    private static byte[]? CorrectResponse(ProtocolRequest request) => request switch
    {
        HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", RequiredCapabilities) { CommandId = request.CommandId }),
        OpenPackageRequest => Serialize(CreateOpened(Session, request.CommandId)),
        _ => throw new AssertionException("The GUI bridge sent a command outside this slice.")
    };

    private static PackageOpenedEvent CreateOpened(ProtocolSessionId session, ProtocolCommandId command, int alpha = 2, string? sourceCommit = null) => new(
        session,
        ProtocolPackageId.Parse("22222222222222222222222222222222"),
        new(
            "https://github.com/4eh5xitv6787h645ebv/SMAPI",
            $"fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.{alpha}",
            $"4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}",
            PackageName(alpha),
            sourceCommit ?? new string('1', 40),
            new string('2', 40),
            new string('a', 64),
            123,
            $"4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.{alpha}",
            "Release",
            "linux-x64"
        )
    )
    {
        CommandId = command
    };

    private static string PackageName(int alpha) => $"SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}-linux-x64-installer.zip";

    private static string[] RequiredCapabilities =>
    [
        ProcessInstallerProtocolClient.PackageVerificationCapability,
        ProcessInstallerProtocolClient.GameDiscoveryCapability,
        ProcessInstallerProtocolClient.GameValidationCapability,
        ProcessInstallerProtocolClient.PlanInspectionCapability,
        ProcessInstallerProtocolClient.CandidateApprovalCapability,
        ProcessInstallerProtocolClient.ExactCoreProgressCapability,
        ProcessInstallerProtocolClient.CancellationCapability,
        ProcessInstallerProtocolClient.InterruptedRecoveryCapability
    ];

    private static byte[] Serialize(ProtocolEvent value) => Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(value) + "\n");

    private static byte[] SerializeMany(params ProtocolEvent[] values) => Encoding.UTF8.GetBytes(
        string.Concat(values.Select(value => ProtocolJsonSerializer.SerializeLine(value) + "\n"))
    );

    private static async Task<InstallerConfirmedPlanAuthority> PrepareConfirmedPlanAsync(
        ProcessInstallerProtocolClient client,
        ReadOnlyPlanScript script
    )
    {
        await client.HandshakeAsync("SMAPI GUI", "1");
        (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>();
        InstallerReadOnlyPlanSuccess plan = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, script.Operation))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;
        return await client.ConfirmPlanAsync(plan.Confirmation!);
    }

    private static SuccessEvent Success(
        ReadOnlyPlanScript script,
        ProtocolCommandId commandId,
        string summary,
        string? log
    ) => new(
        Session,
        script.PlanId,
        script.PlanDigest,
        script.Operation,
        ProtocolExecutionOutcome.Succeeded,
        new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
        new(0, 0, 0, 0, 0, 0),
        summary,
        log
    )
    {
        CommandId = commandId
    };

    private static async Task SpinWaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingFactory(ScriptedProcess process) : IInstallerProtocolProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public IInstallerProtocolProcess Start(ProcessStartInfo startInfo)
        {
            this.StartInfo = startInfo;
            return process;
        }
    }

    private sealed class SingleProcessFactory(IInstallerProtocolProcess process) : IInstallerProtocolProcessFactory
    {
        public IInstallerProtocolProcess Start(ProcessStartInfo startInfo) => process;
    }

    private sealed class RollForwardSystemFactory : IInstallerProtocolProcessFactory
    {
        public IInstallerProtocolProcess Start(ProcessStartInfo startInfo)
        {
            // Local/CI developer hosts may only retain the current SDK runtime; release packages are self-contained.
            startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            return new SystemInstallerProtocolProcessFactory().Start(startInfo);
        }
    }

    private sealed class ScriptedProcess : IInstallerProtocolProcess
    {
        private readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool CompleteExitOnTerminate;
        private readonly bool FaultWait;
        private readonly bool FaultDispose;
        private readonly ResponseStream Responses;
        private readonly RequestStream RequestsStream;
        public Func<ProtocolRequest, byte[]?> Responder { get => this.RequestsStream.Responder; set => this.RequestsStream.Responder = value; }
        public IReadOnlyList<ProtocolRequest> Requests => this.RequestsStream.SnapshotRequests();
        public TaskCompletionSource RequestObserved => this.RequestsStream.RequestObserved;
        public Stream Input { get; }
        public Stream Output { get; }
        public Stream Error { get; }
        public bool Terminated { get; private set; }
        public bool WaitObserved { get; private set; }
        public bool Disposed { get; private set; }
        public bool InputDisposed => this.Input switch { RequestStream request => request.Disposed, CancellationResistantWriteStream resistant => resistant.Disposed, _ => false };

        public ScriptedProcess(
            Func<ProtocolRequest, byte[]?> responder,
            Stream? input = null,
            Stream? output = null,
            Stream? error = null,
            bool completeWaitInitially = true,
            bool completeExitOnTerminate = true,
            bool faultWait = false,
            bool faultDispose = false,
            Func<int, Task>? beforeResponseChunk = null
        )
        {
            this.Responses = new ResponseStream(beforeResponseChunk);
            this.RequestsStream = new RequestStream(this.Responses, responder);
            this.Input = input ?? this.RequestsStream;
            this.Output = output ?? this.Responses;
            this.Error = error ?? new MemoryStream();
            this.CompleteExitOnTerminate = completeExitOnTerminate;
            this.FaultWait = faultWait;
            this.FaultDispose = faultDispose;
            if (completeWaitInitially)
                this.Exit.TrySetResult();
        }

        public Task WaitForExitAsync()
        {
            this.WaitObserved = true;
            if (this.FaultWait)
                return Task.FromException(new IOException("private wait failure"));
            if (this.Exit.Task.IsCompletedSuccessfully)
                this.Responses.Complete();
            return this.Exit.Task;
        }


        public void Terminate()
        {
            this.Terminated = true;
            if (this.CompleteExitOnTerminate)
                this.Exit.TrySetResult();
            this.Responses.Complete();
        }

        public void CompleteExit()
        {
            this.Exit.TrySetResult();
            this.Responses.Complete();
        }

        public void CompleteOutput() => this.Responses.Complete();

        public void Publish(byte[] response) => this.Responses.Set(response);

        public void Dispose()
        {
            this.Disposed = true;
            this.Responses.Complete();
            if (this.FaultDispose)
                throw new IOException("/home/private/dispose-failure");
        }
    }

    private sealed class ThrowingSetupProcess : IInstallerProtocolProcess
    {
        private readonly MemoryStream InputStream = new();
        public Stream Input => this.InputStream;
        public Stream Output => throw new IOException("private output setup detail");
        public Stream Error => throw new IOException("private error setup detail");
        public bool Terminated { get; private set; }
        public bool WaitObserved { get; private set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync() { this.WaitObserved = true; return Task.CompletedTask; }
        public void Terminate() => this.Terminated = true;
        public void Dispose() => this.Disposed = true;
    }

    private sealed class RequestStream(ResponseStream responses, Func<ProtocolRequest, byte[]?> responder) : MemoryStream
    {
        private readonly object RequestsLock = new();
        private readonly List<ProtocolRequest> Requests = [];
        private long Consumed;
        public Func<ProtocolRequest, byte[]?> Responder { get; set; } = responder;
        public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public IReadOnlyList<ProtocolRequest> SnapshotRequests()
        {
            lock (this.RequestsLock)
                return this.Requests.ToArray();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            byte[] all = this.ToArray();
            int length = checked((int)(all.Length - this.Consumed));
            string line = Encoding.UTF8.GetString(all, checked((int)this.Consumed), length).TrimEnd('\n');
            this.Consumed = all.Length;
            ProtocolRequest request = ProtocolJsonSerializer.DeserializeRequestLine(line);
            lock (this.RequestsLock)
                this.Requests.Add(request);
            this.RequestObserved.TrySetResult();
            byte[]? response = this.Responder(request);
            if (response is not null)
            {
                if (response.Length == 0)
                    responses.Complete();
                else
                    responses.Set(response);
            }
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ResponseStream(Func<int, Task>? beforeChunk = null) : Stream
    {
        private readonly Channel<byte[]> Responses = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        private byte[] Current = [];
        private int Offset;
        private int ChunkCount;
        public void Set(byte[] bytes) => this.Responses.Writer.TryWrite(bytes);
        public void Complete() => this.Responses.Writer.TryComplete();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (this.Offset == this.Current.Length)
            {
                try { this.Current = await this.Responses.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
                int chunk = Interlocked.Increment(ref this.ChunkCount);
                if (beforeChunk is not null)
                    await beforeChunk(chunk);
                this.Offset = 0;
            }
            int copy = Math.Min(buffer.Length, this.Current.Length - this.Offset);
            this.Current.AsMemory(this.Offset, copy).CopyTo(buffer);
            this.Offset += copy;
            return copy;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationResistantStream : Stream
    {
        private readonly TaskCompletionSource<int> Never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(this.Never.Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationResistantWriteStream : Stream
    {
        private readonly TaskCompletionSource Never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(this.Never.Task);
        protected override void Dispose(bool disposing) { this.Disposed = true; base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
