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

/// <summary>
/// Covers the paged snapshot index: dense production ids page, sparse/extreme ids
/// stay correct, and snapshots stay immutable. Fixtures stay modest (≤10K nodes);
/// no test allocates anywhere near OOM.
/// </summary>
public class SnapshotPagedIndexTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8190)]
    public void NegativeParentIdsResolveInBothIndexShapes(int fileCount)
    {
        TreemapNode[] files = Enumerable.Range(2, fileCount)
            .Select(id => TreeBuilder.File(id, "f" + id, 10)).ToArray();
        TreemapNode branch = TreemapNode.Create(-1, "branch", NodeKind.Folder, children: files);
        TreemapNode root = TreemapNode.Create(int.MinValue, "root", NodeKind.Folder, children: [branch]);
        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.False(snapshot.TryGetParent(root.Id, out _));
        Assert.True(snapshot.TryGetParent(branch.Id, out int rootId));
        Assert.Equal(root.Id, rootId);
        Assert.True(snapshot.TryGetParent(2, out int branchId));
        Assert.Equal(-1, branchId);
        Assert.Equal("root/branch/f2", snapshot.GetPath(2));
    }
    [Fact]
    public void DenseMultiPageIdsResolveNodesParentsAndPaths()
    {
        // 5001 visits over 2 pages => paged (avg above the half-page occupancy threshold).
        TreemapNode root = BuildFlat(1, "root", startId: 2, count: 5000);

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 7, rootPath: null);

        Assert.Equal(5001, snapshot.NodeCount);
        Assert.Equal(7, snapshot.Version);
        Assert.Equal("root", snapshot.Root.Name);

        // Page-boundary ids: 4095/4096/4097 share neighbouring pages/offsets.
        foreach (int id in new[] { 1, 2, 4095, 4096, 4097, 5001 })
        {
            Assert.True(snapshot.TryGetNode(id, out _), $"id {id} should resolve");
            if (id != 1)
            {
                Assert.True(snapshot.TryGetParent(id, out int parent));
                Assert.Equal(1, parent);
            }
            else
            {
                Assert.False(snapshot.TryGetParent(id, out _));
            }
        }

        Assert.False(snapshot.TryGetNode(5002, out _));
        Assert.False(snapshot.TryGetParent(5002, out _));
        Assert.Equal("5002", snapshot.GetPath(5002));
        Assert.Equal("root/f4096", snapshot.GetPath(4096));
    }

    [Fact]
    public void RootOutlierWithHighDenseRangeResolves()
    {
        // Root id 1 plus a dense block at 100M+: min/max span is ~100M but only
        // a few distinct pages exist, so occupancy still selects the paged index.
        const int Base = 100_000_000;
        const int Count = 7000;
        TreemapNode[] files = new TreemapNode[Count];
        for (int i = 0; i < Count; i++)
        {
            int id = Base + i;
            files[i] = TreeBuilder.File(id, "h" + id, 100);
        }

        TreemapNode folder = TreemapNode.Create(Base - 1, "hot", NodeKind.Folder, 0, 0, null, 0, files);
        TreemapNode root = TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0, [folder]);

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(Count + 2, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(Base, out TreemapNode first));
        Assert.Equal("h" + Base, first.Name);
        Assert.True(snapshot.TryGetNode(Base + Count - 1, out _));
        Assert.True(snapshot.TryGetParent(Base, out int parent));
        Assert.Equal(Base - 1, parent);
        Assert.True(snapshot.TryGetParent(Base - 1, out int grandparent));
        Assert.Equal(1, grandparent);
        Assert.False(snapshot.TryGetParent(1, out _));
        Assert.Equal("root/hot/h" + Base, snapshot.GetPath(Base));
    }

    [Fact]
    public void NegativeAndLimitIdsDoNotAlias()
    {
        TreemapNode root = TreeBuilder.Folder(0, "root",
            TreeBuilder.File(int.MinValue, "min", 10),
            TreeBuilder.File(-4096, "neg-page", 10),
            TreeBuilder.File(-1, "neg-one", 10),
            TreeBuilder.File(4096, "pos-page", 10),
            TreeBuilder.File(int.MaxValue, "max", 10));

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(6, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(int.MinValue, out TreemapNode min));
        Assert.Equal("min", min.Name);
        Assert.True(snapshot.TryGetNode(-4096, out TreemapNode negPage));
        Assert.Equal("neg-page", negPage.Name);
        Assert.True(snapshot.TryGetNode(-1, out TreemapNode negOne));
        Assert.Equal("neg-one", negOne.Name);

        // 0 and 4096 share offset 0 on different pages; they must not alias.
        Assert.True(snapshot.TryGetNode(0, out TreemapNode rootNode));
        Assert.Equal("root", rootNode.Name);
        Assert.True(snapshot.TryGetNode(4096, out TreemapNode posPage));
        Assert.Equal("pos-page", posPage.Name);

        Assert.True(snapshot.TryGetNode(int.MaxValue, out TreemapNode max));
        Assert.Equal("max", max.Name);

        Assert.True(snapshot.TryGetParent(int.MaxValue, out int parent));
        Assert.Equal(0, parent);
        Assert.False(snapshot.TryGetParent(0, out _));
        Assert.False(snapshot.TryGetNode(1, out _));
    }

    [Fact]
    public void DenseNegativePagedRangeResolves()
    {
        // 9000 sequential negative ids fill three pages enough to select paging.
        const int Count = 9000;
        const int Start = -9000;
        TreemapNode[] files = new TreemapNode[Count];
        for (int i = 0; i < Count; i++)
        {
            int id = Start + 1 + i;
            files[i] = TreeBuilder.File(id, "n" + id, 10);
        }

        TreemapNode root = TreemapNode.Create(Start, "root", NodeKind.Folder, 0, 0, null, 0, files);
        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(Count + 1, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(Start + 1, out _));
        Assert.True(snapshot.TryGetNode(-4096, out TreemapNode boundary));
        Assert.Equal("n-4096", boundary.Name);
        Assert.True(snapshot.TryGetNode(-4000, out _));
        Assert.True(snapshot.TryGetParent(-4000, out int parent));
        Assert.Equal(Start, parent);
        Assert.Equal("root/n-4096", snapshot.GetPath(-4096));
    }

    [Fact]
    public void SparseIdsStayCorrectWithoutAliasing()
    {
        // Four nodes on four far-apart pages: dictionary fallback, still exact.
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(5_000_000, "a", 10),
            TreeBuilder.File(10_000_000, "b", 10),
            TreeBuilder.File(100_000_000, "c", 10));

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(4, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(5_000_000, out TreemapNode a));
        Assert.Equal("a", a.Name);
        Assert.True(snapshot.TryGetNode(10_000_000, out _));
        Assert.True(snapshot.TryGetNode(100_000_000, out _));
        Assert.False(snapshot.TryGetNode(5_000_001, out _));
        Assert.True(snapshot.TryGetParent(100_000_000, out int parent));
        Assert.Equal(1, parent);
    }

    [Fact]
    public void DuplicateIdsInDensePagedTreeKeepLastVisitAndUniqueCount()
    {
        // Enough filler to force paging, plus a duplicated file id across folders.
        const int Filler = 4990;
        TreemapNode[] filler = new TreemapNode[Filler];
        for (int i = 0; i < Filler; i++)
        {
            int id = 10 + i;
            filler[i] = TreeBuilder.File(id, "pad" + id, 10);
        }

        TreemapNode left = TreeBuilder.Folder(2, "left", TreeBuilder.File(5000, "first", 100));
        TreemapNode right = TreeBuilder.Folder(3, "right", TreeBuilder.File(5000, "second", 200));
        TreemapNode[] kids = [left, right, .. filler];
        TreemapNode root = TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0, kids);

        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1);

        Assert.Equal(1 + 2 + Filler + 1, snapshot.NodeCount);
        Assert.True(snapshot.TryGetNode(5000, out TreemapNode winner));
        Assert.Equal("second", winner.Name);
        Assert.True(snapshot.TryGetParent(5000, out int parent));
        Assert.Equal(3, parent);
    }

    [Fact]
    public void OldAndNewPagedSnapshotsStayIndependent()
    {
        SnapshotPublisher publisher = new(TreeBuilder.Folder(1, "empty"));

        const int Count = 8190;
        TreemapSnapshot first = publisher.Publish(BuildFlat(1, "root", startId: 2, count: Count));
        TreemapSnapshot second = publisher.Publish(BuildFlat(1, "root", startId: 2 + Count, count: Count));

        Assert.Equal(Count + 1, first.NodeCount);
        Assert.Equal(Count + 1, second.NodeCount);
        Assert.True(first.TryGetNode(2, out _));
        Assert.False(first.TryGetNode(2 + Count, out _));
        Assert.False(second.TryGetNode(2, out _));
        Assert.True(second.TryGetNode(2 + Count, out _));
        Assert.NotEqual(first.Version, second.Version);
    }

    private static TreemapNode BuildFlat(int rootId, string rootName, int startId, int count)
    {
        TreemapNode[] files = new TreemapNode[count];
        for (int i = 0; i < count; i++)
        {
            int id = startId + i;
            files[i] = TreeBuilder.File(id, "f" + id, 100);
        }

        return TreemapNode.Create(rootId, rootName, NodeKind.Folder, 0, 0, null, 0, files);
    }
}
