using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Gui.Backend;

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
                printf '%s\n' "{\"protocolVersion\":1,\"messageType\":\"handshake.event\",\"payload\":{\"commandId\":\"$command_id\",\"sessionId\":\"11111111111111111111111111111111\",\"serverVersion\":\"1\",\"capabilities\":[\"verified-local-package\",\"linux-game-discovery\",\"linux-game-validation\"]}}"
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
                nameof(IInstallerProtocolClient.InspectPlanAsync)
            ]);
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
        result.WarningCount.Should().Be(0);
        result.RecommendedDefault.Should().Be(ProtocolRecommendedDefault.Cancel);
        result.RequiresConfirmation.Should().BeTrue();
        if (operation == InstallerOperation.Backup)
            result.TargetRelease.Should().Be(result.CurrentRelease);
    }

    [Test]
    public async Task BackupAcceptsTheExactUninstalledNullReleasePair()
    {
        ReadOnlyPlanScript script = new(InstallerOperation.Backup)
        {
            CurrentRelease = null,
            ObservedState = ObservedInstallState.NotInstalled
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanSuccess result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Backup))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        result.ObservedState.Should().Be(ObservedInstallState.NotInstalled);
        result.CurrentRelease.Should().BeNull();
        result.TargetRelease.Should().BeNull();
    }

    [Test]
    public async Task InspectPlanAggregatesDynamicPagesAndReturnsOnlyCountedPathFreePresentation()
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
        result.CanExecute.Should().BeFalse();
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
        result.WarningCount.Should().Be(2);
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
            .And.NotContain("private-")
            .And.NotContain(ReadOnlyPlanScript.PackageId.Value)
            .And.NotContain(script.PlanId.Value)
            .And.NotContain(script.ExecutionDigest.Value)
            .And.NotContain(new string('a', 64));
        GetProjectionPropertyNames(typeof(InstallerReadOnlyPlanResult)).Should().NotContain(name =>
            name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Evidence", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Warning", StringComparison.OrdinalIgnoreCase) && name != nameof(InstallerReadOnlyPlanSuccess.WarningCount)
        );
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task InspectPlanProjectsNormalRejectionWithoutPrivateTextOrAuthorityAndClosesOnlyTerminal(bool terminal)
    {
        const string privateText = "/home/private-user/secret package detail";
        ReadOnlyPlanScript script = new(InstallerOperation.Install)
        {
            Rejection = new(
                Session,
                ProtocolPrePlanErrorCode.InspectionFailed,
                privateText,
                ProtocolNextAction.InspectAgain,
                terminal,
                "/tmp/private-installer.log"
            )
        };
        ScriptedProcess process = new(script.Respond);
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        InstallerReadOnlyPlanRejection result = (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install))
            .Should().BeOfType<InstallerReadOnlyPlanRejection>().Subject;

        result.Should().Be(new InstallerReadOnlyPlanRejection(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, terminal));
        result.ToString().Should().NotContain(privateText).And.NotContain("private-installer.log");
        process.Terminated.Should().Be(terminal);
        if (!terminal)
        {
            script.Rejection = null;
            (await client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();
        }
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
        foreach (string omitted in BaseRequiredCapabilities)
        {
            ScriptedProcess process = new(request => Serialize(new HandshakeEvent(
                Session,
                "1",
                BaseRequiredCapabilities.Where(value => value != omitted).ToArray()
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
    public async Task MissingOptionalPlanCapabilityFailsClosedOnlyWhenPlanInspectionIsRequested()
    {
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", BaseRequiredCapabilities) { CommandId = request.CommandId }),
            OpenPackageRequest => Serialize(CreateOpened(Session, request.CommandId)),
            _ => throw new AssertionException("A capability-rejected plan must not be sent to the backend.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await OpenVerifiedSessionAsync(client);

        Func<Task> action = () => client.InspectPlanAsync(ReadOnlyPlanScript.GamePath, InstallerOperation.Install);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Requests.Any(item => item is InspectPlanRequest).Should().BeFalse();
        process.Terminated.Should().BeTrue();
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
            typeof(InstallerPlanCandidateCount)
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

    private sealed class ReadOnlyPlanScript
    {
        public const string GamePath = "/games/private Stardew Valley";
        public static readonly ProtocolPackageId PackageId = ProtocolPackageId.Parse("22222222222222222222222222222222");

        private bool PageFaultReturned;

        public InstallerOperation Operation { get; }
        public ProtocolPlanId PlanId { get; } = ProtocolPlanId.Parse("33333333333333333333333333333333");
        public ProtocolPlanDigest ExecutionDigest { get; } = ProtocolPlanDigest.Parse(new string('8', 64));
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
        public bool SuppressPageResponse { get; init; }

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
                InspectPlanRequest inspect => this.CreatePlanResponse(inspect),
                GetPlanPageRequest when this.SuppressPageResponse => null,
                GetPlanPageRequest page => this.CreatePageResponse(page),
                _ => throw new AssertionException("Unexpected protocol request in read-only plan script.")
            };
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
            PlanEvent plan = this.CreatePlan(request);
            string line = ProtocolJsonSerializer.SerializeLine(plan);
            if (this.HeaderFault == PlanHeaderFault.WrongDefault)
                line = line.Replace("\"recommendedDefault\":\"cancel\"", "\"recommendedDefault\":999", StringComparison.Ordinal);
            else if (this.HeaderFault == PlanHeaderFault.NoConfirmation)
                line = line.Replace("\"requiresConfirmation\":true", "\"requiresConfirmation\":false", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(line + "\n");
        }

        private PlanEvent CreatePlan(InspectPlanRequest request)
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
                4
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
                CommandId = request.CommandId
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

    private static string[] BaseRequiredCapabilities =>
    [
        ProcessInstallerProtocolClient.PackageVerificationCapability,
        ProcessInstallerProtocolClient.GameDiscoveryCapability,
        ProcessInstallerProtocolClient.GameValidationCapability
    ];

    private static string[] RequiredCapabilities =>
    [
        .. BaseRequiredCapabilities,
        ProcessInstallerProtocolClient.PlanInspectionCapability
    ];

    private static byte[] Serialize(ProtocolEvent value) => Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(value) + "\n");

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
        private readonly ResponseStream Responses;
        private readonly RequestStream RequestsStream;
        public Func<ProtocolRequest, byte[]?> Responder { get => this.RequestsStream.Responder; set => this.RequestsStream.Responder = value; }
        public List<ProtocolRequest> Requests => this.RequestsStream.Requests;
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
            bool faultWait = false
        )
        {
            this.Responses = new ResponseStream();
            this.RequestsStream = new RequestStream(this.Responses, responder);
            this.Input = input ?? this.RequestsStream;
            this.Output = output ?? this.Responses;
            this.Error = error ?? new MemoryStream();
            this.CompleteExitOnTerminate = completeExitOnTerminate;
            this.FaultWait = faultWait;
            if (completeWaitInitially)
                this.Exit.TrySetResult();
        }

        public Task WaitForExitAsync()
        {
            this.WaitObserved = true;
            if (this.FaultWait)
                return Task.FromException(new IOException("private wait failure"));
            return this.Exit.Task;
        }


        public void Terminate()
        {
            this.Terminated = true;
            if (this.CompleteExitOnTerminate)
                this.Exit.TrySetResult();
            this.Responses.Complete();
        }

        public void CompleteExit() => this.Exit.TrySetResult();

        public void Publish(byte[] response) => this.Responses.Set(response);

        public void Dispose()
        {
            this.Disposed = true;
            this.Responses.Complete();
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
        private long Consumed;
        public Func<ProtocolRequest, byte[]?> Responder { get; set; } = responder;
        public List<ProtocolRequest> Requests { get; } = [];
        public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            byte[] all = this.ToArray();
            int length = checked((int)(all.Length - this.Consumed));
            string line = Encoding.UTF8.GetString(all, checked((int)this.Consumed), length).TrimEnd('\n');
            this.Consumed = all.Length;
            ProtocolRequest request = ProtocolJsonSerializer.DeserializeRequestLine(line);
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

    private sealed class ResponseStream : Stream
    {
        private readonly Channel<byte[]> Responses = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        private byte[] Current = [];
        private int Offset;
        public void Set(byte[] bytes) => this.Responses.Writer.TryWrite(bytes);
        public void Complete() => this.Responses.Writer.TryComplete();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (this.Offset == this.Current.Length)
            {
                try { this.Current = await this.Responses.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
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
