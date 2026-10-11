using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Covers snapshot index pre-sizing.
/// </summary>
public class SnapshotAllocationTests
{
    // Generous bound for a single pre-sized index of NodeTotal nodes.
    private const long AllocationBoundBytes = 1_500_000;

    private const int Folders = 100;
    private const int LeavesPerFolder = 200;
    private const int NodeTotal = 1 + Folders + (Folders * LeavesPerFolder);

    [Fact]
    public void SnapshotConstructorStaysWithinAllocationBound()
    {
        // Built entirely outside the measurement window.
        TreemapNode root = BuildWideTree(Folders, LeavesPerFolder);

        // Warm up JIT so the window sees only steady-state constructor allocations.
        _ = new TreemapSnapshot(TreeBuilder.Folder(1, "warm"), 0, DateTimeOffset.UtcNow);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TreemapSnapshot snapshot = new(root, 1, DateTimeOffset.UtcNow);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(NodeTotal, snapshot.NodeCount);
        Assert.True(
            allocated <= AllocationBoundBytes,
            $"Snapshot of {NodeTotal:N0} nodes allocated {allocated:N0} bytes; bound is {AllocationBoundBytes:N0} bytes.");
    }

    [Fact]
    public void PresizedIndexKeepsNodeCountAndParentLookup()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "child",
                TreeBuilder.File(3, "leaf", 100)));

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(3, snapshot.NodeCount);
        Assert.False(snapshot.TryGetParent(1, out _));
        Assert.True(snapshot.TryGetParent(2, out int parentOfChild));
        Assert.Equal(1, parentOfChild);
        Assert.True(snapshot.TryGetParent(3, out int parentOfLeaf));
        Assert.Equal(2, parentOfLeaf);
        Assert.True(snapshot.TryGetNode(3, out TreemapNode leaf));
        Assert.Equal("leaf", leaf.Name);

        TreemapSnapshot solo = TreemapSnapshot.Create(TreeBuilder.Folder(7, "solo"), 2);
        Assert.Equal(1, solo.NodeCount);
        Assert.False(solo.TryGetParent(7, out _));
        Assert.False(solo.TryGetParent(999, out _));
    }

    [Fact]
    public void DeepNodePathResolvesThroughParents()
    {
        const int Depth = 12;
        TreemapNode node = TreeBuilder.File(Depth, "leaf", 10);
        for (int id = Depth - 1; id >= 1; id--)
        {
            string name = id == 1 ? "root" : "n" + id;
            node = TreeBuilder.Folder(id, name, node);
        }

        TreemapSnapshot snapshot = TreemapSnapshot.Create(node, 1);

        Assert.Equal(Depth, snapshot.NodeCount);

        int current = Depth;
        for (int expected = Depth; expected >= 1; expected--)
        {
            Assert.True(snapshot.TryGetNode(current, out _));
            if (expected == 1)
            {
                Assert.False(snapshot.TryGetParent(current, out _));
            }
            else
            {
                Assert.True(snapshot.TryGetParent(current, out int parent));
                Assert.Equal(expected - 1, parent);
                current = parent;
            }
        }

        string expectedPath = "root/" + string.Join("/", Enumerable.Range(2, Depth - 2).Select(i => "n" + i)) + "/leaf";
        Assert.Equal(expectedPath, snapshot.GetPath(Depth));
    }

    [Fact]
    public void DuplicateIdsKeepLastWriteWinsLookupSemantics()
    {
        // Last depth-first visit wins.
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(10, "a", TreeBuilder.File(2, "first", 100)),
            TreeBuilder.Folder(20, "b", TreeBuilder.File(2, "second", 200)));

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(4, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(2, out TreemapNode winner));
        Assert.Equal("second", winner.Name);
        Assert.True(snapshot.TryGetParent(2, out int parent));
        Assert.Equal(20, parent);
    }

    private static TreemapNode BuildWideTree(int folders, int leavesPerFolder)
    {
        int nextId = 2;
        TreemapNode[] kids = new TreemapNode[folders];
        for (int f = 0; f < folders; f++)
        {
            TreemapNode[] leaves = new TreemapNode[leavesPerFolder];
            for (int l = 0; l < leavesPerFolder; l++)
            {
                int id = nextId++;
                leaves[l] = TreeBuilder.File(id, "f" + id, 100);
            }

            int folderId = nextId++;
            kids[f] = TreemapNode.Create(folderId, "d" + folderId, NodeKind.Folder, 0, 0, null, 0, leaves);
        }

        return TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0, kids);
    }
}
