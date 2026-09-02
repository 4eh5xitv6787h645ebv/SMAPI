using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class RetainedPreparedReleasePackageTests
{
    [Test]
    public async Task DisposeAsync_ImmediatelyRevokesPackageAndJoinsOneOwnerCleanup()
    {
        PendingAsyncOwner owner = new();
        InstallerPackageOpenInput package = CreatePackage();
        RetainedPreparedReleasePackage prepared = new(package, owner);

        prepared.Package.Should().BeSameAs(package);
        ValueTask first = prepared.DisposeAsync();
        ValueTask second = prepared.DisposeAsync();

        owner.DisposeCalls.Should().Be(1);
        first.AsTask().Should().BeSameAs(second.AsTask());
        first.IsCompleted.Should().BeFalse();
        FluentActions.Invoking(() => _ = prepared.Package).Should().Throw<ObjectDisposedException>();

        owner.Complete();
        await Task.WhenAll(first.AsTask(), second.AsTask());
        owner.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposeAsync_OwnerFailureIsSharedByEveryCallerAndPackageStaysRevoked()
    {
        InvalidOperationException failure = new("sanitized test failure");
        PendingAsyncOwner owner = new();
        RetainedPreparedReleasePackage prepared = new(CreatePackage(), owner);

        Task first = prepared.DisposeAsync().AsTask();
        Task second = prepared.DisposeAsync().AsTask();
        owner.Fail(failure);

        Func<Task> awaitFirst = async () => await first;
        Func<Task> awaitSecond = async () => await second;
        (await awaitFirst.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        (await awaitSecond.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        owner.DisposeCalls.Should().Be(1);
        FluentActions.Invoking(() => _ = prepared.Package).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposeAsync_SynchronousOwnerFailureIsPublishedOnceAndPackageStaysRevoked()
    {
        InvalidOperationException failure = new("synchronous sanitized test failure");
        SynchronouslyThrowingOwner owner = new(failure);
        RetainedPreparedReleasePackage prepared = new(CreatePackage(), owner);

        Task first = prepared.DisposeAsync().AsTask();
        Task second = prepared.DisposeAsync().AsTask();

        first.Should().BeSameAs(second);
        Func<Task> awaitFirst = async () => await first;
        Func<Task> awaitSecond = async () => await second;
        (await awaitFirst.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        (await awaitSecond.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        owner.DisposeCalls.Should().Be(1);
        FluentActions.Invoking(() => _ = prepared.Package).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task PackageAndDispose_ConcurrentCallsAreLinearizedWithoutDuplicateCleanup()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            InstallerPackageOpenInput package = CreatePackage();
            CompletedAsyncOwner owner = new();
            RetainedPreparedReleasePackage prepared = new(package, owner);
            using CountdownEvent ready = new(2);
            using ManualResetEventSlim start = new(false);
            Task<bool> read = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    return ReferenceEquals(prepared.Package, package);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            });
            Task dispose = Task.Run(async () =>
            {
                ready.Signal();
                start.Wait();
                await prepared.DisposeAsync();
            });
            ready.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
            start.Set();

            _ = await read.WaitAsync(TimeSpan.FromSeconds(2));
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            owner.DisposeCalls.Should().Be(1);
            FluentActions.Invoking(() => _ = prepared.Package).Should().Throw<ObjectDisposedException>();
        }
    }

    [Test]
    public void Constructor_NullPackageOrOwner_IsRejected()
    {
        FluentActions.Invoking(() => new RetainedPreparedReleasePackage(null!, new PendingAsyncOwner()))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new RetainedPreparedReleasePackage(CreatePackage(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static InstallerPackageOpenInput CreatePackage()
    {
        return new InstallerPackageOpenInput(
            "tag",
            "commit",
            "package",
            "checksums",
            "metadata",
            "manifest",
            "bundle",
            "bundle-checksum",
            new ProtocolProcWorkspaceIdentity(1, 2, 3, 4, 5)
        );
    }

    private sealed class PendingAsyncOwner : IAsyncDisposable
    {
        private readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return new ValueTask(this.Completion.Task);
        }

        public void Complete()
        {
            this.Completion.TrySetResult();
        }

        public void Fail(Exception exception)
        {
            this.Completion.TrySetException(exception);
        }
    }

    private sealed class SynchronouslyThrowingOwner(Exception failure) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            throw failure;
        }
    }

    private sealed class CompletedAsyncOwner : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
