using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The exact immutable inputs needed to verify one tagged package attestation.</summary>
internal sealed class GitHubArtifactAttestationVerificationRequest
{
    private static readonly Regex ProcFileDescriptorPattern = new(
        @"\A/proc/(?<pid>[1-9][0-9]*)/fd/(?<fd>0|[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant
    );

    public InstallationReleaseIdentity Identity { get; }
    public string PackageProcPath { get; }
    public VerifiedAttestedSubject ManifestSubject { get; }
    public string GitHubCliPath { get; }
    public string BundleProcPath { get; }

    internal GitHubArtifactAttestationVerificationRequest(
        InstallationReleaseIdentity identity,
        string packageProcPath,
        VerifiedAttestedSubject manifestSubject,
        string gitHubCliPath,
        string bundleProcPath
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(manifestSubject);
        string procPathValue = AssertCurrentProcessDescriptorPath(packageProcPath, nameof(packageProcPath), "package");
        string gitHubCliPathValue = AssertCurrentProcessDescriptorPath(gitHubCliPath, nameof(gitHubCliPath), "GitHub CLI");
        string bundleProcPathValue = AssertCurrentProcessDescriptorPath(bundleProcPath, nameof(bundleProcPath), "attestation bundle");
        string expectedManifestName = $"SMAPI-{identity.EmbeddedVersion}-linux-x64-install-manifest.json";
        if (!string.Equals(manifestSubject.Name, expectedManifestName, StringComparison.Ordinal))
            throw new ArgumentException("The retained manifest subject doesn't match the tagged release identity.", nameof(manifestSubject));

        this.Identity = identity;
        this.PackageProcPath = procPathValue;
        this.ManifestSubject = manifestSubject;
        this.GitHubCliPath = gitHubCliPathValue;
        this.BundleProcPath = bundleProcPathValue;
    }

    private static string AssertCurrentProcessDescriptorPath(string? value, string parameterName, string authorityName)
    {
        string pathValue = value ?? "";
        Match procPath = GitHubArtifactAttestationVerificationRequest.ProcFileDescriptorPattern.Match(pathValue);
        if (
            !procPath.Success
            || !int.TryParse(procPath.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid != Environment.ProcessId
            || !int.TryParse(procPath.Groups["fd"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        )
        {
            throw new ArgumentException(
                $"The {authorityName} must be exposed through this process's retained file descriptor.",
                parameterName
            );
        }
        return pathValue;
    }
}

/// <summary>Verifies one exact two-subject GitHub artifact attestation and emits closed installer trust.</summary>
internal sealed class GitHubArtifactAttestationVerifier
{
    private const int MaximumOutputBytes = 2 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private const int MaximumJsonDepth = 32;
    private const string ReviewedRepositoryName = "4eh5xitv6787h645ebv/SMAPI";
    private const string ReviewedOwnerUrl = "https://github.com/4eh5xitv6787h645ebv";
    private const string OidcIssuer = "https://token.actions.githubusercontent.com";
    private const string WorkflowName = "Linux alpha release qualification";
    private const string StatementType = "https://in-toto.io/Statement/v1";
    private const string PredicateType = "https://slsa.dev/provenance/v1";
    private const string BuildType = "https://actions.github.io/buildtypes/workflow/v1";
    private const string VerificationResultMediaType = "application/vnd.dev.sigstore.verificationresult+json;version=0.1";
    private const string RekorUri = "https://rekor.sigstore.dev";
    private const string WorkflowPath = ".github/workflows/linux-alpha-release.yml";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex InvocationUriPattern = new(
        @"\Ahttps://github\.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/[1-9][0-9]*/attempts/[1-9][0-9]*\z",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex TimestampPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant
    );

    private readonly IGitHubAttestationProcessRunner Runner;

    internal GitHubArtifactAttestationVerifier(IGitHubAttestationProcessRunner runner)
    {
        this.Runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    internal async Task<VerifiedTaggedPackageTrust> VerifyAsync(
        VerifiedInstallerPackage package,
        VerifiedGitHubAttestationBundle bundle,
        PinnedGitHubCli cli,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(cli);
        cancellationToken.ThrowIfCancellationRequested();

        LinuxSealedFileLease? packageLease = null;
        LinuxSealedFileLease? manifestLease = null;
        LinuxSealedFileLease? bundleLease = null;
        LinuxSealedFileLease? executableLease = null;
        try
        {
            packageLease = package.Package.LeasePackageForExternalRead();
            manifestLease = package.LeaseManifestForExternalRead();
            bundleLease = bundle.LeaseForExternalRead();
            executableLease = cli.LeaseForExecution();
            cancellationToken.ThrowIfCancellationRequested();

            if (!bundle.Release.Equals(package.Release))
                throw new PackageSecurityException("The local attestation bundle is bound to a different tagged release.");

            GitHubArtifactAttestationVerificationRequest request = new(
                package.Release,
                packageLease.ProcPath,
                new VerifiedAttestedSubject(package.ManifestAssetName, package.ManifestSha256, package.ManifestSizeBytes),
                executableLease.ProcPath,
                bundleLease.ProcPath
            );
            return await this.VerifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            executableLease?.Dispose();
            bundleLease?.Dispose();
            manifestLease?.Dispose();
            packageLease?.Dispose();
        }
    }

    internal async Task<VerifiedTaggedPackageTrust> VerifyAsync(
        GitHubArtifactAttestationVerificationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        GitHubAttestationProcessRequest processRequest = new(
            request.GitHubCliPath,
            CreateArguments(request),
            TimeSpan.FromSeconds(30),
            MaximumOutputBytes,
            MaximumErrorBytes,
            new GitHubAttestationProcessBundleAuthority(request.BundleProcPath)
        );
        GitHubAttestationProcessResult result = await this.Runner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(result.StandardOutput, request);
    }

    private static string[] CreateArguments(GitHubArtifactAttestationVerificationRequest request)
    {
        InstallationReleaseIdentity identity = request.Identity;
        string sourceReference = $"refs/tags/{identity.Tag}";
        string san = $"https://github.com/{identity.BuildWorkflow}";
        return
        [
            "attestation",
            "verify",
            request.PackageProcPath,
            "--bundle",
            GitHubAttestationProcessRequest.BundlePathPlaceholder,
            "--hostname",
            "github.com",
            "--repo",
            ReviewedRepositoryName,
            "--predicate-type",
            PredicateType,
            "--cert-oidc-issuer",
            OidcIssuer,
            "--cert-identity",
            san,
            "--signer-digest",
            identity.SourceCommit,
            "--source-ref",
            sourceReference,
            "--source-digest",
            identity.SourceCommit,
            "--deny-self-hosted-runners",
            "--limit",
            "2",
            "--format",
            "json"
        ];
    }

    private static VerifiedTaggedPackageTrust Parse(
        string output,
        GitHubArtifactAttestationVerificationRequest request
    )
    {
        string boundedOutput = output ?? "";
        if (boundedOutput.Length is <= 0 or > MaximumOutputBytes)
            throw InvalidEvidence();
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(boundedOutput);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidEvidence();
        }
        if (utf8.Length > MaximumOutputBytes)
            throw InvalidEvidence();

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                }
            );
            JsonElement root = document.RootElement;
            RequireKind(root, JsonValueKind.Array);
            if (root.GetArrayLength() != 1)
                throw InvalidEvidence();

            Dictionary<string, JsonElement> result = ReadObject(root[0], ["attestation", "verificationResult"]);
            RequireKind(result["attestation"], JsonValueKind.Object); // Cryptographic payload is intentionally opaque after gh verifies it.
            Dictionary<string, JsonElement> verification = ReadObject(
                result["verificationResult"],
                ["mediaType", "signature", "verifiedTimestamps", "verifiedIdentity", "statement"]
            );
            RequireExactString(verification["mediaType"], VerificationResultMediaType);

            InstallationReleaseIdentity identity = request.Identity;
            string sourceReference = $"refs/tags/{identity.Tag}";
            string san = $"https://github.com/{identity.BuildWorkflow}";
            string dependencyUri = $"git+{identity.Repository}@{sourceReference}";

            Dictionary<string, JsonElement> signature = ReadObject(verification["signature"], ["certificate"]);
            Dictionary<string, JsonElement> certificate = ReadObject(
                signature["certificate"],
                [
                    "subjectAlternativeName",
                    "issuer",
                    "githubWorkflowTrigger",
                    "githubWorkflowSHA",
                    "githubWorkflowName",
                    "githubWorkflowRepository",
                    "githubWorkflowRef",
                    "buildSignerURI",
                    "buildSignerDigest",
                    "runnerEnvironment",
                    "sourceRepositoryURI",
                    "sourceRepositoryDigest",
                    "sourceRepositoryRef",
                    "sourceRepositoryIdentifier",
                    "sourceRepositoryOwnerURI",
                    "sourceRepositoryOwnerIdentifier",
                    "buildConfigURI",
                    "buildConfigDigest",
                    "buildTrigger",
                    "runInvocationURI",
                    "sourceRepositoryVisibilityAtSigning"
                ],
                ["certificateIssuer"]
            );
            if (certificate.TryGetValue("certificateIssuer", out JsonElement certificateIssuer))
                RequireBoundedDisplayString(certificateIssuer);
            RequireExactString(certificate["subjectAlternativeName"], san);
            RequireExactString(certificate["issuer"], OidcIssuer);
            RequireExactString(certificate["githubWorkflowTrigger"], VerifiedGitHubWorkflowEvidence.RequiredTrigger);
            RequireExactString(certificate["githubWorkflowSHA"], identity.SourceCommit);
            RequireExactString(certificate["githubWorkflowName"], WorkflowName);
            RequireExactString(certificate["githubWorkflowRepository"], ReviewedRepositoryName);
            RequireExactString(certificate["githubWorkflowRef"], sourceReference);
            RequireExactString(certificate["buildSignerURI"], san);
            RequireExactString(certificate["buildSignerDigest"], identity.SourceCommit);
            RequireExactString(certificate["runnerEnvironment"], VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment);
            RequireExactString(certificate["sourceRepositoryURI"], identity.Repository);
            RequireExactString(certificate["sourceRepositoryDigest"], identity.SourceCommit);
            RequireExactString(certificate["sourceRepositoryRef"], sourceReference);
            RequireExactString(certificate["sourceRepositoryIdentifier"], VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier);
            RequireExactString(certificate["sourceRepositoryOwnerURI"], ReviewedOwnerUrl);
            RequireExactString(certificate["sourceRepositoryOwnerIdentifier"], VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier);
            RequireExactString(certificate["buildConfigURI"], san);
            RequireExactString(certificate["buildConfigDigest"], identity.SourceCommit);
            RequireExactString(certificate["buildTrigger"], VerifiedGitHubWorkflowEvidence.RequiredTrigger);
            string invocationUri = RequireCanonicalInvocationUri(certificate["runInvocationURI"]);
            RequireExactString(certificate["sourceRepositoryVisibilityAtSigning"], "public");

            VerifyIdentity(verification["verifiedIdentity"], san);
            DateTimeOffset tlogTimestampUtc = ReadTransparencyLogTimestamp(verification["verifiedTimestamps"]);
            (VerifiedAttestedSubject Manifest, VerifiedAttestedSubject Package) attestedSubjects = VerifyStatement(
                verification["statement"],
                request,
                sourceReference,
                san,
                dependencyUri,
                invocationUri
            );

            VerifiedGitHubWorkflowEvidence evidence = new(
                identity,
                identity.Repository,
                sourceReference,
                identity.SourceCommit,
                identity.BuildWorkflow,
                san,
                invocationUri,
                VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment,
                VerifiedGitHubWorkflowEvidence.RequiredTrigger,
                VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier,
                VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier,
                tlogTimestampUtc
            );
            return new VerifiedTaggedPackageTrust(
                identity,
                attestedSubjects.Package,
                attestedSubjects.Manifest,
                request.ManifestSubject.Sha256,
                request.ManifestSubject.ObservedSizeBytes,
                evidence
            );
        }
        catch (JsonException)
        {
            throw InvalidEvidence();
        }
        catch (ArgumentException)
        {
            throw InvalidEvidence();
        }
    }

    private static void VerifyIdentity(JsonElement element, string san)
    {
        Dictionary<string, JsonElement> identity = ReadObject(
            element,
            ["subjectAlternativeName", "issuer", "runnerEnvironment"]
        );
        Dictionary<string, JsonElement> subjectAlternativeName = ReadObject(
            identity["subjectAlternativeName"],
            ["subjectAlternativeName"],
            ["regexp"]
        );
        RequireExactString(subjectAlternativeName["subjectAlternativeName"], san);
        RequireAbsentOrEmpty(subjectAlternativeName, "regexp");
        Dictionary<string, JsonElement> issuer = ReadObject(identity["issuer"], ["issuer", "regexp"]);
        RequireBoundedDisplayString(issuer["issuer"]);
        RequireBoundedDisplayString(issuer["regexp"]);
        RequireExactString(identity["runnerEnvironment"], VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment);
    }

    private static DateTimeOffset ReadTransparencyLogTimestamp(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        if (element.GetArrayLength() == 0)
            throw InvalidEvidence();
        DateTimeOffset? normalized = null;
        foreach (JsonElement item in element.EnumerateArray())
        {
            Dictionary<string, JsonElement> timestamp = ReadObject(item, ["type", "uri", "timestamp"]);
            RequireExactString(timestamp["type"], "Tlog");
            RequireExactString(timestamp["uri"], RekorUri);
            string raw = RequireString(timestamp["timestamp"]);
            if (
                raw.Length > 64
                || !TimestampPattern.IsMatch(raw)
                || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            )
            {
                throw InvalidEvidence();
            }
            DateTimeOffset utc = parsed.ToUniversalTime();
            if (normalized.HasValue && normalized.Value != utc)
                throw InvalidEvidence();
            normalized = utc;
        }

        return normalized ?? throw InvalidEvidence();
    }

    private static (VerifiedAttestedSubject Manifest, VerifiedAttestedSubject Package) VerifyStatement(
        JsonElement element,
        GitHubArtifactAttestationVerificationRequest request,
        string sourceReference,
        string san,
        string dependencyUri,
        string invocationUri
    )
    {
        Dictionary<string, JsonElement> statement = ReadObject(element, ["_type", "subject", "predicateType", "predicate"]);
        RequireExactString(statement["_type"], StatementType);
        RequireExactString(statement["predicateType"], PredicateType);
        (VerifiedAttestedSubject Manifest, VerifiedAttestedSubject Package) subjects = VerifySubjects(statement["subject"], request);

        Dictionary<string, JsonElement> predicate = ReadObject(statement["predicate"], ["buildDefinition", "runDetails"]);
        Dictionary<string, JsonElement> buildDefinition = ReadObject(
            predicate["buildDefinition"],
            ["buildType", "externalParameters", "internalParameters", "resolvedDependencies"]
        );
        RequireExactString(buildDefinition["buildType"], BuildType);

        Dictionary<string, JsonElement> externalParameters = ReadObject(buildDefinition["externalParameters"], ["workflow"]);
        Dictionary<string, JsonElement> workflow = ReadObject(externalParameters["workflow"], ["path", "ref", "repository"]);
        RequireExactString(workflow["path"], WorkflowPath);
        RequireExactString(workflow["ref"], sourceReference);
        RequireExactString(workflow["repository"], request.Identity.Repository);

        Dictionary<string, JsonElement> internalParameters = ReadObject(buildDefinition["internalParameters"], ["github"]);
        Dictionary<string, JsonElement> github = ReadObject(
            internalParameters["github"],
            ["event_name", "repository_id", "repository_owner_id", "runner_environment"]
        );
        RequireExactString(github["event_name"], VerifiedGitHubWorkflowEvidence.RequiredTrigger);
        RequireExactString(github["repository_id"], VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier);
        RequireExactString(github["repository_owner_id"], VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier);
        RequireExactString(github["runner_environment"], VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment);

        JsonElement dependencies = buildDefinition["resolvedDependencies"];
        RequireKind(dependencies, JsonValueKind.Array);
        if (dependencies.GetArrayLength() != 1)
            throw InvalidEvidence();
        Dictionary<string, JsonElement> dependency = ReadObject(dependencies[0], ["digest", "uri"]);
        Dictionary<string, JsonElement> dependencyDigest = ReadObject(dependency["digest"], ["gitCommit"]);
        RequireExactString(dependencyDigest["gitCommit"], request.Identity.SourceCommit);
        RequireExactString(dependency["uri"], dependencyUri);

        Dictionary<string, JsonElement> runDetails = ReadObject(predicate["runDetails"], ["builder", "metadata"]);
        Dictionary<string, JsonElement> builder = ReadObject(runDetails["builder"], ["id"]);
        RequireExactString(builder["id"], san);
        Dictionary<string, JsonElement> metadata = ReadObject(runDetails["metadata"], ["invocationId"]);
        RequireExactString(metadata["invocationId"], invocationUri);
        return subjects;
    }

    private static (VerifiedAttestedSubject Manifest, VerifiedAttestedSubject Package) VerifySubjects(
        JsonElement element,
        GitHubArtifactAttestationVerificationRequest request
    )
    {
        RequireKind(element, JsonValueKind.Array);
        if (element.GetArrayLength() != 2)
            throw InvalidEvidence();

        // ReleaseAssetSet writes SHA256SUMS in this exact deterministic order: install manifest, then package.
        VerifiedAttestedSubject manifest = ReadSubject(
            element[0],
            request.ManifestSubject.Name,
            request.ManifestSubject.Sha256,
            request.ManifestSubject.ObservedSizeBytes
        );
        VerifiedAttestedSubject package = ReadSubject(
            element[1],
            request.Identity.PackageAssetName,
            request.Identity.PackageSha256,
            request.Identity.PackageSizeBytes
        );
        if (string.Equals(request.ManifestSubject.Name, request.Identity.PackageAssetName, StringComparison.Ordinal))
            throw InvalidEvidence();
        return (manifest, package);
    }

    private static VerifiedAttestedSubject ReadSubject(
        JsonElement element,
        string expectedName,
        Sha256Digest expectedSha256,
        long observedSizeBytes
    )
    {
        Dictionary<string, JsonElement> subject = ReadObject(element, ["name", "digest"]);
        string name = RequireString(subject["name"]);
        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            throw InvalidEvidence();
        Dictionary<string, JsonElement> digest = ReadObject(subject["digest"], ["sha256"]);
        Sha256Digest sha256 = Sha256Digest.Parse(RequireString(digest["sha256"]));
        if (sha256 != expectedSha256)
            throw InvalidEvidence();
        return new VerifiedAttestedSubject(name, sha256, observedSizeBytes);
    }

    private static string RequireCanonicalInvocationUri(JsonElement element)
    {
        string value = RequireString(element);
        if (
            value.Length > 256
            || !InvocationUriPattern.IsMatch(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal)
        )
        {
            throw InvalidEvidence();
        }
        return value;
    }

    private static Dictionary<string, JsonElement> ReadObject(
        JsonElement element,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null
    )
    {
        RequireKind(element, JsonValueKind.Object);
        HashSet<string> allowed = new(required, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                throw InvalidEvidence();
        }
        if (required.Any(name => !values.ContainsKey(name)))
            throw InvalidEvidence();
        return values;
    }

    private static void RequireAbsentOrEmpty(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        if (values.TryGetValue(name, out JsonElement value) && RequireString(value).Length != 0)
            throw InvalidEvidence();
    }

    private static void RequireBoundedDisplayString(JsonElement element)
    {
        string value = RequireString(element);
        if (value.Length > 512 || value.Any(char.IsControl))
            throw InvalidEvidence();
    }

    private static string RequireString(JsonElement element)
    {
        RequireKind(element, JsonValueKind.String);
        return element.GetString() ?? throw InvalidEvidence();
    }

    private static void RequireExactString(JsonElement element, string expected)
    {
        if (!string.Equals(RequireString(element), expected, StringComparison.Ordinal))
            throw InvalidEvidence();
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected)
    {
        if (element.ValueKind != expected)
            throw InvalidEvidence();
    }

    private static PackageSecurityException InvalidEvidence()
    {
        return new PackageSecurityException("The GitHub attestation verifier returned evidence outside the reviewed release policy.");
    }
}
