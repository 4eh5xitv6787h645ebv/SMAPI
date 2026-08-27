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
        ModHealthViewerAction duplicate = new(ModHealthViewerActionKind.SaveSnapshot, owner, Guid.NewGuid());

        queue.TryEnqueue(duplicate).Should().BeTrue();
        queue.TryEnqueue(duplicate).Should().BeTrue();
        for (int i = 1; i < ModHealthViewerActionQueue.Capacity; i++)
            queue.TryEnqueue(new(ModHealthViewerActionKind.Open, Guid.NewGuid())).Should().BeTrue();

        queue.PendingCount.Should().Be(ModHealthViewerActionQueue.Capacity);
        queue.TryEnqueue(new(ModHealthViewerActionKind.AddMark, Guid.NewGuid())).Should().BeFalse();
    }

    [Test]
    public void Close_DiscardsStaleActionsAndTakesPriority()
    {
        ModHealthViewerActionQueue queue = new();
        Guid owner = Guid.NewGuid();
        queue.TryEnqueue(new(ModHealthViewerActionKind.SaveSnapshot, owner));
        queue.TryEnqueue(new(ModHealthViewerActionKind.RetrySave, owner));

        ModHealthViewerAction close = new(ModHealthViewerActionKind.Close, owner);
        queue.TryEnqueue(close).Should().BeTrue();

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
        queue.TryEnqueue(first);
        queue.TryEnqueue(second);
        queue.TryDequeue(out _);
        queue.TryEnqueue(third);

        queue.TryDequeue(out ModHealthViewerAction actualSecond).Should().BeTrue();
        queue.TryDequeue(out ModHealthViewerAction actualThird).Should().BeTrue();

        actualSecond.Should().Be(second);
        actualThird.Should().Be(third);
    }
}
