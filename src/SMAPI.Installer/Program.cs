using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace StardewModdingApi.Installer;

/// <summary>The entry point for SMAPI's install and uninstall console app.</summary>
internal class Program
{
    /// <summary>The exact command-line switch which selects the machine-readable Linux installer backend.</summary>
    private const string LinuxProtocolV1JsonlFlag = "--linux-protocol-v1-jsonl";

    /*********
    ** Fields
    *********/
    /// <summary>The absolute path of the installer folder.</summary>
    [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute", Justification = "The assembly location is never null in this context.")]
    private static readonly string InstallerPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    /// <summary>The absolute path of the folder containing the unzipped installer files.</summary>
    private static readonly string ExtractedBundlePath = Path.Combine(Path.GetTempPath(), $"SMAPI-installer-{Guid.NewGuid():N}");

    /// <summary>The absolute path for referenced assemblies.</summary>
    private static readonly string InternalFilesPath = Path.Combine(Program.ExtractedBundlePath, "smapi-internal");

    /*********
    ** Public methods
    *********/
    /// <summary>Run the install or uninstall script.</summary>
    /// <param name="args">The command line arguments.</param>
    public static int Main(string[] args)
    {
        // A normal Linux installation must never need elevated privileges. Refuse them before
        // extracting or touching any files, including when the binary is invoked directly.
        if (OperatingSystem.IsLinux() && Program.GetEffectiveUserId() == 0)
        {
            Console.Error.WriteLine("The SMAPI installer must not be run as root or with sudo. Run it as your normal desktop user instead.");
            return 2;
        }

        // The protocol host is a separate, machine-readable execution mode. Route it before
        // inspecting or extracting install.dat so stdout remains JSON-only and the shared core
        // owns all mutations performed for a desktop frontend.
        bool protocolRequested = Array.IndexOf(args, Program.LinuxProtocolV1JsonlFlag) >= 0;
        if (protocolRequested)
        {
            if (!OperatingSystem.IsLinux() || args.Length != 1 || args[0] != Program.LinuxProtocolV1JsonlFlag)
            {
                Console.Error.WriteLine("The Linux protocol host requires exactly --linux-protocol-v1-jsonl on Linux.");
                return 2;
            }
            return Program.RunLinuxProtocolHost();
        }

        // find install bundle
        FileInfo zipFile = new(Path.Combine(Program.InstallerPath, "install.dat"));
        if (!zipFile.Exists)
        {
            Console.WriteLine($"Oops! Some of the installer files are missing; try re-downloading the installer. (Missing file: {zipFile.FullName})");
            return 2;
        }

        string? error = null;
        int exitCode = 0;
        try
        {
            // unzip bundle into temp folder
            DirectoryInfo bundleDir = new(Program.ExtractedBundlePath);
            Console.WriteLine("Extracting install files...");
            ZipFile.ExtractToDirectory(zipFile.FullName, bundleDir.FullName);

            // set up assembly resolution and launch installer
            AppDomain.CurrentDomain.AssemblyResolve += Program.CurrentDomain_AssemblyResolve;
            try
            {
                var installer = new InteractiveInstaller(bundleDir.FullName);
                if (!installer.Run(args))
                    exitCode = 2;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= Program.CurrentDomain_AssemblyResolve;
            }
        }
        catch (Exception ex)
        {
            error = $"The installer failed with an unexpected exception.\nIf you need help fixing this error, see https://smapi.io/help\n\n{ex}";
            exitCode = 1;
        }
        finally
        {
            Program.TryDeleteExtractedBundle();
        }

        if (error != null)
            Program.PrintError(error, allowUserInput: Array.IndexOf(args, "--no-prompt") < 0);

        return exitCode;
    }

    /*********
    ** Private methods
    *********/
    /// <summary>Run the bounded JSONL backend without extracting the legacy interactive installer bundle.</summary>
    private static int RunLinuxProtocolHost()
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelKeyPress = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelKeyPress;
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        try
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            string githubCliPath = Path.Combine(Program.InstallerPath, "gh");
            var host = new StardewModdingAPI.Installer.Core.Protocol.V1.LinuxInstallerProtocolJsonlHost(version, githubCliPath);
            return host.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancellation.Token).GetAwaiter().GetResult();
        }
        catch
        {
            Console.Error.WriteLine("The Linux protocol host failed before starting safely.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelKeyPress;
        }
    }

    /// <summary>Get the effective Unix user ID.</summary>
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    /// <summary>Method called when assembly resolution fails, which may return a manually resolved assembly.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private static Assembly? CurrentDomain_AssemblyResolve(object? sender, ResolveEventArgs e)
    {
        try
        {
            AssemblyName name = new(e.Name);
            foreach (FileInfo dll in new DirectoryInfo(Program.InternalFilesPath).EnumerateFiles("*.dll"))
            {
                if (name.Name != null && name.Name.Equals(AssemblyName.GetAssemblyName(dll.FullName).Name, StringComparison.OrdinalIgnoreCase))
                    return Assembly.LoadFrom(dll.FullName);
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error resolving assembly: {ex}");
            return null;
        }
    }

    /// <summary>Delete the temporary extracted installer bundle if possible.</summary>
    private static void TryDeleteExtractedBundle()
    {
        try
        {
            if (Directory.Exists(Program.ExtractedBundlePath))
                Directory.Delete(Program.ExtractedBundlePath, recursive: true);
        }
        catch
        {
            // best-effort cleanup; loaded files may still be locked on some platforms
        }
    }

    /// <summary>Write an error directly to the console and optionally wait so an interactive user can read it.</summary>
    /// <param name="message">The error message to display.</param>
    private static void PrintError(string message, bool allowUserInput)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();

        if (allowUserInput)
        {
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }
    }
}
