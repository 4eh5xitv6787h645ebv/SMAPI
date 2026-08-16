using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Logging;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="LogFileManager"/>.</summary>
[TestFixture]
internal class LogFileManagerTests
{
    /*********
    ** Unit tests
    *********/
    [Test(Description = "Assert that an explicit flush writes all messages queued before it.")]
    public void Flush_WritesPendingMessages()
    {
        // arrange
        string path = this.GetTempFilePath();
        LogFileManager log = new(path);

        try
        {
            // act
            log.WriteLine("first");
            log.WriteLine("second");
            log.Flush();

            // assert
            File.ReadAllText(path).Should().Be("first\r\nsecond\r\n");
        }
        finally
        {
            log.Dispose();
            File.Delete(path);
        }
    }

    [Test(Description = "Assert that disposal drains concurrent messages without losing or corrupting entries.")]
    public void Dispose_DrainsConcurrentMessages()
    {
        // arrange
        const int producerCount = 8;
        const int messagesPerProducer = 2000; // exceeds the bounded queue capacity in total
        string path = this.GetTempFilePath();
        LogFileManager log = new(path);

        try
        {
            // act
            Parallel.For(0, producerCount, producer =>
            {
                for (int message = 0; message < messagesPerProducer; message++)
                    log.WriteLine($"{producer}:{message}");
            });
            log.Dispose();

            // assert
            string[] actual = File.ReadAllLines(path);
            HashSet<string> expected = Enumerable
                .Range(0, producerCount)
                .SelectMany(producer => Enumerable.Range(0, messagesPerProducer).Select(message => $"{producer}:{message}"))
                .ToHashSet();
            HashSet<string> actualSet = actual.ToHashSet();

            actual.Should().HaveCount(expected.Count);
            actualSet.Should().HaveCount(expected.Count);
            actualSet.SetEquals(expected).Should().BeTrue();
        }
        finally
        {
            log.Dispose();
            File.Delete(path);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get a unique temporary file path.</summary>
    private string GetTempFilePath()
    {
        return Path.Combine(Path.GetTempPath(), $"smapi-log-test-{Guid.NewGuid():N}.txt");
    }
}
