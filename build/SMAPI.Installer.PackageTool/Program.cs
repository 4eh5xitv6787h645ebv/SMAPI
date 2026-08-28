using System.Text;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.PackageTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        return await Program.RunAsync(args, Environment.GetEnvironmentVariable).ConfigureAwait(false);
    }

    /// <summary>Run the command with an explicit environment reader so the GitHub context boundary is testable.</summary>
    internal static async Task<int> RunAsync(string[] args, Func<string, string?> getEnvironmentVariable)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Program.WriteUsage();
                return args.Length == 0 ? 2 : 0;
            }
            string command = args[0];
            Dictionary<string, string> options = Program.ParseOptions(args[1..]);
            switch (command)
            {
                case "create":
                    string createDirectory = Program.Take(options, "asset-directory");
                    ForkReleaseIdentity createIdentity = ForkReleaseIdentity.Parse(Program.Take(options, "tag"));
                    string createSourceCommit = Program.Take(options, "source-commit");
                    string createSourceTree = Program.Take(options, "source-tree");
                    string package = Program.Take(options, "package");
                    ReleaseAssetSetInputs createInputs = new(
                        createIdentity,
                        createSourceCommit,
                        createSourceTree,
                        Program.Take(options, "workflow-ref"),
                        Program.Take(options, "workflow-run"),
                        Program.Take(options, "runner-image"),
                        Program.Take(options, "runner-arch"),
                        Program.Take(options, "reference-assemblies-commit"),
                        Program.Take(options, "timestamp-utc"),
                        Program.ReadDotNetInfo(Program.Take(options, "dotnet-info-file"))
                    );
                    Program.AssertNoUnknownOptions(options);
                    Program.AssertAuthoritativeTagPushContext(
                        createIdentity,
                        createSourceCommit,
                        createInputs.Workflow,
                        getEnvironmentVariable
                    );
                    await new ReleaseAssetSet().CreateAsync(package, createDirectory, createInputs).ConfigureAwait(false);
                    Console.WriteLine(
                        $"create: created and self-verified {createIdentity.Tag}; the GitHub tag-push context guard isn't cryptographic provenance"
                    );
                    break;
                case "verify-release":
                    string verifyDirectory = Program.Take(options, "asset-directory");
                    ForkReleaseIdentity verifyIdentity = ForkReleaseIdentity.Parse(Program.Take(options, "tag"));
                    string verifySourceCommit = Program.Take(options, "source-commit");
                    string verifySourceTree = Program.Take(options, "source-tree");
                    Program.AssertNoUnknownOptions(options);
                    await new ReleaseAssetSet().VerifyReleaseAsync(
                        verifyDirectory,
                        new ReleaseVerificationInputs(verifyIdentity, verifySourceCommit, verifySourceTree)
                    ).ConfigureAwait(false);
                    Console.WriteLine(
                        $"verify-release: verified package authority for {verifyIdentity.Tag}; runner metadata is informational and unauthenticated"
                    );
                    break;
                case "inspect-candidate":
                    string candidatePackage = Program.Take(options, "package");
                    ForkReleaseIdentity candidateIdentity = ForkReleaseIdentity.Parse(Program.Take(options, "tag"));
                    Program.AssertNoUnknownOptions(options);
                    LinuxPackageStructuralInspection inspection = await new LinuxPackageStructuralInspector().InspectAsync(
                        candidatePackage,
                        candidateIdentity
                    ).ConfigureAwait(false);
                    Console.WriteLine(
                        $"inspect-candidate: non-authoritative structure passed ({inspection.PayloadFileCount} files, "
                        + $"{inspection.PayloadExpandedBytes} expanded bytes); no release authority or artifacts were created"
                    );
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{command}'.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Package tool failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Refuse to mint release authority outside the exact GitHub tag-push context. This is a workflow misuse guard,
    /// not cryptographic proof; downloaded artifacts still require GitHub attestation verification.
    /// </summary>
    private static void AssertAuthoritativeTagPushContext(
        ForkReleaseIdentity identity,
        string sourceCommit,
        string workflow,
        Func<string, string?> getEnvironmentVariable
    )
    {
        string expectedRef = $"refs/tags/{identity.Tag}";
        string expectedWorkflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@{expectedRef}";
        bool valid = string.Equals(getEnvironmentVariable("GITHUB_EVENT_NAME"), "push", StringComparison.Ordinal)
            && string.Equals(getEnvironmentVariable("GITHUB_REF_TYPE"), "tag", StringComparison.Ordinal)
            && string.Equals(getEnvironmentVariable("GITHUB_REF"), expectedRef, StringComparison.Ordinal)
            && string.Equals(getEnvironmentVariable("GITHUB_WORKFLOW_REF"), expectedWorkflow, StringComparison.Ordinal)
            && string.Equals(getEnvironmentVariable("GITHUB_REPOSITORY"), ForkReleaseIdentity.Repository, StringComparison.Ordinal)
            && string.Equals(getEnvironmentVariable("GITHUB_SHA"), sourceCommit, StringComparison.Ordinal)
            && string.Equals(workflow, expectedWorkflow, StringComparison.Ordinal);
        if (!valid)
        {
            throw new InvalidOperationException(
                "Authoritative release assets may only be created by the exact reviewed GitHub tag-push context. "
                + "Candidate validation must not mint a release manifest or quartet."
            );
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
            throw new ArgumentException("Every option must have exactly one value.");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            string name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length == 2 || !result.TryAdd(name[2..], args[index + 1]))
                throw new ArgumentException($"Invalid or duplicate option '{name}'.");
        }
        return result;
    }

    private static string Take(IDictionary<string, string> options, string name)
    {
        if (!options.Remove(name, out string? value) || string.IsNullOrEmpty(value))
            throw new ArgumentException($"Required option '--{name}' is missing.");
        return value;
    }

    private static void AssertNoUnknownOptions(IReadOnlyDictionary<string, string> options)
    {
        if (options.Count != 0)
            throw new ArgumentException($"Unknown option '--{options.Keys.Order(StringComparer.Ordinal).First()}'.");
    }

    private static string ReadDotNetInfo(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using LinuxAnchoredFileSystem directory = new(Path.GetDirectoryName(fullPath)!);
        using LinuxAnchoredFile file = directory.OpenRegularFileForRead(Path.GetFileName(fullPath));
        if (file.Identity.Size <= 0 || file.Identity.Size > 256 * 1024)
            throw new ArgumentException("The .NET information file must be an ordinary bounded non-empty file.");
        return new UTF8Encoding(false, true).GetString(directory.ReadAllBytes(file, 256 * 1024));
    }

    private static void WriteUsage()
    {
        Console.WriteLine(
            "Usage: SMAPI.Installer.PackageTool inspect-candidate --package ZIP --tag TAG\n"
            + "   or: SMAPI.Installer.PackageTool <create|verify-release> --asset-directory PATH --tag TAG "
            + "--source-commit SHA --source-tree SHA [create only: --package ZIP --workflow-ref REF "
            + "--workflow-run URL --runner-image IMAGE --runner-arch ARCH --reference-assemblies-commit SHA "
            + "--timestamp-utc UTC --dotnet-info-file PATH]"
        );
    }
}
