using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        // always use Windows-style line endings for convenience
        // (Linux/macOS editors are fine with them, Windows editors often require them)
        this.Enqueue(new LogEntry(message + "\r\n", FlushCompletion: null));
    }

    /// <summary>Wait until all messages queued so far have been flushed to disk.</summary>
    public void Flush()
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this.Enqueue(new LogEntry(Message: null, FlushCompletion: completion));
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

                if (entry.Message != null)
                {
                    this.Stream.Write(entry.Message);
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


    /*********
    ** Private types
    *********/
    /// <summary>An entry queued for the log writer.</summary>
    /// <param name="Message">The formatted message to write, or <c>null</c> for a flush-only entry.</param>
    /// <param name="FlushCompletion">A completion to signal after flushing this entry, if any.</param>
    private readonly record struct LogEntry(string? Message, TaskCompletionSource<bool>? FlushCompletion);
}
