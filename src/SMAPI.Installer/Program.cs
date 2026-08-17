using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace StardewModdingApi.Installer;

/// <summary>The entry point for SMAPI's install and uninstall console app.</summary>
internal class Program
{
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
    public static void Main(string[] args)
    {
        // find install bundle
        FileInfo zipFile = new(Path.Combine(Program.InstallerPath, "install.dat"));
        if (!zipFile.Exists)
        {
            Console.WriteLine($"Oops! Some of the installer files are missing; try re-downloading the installer. (Missing file: {zipFile.FullName})");
            Console.ReadLine();
            return;
        }

        string? error = null;
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
                installer.Run(args);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= Program.CurrentDomain_AssemblyResolve;
            }
        }
        catch (Exception ex)
        {
            error = $"The installer failed with an unexpected exception.\nIf you need help fixing this error, see https://smapi.io/help\n\n{ex}";
        }
        finally
        {
            Program.TryDeleteExtractedBundle();
        }

        if (error != null)
            Program.PrintErrorAndExit(error);
    }

    /*********
    ** Private methods
    *********/
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

    /// <summary>Write an error directly to the console and exit.</summary>
    /// <param name="message">The error message to display.</param>
    private static void PrintErrorAndExit(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();

        Console.WriteLine("Game has ended. Press any key to exit.");
        Thread.Sleep(100);
        Console.ReadKey();
        Environment.Exit(0);
    }
}
