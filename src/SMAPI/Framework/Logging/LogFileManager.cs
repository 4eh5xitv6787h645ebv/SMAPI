using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>Manages reading and writing to log file.</summary>
internal class LogFileManager : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>The maximum number of messages which can wait for the writer thread.</summary>
    private const int MaxPendingMessages = 8192;

    /// <summary>The maximum number of messages written before an explicit flush.</summary>
    private const int MaxBatchSize = 256;

    /// <summary>The maximum time between explicit flushes while messages are continuously queued.</summary>
    private static readonly TimeSpan MaxFlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The underlying stream writer.</summary>
    private readonly StreamWriter Stream;

    /// <summary>The ordered queue of pending log entries.</summary>
    private readonly BlockingCollection<LogEntry> PendingEntries = new(new ConcurrentQueue<LogEntry>(), LogFileManager.MaxPendingMessages);

    /// <summary>The thread which writes queued messages to disk.</summary>
    private readonly Thread WriterThread;

    /// <summary>The first exception raised by the writer thread, if any.</summary>
    private Exception? WriterFailure;

    /// <summary>Whether this instance is being or has been disposed.</summary>
    private int IsDisposed;


    /*********
    ** Accessors
    *********/
    /// <summary>The full path to the log file being written.</summary>
    public string Path { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="path">The log file to write.</param>
    public LogFileManager(string path)
    {
        this.Path = path;

        // create log directory if needed
        string? logDir = System.IO.Path.GetDirectoryName(path);
        if (logDir == null)
            throw new ArgumentException($"The log path '{path}' is not valid.");
        Directory.CreateDirectory(logDir);

        // open log file stream
        this.Stream = new StreamWriter(path, append: false);

        // serialize file writes on a background thread so callers don't wait for filesystem flushes
        this.WriterThread = new Thread(this.WriteQueuedEntries)
        {
            IsBackground = true,
            Name = "SMAPI log writer"
        };

        try
        {
            this.WriterThread.Start();
        }
        catch
        {
            this.Stream.Dispose();
            this.PendingEntries.Dispose();
            throw;
        }
    }

    /// <summary>Write a message to the log.</summary>
    /// <param name="message">The message to log.</param>
    public void WriteLine(string message)
    {
        this.Enqueue(new LogEntry(message ?? "", StructuredMessage: null, FlushCompletion: null));
    }

    /// <summary>Write a structured log record whose file line will be formatted by the writer thread.</summary>
    /// <param name="timestamp">The time at which the caller logged the message.</param>
    /// <param name="level">The padded log-level text.</param>
    /// <param name="screenId">The split-screen ID associated with the caller, if any.</param>
    /// <param name="source">The name of the module logging the message.</param>
    /// <param name="message">The message text.</param>
    public void WriteLine(DateTime timestamp, string level, int? screenId, string source, string message)
    {
        this.Enqueue(new LogEntry(
            RawMessage: null,
            StructuredMessage: new StructuredLogMessage(timestamp, level, screenId, source, message),
            FlushCompletion: null
        ));
    }

    /// <summary>Write a structured log record whose message and file line will be formatted by the writer thread.</summary>
    /// <typeparam name="TState">The type of state passed to the message factory.</typeparam>
    /// <param name="timestamp">The time at which the caller logged the message.</param>
    /// <param name="level">The padded log-level text.</param>
    /// <param name="screenId">The split-screen ID associated with the caller, if any.</param>
    /// <param name="source">The name of the module logging the message.</param>
    /// <param name="state">The immutable or privately owned state from which to create the message.</param>
    /// <param name="getMessage">Create the message text.</param>
    public void WriteLine<TState>(DateTime timestamp, string level, int? screenId, string source, TState state, Func<TState, string> getMessage)
    {
        this.Enqueue(new LogEntry(
            RawMessage: null,
            StructuredMessage: new StructuredLogMessage(timestamp, level, screenId, source, new DeferredLogMessage<TState>(state, getMessage)),
            FlushCompletion: null
        ));
    }

    /// <summary>Wait until all messages queued so far have been flushed to disk.</summary>
    public void Flush()
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this.Enqueue(new LogEntry(RawMessage: null, StructuredMessage: null, FlushCompletion: completion));
        completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Release all resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.IsDisposed, 1) != 0)
            return;

        this.PendingEntries.CompleteAdding();
        this.WriterThread.Join();
        this.PendingEntries.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Add an entry to the bounded writer queue.</summary>
    /// <param name="entry">The entry to add.</param>
    private void Enqueue(LogEntry entry)
    {
        if (Volatile.Read(ref this.IsDisposed) != 0)
            throw new ObjectDisposedException(nameof(LogFileManager));

        Exception? failure = Volatile.Read(ref this.WriterFailure);
        if (failure != null)
            throw new IOException("The SMAPI log writer failed.", failure);

        try
        {
            // This deliberately applies backpressure instead of dropping log messages if the
            // bounded queue is saturated for longer than the writer can drain it.
            this.PendingEntries.Add(entry);
        }
        catch (InvalidOperationException)
        {
            failure = Volatile.Read(ref this.WriterFailure);
            if (failure != null)
                throw new IOException("The SMAPI log writer failed.", failure);

            throw new ObjectDisposedException(nameof(LogFileManager));
        }
    }

    /// <summary>Write queued entries to the underlying stream in order.</summary>
    private void WriteQueuedEntries()
    {
        LogEntry? currentEntry = null;

        try
        {
            Stopwatch flushTimer = Stopwatch.StartNew();
            int batchSize = 0;

            foreach (LogEntry entry in this.PendingEntries.GetConsumingEnumerable())
            {
                currentEntry = entry;

                if (entry.StructuredMessage is StructuredLogMessage message)
                {
                    this.WriteStructuredMessage(message);
                    batchSize++;
                }
                else if (entry.RawMessage != null)
                {
                    this.Stream.Write(entry.RawMessage);
                    this.Stream.Write("\r\n");
                    batchSize++;
                }

                bool flush =
                    entry.FlushCompletion != null
                    || batchSize >= LogFileManager.MaxBatchSize
                    || flushTimer.Elapsed >= LogFileManager.MaxFlushInterval
                    || this.PendingEntries.Count == 0;

                if (flush)
                {
                    this.Stream.Flush();
                    batchSize = 0;
                    flushTimer.Restart();
                }

                entry.FlushCompletion?.TrySetResult(true);
                currentEntry = null;
            }

            this.Stream.Flush();
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref this.WriterFailure, ex, null);
            this.PendingEntries.CompleteAdding();

            IOException wrapped = new("The SMAPI log writer failed.", ex);
            currentEntry?.FlushCompletion?.TrySetException(wrapped);
            while (this.PendingEntries.TryTake(out LogEntry entry))
                entry.FlushCompletion?.TrySetException(wrapped);
        }
        finally
        {
            try
            {
                this.Stream.Dispose();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref this.WriterFailure, ex, null);
            }
        }
    }

    /// <summary>Format and write one structured log message.</summary>
    private void WriteStructuredMessage(StructuredLogMessage message)
    {
        this.Stream.Write('[');
        this.Stream.Write(message.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        this.Stream.Write(' ');
        this.Stream.Write(message.Level);
        if (message.ScreenId != null)
        {
            this.Stream.Write(" screen_");
            this.Stream.Write(message.ScreenId.Value);
        }
        this.Stream.Write(' ');
        this.Stream.Write(message.Source);
        this.Stream.Write("] ");
        this.Stream.Write(
            message.Message switch
            {
                string text => text,
                IDeferredLogMessage deferred => deferred.GetMessage(),
                null => "",
                _ => throw new InvalidOperationException($"Unsupported structured log message type '{message.Message.GetType().FullName}'.")
            }
        );

        // always use Windows-style line endings for convenience
        // (Linux/macOS editors are fine with them, Windows editors often require them)
        this.Stream.Write("\r\n");
    }


    /*********
    ** Private types
    *********/
    /// <summary>An entry queued for the log writer.</summary>
    /// <param name="RawMessage">A raw message to write, or <c>null</c> for a structured/flush entry.</param>
    /// <param name="StructuredMessage">A structured message to format and write, or <c>null</c> for a raw/flush entry.</param>
    /// <param name="FlushCompletion">A completion to signal after flushing this entry, if any.</param>
    private readonly record struct LogEntry(string? RawMessage, StructuredLogMessage? StructuredMessage, TaskCompletionSource<bool>? FlushCompletion);

    /// <summary>A log message captured on the calling thread and formatted by the file writer.</summary>
    /// <param name="Timestamp">The time at which the message was logged.</param>
    /// <param name="Level">The padded log-level text.</param>
    /// <param name="ScreenId">The split-screen ID associated with the caller, if any.</param>
    /// <param name="Source">The name of the module logging the message.</param>
    /// <param name="Message">The message text or deferred message factory.</param>
    private readonly record struct StructuredLogMessage(DateTime Timestamp, string Level, int? ScreenId, string Source, object? Message);

    /// <summary>A deferred structured log message.</summary>
    private interface IDeferredLogMessage
    {
        /// <summary>Create the message text.</summary>
        string GetMessage();
    }

    /// <summary>A typed deferred structured log message.</summary>
    /// <typeparam name="TState">The type of state passed to the message factory.</typeparam>
    private sealed class DeferredLogMessage<TState> : IDeferredLogMessage
    {
        /// <summary>The state from which to create the message.</summary>
        private readonly TState State;

        /// <summary>Create the message text.</summary>
        private readonly Func<TState, string> GetMessageImpl;

        /// <summary>Construct an instance.</summary>
        public DeferredLogMessage(TState state, Func<TState, string> getMessage)
        {
            this.State = state;
            this.GetMessageImpl = getMessage ?? throw new ArgumentNullException(nameof(getMessage));
        }

        /// <inheritdoc />
        public string GetMessage()
        {
            return this.GetMessageImpl(this.State);
        }
    }
}
