using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Covers the node immutability contract and the size-aggregation rules that the
/// snapshot design depends on: a node must be a value, not a view onto a collection
/// the scanner is still editing.
/// </summary>
public class ModelTests
{
    [Fact]
    public void CreateCopiesTheCallerArray()
    {
        TreemapNode[] backing = new TreemapNode[] { TreeBuilder.File(2, "a", 100) };
        TreemapNode root = TreeBuilder.Folder(1, "root", backing);

        // The scanner reuses and overwrites its staging arrays between publishes.
        backing[0] = TreeBuilder.File(99, "intruder", 999_999);

        Assert.Single(root.Children);
        Assert.Equal("a", root.Children[0].Name);
        Assert.Equal(100, root.LogicalSize);
        Assert.Equal(100, root.PhysicalSize);
    }

    [Fact]
    public void CreateCopiesTheCallerList()
    {
        List<TreemapNode> staging = new() { TreeBuilder.File(2, "a", 100) };
        TreemapNode root = TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0, staging);

        // A scanner appends to the live list as the walk proceeds.
        staging.Add(TreeBuilder.File(3, "b", 7));
        staging[0] = TreeBuilder.File(99, "intruder", 999_999);
        staging.Clear();

        Assert.Single(root.Children);
        Assert.Equal("a", root.Children[0].Name);
        Assert.Equal(100, root.LogicalSize);
    }

    [Fact]
    public void ChildrenCannotBeMutatedThroughAnExposedIList()
    {
        TreemapNode leaf = TreeBuilder.File(2, "a", 100);
        TreemapNode root = TreeBuilder.Folder(1, "root", leaf);

        Assert.IsAssignableFrom<IList<TreemapNode>>(root.Children);

        IList<TreemapNode> exposed = Assert.IsAssignableFrom<IList<TreemapNode>>(root.Children);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed.Add(TreeBuilder.File(3, "b", 1)));
        Assert.Throws<NotSupportedException>(() => exposed[0] = TreeBuilder.File(3, "b", 1));
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        Assert.Throws<NotSupportedException>(() => exposed.RemoveAt(0));

        Assert.Single(root.Children);
        Assert.Same(leaf, root.Children[0]);
    }

    [Fact]
    public void LeafChildrenViewIsAlsoReadOnly()
    {
        TreemapNode leaf = TreeBuilder.File(2, "a", 100);

        IList<TreemapNode> exposed = Assert.IsAssignableFrom<IList<TreemapNode>>(leaf.Children);
        Assert.Empty(leaf.Children);
        Assert.Throws<NotSupportedException>(() => exposed.Add(TreeBuilder.File(3, "b", 1)));
    }

    /// <summary>
    /// Block allocation can occupy more bytes than the file content. Summing the layout
    /// size for both dimensions would report the parent's apparent size as its
    /// allocated size.
    /// </summary>
    [Fact]
    public void CreateSumsLogicalAndPhysicalIndependently()
    {
        TreemapNode rounded = TreemapNode.Create(2, "rounded.bin", NodeKind.File, logicalSize: 5, physicalSize: 10);
        TreemapNode parent = TreeBuilder.Folder(1, "root", rounded);

        Assert.Equal(5, parent.LogicalSize);
        Assert.Equal(10, parent.PhysicalSize);
        Assert.Equal(10, parent.LayoutSize);
    }

    [Fact]
    public void CreateSumsMixedChildrenAcrossBothDimensions()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreemapNode.Create(2, "rounded.bin", NodeKind.File, logicalSize: 5, physicalSize: 10),
            TreemapNode.Create(3, "plain.bin", NodeKind.File, logicalSize: 7, physicalSize: 7));

        Assert.Equal(12, root.LogicalSize);
        Assert.Equal(17, root.PhysicalSize);
    }

    [Fact]
    public void AggregationSumsLogicalAndPhysicalIndependently()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreemapNode.Create(2, "rounded.bin", NodeKind.File, logicalSize: 5, physicalSize: 10),
            TreemapNode.Create(3, "plain.bin", NodeKind.File, logicalSize: 7, physicalSize: 7));

        TreemapNode aggregated = TreemapNode.WithAggregatedSizes(root);

        Assert.Equal(12, aggregated.LogicalSize);
        Assert.Equal(17, aggregated.PhysicalSize);
    }

    [Fact]
    public void AggregatingAnUnchangedLeafReturnsTheSameInstance()
    {
        TreemapNode leaf = TreeBuilder.File(2, "a", 100);

        Assert.Same(leaf, TreemapNode.WithAggregatedSizes(leaf));
    }

    /// <summary>
    /// A single-child chain 256 deep costs 257 visits, not 2^257, and because every
    /// ancestor already agrees with its child nothing is rebuilt at all.
    /// </summary>
    [Fact]
    public void AggregatingAnUnchangedDeepChainReturnsTheSameInstance()
    {
        TreemapNode node = TreeBuilder.File(1, "leaf", 42);
        for (int depth = 1; depth <= 256; depth++)
        {
            node = TreeBuilder.Folder(depth + 1, $"d{depth}", node);
        }

        TreemapNode aggregated = TreemapNode.WithAggregatedSizes(node);

        Assert.Same(node, aggregated);
        Assert.Equal(42, aggregated.LogicalSize);
        Assert.Equal(42, aggregated.PhysicalSize);

        // Spot-check the deepest leaf still reads through the whole chain.
        TreemapNode cursor = aggregated;
        for (int depth = 256; depth >= 1; depth--)
        {
            cursor = cursor.Children[0];
        }

        Assert.Equal("leaf", cursor.Name);
    }

    [Fact]
    public void AggregationRebuildsOnlyNodesWhoseTotalsWereStale()
    {
        TreemapNode leaf = TreeBuilder.File(3, "leaf", 100);
        TreemapNode folder = TreemapNode.Create(2, "folder", NodeKind.Folder, 50, 50, null, 0,
            new List<TreemapNode> { leaf });
        TreemapNode drive = TreemapNode.Create(1, "drive", NodeKind.Drive, 50, 50, null, 0,
            new List<TreemapNode> { folder });

        TreemapNode aggregated = TreemapNode.WithAggregatedSizes(drive);

        Assert.NotSame(drive, aggregated);
        Assert.Equal(100, aggregated.LogicalSize);
        Assert.Equal(100, aggregated.PhysicalSize);

        TreemapNode rebuiltFolder = aggregated.Children[0];
        Assert.NotSame(folder, rebuiltFolder);
        Assert.Equal(100, rebuiltFolder.LogicalSize);

        // The leaf never changed, so its instance is carried through untouched.
        Assert.Same(leaf, rebuiltFolder.Children[0]);
    }

    [Fact]
    public void AggregationLeavesConsistentSiblingsAlone()
    {
        TreemapNode untouched = TreeBuilder.Folder(50, "untouched", TreeBuilder.File(51, "x", 100));
        TreemapNode stale = TreemapNode.Create(2, "stale", NodeKind.Folder, 5, 5, null, 0,
            new List<TreemapNode> { TreeBuilder.File(3, "inner", 300) });

        TreemapNode root = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0,
            new List<TreemapNode> { stale, untouched });

        TreemapNode aggregated = TreemapNode.WithAggregatedSizes(root);

        Assert.NotSame(root, aggregated);
        Assert.Equal(400, aggregated.LogicalSize);
        Assert.Equal(400, aggregated.PhysicalSize);

        Assert.Same(untouched, aggregated.Children[1]);

        TreemapNode rebuilt = aggregated.Children[0];
        Assert.NotSame(stale, rebuilt);
        Assert.Equal(300, rebuilt.LogicalSize);
        Assert.Same(stale.Children[0], rebuilt.Children[0]);
    }

    [Fact]
    public void RebuildingChildrenStillYieldsAReadOnlyList()
    {
        TreemapNode stale = TreemapNode.Create(2, "stale", NodeKind.Folder, 5, 5, null, 0,
            new List<TreemapNode> { TreeBuilder.File(3, "inner", 300) });

        TreemapNode root = TreeBuilder.Folder(1, "root", stale);
        TreemapNode aggregated = TreemapNode.WithAggregatedSizes(root);

        IList<TreemapNode> exposed = Assert.IsAssignableFrom<IList<TreemapNode>>(aggregated.Children);
        Assert.Single(exposed);
        Assert.Throws<NotSupportedException>(() => exposed.Add(TreeBuilder.File(4, "x", 1)));
    }

    [Fact]
    public void OverridesReplaceComputedTotalsOnAParent()
    {
        TreemapNode free = TreemapNode.WithAggregatedSizes(
            TreeBuilder.Folder(1, "drive", TreeBuilder.File(2, "a", 100)),
            logicalSizeOverride: 500,
            physicalSizeOverride: 900);

        Assert.Equal(500, free.LogicalSize);
        Assert.Equal(900, free.PhysicalSize);
        Assert.Equal(900, free.LayoutSize);

        // Only the totals are overridden; the children keep their own sizes.
        Assert.Equal(100, free.Children[0].LogicalSize);
    }

    [Fact]
    public void OverridesApplyToLeavesToo()
    {
        TreemapNode leaf = TreemapNode.WithAggregatedSizes(
            TreeBuilder.File(2, "a", 100),
            logicalSizeOverride: 1,
            physicalSizeOverride: 2);

        Assert.Equal(1, leaf.LogicalSize);
        Assert.Equal(2, leaf.PhysicalSize);
        Assert.Empty(leaf.Children);
    }

    [Fact]
    public void OverridingWithTheValueAlreadyHeldReturnsTheSameInstance()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));

        Assert.Same(root, TreemapNode.WithAggregatedSizes(root, logicalSizeOverride: 100, physicalSizeOverride: 100));
    }
}
