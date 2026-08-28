using System.Security.Cryptography;
using System.Runtime.Versioning;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Transactions;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class InstallerOperationLeaseTests
{
    private string TempRoot = null!;
    private string GameRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-lease-tests-{Guid.NewGuid():N}");
        this.GameRoot = Path.Combine(this.TempRoot, "game");
        Directory.CreateDirectory(this.GameRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public void ReserveNextGeneration_IsDurableAndInvalidatesPriorGeneration()
    {
        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot))
        {
            lease.Generation.Should().Be(0);
            lease.AssertRootAndGeneration(lease.RootIdentity, 0);
            lease.ReserveNextGeneration(0).Should().Be(1);
            Action stale = () => lease.AssertRootAndGeneration(lease.RootIdentity, 0);
            stale.Should().Throw<InstallerTransactionException>()
                .Which.Code.Should().Be(TransactionErrorCode.PathChanged);
        }

        using InstallerOperationLease reopened = InstallerOperationLease.Acquire(this.GameRoot);
        reopened.Generation.Should().Be(1);
        reopened.AssertRootAndGeneration(reopened.RootIdentity, 1);
    }

    [Test]
    public void ReserveNextGeneration_TemporaryPathSwapBeforePublicationRejectsWithoutAdvancing()
    {
        GenerationPublicationSwapFaultInjector fault = new(this.GameRoot, this.TempRoot);
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot, fault);

        Action reserve = () => lease.ReserveNextGeneration(0);

        reserve.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.WorkspaceConflict);
        fault.DisplacedPath.Should().NotBeNull();
        File.Exists(fault.DisplacedPath!).Should().BeTrue();
        lease.Generation.Should().Be(0);
        lease.AssertRootAndGeneration(lease.RootIdentity, 0);
    }

    [Test]
    public void AssertRootAndGeneration_IdenticalContentRootPathSwap_Rejects()
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot);
        string displaced = Path.Combine(this.TempRoot, "displaced");
        Directory.Move(this.GameRoot, displaced);
        Directory.CreateDirectory(this.GameRoot);

        Action action = () => lease.AssertRootAndGeneration(lease.RootIdentity, lease.Generation);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.PathChanged);
    }

    [Test]
    public void AssertRootAndGeneration_GenerationLeafChanged_Rejects()
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot);
        string generationPath = Path.Combine(this.GameRoot, ".smapi-installer", "operation-generation");
        File.WriteAllText(generationPath, "00000000000000000001\n");

        Action action = () => lease.AssertRootAndGeneration(lease.RootIdentity, 0);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.PathChanged);
    }

    [Test]
    public void Acquire_ConcurrentLease_Rejects()
    {
        using InstallerOperationLease first = InstallerOperationLease.Acquire(this.GameRoot);

        Action action = () => InstallerOperationLease.Acquire(this.GameRoot).Dispose();

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.ConcurrentOperation);
    }

    [Test]
    public void ApplyLocked_ConfirmedRootAndGeneration_CommitsAndConsumesGeneration()
    {
        string payloadRoot = Path.Combine(this.TempRoot, "payload");
        Directory.CreateDirectory(Path.Combine(payloadRoot, "files"));
        File.WriteAllText(Path.Combine(payloadRoot, "files", "runtime"), "verified runtime");
        string hash = Hash("verified runtime");
        TransactionPlan plan = new(
            Guid.NewGuid(),
            [
                new TransactionFileOperation(
                    TransactionOperationKind.WriteFile,
                    "smapi-internal/runtime",
                    null,
                    "files/runtime",
                    hash,
                    420
                )
            ]
        );
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot);
        using StardewModdingAPI.Installer.Core.Security.LinuxAnchoredFileSystem payload = new(payloadRoot);

        TransactionResult result = new InstallerTransactionExecutor().ApplyLocked(
            lease,
            payload,
            plan,
            lease.RootIdentity,
            lease.Generation
        );

        result.Status.Should().Be(TransactionStatus.Committed);
        lease.Generation.Should().Be(1);
        File.ReadAllText(Path.Combine(this.GameRoot, "smapi-internal", "runtime")).Should().Be("verified runtime");
    }

    [Test]
    public void ApplyLocked_RootPathSwap_RejectsWithoutMutation()
    {
        string payloadRoot = Path.Combine(this.TempRoot, "payload");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Combine(payloadRoot, "runtime"), "verified runtime");
        TransactionPlan plan = new(
            Guid.NewGuid(),
            [
                new TransactionFileOperation(
                    TransactionOperationKind.WriteFile,
                    "smapi-internal/runtime",
                    null,
                    "runtime",
                    Hash("verified runtime"),
                    420
                )
            ]
        );
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(this.GameRoot);
        using StardewModdingAPI.Installer.Core.Security.LinuxAnchoredFileSystem payload = new(payloadRoot);
        string displaced = Path.Combine(this.TempRoot, "displaced");
        Directory.Move(this.GameRoot, displaced);
        Directory.CreateDirectory(this.GameRoot);

        Action action = () => new InstallerTransactionExecutor().ApplyLocked(
            lease,
            payload,
            plan,
            lease.RootIdentity,
            lease.Generation
        );

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.PathChanged);
        File.Exists(Path.Combine(this.GameRoot, "smapi-internal", "runtime")).Should().BeFalse();
        File.Exists(Path.Combine(displaced, "smapi-internal", "runtime")).Should().BeFalse();
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class GenerationPublicationSwapFaultInjector : IInstallerOperationLeaseFaultInjector
    {
        private readonly string GameRoot;
        private readonly string TempRoot;
        public string? DisplacedPath { get; private set; }

        public GenerationPublicationSwapFaultInjector(string gameRoot, string tempRoot)
        {
            this.GameRoot = gameRoot;
            this.TempRoot = tempRoot;
        }

        public void BeforeGenerationPublicationIdentityCheck(string temporaryName)
        {
            string current = Path.Combine(this.GameRoot, ".smapi-installer", temporaryName);
            this.DisplacedPath = Path.Combine(this.TempRoot, "displaced-operation-generation.tmp");
            File.Move(current, this.DisplacedPath);
            File.Copy(this.DisplacedPath, current);
            File.SetUnixFileMode(current, (UnixFileMode)0x180);
        }
    }
}
