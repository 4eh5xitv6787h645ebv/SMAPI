using System.Text;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.PackageTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Program.WriteUsage();
                return args.Length == 0 ? 2 : 0;
            }
            string command = args[0];
            Dictionary<string, string> options = Program.ParseOptions(args[1..]);
            string directory = Program.Take(options, "asset-directory");
            ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(Program.Take(options, "tag"));
            string sourceCommit = Program.Take(options, "source-commit");
            string sourceTree = Program.Take(options, "source-tree");
            ReleaseAssetSet tool = new();
            switch (command)
            {
                case "create":
                    string package = Program.Take(options, "package");
                    ReleaseAssetSetInputs createInputs = new(
                        identity,
                        sourceCommit,
                        sourceTree,
                        Program.Take(options, "workflow-ref"),
                        Program.Take(options, "workflow-run"),
                        Program.Take(options, "runner-image"),
                        Program.Take(options, "runner-arch"),
                        Program.Take(options, "reference-assemblies-commit"),
                        Program.Take(options, "timestamp-utc"),
                        Program.ReadDotNetInfo(Program.Take(options, "dotnet-info-file"))
                    );
                    Program.AssertNoUnknownOptions(options);
                    await tool.CreateAsync(package, directory, createInputs).ConfigureAwait(false);
                    break;
                case "verify-release":
                    Program.AssertNoUnknownOptions(options);
                    await tool.VerifyReleaseAsync(
                        directory,
                        new ReleaseVerificationInputs(identity, sourceCommit, sourceTree)
                    ).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{command}'.");
            }
            Console.WriteLine($"{command}: verified {identity.Tag}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Package tool failed: {ex.Message}");
            return 1;
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
            "Usage: SMAPI.Installer.PackageTool <create|verify-release> --asset-directory PATH --tag TAG "
            + "--source-commit SHA --source-tree SHA [create only: --package ZIP --workflow-ref REF "
            + "--workflow-run URL --runner-image IMAGE --runner-arch ARCH --reference-assemblies-commit SHA "
            + "--timestamp-utc UTC --dotnet-info-file PATH]"
        );
    }
}
