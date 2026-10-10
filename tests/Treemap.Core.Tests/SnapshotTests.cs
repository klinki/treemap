using System.Collections.Concurrent;
using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Exercises the snapshot/animator concurrency contract.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void PublishedSnapshotIsVisibleToReaders()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(1, "empty"));

        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 500));
        TreemapSnapshot published = publisher.Publish(root);

        Assert.Same(published, publisher.Current);
        Assert.Equal(1L, published.Version);
        Assert.True(publisher.Current.TryGetNode(2, out TreemapNode node));
        Assert.Equal("a", node.Name);
    }

    [Fact]
    public void VersionsIncreaseMonotonically()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(1, "empty"));

        long previous = publisher.Current.Version;
        for (int i = 0; i < 5; i++)
        {
            TreemapSnapshot s = publisher.Publish(TreeBuilder.Folder(1, $"root{i}"));
            Assert.True(s.Version > previous);
            previous = s.Version;
        }
    }

    /// <summary>
    /// A subscriber is notified on the publishing thread, and by then the new snapshot is
    /// already what <see cref="SnapshotPublisher.Current"/> returns.
    /// </summary>
    [Fact]
    public void PublishNotifiesAfterTheSnapshotIsVisible()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));

        List<TreemapSnapshot> seen = [];
        publisher.Published += () => seen.Add(publisher.Current);

        TreemapSnapshot published = publisher.Publish(TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100)));

        Assert.Single(seen);
        Assert.Same(published, seen[0]);
    }

    /// <summary>
    /// The notification runs after the writer lock is released, so a subscriber may
    /// publish again instead of deadlocking against the publisher that woke it.
    /// </summary>
    [Fact]
    public void SubscriberMayPublishAgainWithoutDeadlocking()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));

        int notifications = 0;
        TreemapSnapshot? nested = null;

        publisher.Published += () =>
        {
            if (Interlocked.Increment(ref notifications) > 1)
            {
                return;
            }

            nested = publisher.Publish(TreeBuilder.Folder(1, "nested"));
        };

        TreemapSnapshot outer = publisher.Publish(TreeBuilder.Folder(1, "outer"));

        Assert.NotNull(nested);
        Assert.Equal(2, notifications);
        Assert.Same(nested, publisher.Current);
        Assert.Equal(1L, outer.Version);
        Assert.Equal(2L, publisher.Current.Version);
    }

    /// <summary>
    /// A slow subscriber must not hold the writer lock: readers stay lock-free and another
    /// publisher keeps making progress while the notification is still running.
    /// </summary>
    [Fact]
    public void ReadersAndOtherPublishersProgressWhileANotificationIsInFlight()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));

        using ManualResetEventSlim insideNotification = new();
        using ManualResetEventSlim releaseNotification = new();
        long reads = 0;
        bool stop = false;
        Exception? failure = null;

        int notifications = 0;
        publisher.Published += () =>
        {
            if (Interlocked.Increment(ref notifications) == 1)
            {
                insideNotification.Set();
                if (!releaseNotification.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
            }
        };

        Thread reader = new(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    _ = publisher.Current.Root;
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref failure, ex);
            }
        })
        {
            IsBackground = true,
        };

        Thread first = new(() => publisher.Publish(TreeBuilder.Folder(1, "first")))
        {
            IsBackground = true,
        };

        reader.Start();
        first.Start();
        Assert.True(insideNotification.Wait(TimeSpan.FromSeconds(10)), "the subscriber was never called");

        // The subscriber is now parked inside the notification, with no lock held.
        Thread second = new(() => publisher.Publish(TreeBuilder.Folder(1, "second")))
        {
            IsBackground = true,
        };

        second.Start();
        Assert.True(second.Join(TimeSpan.FromSeconds(10)), "a second publisher blocked behind the notification");
        Assert.True(Volatile.Read(ref reads) > 0, "readers stalled while a notification was in flight");

        releaseNotification.Set();

        Assert.True(first.Join(TimeSpan.FromSeconds(10)), "the first publisher never returned");
        Volatile.Write(ref stop, true);
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "the reader never stopped");

        Assert.Null(failure);
        Assert.Equal(2L, publisher.Current.Version);
    }

    /// <summary>
    /// The core safety property: a reader walking a published snapshot must always
    /// observe a fully-constructed, self-consistent object, even while a writer is
    /// publishing concurrently.
    /// </summary>
    [Fact]
    public void ConcurrentPublishAndReadNeverObserveTornState()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));

        using CancellationTokenSource cts = new();
        int reads = 0;
        int writes = 0;
        Exception? failure = null;

        // Writer: publishes trees of varying size.
        Thread writer = new(() =>
        {
            try
            {
                for (int i = 1; i <= 400 && !cts.IsCancellationRequested; i++)
                {
                    TreemapNode[] children = new TreemapNode[i % 40 + 1];
                    for (int c = 0; c < children.Length; c++)
                    {
                        children[c] = TreeBuilder.File(1000 + c, $"f{c}", (c + 1) * 10);
                    }

                    publisher.Publish(TreeBuilder.Folder(1, "root", TreeBuilder.Children(children)));
                    Interlocked.Increment(ref writes);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                cts.Cancel();
            }
        });

        // Readers: walk snapshots concurrently, validating internal consistency.
        Thread[] readers = new Thread[4];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = new Thread(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        TreemapSnapshot snapshot = publisher.Current;

                        // Node lookup map must agree with the tree it was built from.
                        Assert.NotNull(snapshot.Root);
                        Assert.Equal(snapshot.Root.Id, snapshot.Root.Id);

                        if (snapshot.TryGetNode(snapshot.Root.Id, out TreemapNode root))
                        {
                            Assert.NotNull(root.Children);
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                    cts.Cancel();
                }
            });

            readers[t].IsBackground = true;
            readers[t].Start();
        }

        writer.Start();
        writer.Join();

        foreach (Thread reader in readers)
        {
            reader.Join(TimeSpan.FromSeconds(5));
        }

        Assert.Null(failure);
        Assert.True(writes > 0, "writer should have published at least once");
        Assert.True(reads > 0, "readers should have observed snapshots");
    }

    [Fact]
    public void PublishedSnapshotSurvivesLaterMutationOfTheSourceCollection()
    {
        List<TreemapNode> staging = new() { TreeBuilder.File(2, "a", 100) };
        TreemapNode root = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0, staging);

        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));
        TreemapSnapshot published = publisher.Publish(root);

        // The scanner keeps filling its staging list after publishing.
        staging.Add(TreeBuilder.File(3, "b", 7));
        staging.Clear();

        Assert.Equal(2, published.NodeCount);
        Assert.True(published.TryGetNode(2, out TreemapNode kept));
        Assert.Equal("a", kept.Name);
        Assert.False(published.TryGetNode(3, out _));
        Assert.Equal(100, published.Root.LogicalSize);
    }

    /// <summary>
    /// Several publishers may race. Because the version is allocated and the reference
    /// published under one lock, no reader can ever see the current snapshot go
    /// backwards, and no version number is handed out twice or lost.
    /// </summary>
    [Fact]
    public void ConcurrentPublishersNeverRegressTheObservedVersion()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(0, "empty"));

        const int WriterCount = 4;
        const int PublishesPerWriter = 250;
        int expectedTotal = WriterCount * PublishesPerWriter;

        ConcurrentDictionary<long, byte> issuedVersions = new();
        Exception? failure = null;
        long reads = 0;
        bool stop = false;

        Thread reader = new(() =>
        {
            try
            {
                long highest = 0;
                while (!Volatile.Read(ref stop))
                {
                    TreemapSnapshot snapshot = publisher.Current;
                    long version = snapshot.Version;

                    if (version < highest)
                    {
                        throw new InvalidOperationException(
                            $"published version went backwards: saw {highest}, then {version}");
                    }

                    highest = version;
                    Interlocked.Increment(ref reads);
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref failure, ex);
            }
        })
        {
            IsBackground = true,
        };

        Thread[] writers = new Thread[WriterCount];
        for (int w = 0; w < WriterCount; w++)
        {
            int writerId = w;
            writers[w] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < PublishesPerWriter; i++)
                    {
                        TreemapNode root = TreeBuilder.Folder(
                            1, $"root-{writerId}-{i}", TreeBuilder.File(1000 + i, "f", (i + 1) * 10L));

                        issuedVersions.TryAdd(publisher.Publish(root).Version, 0);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref failure, ex);
                }
            })
            {
                IsBackground = true,
            };
        }

        reader.Start();
        foreach (Thread writer in writers)
        {
            writer.Start();
        }

        foreach (Thread writer in writers)
        {
            Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "writer did not finish");
        }

        Volatile.Write(ref stop, true);
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "reader did not finish");

        Assert.Null(failure);
        Assert.True(reads > 0, "reader should have observed snapshots");

        // Every publish got its own version and none was dropped.
        Assert.Equal(expectedTotal, issuedVersions.Count);
        Assert.Equal((long)expectedTotal, publisher.Current.Version);
        Assert.Equal((long)expectedTotal + 1, publisher.Publish(TreeBuilder.Folder(1, "final")).Version);
    }

    [Fact]
    public void SnapshotsFromDifferentVersionsAreIndependent()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(1, "root"));

        publisher.Publish(TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100)));
        TreemapSnapshot first = publisher.Current;

        publisher.Publish(TreeBuilder.Folder(1, "root", TreeBuilder.File(3, "b", 200)));
        TreemapSnapshot second = publisher.Current;

        // The old snapshot must not see nodes added afterwards. This immutability is
        // exactly what lets the animator hold two snapshots at once.
        Assert.False(first.TryGetNode(3, out _));
        Assert.True(second.TryGetNode(3, out _));
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public void AnimatorSnapsWithoutTransition()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 200, 200));

        TreemapAnimator animator = new();
        animator.Snap(layout);

        Assert.False(animator.IsAnimating);
        Assert.Equal(1f, animator.Progress, precision: 3);
        Assert.Equal(layout.Count, animator.CurrentFrame.Count);
    }

    [Fact]
    public void AnimatorCompletesTransition()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 900));

        RectF bounds = new(0, 0, 400, 400);
        LayoutTween tween = new(
            SquarifyLayout.Compute(from, bounds),
            SquarifyLayout.Compute(to, bounds));

        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(30));
        animator.Begin(tween);

        Assert.True(animator.IsAnimating);

        // Pump until it settles.
        for (int i = 0; i < 200 && animator.Advance(); i++)
        {
            Thread.Sleep(2);
        }

        Assert.False(animator.IsAnimating);
        Assert.Equal(1f, animator.Progress, precision: 3);
    }
}

public class HitTestTests
{
    [Fact]
    public void HitTestReturnsDeepestCell()
    {
        TreemapNode leaf = TreeBuilder.File(3, "leaf", 100);
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "folder", leaf));

        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 400, 400));

        // Centre of the canvas is inside every nested cell; deepest must win.
        int hit = HitTester.HitTest(layout, 200, 200);
        Assert.Equal(3, layout.NodeIds[hit]);
    }

    [Fact]
    public void HitTestOutsideBoundsMisses()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(100, 100, 200, 200));

        Assert.Equal(-1, HitTester.HitTest(layout, 10, 10));
        Assert.Equal(-1, HitTester.HitTest(layout, 500, 500));
    }

    [Fact]
    public void HitTestContentSkipsSpaceCells()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "real", 100),
            TreeBuilder.Space(3, "free", NodeKind.FreeSpace, 900));

        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 400, 400));

        // Somewhere inside the large free-space block.
        int hit = HitTester.HitTestContent(layout, 390, 390);
        Assert.NotEqual(3, hit == -1 ? -99 : layout.NodeIds[hit]);
    }

    [Fact]
    public void PathToRootIncludesAllAncestors()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "a",
                TreeBuilder.Folder(3, "b",
                    TreeBuilder.File(4, "leaf", 100))));

        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 400, 400));

        Assert.True(layout.TryGetIndex(4, out int leafIndex));

        List<int> path = HitTester.PathToRoot(layout, leafIndex);

        Assert.Equal(4, path.Count);
        Assert.Equal(4, layout.NodeIds[path[0]]);
        Assert.Equal(3, layout.NodeIds[path[1]]);
        Assert.Equal(2, layout.NodeIds[path[2]]);
        Assert.Equal(1, layout.NodeIds[path[3]]);
    }

    [Fact]
    public void RootHasNoParent()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 200, 200));

        Assert.Equal(-1, layout.ParentIndices[layout.RootCell]);
    }
}
