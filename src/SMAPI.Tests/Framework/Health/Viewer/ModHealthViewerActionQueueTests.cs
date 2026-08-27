using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer;

namespace SMAPI.Tests.Framework.Health.Viewer;

[TestFixture]
internal sealed class ModHealthViewerActionQueueTests
{
    [Test]
    public void Queue_IsBoundedAndCoalescesExactDuplicates()
    {
        ModHealthViewerActionQueue queue = new();
        Guid owner = Guid.NewGuid();
        ModHealthViewerAction duplicate = new(ModHealthViewerActionKind.RefreshAndSaveSnapshot, owner, Guid.NewGuid());

        queue.Enqueue(duplicate).Should().Be(ModHealthViewerActionDisposition.Queued);
        queue.Enqueue(duplicate).Should().Be(ModHealthViewerActionDisposition.Coalesced);
        for (int i = 1; i < ModHealthViewerActionQueue.Capacity; i++)
            queue.Enqueue(new(ModHealthViewerActionKind.Open, Guid.NewGuid())).Should().Be(ModHealthViewerActionDisposition.Queued);

        queue.PendingCount.Should().Be(ModHealthViewerActionQueue.Capacity);
        queue.Enqueue(new(ModHealthViewerActionKind.AddMark, Guid.NewGuid())).Should().Be(ModHealthViewerActionDisposition.RejectedFull);
    }

    [Test]
    public void Close_DiscardsStaleActionsAndTakesPriority()
    {
        ModHealthViewerActionQueue queue = new();
        Guid owner = Guid.NewGuid();
        queue.Enqueue(new(ModHealthViewerActionKind.RefreshAndSaveSnapshot, owner));
        queue.Enqueue(new(ModHealthViewerActionKind.RetrySave, owner));

        ModHealthViewerAction close = new(ModHealthViewerActionKind.Close, owner);
        queue.Enqueue(close).Should().Be(ModHealthViewerActionDisposition.Queued);

        queue.PendingCount.Should().Be(1);
        queue.TryDequeue(out ModHealthViewerAction actual).Should().BeTrue();
        actual.Should().Be(close);
        queue.HasPendingActions.Should().BeFalse();
    }

    [Test]
    public void Queue_PreservesFifoOrderAcrossRingWrap()
    {
        ModHealthViewerActionQueue queue = new();
        Guid owner = Guid.NewGuid();
        ModHealthViewerAction first = new(ModHealthViewerActionKind.Open, owner);
        ModHealthViewerAction second = new(ModHealthViewerActionKind.AddMark, owner);
        ModHealthViewerAction third = new(ModHealthViewerActionKind.StopCapture, owner);
        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.TryDequeue(out _);
        queue.Enqueue(third);

        queue.TryDequeue(out ModHealthViewerAction actualSecond).Should().BeTrue();
        queue.TryDequeue(out ModHealthViewerAction actualThird).Should().BeTrue();

        actualSecond.Should().Be(second);
        actualThird.Should().Be(third);
    }

    [Test]
    public void StaleClose_DoesNotDiscardCurrentViewerActions()
    {
        ModHealthViewerActionQueue queue = new();
        Guid current = Guid.NewGuid();
        Guid stale = Guid.NewGuid();
        ModHealthViewerAction save = new(ModHealthViewerActionKind.RefreshAndSaveSnapshot, current);
        ModHealthViewerAction staleClose = new(ModHealthViewerActionKind.Close, stale);
        queue.Enqueue(save);

        queue.Enqueue(staleClose).Should().Be(ModHealthViewerActionDisposition.Queued);

        queue.TryDequeue(out ModHealthViewerAction first).Should().BeTrue();
        queue.TryDequeue(out ModHealthViewerAction second).Should().BeTrue();
        first.Should().Be(save);
        second.Should().Be(staleClose);
    }

    [Test]
    public void RepeatedMarksAndStateTransitions_PreserveFifoSemantics()
    {
        ModHealthViewerActionQueue queue = new();
        Guid owner = Guid.NewGuid();
        ModHealthViewerAction mark = new(ModHealthViewerActionKind.AddMark, owner);
        ModHealthViewerAction stop = new(ModHealthViewerActionKind.StopCapture, owner);

        queue.Enqueue(mark).Should().Be(ModHealthViewerActionDisposition.Queued);
        queue.Enqueue(mark).Should().Be(ModHealthViewerActionDisposition.Queued);
        queue.Enqueue(stop).Should().Be(ModHealthViewerActionDisposition.Queued);
        queue.Enqueue(mark).Should().Be(ModHealthViewerActionDisposition.Queued);

        queue.PendingCount.Should().Be(4);
        queue.TryDequeue(out ModHealthViewerAction first);
        queue.TryDequeue(out ModHealthViewerAction second);
        queue.TryDequeue(out ModHealthViewerAction third);
        queue.TryDequeue(out ModHealthViewerAction fourth);
        new[] { first, second, third, fourth }.Should().Equal(mark, mark, stop, mark);
    }
}
