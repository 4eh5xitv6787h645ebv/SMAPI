using System;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Extensions;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for the state-passing <see cref="ReaderWriterLockSlimExtensions"/> methods.</summary>
[TestFixture]
internal class ReaderWriterLockSlimExtensionsTests
{
    /// <summary>Return the passed integer state.</summary>
    private static readonly Func<int, int> ReturnState = static value => value;

    [Test(Description = "Assert that stateful callbacks run under the requested lock and return their result.")]
    public void StatefulCallbacks_RunUnderLockAndReturnResult()
    {
        using ReaderWriterLockSlim sync = new();

        int readResult = sync.InReadLock(
            (Lock: sync, Value: 41),
            static state =>
            {
                state.Lock.IsReadLockHeld.Should().BeTrue();
                return state.Value + 1;
            }
        );
        int writeResult = sync.InWriteLock(
            (Lock: sync, Value: 42),
            static state =>
            {
                state.Lock.IsWriteLockHeld.Should().BeTrue();
                return state.Value + 1;
            }
        );

        readResult.Should().Be(42);
        writeResult.Should().Be(43);
    }

    [Test(Description = "Assert that stateful callbacks release their lock after an exception.")]
    public void StatefulCallbacks_ReleaseLockAfterException()
    {
        using ReaderWriterLockSlim sync = new();

        FluentActions.Invoking(() => sync.InWriteLock(0, static _ => throw new InvalidOperationException("test")))
            .Should().Throw<InvalidOperationException>();

        sync.IsWriteLockHeld.Should().BeFalse();
    }

    [Test(Description = "Assert that warmed stateful lock callbacks don't allocate.")]
    public void StatefulCallbacks_DoNotAllocate()
    {
        using ReaderWriterLockSlim sync = new();
        const int iterations = 10_000;
        _ = sync.InReadLock(0, ReaderWriterLockSlimExtensionsTests.ReturnState);
        _ = sync.InWriteLock(0, ReaderWriterLockSlimExtensionsTests.ReturnState);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            _ = sync.InReadLock(i, ReaderWriterLockSlimExtensionsTests.ReturnState);
            _ = sync.InWriteLock(i, ReaderWriterLockSlimExtensionsTests.ReturnState);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        allocatedBytes.Should().Be(0);
    }
}
