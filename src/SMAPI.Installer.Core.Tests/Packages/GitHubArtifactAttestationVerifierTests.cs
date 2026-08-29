using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
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

    public static IEnumerable<AuthorityField> EveryAuthorityField => Enum.GetValues<AuthorityField>().Where(value => value != AuthorityField.None);

    [Test]
    public async Task VerifyAsync_UsesExactCompatibleArgumentsAndReturnsClosedTwoSubjectTrust()
    {
        StubRunner runner = new(WriteJson());
        GitHubArtifactAttestationVerificationRequest request = CreateRequest();

        VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(request);

        runner.Request.Should().NotBeNull();
        runner.Request!.ExecutablePath.Should().Be("/usr/bin/gh");
        runner.Request.IsolatedDirectory.Should().Be("/tmp/smapi-attestation-private");
        runner.Request.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        runner.Request.MaximumStandardOutputBytes.Should().Be(2 * 1024 * 1024);
        runner.Request.MaximumStandardErrorBytes.Should().Be(64 * 1024);
        runner.Request.Arguments.Should().Equal(
            "attestation",
            "verify",
            $"/proc/{Environment.ProcessId}/fd/123",
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
        runner.Request.Arguments.Should().NotContain(["--signer-workflow", "--bundle", "--no-public-good"]);
        trust.Identity.Should().Be(request.Identity);
        trust.ManifestSubject.Should().Be(request.ManifestSubject);
        trust.ManifestSubject.Should().NotBeSameAs(request.ManifestSubject, "attested fields must be parsed independently of retained-byte authority");
        trust.PackageSubject.Name.Should().Be(PackageName);
        trust.PackageSubject.Sha256.Should().Be(PackageSha256);
        trust.PackageSubject.ObservedSizeBytes.Should().Be(123456);
        trust.Evidence.RunInvocationUri.Should().Be(Invocation);
        trust.Evidence.TransparencyLogTimestampUtc.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 51, 36, TimeSpan.Zero));
    }

    [TestCaseSource(nameof(EveryAuthorityField))]
    public async Task VerifyAsync_RejectsEveryMutatedAuthorityField(AuthorityField mutation)
    {
        StubRunner runner = new(WriteJson(new FixtureOptions { Mutation = mutation }));
        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(CreateRequest());

        await verify.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("The GitHub attestation verifier returned evidence outside the reviewed release policy.");
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task VerifyAsync_RequiresExactlyOneVerificationResult(int resultCount)
    {
        Func<Task> verify = () => Verify(WriteJson(new FixtureOptions { ResultCount = resultCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_NeverCombinesIndependentOneSubjectResults()
    {
        FixtureOptions options = new() { ResultCount = 2, IndependentOneSubjectResults = true };
        Func<Task> verify = () => Verify(WriteJson(options));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task VerifyAsync_RequiresExactlyTwoSubjectsInOneStatement(int subjectCount)
    {
        Func<Task> verify = () => Verify(WriteJson(new FixtureOptions { SubjectCount = subjectCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsReversedCanonicalSubjectOrderAndExtraDigestAlgorithm()
    {
        Func<Task> reversed = () => Verify(WriteJson(new FixtureOptions { ReverseSubjects = true }));
        Func<Task> extraDigest = () => Verify(WriteJson(new FixtureOptions { ExtraDigestAlgorithm = true }));

        await reversed.Should().ThrowAsync<PackageSecurityException>();
        await extraDigest.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsDuplicateAndUnknownAuthorityPropertiesButIgnoresOpaqueRawPayload()
    {
        Func<Task> duplicate = () => Verify(WriteJson(new FixtureOptions { DuplicateMediaType = true }));
        Func<Task> unknown = () => Verify(WriteJson(new FixtureOptions { UnknownCertificateProperty = true }));
        VerifiedTaggedPackageTrust accepted = await Verify(WriteJson(new FixtureOptions { ExtraOpaqueRawProperty = true }));

        await duplicate.Should().ThrowAsync<PackageSecurityException>();
        await unknown.Should().ThrowAsync<PackageSecurityException>();
        accepted.PackageSubject.Name.Should().Be(PackageName);
    }

    [Test]
    public async Task VerifyAsync_TreatsDisplayCertificateChainAndVerifiedIssuerProjectionAsBoundedNonAuthority()
    {
        VerifiedTaggedPackageTrust changed = await Verify(
            WriteJson(
                new FixtureOptions
                {
                    CertificateIssuer = "future verified chain display",
                    VerifiedIdentityIssuer = "display-only",
                    VerifiedIdentityIssuerRegexp = "future-display-regexp"
                }
            )
        );
        VerifiedTaggedPackageTrust omittedChain = await Verify(WriteJson(new FixtureOptions { OmitCertificateIssuer = true }));
        Func<Task> wrongChainType = () => Verify(WriteJson(new FixtureOptions { MalformedDisplayProjection = true }));
        Func<Task> wrongIssuerType = () => Verify(WriteJson(new FixtureOptions { MalformedVerifiedIssuerProjection = true }));
        Func<Task> oversizedChain = () => Verify(WriteJson(new FixtureOptions { CertificateIssuer = new string('x', 513) }));
        Func<Task> oversizedIssuer = () => Verify(WriteJson(new FixtureOptions { VerifiedIdentityIssuerRegexp = new string('x', 513) }));

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
        Func<Task> verify = () => Verify(WriteJson(new FixtureOptions { DependencyCount = dependencyCount }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RequiresUnambiguousRekorTimestampAndNormalizesEquivalentValues()
    {
        Func<Task> zero = () => Verify(WriteJson(new FixtureOptions { TimestampCount = 0 }));
        Func<Task> conflicting = () => Verify(
            WriteJson(new FixtureOptions { TimestampCount = 2, SecondTimestamp = "2026-08-28T21:51:37+08:00" })
        );
        VerifiedTaggedPackageTrust same = await Verify(
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
        Func<Task> verify = () => Verify(WriteJson(new FixtureOptions { FirstTimestamp = timestamp }));

        await verify.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_RejectsMalformedOversizedAndNonUnicodeOutputWithoutEchoingRawSecret()
    {
        const string secret = "raw-private-signature-secret";
        string invalidUnicode = "[\"\ud800\"]";
        Func<Task> malformed = () => Verify("{");
        Func<Task> oversized = () => Verify(new string(' ', 2 * 1024 * 1024 + 1));
        Func<Task> unicode = () => Verify(invalidUnicode);
        Func<Task> secretFailure = () => Verify(WriteJson(new FixtureOptions { RawSecret = secret, Mutation = AuthorityField.MediaType }));

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
            "/usr/bin/gh",
            "/tmp/private"
        );
        Action relativeProc = () => new GitHubArtifactAttestationVerificationRequest(identity, "package.zip", manifest, "/usr/bin/gh", "/tmp/private");
        Action nonCanonicalDescriptor = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId}/fd/01",
            manifest,
            "/usr/bin/gh",
            "/tmp/private"
        );
        Action wrongManifest = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId}/fd/1",
            new VerifiedAttestedSubject("manifest.json", ManifestSha256, 1),
            "/usr/bin/gh",
            "/tmp/private"
        );
        Action relativeGh = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId}/fd/1",
            manifest,
            "gh",
            "/tmp/private"
        );
        Action relativeDirectory = () => new GitHubArtifactAttestationVerificationRequest(
            identity,
            $"/proc/{Environment.ProcessId}/fd/1",
            manifest,
            "/usr/bin/gh",
            "private"
        );

        otherProcess.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        relativeProc.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        nonCanonicalDescriptor.Should().Throw<ArgumentException>().WithParameterName("packageProcPath");
        wrongManifest.Should().Throw<ArgumentException>().WithParameterName("manifestSubject");
        relativeGh.Should().Throw<ArgumentException>().WithParameterName("gitHubCliPath");
        relativeDirectory.Should().Throw<ArgumentException>().WithParameterName("isolatedDirectory");
    }

    [Test]
    public async Task VerifyAsync_PropagatesCancellationWithoutCallingParser()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        StubRunner runner = new("private-invalid-output");
        Func<Task> verify = async () => await new GitHubArtifactAttestationVerifier(runner).VerifyAsync(CreateRequest(), cancellation.Token);

        await verify.Should().ThrowAsync<OperationCanceledException>();
        runner.Request.Should().BeNull();
    }

    private static Task<VerifiedTaggedPackageTrust> Verify(string output)
    {
        return new GitHubArtifactAttestationVerifier(new StubRunner(output)).VerifyAsync(CreateRequest());
    }

    private static GitHubArtifactAttestationVerificationRequest CreateRequest()
    {
        return new GitHubArtifactAttestationVerificationRequest(
            CreateIdentity(),
            $"/proc/{Environment.ProcessId}/fd/123",
            CreateManifestSubject(),
            "/usr/bin/gh",
            "/tmp/smapi-attestation-private"
        );
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

    private static string WriteJson(FixtureOptions? options = null)
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
            (manifest ? ManifestSha256 : PackageSha256).Value
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

    private sealed class FixtureOptions
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
    }

    private sealed class StubRunner : IGitHubAttestationProcessRunner
    {
        private readonly string Output;

        public GitHubAttestationProcessRequest? Request { get; private set; }

        public StubRunner(string output)
        {
            this.Output = output;
        }

        public Task<GitHubAttestationProcessResult> RunAsync(
            GitHubAttestationProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Request = request;
            return Task.FromResult(new GitHubAttestationProcessResult(this.Output));
        }
    }
}
