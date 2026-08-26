using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Internal.ConsoleWriting;

namespace StardewModdingAPI.Framework;

/// <summary>Encapsulates monitoring and logic for a given module.</summary>
internal class Monitor : IMonitor
{
    /*********
    ** Fields
    *********/
    /// <summary>The name of the module which logs messages using this instance.</summary>
    private readonly string Source;

    /// <summary>Handles writing text to the console.</summary>
    private readonly IConsoleWriter ConsoleWriter;

    /// <summary>The log file to which to write messages.</summary>
    private readonly LogFileManager LogFile;

    /// <summary>The maximum length of the <see cref="LogLevel"/> values.</summary>
    private static readonly int MaxLevelLength = Enum.GetValues<LogLevel>().Max(level => level.ToString().Length);

    /// <summary>The cached representation for each level when added to a log header.</summary>
    private static readonly Dictionary<ConsoleLogLevel, string> LogStrings = Enum.GetValues<ConsoleLogLevel>().ToDictionary(level => level, level => level.ToString().ToUpperInvariant().PadRight(Monitor.MaxLevelLength));

    /// <summary>A cache of messages that should only be logged once.</summary>
    private readonly HashSet<LogOnceCacheKey> LogOnceCache = [];

    /// <summary>Get the screen ID that should be logged to distinguish between players in split-screen mode, if any.</summary>
    private readonly Func<int?> GetScreenIdForLog;

    /// <summary>Receives warning/error metadata for diagnostics, if any.</summary>
    private readonly Action<string, string, LogLevel>? OnLog;

    /// <summary>The pre-registered allocation-free health counter, if enabled.</summary>
    private readonly IModHealthLogCounter? HealthLogCounter;


    /*********
    ** Accessors
    *********/
    /// <summary>The mod ID, if applicable.</summary>
    public string ModId { get; }

    /// <summary>Whether to log basic contextual info (like buttons pressed and menus opened) even if <see cref="IsVerbose"/> is disabled.</summary>
    public static bool ForceLogContext { get; set; }

    /// <summary>The log contexts for which to enable verbose logging regardless of the configured settings.</summary>
    public static HashSet<string> ForceVerboseLogging { get; } = [];

    /// <summary>Whether to force verbose logging for SMAPI and all mods.</summary>
    public static bool ForceVerboseLoggingForAll { get; set; }

    /// <summary>The current log level for contextual info that's relevant to the <see cref="ForceLogContext"/> flag.</summary>
    public static LogLevel ContextLogLevel => Monitor.ForceLogContext ? LogLevel.Info : LogLevel.Trace;

    /// <inheritdoc />
    public bool IsVerbose
    {
        get => field || Monitor.ForceVerboseLoggingForAll || Monitor.ForceVerboseLogging.Contains(this.ModId);
        set => field = value;
    }

    /// <summary>Whether to show the full log stamps (with time/level/logger) in the console. If false, shows a simplified stamp with only the logger.</summary>
    internal bool ShowFullStampInConsole { get; set; }

    /// <summary>Whether to show trace messages in the console.</summary>
    internal bool ShowTraceInConsole { get; set; }

    /// <summary>Whether to write anything to the console. This should be disabled if no console is available.</summary>
    internal bool WriteToConsole { get; set; } = true;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="modId">The mod ID, if applicable.</param>
    /// <param name="source">The name of the module which logs messages using this instance.</param>
    /// <param name="logFile">The log file to which to write messages.</param>
    /// <param name="consoleWriter">Handles writing text to the console.</param>
    /// <param name="getScreenIdForLog">Get the screen ID that should be logged to distinguish between players in split-screen mode, if any.</param>
    /// <param name="onLog">Receives log metadata for diagnostics, if any.</param>
    /// <param name="healthLogCounter">The pre-registered health counter, if enabled.</param>
    public Monitor(string modId, string source, LogFileManager logFile, IConsoleWriter consoleWriter, Func<int?> getScreenIdForLog, Action<string, string, LogLevel>? onLog = null, IModHealthLogCounter? healthLogCounter = null)
    {
        // validate
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("The log source cannot be empty.");

        // initialize
        this.ModId = modId;
        this.Source = source;
        this.LogFile = logFile ?? throw new ArgumentNullException(nameof(logFile), "The log file manager cannot be null.");
        this.ConsoleWriter = consoleWriter ?? throw new ArgumentNullException(nameof(consoleWriter), "The console writer cannot be null.");
        this.GetScreenIdForLog = getScreenIdForLog;
        this.OnLog = onLog;
        this.HealthLogCounter = healthLogCounter;
    }

    /// <inheritdoc />
    public void Log(string message, LogLevel level = LogLevel.Trace)
    {
        this.LogImpl(this.Source, message, (ConsoleLogLevel)level);
    }

    /// <inheritdoc />
    public void LogOnce(string message, LogLevel level = LogLevel.Trace)
    {
        if (this.LogOnceCache.Add(new LogOnceCacheKey(message, level)))
            this.LogImpl(this.Source, message, (ConsoleLogLevel)level);
    }

    /// <inheritdoc />
    public void VerboseLog(string message)
    {
        if (this.IsVerbose)
            this.Log(message);
    }

    /// <inheritdoc />
    public void VerboseLog([InterpolatedStringHandlerArgument("")] ref VerboseLogStringHandler message)
    {
        if (this.IsVerbose)
            this.Log(message.ToString());
    }

    /// <summary>Write a newline to the console and log file.</summary>
    internal void Newline()
    {
        if (this.WriteToConsole)
            Console.WriteLine();
        this.LogFile.WriteLine("");
    }

    /// <summary>Log a fatal error message.</summary>
    /// <param name="message">The message to log.</param>
    internal void LogFatal(string message)
    {
        this.LogImpl(this.Source, message, ConsoleLogLevel.Critical);
    }

    /// <summary>Log console input from the user.</summary>
    /// <param name="input">The user input to log.</param>
    internal void LogUserInput(string input)
    {
        // user input already appears in the console, so just need to write to file
        this.LogFile.WriteLine(
            timestamp: DateTime.Now,
            level: Monitor.LogStrings[(ConsoleLogLevel)LogLevel.Info],
            screenId: this.GetScreenIdForLog(),
            source: this.Source,
            message: $"$>{input}"
        );
    }

    /// <summary>Log a message whose text can be created on the log-writer thread when it isn't visible in the console.</summary>
    /// <typeparam name="TState">The type of state passed to the message factory.</typeparam>
    /// <param name="state">The immutable or privately owned state from which to create the message.</param>
    /// <param name="getMessage">Create the message text.</param>
    /// <param name="level">The message severity.</param>
    internal void LogDeferred<TState>(TState state, Func<TState, string> getMessage, LogLevel level)
    {
        DateTime timestamp = DateTime.Now;
        ConsoleLogLevel consoleLevel = (ConsoleLogLevel)level;
        string levelText = Monitor.LogStrings[consoleLevel];
        int? screenId = this.GetScreenIdForLog();

        bool writeToConsole = this.WriteToConsole && (this.ShowTraceInConsole || consoleLevel != ConsoleLogLevel.Trace || Monitor.ForceVerboseLoggingForAll || Monitor.ForceVerboseLogging.Contains(this.ModId));
        if (writeToConsole)
        {
            string message = getMessage(state);
            string consoleMessage = this.ShowFullStampInConsole
                ? $"{Monitor.GenerateMessagePrefix(timestamp, this.Source, levelText, screenId)} {message}"
                : $"[{this.Source}] {message}";
            this.ConsoleWriter.WriteLine(consoleMessage, consoleLevel);
            this.LogFile.WriteLine(timestamp, levelText, screenId, this.Source, message);
        }
        else
            this.LogFile.WriteLine(timestamp, levelText, screenId, this.Source, state, getMessage);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Write a message line to the log.</summary>
    /// <param name="source">The name of the mod logging the message.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="level">The log level.</param>
    private void LogImpl(string source, string message, ConsoleLogLevel level)
    {
        LogLevel normalizedLevel = level switch
        {
            ConsoleLogLevel.Critical => LogLevel.Error,
            ConsoleLogLevel.Success => LogLevel.Info,
            _ => (LogLevel)level
        };
        this.OnLog?.Invoke(this.ModId, source, normalizedLevel);
        this.HealthLogCounter?.Record(
            normalizedLevel,
            message.Length,
            Environment.CurrentManagedThreadId,
            ModHealthReporterLogScope.IsActive ? ModHealthLogObservationCategory.Reporter : ModHealthLogObservationCategory.Normal
        );

        DateTime timestamp = DateTime.Now;
        string levelText = Monitor.LogStrings[level];
        int? screenId = this.GetScreenIdForLog();

        // write to console
        if (this.WriteToConsole && (this.ShowTraceInConsole || level != ConsoleLogLevel.Trace || Monitor.ForceVerboseLoggingForAll || Monitor.ForceVerboseLogging.Contains(this.ModId)))
        {
            string consoleMessage = this.ShowFullStampInConsole
                ? $"{Monitor.GenerateMessagePrefix(timestamp, source, levelText, screenId)} {message}"
                : $"[{source}] {message}";
            this.ConsoleWriter.WriteLine(consoleMessage, level);
        }

        // Queue raw fields so the file writer formats them outside the game thread.
        this.LogFile.WriteLine(timestamp, levelText, screenId, source, message);
    }

    /// <summary>Generate a message prefix for the current time.</summary>
    /// <param name="timestamp">The time at which the message was logged.</param>
    /// <param name="source">The name of the mod logging the message.</param>
    /// <param name="level">The padded log-level text.</param>
    /// <param name="screenId">The split-screen ID associated with the caller, if any.</param>
    private static string GenerateMessagePrefix(DateTime timestamp, string source, string level, int? screenId)
    {
        return $"[{timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {level}{(screenId != null ? $" screen_{screenId}" : "")} {source}]";
    }
}
