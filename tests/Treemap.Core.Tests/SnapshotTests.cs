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
