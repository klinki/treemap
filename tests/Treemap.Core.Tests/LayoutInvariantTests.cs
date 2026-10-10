using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Layout invariants. These are the properties that must hold for any treemap
/// to be correct, independent of the specific squarify heuristic.
/// </summary>
public class LayoutInvariantTests
{
    /// <summary>Every rendered cell must fall inside its parent's rect.</summary>
    [Fact]
    public void ChildrenAreContainedWithinParent()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.Folder(2, "a", TreeBuilder.Children(
                TreeBuilder.File(3, "a1.bin", 5000),
                TreeBuilder.File(4, "a2.bin", 3000))),
            TreeBuilder.Folder(5, "b", TreeBuilder.Children(
                TreeBuilder.File(6, "b1.bin", 8000),
                TreeBuilder.File(7, "b2.bin", 2000)))));

        RectF bounds = new(0, 0, 1000, 800);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds);

        for (int i = 0; i < layout.Count; i++)
        {
            if (layout.Depths[i] != 1)
            {
                continue;
            }

            RectF child = layout.Bounds[i];
            Assert.True(child.Left >= bounds.Left - 0.01f, "child escapes left edge");
            Assert.True(child.Top >= bounds.Top - 0.01f, "child escapes top edge");
            Assert.True(child.Right <= bounds.Right + 0.01f, "child escapes right edge");
            Assert.True(child.Bottom <= bounds.Bottom + 0.01f, "child escapes bottom edge");
        }
    }

    /// <summary>Siblings must not overlap, and together must tile the parent rect.</summary>
    [Fact]
    public void SiblingsTileParentWithoutOverlap()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.File(2, "a", 4000),
            TreeBuilder.File(3, "b", 3000),
            TreeBuilder.File(4, "c", 2000),
            TreeBuilder.File(5, "d", 1000)));

        RectF bounds = new(0, 0, 600, 600);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds);

        List<int> siblings = [];
        for (int i = 0; i < layout.Count; i++)
        {
            if (layout.Depths[i] == 1)
            {
                siblings.Add(i);
            }
        }

        Assert.Equal(4, siblings.Count);

        // No two siblings may overlap.
        for (int a = 0; a < siblings.Count; a++)
        {
            for (int b = a + 1; b < siblings.Count; b++)
            {
                RectF ra = layout.Bounds[siblings[a]];
                RectF rb = layout.Bounds[siblings[b]];
                Assert.False(
                    ra.Intersects(rb),
                    $"cells {layout.NodeIds[siblings[a]]} and {layout.NodeIds[siblings[b]]} overlap");
            }
        }

        // Combined area equals parent area: sizes map to pixel area proportionally.
        float total = 0f;
        foreach (int i in siblings)
        {
            total += layout.Bounds[i].Area;
        }

        Assert.Equal(bounds.Area, total, precision: 1);
    }

    /// <summary>Cell area must be proportional to the node's size.</summary>
    [Fact]
    public void CellAreaIsProportionalToSize()
    {
        const long bigSize = 8_000_000;
        const long smallSize = 2_000_000;

        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.File(2, "big", bigSize),
            TreeBuilder.File(3, "small", smallSize)));

        RectF bounds = new(0, 0, 1000, 1000);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds);

        Assert.True(layout.TryGetIndex(2, out int bigIndex));
        Assert.True(layout.TryGetIndex(3, out int smallIndex));

        float expectedRatio = (float)bigSize / smallSize;
        float actualRatio = layout.Bounds[bigIndex].Area / layout.Bounds[smallIndex].Area;

        Assert.Equal(expectedRatio, actualRatio, precision: 2);
    }

    /// <summary>Squarification should beat naive slice-and-dice on aspect ratio.</summary>
    [Fact]
    public void SquarifyKeepsCellsCloserToSquareThanSliceAndDice()
    {
        // Descending geometric sizes: the pathological case for slicing.
        TreemapNode[] files = new TreemapNode[12];
        long size = 100_000_000;
        for (int i = 0; i < files.Length; i++)
        {
            files[i] = TreeBuilder.File(i + 2, $"f{i}", size);
            size /= 2;
        }

        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(files));

        RectF bounds = new(0, 0, 1200, 900);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds);

        double worstAspect = 0;
        int counted = 0;

        for (int i = 0; i < layout.Count; i++)
        {
            if (layout.Depths[i] != 1)
            {
                continue;
            }

            RectF r = layout.Bounds[i];
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }

            double aspect = Math.Max(r.Width / r.Height, r.Height / r.Width);
            worstAspect = Math.Max(worstAspect, aspect);
            counted++;
        }

        Assert.Equal(files.Length, counted);

        // The whole point of the heuristic: no cell should be grotesquely elongated.
        Assert.True(
            worstAspect < 6.0,
            $"worst aspect ratio was {worstAspect:0.00}; squarify should keep cells near square");
    }

    /// <summary>A cell smaller than the threshold culls its whole subtree.</summary>
    [Fact]
    public void SubtreeIsCulledBelowMinimumSize()
    {
        // Ratio is extreme enough that the tiny branch collapses below the pixel
        // threshold on a 200x200 canvas (its cell ends up ~0.004 px wide).
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.File(2, "visible", 1_000_000_000),
            TreeBuilder.Folder(3, "tiny", TreeBuilder.Children(
                TreeBuilder.File(4, "invisible", 1)))));

        RectF bounds = new(0, 0, 200, 200);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds, new LayoutOptions
        {
            MinCellSize = 8f,
        });

        Assert.True(layout.TryGetIndex(2, out _), "large sibling must render");
        Assert.False(layout.TryGetIndex(4, out _), "descendant of a culled cell must not render");
    }

    /// <summary>
    /// Regression: folder nodes created without explicit sizes must still lay out.
    /// A prior version required callers to pre-aggregate subtree totals, which made
    /// an un-aggregated tree render as a single empty cell.
    /// </summary>
    [Fact]
    public void FoldersAggregateChildSizesAutomatically()
    {
        TreemapNode leaf = TreeBuilder.File(2, "only.bin", 4096);
        TreemapNode folder = TreeBuilder.Folder(1, "folder", leaf);

        Assert.Equal(4096, folder.LayoutSize);

        LayoutResult layout = SquarifyLayout.Compute(folder, new RectF(0, 0, 100, 100));

        Assert.Equal(2, layout.Count);
        Assert.True(layout.TryGetIndex(2, out _), "child of an un-sized folder must render");
    }

    [Fact]
    public void EmptyTreeProducesEmptyLayout()
    {
        TreemapNode root = TreeBuilder.File(1, "solo", 100);
        LayoutResult layout = SquarifyLayout.Compute(root, RectF.Empty);

        Assert.True(layout.IsEmpty);
        Assert.Equal(-1, layout.RootCell);
    }

    [Fact]
    public void ZeroSizedChildrenDoNotProduceNegativeRects()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.File(2, "real", 1000),
            TreeBuilder.File(3, "empty", 0),
            TreeBuilder.File(4, "alsoEmpty", 0)));

        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 500, 500));

        for (int i = 0; i < layout.Count; i++)
        {
            RectF r = layout.Bounds[i];
            Assert.True(r.Width >= 0f && r.Height >= 0f, "rect must not have negative extent");
        }
    }

    [Fact]
    public void DeepTreeRespectsMaxDepth()
    {
        TreemapNode node = TreeBuilder.File(1000, "leaf", 1);
        for (int depth = 20; depth >= 0; depth--)
        {
            node = TreeBuilder.Folder(depth, $"d{depth}", TreeBuilder.Children(node));
        }

        LayoutResult layout = SquarifyLayout.Compute(node, new RectF(0, 0, 800, 800), new LayoutOptions
        {
            MaxDepth = 5,
            MinCellSize = 0.01f,
        });

        int maxSeen = 0;
        for (int i = 0; i < layout.Count; i++)
        {
            maxSeen = Math.Max(maxSeen, layout.Depths[i]);
        }

        Assert.True(maxSeen <= 5, $"depth {maxSeen} exceeded MaxDepth 5");
    }

    [Fact]
    public void AllCellsFallInsideRootBounds()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root", TreeBuilder.Children(
            TreeBuilder.Folder(2, "src", TreeBuilder.Children(
                TreeBuilder.File(3, "Program.cs", 12000),
                TreeBuilder.File(4, "Engine.cs", 34000))),
            TreeBuilder.Folder(5, "assets", TreeBuilder.Children(
                TreeBuilder.File(6, "logo.png", 88000),
                TreeBuilder.File(7, "font.ttf", 45000)))));

        RectF bounds = new(17, 23, 1300, 777);
        LayoutResult layout = SquarifyLayout.Compute(root, bounds);

        for (int i = 0; i < layout.Count; i++)
        {
            RectF r = layout.Bounds[i];
            Assert.True(r.Left >= bounds.Left - 0.5f, "escaped left");
            Assert.True(r.Top >= bounds.Top - 0.5f, "escaped top");
            Assert.True(r.Right <= bounds.Right + 0.5f, "escaped right");
            Assert.True(r.Bottom <= bounds.Bottom + 0.5f, "escaped bottom");
        }
    }
}
