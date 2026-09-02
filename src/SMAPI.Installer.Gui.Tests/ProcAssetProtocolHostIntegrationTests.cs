using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class ProcAssetProtocolHostIntegrationTests
{
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;
    private const string ExpectedGenericMessage = "The selected release asset set failed strict package verification.";
    private const string ExpectedIntegrityMessage = "The selected release package failed an integrity verification check.";

    [Test]
    public async Task BuiltHostCorrelatesProcRejectionsAndRemainsUsableAfterMalformedPath()
    {
        string installer = GetBuiltInstallerPath();
        File.Exists(installer).Should().BeTrue("the GUI test project builds the actual installer protocol host");

        ForkReleaseIdentity release = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1");
        string workspacePath = Path.Combine(Path.GetTempPath(), $"smapi-proc-host-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        File.SetUnixFileMode(
            workspacePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );

        string[] leaves =
        [
            release.PackageAssetName,
            ReleasePackageVerifier.ChecksumAssetName,
            ReleasePackageVerifier.BuildMetadataAssetName,
            VerifiedInstallerPackageFactory.GetManifestAssetName(release),
            VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(release),
            VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(release)
        ];
        foreach (string leaf in leaves)
        {
            string path = Path.Combine(workspacePath, leaf);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        TrackingRollForwardSystemFactory processFactory = new();
        SafeFileHandle? workspaceHandle = null;
        try
        {
            workspaceHandle = new SafeFileHandle(
                (IntPtr)open(workspacePath, OpenDirectory | OpenCloseOnExec),
                ownsHandle: true
            );
            using (workspaceHandle)
            {
                workspaceHandle.IsInvalid.Should().BeFalse("the test workspace must have one live parent-owned directory descriptor");
                using LinuxAnchoredFileSystem identitySource = new(workspacePath);
                LinuxFileIdentity identity = identitySource.GetCurrentRootIdentity();
                string procRoot = $"/proc/{Environment.ProcessId}/fd/{workspaceHandle.DangerousGetHandle().ToInt32()}";
                InstallerPackageOpenInput validProcInput = new(
                    release.Tag,
                    "0123456789abcdef0123456789abcdef01234567",
                    $"{procRoot}/{leaves[0]}",
                    $"{procRoot}/{leaves[1]}",
                    $"{procRoot}/{leaves[2]}",
                    $"{procRoot}/{leaves[3]}",
                    $"{procRoot}/{leaves[4]}",
                    $"{procRoot}/{leaves[5]}",
                    new ProtocolProcWorkspaceIdentity(
                        identity.DeviceMajor,
                        identity.DeviceMinor,
                        identity.Inode,
                        identity.ChangeSeconds,
                        identity.ChangeNanoseconds
                    )
                );

                using LinuxExternalExecutableLease executable = LinuxExternalExecutableLease.Open(installer);
                await using (ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(
                    installer,
                    processFactory,
                    executableLease: executable
                ))
                {
                    HandshakeEvent handshake = await client.HandshakeAsync("SMAPI GUI proc integration test", "1");
                    handshake.Capabilities.Should().Contain(ProcessInstallerProtocolClient.PackageVerificationCapability);
                    handshake.Capabilities.Should().Contain(ProcessInstallerProtocolClient.CandidateApprovalCapability);

                    InstallerPackageOpenRejection retainedAssetRejection = (await client.OpenPackageAsync(validProcInput))
                        .Should().BeOfType<InstallerPackageOpenRejection>().Subject;
                    AssertSanitizedPackageRejection(
                        retainedAssetRejection,
                        ProtocolPrePlanErrorCode.PackageIntegrityRejected,
                        ExpectedIntegrityMessage,
                        workspacePath,
                        procRoot
                    );

                    string malformedPid = validProcInput.PackagePath.Replace(
                        $"/proc/{Environment.ProcessId}/",
                        $"/proc/0{Environment.ProcessId}/",
                        StringComparison.Ordinal
                    );
                    InstallerPackageOpenRejection malformedRejection = (await client.OpenPackageAsync(
                        validProcInput with { PackagePath = malformedPid }
                    )).Should().BeOfType<InstallerPackageOpenRejection>().Subject;
                    AssertSanitizedPackageRejection(
                        malformedRejection,
                        ProtocolPrePlanErrorCode.PackageRejected,
                        ExpectedGenericMessage,
                        workspacePath,
                        procRoot
                    );

                    InstallerPackageOpenRejection postMalformedRejection = (await client.OpenPackageAsync(validProcInput))
                        .Should().BeOfType<InstallerPackageOpenRejection>().Subject;
                    AssertSanitizedPackageRejection(
                        postMalformedRejection,
                        ProtocolPrePlanErrorCode.PackageIntegrityRejected,
                        ExpectedIntegrityMessage,
                        workspacePath,
                        procRoot
                    );
                    client.SessionFaulted.IsCompleted.Should().BeFalse("normal correlated rejections keep the live session usable");
                }
            }

            processFactory.StartedProcess.Should().NotBeNull();
            processFactory.StartedProcess!.WaitCompleted.Should().BeTrue("client disposal must confirm that the actual host exited");
            processFactory.StartedProcess.Disposed.Should().BeTrue("the reaped actual host wrapper must be disposed");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
        workspaceHandle.Should().NotBeNull();
        workspaceHandle!.IsClosed.Should().BeTrue("the parent proc-directory authority must not leak from the test");
        Directory.Exists(workspacePath).Should().BeFalse("the private workspace and all six dummy assets must be removed");
    }

    private static void AssertSanitizedPackageRejection(
        InstallerPackageOpenRejection rejection,
        ProtocolPrePlanErrorCode expectedCode,
        string expectedMessage,
        string privateWorkspacePath,
        string procRoot
    )
    {
        rejection.ErrorCode.Should().Be(expectedCode);
        rejection.NextAction.Should().Be(ProtocolNextAction.ReopenVerifiedPackage);
        rejection.Message.Should().Be(expectedMessage);
        rejection.Message.Should().NotContain(privateWorkspacePath).And.NotContain(procRoot);
        rejection.IsTerminal.Should().BeFalse();
    }

    private static string GetBuiltInstallerPath()
    {
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent?.Name
            ?? throw new AssertionException("The GUI test build configuration couldn't be derived.");
        return Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "SMAPI.Installer", "bin", configuration, "SMAPI.Installer"
        ));
    }

    private sealed class TrackingRollForwardSystemFactory : IInstallerProtocolProcessFactory
    {
        public TrackingProcess? StartedProcess { get; private set; }

        public IInstallerProtocolProcess Start(ProcessStartInfo startInfo)
        {
            // Developer/CI hosts may retain only the current SDK runtime; published releases are self-contained.
            startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            this.StartedProcess = new TrackingProcess(new SystemInstallerProtocolProcessFactory().Start(startInfo));
            return this.StartedProcess;
        }
    }

    private sealed class TrackingProcess(IInstallerProtocolProcess inner) : IInstallerProtocolProcess
    {
        public Stream Input => inner.Input;
        public Stream Output => inner.Output;
        public Stream Error => inner.Error;
        public bool WaitCompleted { get; private set; }
        public bool Disposed { get; private set; }

        public async Task WaitForExitAsync()
        {
            await inner.WaitForExitAsync();
            this.WaitCompleted = true;
        }

        public void Terminate()
        {
            inner.Terminate();
        }

        public void Dispose()
        {
            inner.Dispose();
            this.Disposed = true;
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags);
}
