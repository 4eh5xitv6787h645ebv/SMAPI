using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
internal sealed class GitHubArtifactAttestationVerifierTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string EmbeddedVersion = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1";
    private const string PackageName = "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip";
    private const string ManifestName = "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-install-manifest.json";
    private const string SourceCommit = "1111111111111111111111111111111111111111";
    private const string SourceTree = "2222222222222222222222222222222222222222";
    private const string Workflow = "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string SourceReference = "refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string San = "https://github.com/4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string Invocation = "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353/attempts/1";
    private const string Repository = "https://github.com/4eh5xitv6787h645ebv/SMAPI";
    private const string RepositoryName = "4eh5xitv6787h645ebv/SMAPI";
    private const string OwnerUrl = "https://github.com/4eh5xitv6787h645ebv";
    private const string Timestamp = "2026-08-28T21:51:36+08:00";
    private static readonly Sha256Digest PackageSha256 = Sha256Digest.Parse(new string('a', 64));
    private static readonly Sha256Digest ManifestSha256 = Sha256Digest.Parse(new string('b', 64));
    private string TempRoot = null!;
    private SafeFileHandle PackageFixtureHandle = null!;
    private SafeFileHandle CliFixtureHandle = null!;
    private SafeFileHandle BundleFixtureHandle = null!;
    private LinuxSealedFileLease PackageFixtureLease = null!;
    private LinuxSealedFileLease CliFixtureLease = null!;
    private LinuxSealedFileLease BundleFixtureLease = null!;

    public static IEnumerable<AuthorityField> EveryAuthorityField => Enum.GetValues<AuthorityField>().Where(value => value != AuthorityField.None);

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-attestation-verifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.PackageFixtureHandle = CreateSealedFixture("smapi-attestation-package-fixture", "package"u8);
        this.CliFixtureHandle = CreateSealedFixture("smapi-attestation-cli-fixture", "cli"u8);
        this.BundleFixtureHandle = CreateSealedFixture("smapi-attestation-bundle-fixture", "bundle"u8);
        this.PackageFixtureLease = LinuxSealedFile.LeaseForExternalRead(this.PackageFixtureHandle);
        this.CliFixtureLease = LinuxSealedFile.LeaseForExternalRead(this.CliFixtureHandle);
        this.BundleFixtureLease = LinuxSealedFile.LeaseForExternalRead(this.BundleFixtureHandle);
    }

    [TearDown]
    public void TearDown()
    {
        this.BundleFixtureLease.Dispose();
        this.CliFixtureLease.Dispose();
        this.PackageFixtureLease.Dispose();
        this.BundleFixtureHandle.Dispose();
        this.CliFixtureHandle.Dispose();
        this.PackageFixtureHandle.Dispose();
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyAsync_UsesExactCompatibleArgumentsAndReturnsClosedTwoSubjectTrust()
    {
        StubRunner runner = new(WriteJson());
        GitHubArtifactAttestationVerificationRequest request = this.CreateRequest();

        VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(request);

        runner.Request.Should().NotBeNull();
        runner.Request!.ExecutablePath.Should().Be(this.CliFixtureLease.ProcPath);
        runner.Request.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        runner.Request.MaximumStandardOutputBytes.Should().Be(2 * 1024 * 1024);
        runner.Request.MaximumStandardErrorBytes.Should().Be(64 * 1024);
        runner.Request.BundleAuthority!.ProcPath.Should().Be(this.BundleFixtureLease.ProcPath);
        runner.Request.BundleArgumentIndex.Should().Be(4);
        runner.Request.Arguments.Should().Equal(
            "attestation",
            "verify",
            this.PackageFixtureLease.ProcPath,
            "--bundle",
            GitHubAttestationProcessRequest.BundlePathPlaceholder,
            "--hostname",
            "github.com",
            "--repo",
            RepositoryName,
            "--predicate-type",
            "https://slsa.dev/provenance/v1",
            "--cert-oidc-issuer",
            "https://token.actions.githubusercontent.com",
            "--cert-identity",
            San,
            "--signer-digest",
            SourceCommit,
            "--source-ref",
            SourceReference,
            "--source-digest",
            SourceCommit,
            "--deny-self-hosted-runners",
            "--limit",
            "2",
            "--format",
            "json"
        );
        runner.Request.Arguments.Should().NotContain(["--signer-workflow", "--no-public-good"]);
        trust.Identity.Should().Be(request.Identity);
        trust.ManifestSubject.Should().Be(request.ManifestSubject);
        trust.ManifestSubject.Should().NotBeSameAs(request.ManifestSubject, "attested fields must be parsed independently of retained-byte authority");
        trust.PackageSubject.Name.Should().Be(PackageName);
        trust.PackageSubject.Sha256.Should().Be(PackageSha256);
        trust.PackageSubject.ObservedSizeBytes.Should().Be(123456);
        trust.Evidence.RunInvocationUri.Should().Be(Invocation);
        trust.Evidence.TransparencyLogTimestampUtc.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 51, 36, TimeSpan.Zero));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task ProductionVerifyAsync_CleanProcessRejectsMissingBundleAndUsesLocalBundleWithoutCredentials()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        using VerifiedGitHubAttestationBundle bundle = await this.CreateVerifiedBundleAsync(package);
        string output = WriteJson(
            new FixtureOptions
            {
                PackageSubjectSha256 = package.Release.PackageSha256.Value,
                ManifestSubjectSha256 = package.ManifestSha256.Value
            }
        );
        string encodedOutput = Convert.ToBase64String(Encoding.UTF8.GetBytes(output));
        string script = $$"""
            #!/bin/sh
            if [ "${GH_TOKEN+x}" = x ] || [ "${GITHUB_TOKEN+x}" = x ]; then
                exit 19
            fi
            bundle=
            while [ "$#" -gt 0 ]; do
                if [ "$1" = "--bundle" ]; then
                    shift
                    bundle="$1"
                fi
                shift
            done
            if [ -z "$bundle" ] || [ ! -r "$bundle" ]; then
                exit 4
            fi
            case "$bundle" in
                "$HOME"/verified-attestation-bundle.jsonl) ;;
                *) exit 18 ;;
            esac
            /usr/bin/printf '%s' '{{encodedOutput}}' | /usr/bin/base64 --decode
            """;
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync(script);
        GitHubAttestationProcessRunner processRunner = new();
        using LinuxSealedFileLease cliLease = cli.LeaseForExecution();
        using LinuxSealedFileLease packageLease = package.Package.LeasePackageForExternalRead();
        GitHubAttestationProcessRequest missingBundle = new(
            cliLease.ProcPath,
            ["attestation", "verify", packageLease.ProcPath],
            TimeSpan.FromSeconds(5),
            2 * 1024 * 1024,
            64 * 1024
        );

        Func<Task> onlineOnly = async () => await processRunner.RunAsync(missingBundle);
        PackageSecurityException processFailure = (await onlineOnly.Should().ThrowAsync<PackageSecurityException>()).Which;
        processFailure.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        processFailure.Message.Should().Be("The pinned attestation verifier process did not complete successfully.");

        VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(processRunner).VerifyAsync(package, bundle, cli);
        trust.Identity.Should().Be(package.Release);
        trust.ManifestSubject.Sha256.Should().Be(package.ManifestSha256);
    }

    [TestCaseSource(nameof(EveryAuthorityField))]
    public async Task VerifyAsync_RejectsEveryMutatedAuthorityField(AuthorityField mutation)
    {
        StubRunner runner = new(WriteJson(new FixtureOptions { Mutation = mutation }));
        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(this.CreateRequest());

        await verify.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier returned evidence outside the reviewed release policy.");
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task VerifyAsync_RequiresExactlyOneVerificationResult(int resultCount)
    {
        Func<Task> verify = () => this.Verify(WriteJson(new FixtureOptions { ResultCount = resultCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_NeverCombinesIndependentOneSubjectResults()
    {
        FixtureOptions options = new() { ResultCount = 2, IndependentOneSubjectResults = true };
        Func<Task> verify = () => this.Verify(WriteJson(options));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task VerifyAsync_RequiresExactlyTwoSubjectsInOneStatement(int subjectCount)
    {
        Func<Task> verify = () => this.Verify(WriteJson(new FixtureOptions { SubjectCount = subjectCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsReversedCanonicalSubjectOrderAndExtraDigestAlgorithm()
    {
        Func<Task> reversed = () => this.Verify(WriteJson(new FixtureOptions { ReverseSubjects = true }));
        Func<Task> extraDigest = () => this.Verify(WriteJson(new FixtureOptions { ExtraDigestAlgorithm = true }));

        await reversed.Should().ThrowAsync<PackageSecurityException>();
        await extraDigest.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsDuplicateAndUnknownAuthorityPropertiesButIgnoresOpaqueRawPayload()
    {
        Func<Task> duplicate = () => this.Verify(WriteJson(new FixtureOptions { DuplicateMediaType = true }));
        Func<Task> unknown = () => this.Verify(WriteJson(new FixtureOptions { UnknownCertificateProperty = true }));
        VerifiedTaggedPackageTrust accepted = await this.Verify(WriteJson(new FixtureOptions { ExtraOpaqueRawProperty = true }));

        await duplicate.Should().ThrowAsync<PackageSecurityException>();
        await unknown.Should().ThrowAsync<PackageSecurityException>();
        accepted.PackageSubject.Name.Should().Be(PackageName);
    }

    [Test]
    public async Task VerifyAsync_TreatsDisplayCertificateChainAndVerifiedIssuerProjectionAsBoundedNonAuthority()
    {
        VerifiedTaggedPackageTrust changed = await this.Verify(
            WriteJson(
                new FixtureOptions
                {
                    CertificateIssuer = "future verified chain display",
                    VerifiedIdentityIssuer = "display-only",
                    VerifiedIdentityIssuerRegexp = "future-display-regexp"
                }
            )
        );
        VerifiedTaggedPackageTrust omittedChain = await this.Verify(WriteJson(new FixtureOptions { OmitCertificateIssuer = true }));
        Func<Task> wrongChainType = () => this.Verify(WriteJson(new FixtureOptions { MalformedDisplayProjection = true }));
        Func<Task> wrongIssuerType = () => this.Verify(WriteJson(new FixtureOptions { MalformedVerifiedIssuerProjection = true }));
        Func<Task> oversizedChain = () => this.Verify(WriteJson(new FixtureOptions { CertificateIssuer = new string('x', 513) }));
        Func<Task> oversizedIssuer = () => this.Verify(WriteJson(new FixtureOptions { VerifiedIdentityIssuerRegexp = new string('x', 513) }));

        changed.Identity.Should().Be(CreateIdentity());
        omittedChain.Identity.Should().Be(CreateIdentity());
        await wrongChainType.Should().ThrowAsync<PackageSecurityException>();
        await wrongIssuerType.Should().ThrowAsync<PackageSecurityException>();
        await oversizedChain.Should().ThrowAsync<PackageSecurityException>();
        await oversizedIssuer.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task VerifyAsync_RequiresExactlyOneResolvedTaggedDependency(int dependencyCount)
    {
        Func<Task> verify = () => this.Verify(WriteJson(new FixtureOptions { DependencyCount = dependencyCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RequiresUnambiguousRekorTimestampAndNormalizesEquivalentValues()
    {
        Func<Task> zero = () => this.Verify(WriteJson(new FixtureOptions { TimestampCount = 0 }));
        Func<Task> conflicting = () => this.Verify(
            WriteJson(new FixtureOptions { TimestampCount = 2, SecondTimestamp = "2026-08-28T21:51:37+08:00" })
        );
        VerifiedTaggedPackageTrust same = await this.Verify(
            WriteJson(new FixtureOptions { TimestampCount = 2, SecondTimestamp = "2026-08-28T13:51:36Z" })
        );

        await zero.Should().ThrowAsync<PackageSecurityException>();
        await conflicting.Should().ThrowAsync<PackageSecurityException>();
        same.Evidence.TransparencyLogTimestampUtc.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 51, 36, TimeSpan.Zero));
    }

    [TestCase("2026-08-28T13:51:36")]
    [TestCase("2026-08-28 13:51:36Z")]
    [TestCase("2026-13-28T13:51:36Z")]
    [TestCase("not-a-timestamp")]
    public async Task VerifyAsync_RejectsMalformedOrZoneLessTimestamp(string timestamp)
    {
        Func<Task> verify = () => this.Verify(WriteJson(new FixtureOptions { FirstTimestamp = timestamp }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsMalformedOversizedAndNonUnicodeOutputWithoutEchoingRawSecret()
    {
        const string secret = "raw-private-signature-secret";
        string invalidUnicode = "[\"\ud800\"]";
        Func<Task> malformed = () => this.Verify("{");
        Func<Task> oversized = () => this.Verify(new string(' ', 2 * 1024 * 1024 + 1));
        Func<Task> unicode = () => this.Verify(invalidUnicode);
        Func<Task> secretFailure = () => this.Verify(WriteJson(new FixtureOptions { RawSecret = secret, Mutation = AuthorityField.MediaType }));

        await malformed.Should().ThrowAsync<PackageSecurityException>();
        await oversized.Should().ThrowAsync<PackageSecurityException>();
        await unicode.Should().ThrowAsync<PackageSecurityException>();
        PackageSecurityException exception = (await secretFailure.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.ToString().Should().NotContain(secret);
    }

    [Test]
    public void RequestRequiresCurrentRetainedDescriptorExactManifestAndCanonicalAbsoluteProcessPaths()
    {
        InstallationReleaseIdentity identity = CreateIdentity();
        VerifiedAttestedSubject manifest = CreateManifestSubject();
        Action otherProcess = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId + 1}/fd/1",
            manifest,
            this.CliFixtureLease.ProcPath,
            this.BundleFixtureLease.ProcPath
        );
        Action relativeProc = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            "package.zip",
            manifest,
            this.CliFixtureLease.ProcPath,
            this.BundleFixtureLease.ProcPath
        );
        Action nonCanonicalDescriptor = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId}/fd/01",
            manifest,
            this.CliFixtureLease.ProcPath,
            this.BundleFixtureLease.ProcPath
        );
        Action wrongManifest = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            new VerifiedAttestedSubject("manifest.json", ManifestSha256, 1),
            this.CliFixtureLease.ProcPath,
            this.BundleFixtureLease.ProcPath
        );
        Action relativeGh = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            "gh",
            this.BundleFixtureLease.ProcPath
        );
        Action otherProcessGh = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            $"/proc/{Environment.ProcessId + 1}/fd/1",
            this.BundleFixtureLease.ProcPath
        );
        Action nonCanonicalGhDescriptor = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            $"/proc/{Environment.ProcessId}/fd/01",
            this.BundleFixtureLease.ProcPath
        );
        Action relativeBundle = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            this.CliFixtureLease.ProcPath,
            "bundle.jsonl"
        );
        Action otherProcessBundle = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            this.CliFixtureLease.ProcPath,
            $"/proc/{Environment.ProcessId + 1}/fd/1"
        );
        Action nonCanonicalBundleDescriptor = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            this.PackageFixtureLease.ProcPath,
            manifest,
            this.CliFixtureLease.ProcPath,
            $"/proc/{Environment.ProcessId}/fd/01"
        );

        otherProcess.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        relativeProc.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        nonCanonicalDescriptor.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        wrongManifest.Should().Throw<ArgumentException>().WithParameterName("manifestSubject");
        relativeGh.Should().Throw<ArgumentException>().WithParameterName("gitHubCliPath");
        otherProcessGh.Should().Throw<ArgumentException>().WithParameterName("gitHubCliPath");
        nonCanonicalGhDescriptor.Should().Throw<ArgumentException>().WithParameterName("gitHubCliPath");
        relativeBundle.Should().Throw<ArgumentException>().WithParameterName("bundleProcPath");
        otherProcessBundle.Should().Throw<ArgumentException>().WithParameterName("bundleProcPath");
        nonCanonicalBundleDescriptor.Should().Throw<ArgumentException>().WithParameterName("bundleProcPath");
    }

    [Test]
    public async Task VerifyAsync_PropagatesCancellationWithoutCallingParser()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        StubRunner runner = new("private-invalid-output");
        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(this.CreateRequest(), cancellation.Token);

        await verify.Should().ThrowAsync<OperationCanceledException>();
        runner.Request.Should().BeNull();
    }

    [Test]
    public async Task ProductionVerifyAsync_DerivesOnlyAuthorityFieldsAndHoldsAllLeasesThroughRunnerAndParse()
    {
        HashSet<string> manifestDescriptorBaseline = FindDescriptors("memfd:smapi-installer-verified-manifest");
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        using VerifiedGitHubAttestationBundle bundle = await this.CreateVerifiedBundleAsync(package);
        string manifestProcPath = FindDescriptors("memfd:smapi-installer-verified-manifest")
            .Except(manifestDescriptorBaseline)
            .Single();
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync();
        InstallationReleaseIdentity expectedIdentity = package.Release;
        Sha256Digest expectedManifestSha256 = package.ManifestSha256;
        string output = WriteJson(
            new FixtureOptions
            {
                PackageSubjectSha256 = expectedIdentity.PackageSha256.Value,
                ManifestSubjectSha256 = expectedManifestSha256.Value
            }
        );
        string? packageProcPath = null;
        string? cliProcPath = null;
        string? bundleProcPath = null;
        StubRunner runner = new(
            output,
            request =>
            {
                packageProcPath = request.Arguments[2];
                bundleProcPath = request.BundleAuthority!.ProcPath;
                cliProcPath = request.ExecutablePath;
                File.Exists(packageProcPath).Should().BeTrue();
                File.Exists(bundleProcPath).Should().BeTrue();
                File.Exists(cliProcPath).Should().BeTrue();

                package.Dispose();
                bundle.Dispose();
                cli.Dispose();

                File.Exists(packageProcPath).Should().BeTrue("the package descriptor lease must outlive authority disposal");
                File.Exists(bundleProcPath).Should().BeTrue("the bundle descriptor lease must outlive authority disposal");
                File.Exists(cliProcPath).Should().BeTrue("the executable descriptor lease must outlive authority disposal");
                File.Exists(manifestProcPath).Should().BeTrue("the hidden retained manifest lease must outlive authority disposal");
            }
        );

        VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(package, bundle, cli);

        runner.Request.Should().NotBeNull();
        runner.Request!.ExecutablePath.Should().Be(cliProcPath);
        runner.Request.Arguments[2].Should().Be(packageProcPath);
        runner.Request.Arguments[4].Should().Be(GitHubAttestationProcessRequest.BundlePathPlaceholder);
        runner.Request.BundleAuthority!.ProcPath.Should().Be(bundleProcPath);
        runner.Request.Arguments.Should().ContainInOrder("--signer-digest", SourceCommit, "--source-ref", SourceReference);
        trust.Identity.Should().Be(expectedIdentity);
        trust.PackageSubject.Name.Should().Be(expectedIdentity.PackageAssetName);
        trust.PackageSubject.Sha256.Should().Be(expectedIdentity.PackageSha256);
        trust.ManifestSubject.Name.Should().Be(ManifestName);
        trust.ManifestSubject.Sha256.Should().Be(expectedManifestSha256);
        File.Exists(packageProcPath).Should().BeFalse("the production package lease must be released after parsing");
        File.Exists(bundleProcPath).Should().BeFalse("the production bundle lease must be released after parsing");
        File.Exists(cliProcPath).Should().BeFalse("the production executable lease must be released after parsing");
        File.Exists(manifestProcPath).Should().BeFalse("the production manifest lease must be released after parsing");
        FindDescriptors("memfd:smapi-installer-verified-manifest").Should().BeEquivalentTo(manifestDescriptorBaseline);
    }

    [TestCase("package")]
    [TestCase("bundle")]
    [TestCase("cli")]
    public async Task ProductionVerifyAsync_DisposalBeforeAllLeasesFailsWithoutCallingRunner(string disposedAuthority)
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        using VerifiedGitHubAttestationBundle bundle = await this.CreateVerifiedBundleAsync(package);
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync();
        StubRunner runner = new(WriteJson());
        if (disposedAuthority == "package")
            package.Dispose();
        else if (disposedAuthority == "bundle")
            bundle.Dispose();
        else
            cli.Dispose();

        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(package, bundle, cli);

        await verify.Should().ThrowAsync<ObjectDisposedException>();
        runner.Request.Should().BeNull();
        package.Dispose();
        cli.Dispose();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ProductionVerifyAsync_ReleasesEveryLeaseAfterRunnerOrParserFailure(bool runnerFailure)
    {
        HashSet<string> manifestDescriptorBaseline = FindDescriptors("memfd:smapi-installer-verified-manifest");
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        using VerifiedGitHubAttestationBundle bundle = await this.CreateVerifiedBundleAsync(package);
        string manifestProcPath = FindDescriptors("memfd:smapi-installer-verified-manifest")
            .Except(manifestDescriptorBaseline)
            .Single();
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync();
        string? packageProcPath = null;
        string? cliProcPath = null;
        string? bundleProcPath = null;
        StubRunner runner = new(
            runnerFailure ? WriteJson() : "{",
            request =>
            {
                packageProcPath = request.Arguments[2];
                bundleProcPath = request.BundleAuthority!.ProcPath;
                cliProcPath = request.ExecutablePath;
                package.Dispose();
                bundle.Dispose();
                cli.Dispose();
                if (runnerFailure)
                    throw new PackageSecurityException("Synthetic bounded runner failure.");
            }
        );

        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(package, bundle, cli);

        await verify.Should().ThrowAsync<PackageSecurityException>();
        File.Exists(packageProcPath).Should().BeFalse();
        File.Exists(bundleProcPath).Should().BeFalse();
        File.Exists(cliProcPath).Should().BeFalse();
        File.Exists(manifestProcPath).Should().BeFalse();
        FindDescriptors("memfd:smapi-installer-verified-manifest").Should().BeEquivalentTo(manifestDescriptorBaseline);
    }

    [Test]
    public async Task ProductionVerifyAsync_ReleasesEveryLeaseWhenCancellationArrivesDuringRunner()
    {
        HashSet<string> manifestDescriptorBaseline = FindDescriptors("memfd:smapi-installer-verified-manifest");
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        using VerifiedGitHubAttestationBundle bundle = await this.CreateVerifiedBundleAsync(package);
        string manifestProcPath = FindDescriptors("memfd:smapi-installer-verified-manifest")
            .Except(manifestDescriptorBaseline)
            .Single();
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync();
        using CancellationTokenSource cancellation = new();
        string? packageProcPath = null;
        string? cliProcPath = null;
        string? bundleProcPath = null;
        StubRunner runner = new(
            WriteJson(),
            request =>
            {
                packageProcPath = request.Arguments[2];
                bundleProcPath = request.BundleAuthority!.ProcPath;
                cliProcPath = request.ExecutablePath;
                package.Dispose();
                bundle.Dispose();
                cli.Dispose();
                cancellation.Cancel();
            }
        );

        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(package, bundle, cli, cancellation.Token);

        await verify.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(packageProcPath).Should().BeFalse();
        File.Exists(bundleProcPath).Should().BeFalse();
        File.Exists(cliProcPath).Should().BeFalse();
        File.Exists(manifestProcPath).Should().BeFalse();
        FindDescriptors("memfd:smapi-installer-verified-manifest").Should().BeEquivalentTo(manifestDescriptorBaseline);
    }

    [Test]
    public async Task ProductionVerifyAsync_RejectsBundleBoundToAnotherReleaseBeforeRunner()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        SafeFileHandle retained = CreateSealedFixture("smapi-mismatched-bundle", "bundle"u8);
        using VerifiedGitHubAttestationBundle bundle = new(
            CreateIdentity(),
            VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(CreateIdentity()),
            Sha256Digest.Hash("bundle"u8),
            6,
            retained
        );
        using PinnedGitHubCli cli = await this.CreatePinnedGitHubCliAsync();
        StubRunner runner = new(WriteJson());

        Func<Task> verify = () => new GitHubArtifactAttestationVerifier(runner).VerifyAsync(package, bundle, cli);

        await verify.Should().ThrowAsync<PackageSecurityException>().WithMessage("*different tagged release*");
        runner.Request.Should().BeNull();
    }

    private Task<VerifiedTaggedPackageTrust> Verify(string output)
    {
        return new GitHubArtifactAttestationVerifier(new StubRunner(output)).VerifyAsync(this.CreateRequest());
    }

    private GitHubArtifactAttestationVerificationRequest CreateRequest()
    {
        return new GitHubArtifactAttestationVerificationRequest(
            CreateIdentity(),
            this.PackageFixtureLease.ProcPath,
            CreateManifestSubject(),
            this.CliFixtureLease.ProcPath,
            this.BundleFixtureLease.ProcPath
        );
    }

    private static SafeFileHandle CreateSealedFixture(string name, ReadOnlySpan<byte> bytes)
    {
        SafeFileHandle handle = LinuxSealedFile.CreateAnonymous(name);
        try
        {
            RandomAccess.Write(handle, bytes, 0);
            LinuxSealedFile.SealImmutable(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async Task<VerifiedInstallerPackage> CreateVerifiedInstallerPackageAsync()
    {
        byte[] packageBytes = "synthetic installer package"u8.ToArray();
        string packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        ForkReleaseIdentity forkIdentity = ForkReleaseIdentity.Parse(Tag);
        string packagePath = Path.Combine(this.TempRoot, PackageName);
        File.WriteAllBytes(packagePath, packageBytes);
        InstallationReleaseIdentity releaseIdentity = new(
            InstallationReleaseIdentity.ReviewedRepository,
            Tag,
            EmbeddedVersion,
            PackageName,
            SourceCommit,
            SourceTree,
            Sha256Digest.Parse(packageHash),
            packageBytes.LongLength,
            Workflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            releaseIdentity,
            [
                new PackageManifestEntry(
                    NormalizedRelativePath.Parse("StardewValley"),
                    Sha256Digest.Parse(new string('d', 64)),
                    42,
                    493,
                    OwnedEntryKind.Launcher
                )
            ]
        );
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestPath = Path.Combine(this.TempRoot, ManifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {PackageName}\n{manifestHash}  {ManifestName}\n";
        string metadata = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = new { version = EmbeddedVersion, tag = Tag },
            source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = SourceCommit, tree = SourceTree },
            build = new { workflow = Workflow, configuration = "Release", runtime_identifier = "linux-x64" },
            artifacts = new object[]
            {
                new { name = PackageName, size_bytes = packageBytes.LongLength, sha256 = packageHash },
                new { name = ManifestName, size_bytes = manifestBytes.LongLength, sha256 = manifestHash }
            }
        });
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            metadata,
            forkIdentity,
            SourceCommit
        );
        try
        {
            VerifiedInstallerPackage result = await new VerifiedInstallerPackageFactory().VerifyAsync(release, manifestPath);
            release = null;
            return result;
        }
        finally
        {
            if (release is not null)
                await release.DisposeAsync();
        }
    }

    private async Task<PinnedGitHubCli> CreatePinnedGitHubCliAsync(string? script = null)
    {
        string directory = Path.Combine(this.TempRoot, $"cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, PinnedGitHubCli.ExecutableFilename);
        byte[] bytes = Encoding.UTF8.GetBytes(script ?? "#!/bin/sh\nexit 0\n");
        File.WriteAllBytes(path, bytes);
        PinnedGitHubCliTestIdentity identity = new(
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        );
        return await PinnedGitHubCli.OpenForTestingAsync(path, identity);
    }

    private async Task<VerifiedGitHubAttestationBundle> CreateVerifiedBundleAsync(VerifiedInstallerPackage package)
    {
        byte[] bytes = "synthetic local GitHub attestation bundle"u8.ToArray();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, bytes);
        return await new VerifiedGitHubAttestationBundleFactory().VerifyAsync(package, bundlePath, checksumPath);
    }

    private (string BundlePath, string ChecksumPath) WriteBundleFiles(VerifiedInstallerPackage package, byte[] bytes)
    {
        string bundleName = VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(package.Release);
        string checksumName = VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(package.Release);
        string directory = Path.Combine(this.TempRoot, $"bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string bundlePath = Path.Combine(directory, bundleName);
        string checksumPath = Path.Combine(directory, checksumName);
        File.WriteAllBytes(bundlePath, bytes);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(checksumPath, $"{sha256}  {bundleName}\n", new UTF8Encoding(false));
        return (bundlePath, checksumPath);
    }

    private static HashSet<string> FindDescriptors(string linkTargetFragment)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles($"/proc/{Environment.ProcessId}/fd"))
        {
            try
            {
                if (new FileInfo(path).LinkTarget?.Contains(linkTargetFragment, StringComparison.Ordinal) == true)
                    paths.Add(path);
            }
            catch (IOException)
            {
                // Another runtime descriptor can close while procfs is enumerated.
            }
        }
        return paths;
    }

    private static InstallationReleaseIdentity CreateIdentity()
    {
        return new InstallationReleaseIdentity(
            InstallationReleaseIdentity.ReviewedRepository,
            Tag,
            EmbeddedVersion,
            PackageName,
            SourceCommit,
            SourceTree,
            PackageSha256,
            123456,
            Workflow,
            "Release",
            "linux-x64"
        );
    }

    private static VerifiedAttestedSubject CreateManifestSubject()
    {
        return new VerifiedAttestedSubject(ManifestName, ManifestSha256, 6543);
    }

    internal static string WriteJson(FixtureOptions? options = null)
    {
        options ??= new FixtureOptions();
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            for (int index = 0; index < options.ResultCount; index++)
                WriteResult(writer, options, index);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteResult(Utf8JsonWriter writer, FixtureOptions options, int resultIndex)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("attestation");
        writer.WriteString("opaquePublicPayload", options.RawSecret ?? "synthetic-public-only");
        if (options.ExtraOpaqueRawProperty)
            writer.WriteString("futureOpaqueField", "ignored-after-gh-verification");
        writer.WriteEndObject();
        writer.WriteStartObject("verificationResult");
        Write(writer, options, AuthorityField.MediaType, "mediaType", "application/vnd.dev.sigstore.verificationresult+json;version=0.1");
        if (options.DuplicateMediaType)
            writer.WriteString("mediaType", "duplicate");
        WriteSignature(writer, options);
        WriteTimestamps(writer, options);
        WriteVerifiedIdentity(writer, options);
        WriteStatement(writer, options, resultIndex);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSignature(Utf8JsonWriter writer, FixtureOptions options)
    {
        writer.WriteStartObject("signature");
        writer.WriteStartObject("certificate");
        if (!options.OmitCertificateIssuer)
        {
            if (options.MalformedDisplayProjection)
                writer.WriteNumber("certificateIssuer", 1);
            else
                writer.WriteString("certificateIssuer", options.CertificateIssuer);
        }
        Write(writer, options, AuthorityField.CertificateSan, "subjectAlternativeName", San);
        Write(writer, options, AuthorityField.CertificateOidcIssuer, "issuer", "https://token.actions.githubusercontent.com");
        Write(writer, options, AuthorityField.CertificateWorkflowTrigger, "githubWorkflowTrigger", "push");
        Write(writer, options, AuthorityField.CertificateWorkflowSha, "githubWorkflowSHA", SourceCommit);
        Write(writer, options, AuthorityField.CertificateWorkflowName, "githubWorkflowName", "Linux alpha release qualification");
        Write(writer, options, AuthorityField.CertificateWorkflowRepository, "githubWorkflowRepository", RepositoryName);
        Write(writer, options, AuthorityField.CertificateWorkflowRef, "githubWorkflowRef", SourceReference);
        Write(writer, options, AuthorityField.CertificateBuildSignerUri, "buildSignerURI", San);
        Write(writer, options, AuthorityField.CertificateBuildSignerDigest, "buildSignerDigest", SourceCommit);
        Write(writer, options, AuthorityField.CertificateRunner, "runnerEnvironment", "github-hosted");
        Write(writer, options, AuthorityField.CertificateRepositoryUri, "sourceRepositoryURI", Repository);
        Write(writer, options, AuthorityField.CertificateRepositoryDigest, "sourceRepositoryDigest", SourceCommit);
        Write(writer, options, AuthorityField.CertificateRepositoryRef, "sourceRepositoryRef", SourceReference);
        Write(writer, options, AuthorityField.CertificateRepositoryId, "sourceRepositoryIdentifier", "1336010508");
        Write(writer, options, AuthorityField.CertificateOwnerUri, "sourceRepositoryOwnerURI", OwnerUrl);
        Write(writer, options, AuthorityField.CertificateOwnerId, "sourceRepositoryOwnerIdentifier", "45441845");
        Write(writer, options, AuthorityField.CertificateBuildConfigUri, "buildConfigURI", San);
        Write(writer, options, AuthorityField.CertificateBuildConfigDigest, "buildConfigDigest", SourceCommit);
        Write(writer, options, AuthorityField.CertificateBuildTrigger, "buildTrigger", "push");
        Write(writer, options, AuthorityField.CertificateInvocation, "runInvocationURI", Invocation);
        Write(writer, options, AuthorityField.CertificateVisibility, "sourceRepositoryVisibilityAtSigning", "public");
        if (options.UnknownCertificateProperty)
            writer.WriteString("unexpectedAuthority", "unexpected");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTimestamps(Utf8JsonWriter writer, FixtureOptions options)
    {
        writer.WriteStartArray("verifiedTimestamps");
        for (int index = 0; index < options.TimestampCount; index++)
        {
            writer.WriteStartObject();
            Write(writer, options, AuthorityField.TimestampType, "type", "Tlog");
            Write(writer, options, AuthorityField.TimestampUri, "uri", "https://rekor.sigstore.dev");
            string value = index == 0 ? options.FirstTimestamp : options.SecondTimestamp;
            Write(writer, options, AuthorityField.TimestampValue, "timestamp", value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteVerifiedIdentity(Utf8JsonWriter writer, FixtureOptions options)
    {
        writer.WriteStartObject("verifiedIdentity");
        writer.WriteStartObject("subjectAlternativeName");
        Write(writer, options, AuthorityField.VerifiedIdentitySan, "subjectAlternativeName", San);
        writer.WriteEndObject();
        writer.WriteStartObject("issuer");
        if (options.MalformedVerifiedIssuerProjection)
            writer.WriteNumber("issuer", 1);
        else
            writer.WriteString("issuer", options.VerifiedIdentityIssuer);
        writer.WriteString("regexp", options.VerifiedIdentityIssuerRegexp);
        writer.WriteEndObject();
        Write(writer, options, AuthorityField.VerifiedIdentityRunner, "runnerEnvironment", "github-hosted");
        writer.WriteEndObject();
    }

    private static void WriteStatement(Utf8JsonWriter writer, FixtureOptions options, int resultIndex)
    {
        writer.WriteStartObject("statement");
        Write(writer, options, AuthorityField.StatementType, "_type", "https://in-toto.io/Statement/v1");
        writer.WriteStartArray("subject");
        if (options.IndependentOneSubjectResults)
            WriteSubject(writer, options, manifest: resultIndex == 0);
        else if (options.ReverseSubjects)
        {
            WriteSubject(writer, options, manifest: false);
            WriteSubject(writer, options, manifest: true);
        }
        else
        {
            if (options.SubjectCount >= 1)
                WriteSubject(writer, options, manifest: true);
            if (options.SubjectCount >= 2)
                WriteSubject(writer, options, manifest: false);
            if (options.SubjectCount >= 3)
                WriteExtraSubject(writer);
        }
        writer.WriteEndArray();
        Write(writer, options, AuthorityField.PredicateType, "predicateType", "https://slsa.dev/provenance/v1");
        writer.WriteStartObject("predicate");
        writer.WriteStartObject("buildDefinition");
        Write(writer, options, AuthorityField.BuildType, "buildType", "https://actions.github.io/buildtypes/workflow/v1");
        writer.WriteStartObject("externalParameters");
        writer.WriteStartObject("workflow");
        Write(writer, options, AuthorityField.ExternalWorkflowPath, "path", ".github/workflows/linux-alpha-release.yml");
        Write(writer, options, AuthorityField.ExternalWorkflowRef, "ref", SourceReference);
        Write(writer, options, AuthorityField.ExternalWorkflowRepository, "repository", Repository);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("internalParameters");
        writer.WriteStartObject("github");
        Write(writer, options, AuthorityField.InternalEvent, "event_name", "push");
        Write(writer, options, AuthorityField.InternalRepositoryId, "repository_id", "1336010508");
        Write(writer, options, AuthorityField.InternalOwnerId, "repository_owner_id", "45441845");
        Write(writer, options, AuthorityField.InternalRunner, "runner_environment", "github-hosted");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("resolvedDependencies");
        for (int index = 0; index < options.DependencyCount; index++)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("digest");
            Write(writer, options, AuthorityField.DependencyCommit, "gitCommit", SourceCommit);
            writer.WriteEndObject();
            Write(writer, options, AuthorityField.DependencyUri, "uri", $"git+{Repository}@{SourceReference}");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("runDetails");
        writer.WriteStartObject("builder");
        Write(writer, options, AuthorityField.BuilderId, "id", San);
        writer.WriteEndObject();
        writer.WriteStartObject("metadata");
        Write(writer, options, AuthorityField.StatementInvocation, "invocationId", Invocation);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSubject(Utf8JsonWriter writer, FixtureOptions options, bool manifest)
    {
        writer.WriteStartObject();
        Write(
            writer,
            options,
            manifest ? AuthorityField.ManifestSubjectName : AuthorityField.PackageSubjectName,
            "name",
            manifest ? ManifestName : PackageName
        );
        writer.WriteStartObject("digest");
        Write(
            writer,
            options,
            manifest ? AuthorityField.ManifestSubjectDigest : AuthorityField.PackageSubjectDigest,
            "sha256",
            manifest ? options.ManifestSubjectSha256 : options.PackageSubjectSha256
        );
        if (options.ExtraDigestAlgorithm)
            writer.WriteString("sha512", new string('c', 128));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteExtraSubject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("name", "extra.txt");
        writer.WriteStartObject("digest");
        writer.WriteString("sha256", new string('c', 64));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void Write(
        Utf8JsonWriter writer,
        FixtureOptions options,
        AuthorityField field,
        string propertyName,
        string validValue
    )
    {
        writer.WriteString(propertyName, options.Mutation == field ? "mutated-authority" : validValue);
    }

    public enum AuthorityField
    {
        None,
        MediaType,
        CertificateSan,
        CertificateOidcIssuer,
        CertificateWorkflowTrigger,
        CertificateWorkflowSha,
        CertificateWorkflowName,
        CertificateWorkflowRepository,
        CertificateWorkflowRef,
        CertificateBuildSignerUri,
        CertificateBuildSignerDigest,
        CertificateRunner,
        CertificateRepositoryUri,
        CertificateRepositoryDigest,
        CertificateRepositoryRef,
        CertificateRepositoryId,
        CertificateOwnerUri,
        CertificateOwnerId,
        CertificateBuildConfigUri,
        CertificateBuildConfigDigest,
        CertificateBuildTrigger,
        CertificateInvocation,
        CertificateVisibility,
        VerifiedIdentitySan,
        VerifiedIdentityRunner,
        TimestampType,
        TimestampUri,
        TimestampValue,
        StatementType,
        ManifestSubjectName,
        ManifestSubjectDigest,
        PackageSubjectName,
        PackageSubjectDigest,
        PredicateType,
        BuildType,
        ExternalWorkflowPath,
        ExternalWorkflowRef,
        ExternalWorkflowRepository,
        InternalEvent,
        InternalRepositoryId,
        InternalOwnerId,
        InternalRunner,
        DependencyCommit,
        DependencyUri,
        BuilderId,
        StatementInvocation
    }

    internal sealed class FixtureOptions
    {
        public AuthorityField Mutation { get; init; }
        public int ResultCount { get; init; } = 1;
        public int SubjectCount { get; init; } = 2;
        public int DependencyCount { get; init; } = 1;
        public int TimestampCount { get; init; } = 1;
        public string FirstTimestamp { get; init; } = Timestamp;
        public string SecondTimestamp { get; init; } = Timestamp;
        public bool IndependentOneSubjectResults { get; init; }
        public bool ReverseSubjects { get; init; }
        public bool ExtraDigestAlgorithm { get; init; }
        public bool DuplicateMediaType { get; init; }
        public bool UnknownCertificateProperty { get; init; }
        public bool ExtraOpaqueRawProperty { get; init; }
        public bool OmitCertificateIssuer { get; init; }
        public bool MalformedDisplayProjection { get; init; }
        public bool MalformedVerifiedIssuerProjection { get; init; }
        public string CertificateIssuer { get; init; } = "CN=sigstore-intermediate,O=sigstore.dev";
        public string VerifiedIdentityIssuer { get; init; } = "";
        public string VerifiedIdentityIssuerRegexp { get; init; } = ".*";
        public string? RawSecret { get; init; }
        public string PackageSubjectSha256 { get; init; } = PackageSha256.Value;
        public string ManifestSubjectSha256 { get; init; } = ManifestSha256.Value;
    }

    private sealed class StubRunner : IGitHubAttestationProcessRunner
    {
        private readonly string Output;
        private readonly Action<GitHubAttestationProcessRequest>? OnRun;

        public GitHubAttestationProcessRequest? Request { get; private set; }

        public StubRunner(string output, Action<GitHubAttestationProcessRequest>? onRun = null)
        {
            this.Output = output;
            this.OnRun = onRun;
        }

        public Task<GitHubAttestationProcessResult> RunAsync(
            GitHubAttestationProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Request = request;
            this.OnRun?.Invoke(request);
            return Task.FromResult(new GitHubAttestationProcessResult(this.Output));
        }
    }
}
