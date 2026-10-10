using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

public class TweenTests
{
    private static LayoutResult LayoutOf(TreemapNode root, RectF bounds)
        => SquarifyLayout.Compute(root, bounds);

    private static RectF Bounds => new(0, 0, 400, 400);

    private static int IndexOfFrame(IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].NodeId == nodeId)
            {
                return i;
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return -1;
    }

    /// <summary>
    /// The invariant every draw path depends on: a cell's parent is painted before
    /// it, and the reported depth agrees with the hierarchy the frame carries.
    /// </summary>
    private static void AssertPaintableOrder(IReadOnlyList<TweenedCell> frame)
    {
        for (int i = 0; i < frame.Count; i++)
        {
            int parent = frame[i].ParentIndex;
            if (parent < 0)
            {
                Assert.True(frame[i].Depth == 0, "a frame root sits at depth 0");
                continue;
            }

            Assert.True(
                parent < i,
                $"node {frame[i].NodeId} is painted before its parent at {parent}");

            Assert.True(
                frame[parent].Depth < frame[i].Depth,
                "depth must follow the frame's own hierarchy, not an endpoint layout's");
        }
    }

    [Fact]
    public void AtProgressZeroFrameMatchesSource()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 300));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 900));

        RectF bounds = new(0, 0, 400, 400);
        LayoutResult fromLayout = LayoutOf(from, bounds);
        LayoutResult toLayout = LayoutOf(to, bounds);

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);

        Assert.True(fromLayout.TryGetIndex(3, out int idx));
        TweenedCell cell = frame.Single(c => c.NodeId == 3);

        Assert.Equal(fromLayout.Bounds[idx].X, cell.Bounds.X, precision: 2);
        Assert.Equal(1f, cell.Alpha, precision: 3);
    }

    [Fact]
    public void AtProgressOneFrameMatchesTarget()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 300));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 900));

        RectF bounds = new(0, 0, 400, 400);
        LayoutResult fromLayout = LayoutOf(from, bounds);
        LayoutResult toLayout = LayoutOf(to, bounds);

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];
        tween.Evaluate(1f, frame);

        Assert.True(toLayout.TryGetIndex(3, out int idx));
        TweenedCell cell = frame.Single(c => c.NodeId == 3);

        Assert.Equal(toLayout.Bounds[idx].X, cell.Bounds.X, precision: 2);
        Assert.Equal(toLayout.Bounds[idx].Width, cell.Bounds.Width, precision: 2);
    }

    [Fact]
    public void MidpointIsRoughlyHalfway()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 1100));

        RectF bounds = new(0, 0, 400, 400);
        LayoutResult fromLayout = LayoutOf(from, bounds);
        LayoutResult toLayout = LayoutOf(to, bounds);

        fromLayout.TryGetIndex(3, out int fromIdx);
        toLayout.TryGetIndex(3, out int toIdx);

        float startWidth = fromLayout.Bounds[fromIdx].Width;
        float endWidth = toLayout.Bounds[toIdx].Width;

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);

        float midWidth = frame.Single(c => c.NodeId == 3).Bounds.Width;

        // Ease-in-out at t=0.5 is exactly 0.5, so widths should straddle the mean.
        float mean = (startWidth + endWidth) / 2f;
        Assert.InRange(midWidth, MathF.Min(startWidth, endWidth) - 1f, MathF.Max(startWidth, endWidth) + 1f);
        Assert.True(MathF.Abs(midWidth - mean) < (MathF.Abs(endWidth - startWidth) * 0.15f));
    }

    [Fact]
    public void NewNodesFadeIn()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "new", 100));

        RectF bounds = new(0, 0, 400, 400);
        LayoutTween tween = new(LayoutOf(from, bounds), LayoutOf(to, bounds));

        List<TweenedCell> start = [];
        tween.Evaluate(0f, start);
        Assert.Equal(0f, start.Single(c => c.NodeId == 3).Alpha, precision: 3);

        List<TweenedCell> end = [];
        tween.Evaluate(1f, end);
        Assert.Equal(1f, end.Single(c => c.NodeId == 3).Alpha, precision: 3);
    }

    [Fact]
    public void RemovedNodesFadeOut()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "gone", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));

        RectF bounds = new(0, 0, 400, 400);
        LayoutTween tween = new(LayoutOf(from, bounds), LayoutOf(to, bounds));

        Assert.Equal(1, tween.RemovedCount);

        List<TweenedCell> end = [];
        tween.Evaluate(1f, end);
        Assert.Equal(0f, end.Single(c => c.NodeId == 3).Alpha, precision: 3);
    }

    [Fact]
    public void EasingIsMonotonicAndBounded()
    {
        float previous = -1f;
        for (int i = 0; i <= 100; i++)
        {
            float t = i / 100f;
            float v = Easing.CubicInOut(t);

            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= previous - 1e-4f, "easing must be monotonic");
            previous = v;
        }

        Assert.Equal(0f, Easing.CubicInOut(0f), precision: 5);
        Assert.Equal(1f, Easing.CubicInOut(1f), precision: 5);
    }

    [Fact]
    public void EasingDoesNotOvershoot()
    {
        // Overshoot would make rectangles visibly overlap mid-transition.
        for (int i = 0; i <= 200; i++)
        {
            float t = i / 200f;
            Assert.InRange(Easing.CubicInOut(t), 0f, 1f);
        }
    }

    /// <summary>
    /// Draw order is parent-before-child, decided once when the tween is built.
    /// Sorting by depth is not a substitute: it is only equal to the real hierarchy
    /// by coincidence, and it is not stable under reparenting.
    /// </summary>
    [Fact]
    public void FramePaintsEveryParentBeforeItsChild()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 100),
            TreeBuilder.Folder(4, "old", TreeBuilder.File(5, "deep", 50)));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(4, "old",
                TreeBuilder.File(5, "deep", 50),
                TreeBuilder.File(6, "added", 700)),
            TreeBuilder.Folder(7, "wrap", TreeBuilder.File(8, "moved", 300)));

        LayoutTween tween = new(LayoutOf(from, Bounds), LayoutOf(to, Bounds));
        List<TweenedCell> frame = [];

        foreach (float step in new[] { 0f, 0.15f, 0.5f, 0.85f, 1f })
        {
            tween.Evaluate(step, frame);
            AssertPaintableOrder(frame);
        }
    }

    /// <summary>
    /// An entering parent has a lower alpha than the retained child it now contains,
    /// so an alpha-first sort painted the child first and the parent over it — the
    /// child vanished inside its own folder for the length of the transition.
    /// </summary>
    [Fact]
    public void EnteringParentIsDrawnBeforeTheRetainedChildItNowContains()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "child", 400));
        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(3, "wrap", TreeBuilder.File(2, "child", 400)));

        LayoutResult toLayout = LayoutOf(to, Bounds);
        LayoutTween tween = new(LayoutOf(from, Bounds), toLayout);

        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);
        AssertPaintableOrder(frame);

        int parent = IndexOfFrame(frame, 3);
        int child = IndexOfFrame(frame, 2);

        Assert.True(frame[parent].Alpha < frame[child].Alpha, "the entering parent is still fading in");
        Assert.True(parent < child, "an entering parent must be painted before the retained child");
        Assert.Equal(parent, frame[child].ParentIndex);

        // Entering geometry comes from the target only; borrowing anything from the
        // source (cell 0 in particular) would put the folder somewhere else entirely.
        Assert.True(toLayout.TryGetIndex(3, out int parentIndex));
        Assert.Equal(toLayout.Bounds[parentIndex], frame[parent].Bounds);
    }

    /// <summary>The mirror image: a fading-in child must still sit above its parent.</summary>
    [Fact]
    public void EnteringChildIsDrawnAfterItsRetainedParent()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "folder", TreeBuilder.File(3, "a", 100)));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "folder",
                TreeBuilder.File(3, "a", 100),
                TreeBuilder.File(4, "b", 900)));

        LayoutTween tween = new(LayoutOf(from, Bounds), LayoutOf(to, Bounds));

        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);
        AssertPaintableOrder(frame);

        int parent = IndexOfFrame(frame, 2);
        int enteringChild = IndexOfFrame(frame, 4);

        Assert.True(frame[enteringChild].Alpha < frame[parent].Alpha, "the new child is still fading in");
        Assert.True(parent < enteringChild, "sorting the entering cell first would hide it inside its parent");
        Assert.Equal(parent, frame[enteringChild].ParentIndex);
    }

    /// <summary>
    /// A departing cell keeps the parent it had, even when that parent survives and
    /// moves deeper in the target. Its depth follows the frame hierarchy too, so
    /// the two disagree with the source layout by design.
    /// </summary>
    [Fact]
    public void DepartingChildKeepsItsParentWhenThatParentMovesDeeper()
    {
        TreemapNode fromTree = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "mid", TreeBuilder.File(3, "leaving", 300)));

        // mid survives but gains a level, and loses its only child. It carries an
        // explicit size because a childless folder would otherwise lay out as
        // zero area and be culled.
        TreemapNode toTree = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(4, "wrapper",
                TreemapNode.Create(2, "mid", NodeKind.Folder, 300, 300)));

        LayoutResult fromLayout = LayoutOf(fromTree, Bounds);
        LayoutResult toLayout = LayoutOf(toTree, Bounds);

        // The source says the departing cell is one level down; the frame nests it
        // under a parent that has itself gained a level.
        Assert.True(fromLayout.TryGetIndex(3, out int leavingIndex));
        Assert.Equal(2, fromLayout.Depths[leavingIndex]);
        Assert.True(toLayout.TryGetIndex(2, out int midIndex));
        Assert.Equal(2, toLayout.Depths[midIndex]);

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);
        AssertPaintableOrder(frame);

        int mid = IndexOfFrame(frame, 2);
        int leaving = IndexOfFrame(frame, 3);

        Assert.True(
            frame[leaving].ParentIndex == mid,
            "the departing cell keeps its source parent");
        Assert.True(mid < leaving, "and that parent is still painted first");
        Assert.Equal(3, frame[leaving].Depth);
        Assert.Equal(1f - Easing.CubicInOut(0.5f), frame[leaving].Alpha, precision: 4);
    }

    /// <summary>
    /// The mirror of the entering-parent case. A departing folder is still fully
    /// opaque at progress 0 and sits exactly over the leaf it used to contain, so
    /// painting it after that leaf would hide the leaf for the whole transition.
    /// </summary>
    [Fact]
    public void DepartingParentIsDrawnBeforeTheRetainedChildItHeld()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "wrap", TreeBuilder.File(3, "leaf", 400)));

        TreemapNode to = TreeBuilder.Folder(1, "root", TreeBuilder.File(3, "leaf", 400));

        LayoutTween tween = new(LayoutOf(from, Bounds), LayoutOf(to, Bounds));
        Assert.Equal(1, tween.RemovedCount);

        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);
        AssertPaintableOrder(frame);

        int departing = IndexOfFrame(frame, 2);
        int retained = IndexOfFrame(frame, 3);

        // The overlap is what makes the ordering matter, not a formality.
        Assert.True(frame[departing].Bounds.Contains(
            frame[retained].Bounds.X + 1f,
            frame[retained].Bounds.Y + 1f));

        Assert.Equal(1f, frame[departing].Alpha, precision: 5);
        Assert.Equal(1f, frame[retained].Alpha, precision: 5);
        Assert.True(departing < retained, "a departing parent must not be painted over the leaf it held");

        // The leaf keeps the relationship the target gives it; the departing folder
        // keeps the one the source gave it. Both are children of the root here.
        Assert.Equal(IndexOfFrame(frame, 1), frame[retained].ParentIndex);
        Assert.Equal(IndexOfFrame(frame, 1), frame[departing].ParentIndex);
        Assert.Equal(1, frame[retained].Depth);
        Assert.Equal(1, frame[departing].Depth);
    }

    /// <summary>
    /// Draw order is decided once, so it cannot drift as cells fade in and out.
    /// </summary>
    [Fact]
    public void DrawOrderIsStableAcrossProgress()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "leaving", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(4, "wrap", TreeBuilder.File(5, "entering", 500)));

        LayoutTween tween = new(LayoutOf(from, Bounds), LayoutOf(to, Bounds));
        List<TweenedCell> frame = [];

        int[]? first = null;
        foreach (float step in new[] { 0f, 0.1f, 0.5f, 0.9f, 1f })
        {
            tween.Evaluate(step, frame);

            int[] order = [.. frame.Select(c => c.NodeId)];
            first ??= order;
            Assert.True(first.SequenceEqual(order), "draw order must not drift as cells fade");
        }
    }

    /// <summary>
    /// A tween between a layout and itself has nothing to decide, so it has to
    /// reproduce that layout's own cell and parent order exactly.
    /// </summary>
    [Fact]
    public void SettledTweenKeepsTheTargetLayoutsOrderAndParents()
    {
        TreemapNode tree = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "folder",
                TreeBuilder.File(3, "a", 100),
                TreeBuilder.File(4, "b", 200)),
            TreeBuilder.File(5, "c", 300),
            TreeBuilder.Space(6, "free", NodeKind.FreeSpace, 400));

        LayoutResult layout = LayoutOf(tree, Bounds);
        LayoutTween tween = new(layout, layout);

        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);

        Assert.Equal(layout.Count, frame.Count);
        for (int i = 0; i < layout.Count; i++)
        {
            Assert.Equal(layout.NodeIds[i], frame[i].NodeId);
            Assert.Equal(layout.ParentIndices[i], frame[i].ParentIndex);
            Assert.Equal(layout.Bounds[i], frame[i].Bounds);
            Assert.False(frame[i].IsEntering);
        }
    }

    /// <summary>
    /// Every cell the target owns reads its geometry and metadata from the target,
    /// and a cell missing from the source reads nothing from it at all. Indexing
    /// the source for a cell it does not contain yields the source's first cell,
    /// which is the root's rectangle — a very visible bug at fade-in time.
    /// </summary>
    [Fact]
    public void EnteringCellsDoNotBorrowGeometryFromTheSource()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 900));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(4, "brand new", 800),
            TreeBuilder.Folder(5, "folder", TreeBuilder.File(6, "inner", 50)));

        LayoutResult fromLayout = LayoutOf(from, Bounds);
        LayoutResult toLayout = LayoutOf(to, Bounds);

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);

        foreach (TweenedCell cell in frame.Where(c => c.IsEntering))
        {
            Assert.True(toLayout.TryGetIndex(cell.NodeId, out int index));

            Assert.Equal(toLayout.Bounds[index], cell.Bounds);
            Assert.Equal(toLayout.Kinds[index], cell.Kind);
            Assert.Equal(toLayout.Depths[index], cell.Depth);
        }

        Assert.Equal(3, frame.Count(c => c.IsEntering));
    }

    /// <summary>
    /// An empty source has no cells to read, so a tween out of one is a pure
    /// fade-in and must not index the source at all.
    /// </summary>
    [Fact]
    public void TweenFromAnEmptyLayoutIsAFullFadeIn()
    {
        TreemapNode toTree = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(3, "folder", TreeBuilder.File(4, "inner", 50)));

        LayoutResult toLayout = LayoutOf(toTree, Bounds);
        LayoutTween tween = new(LayoutResult.Empty, toLayout);

        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);

        Assert.Equal(toLayout.Count, frame.Count);
        AssertPaintableOrder(frame);
        Assert.All(frame, cell => Assert.True(cell.IsEntering));

        for (int i = 0; i < frame.Count; i++)
        {
            Assert.Equal(toLayout.NodeIds[i], frame[i].NodeId);
            Assert.Equal(toLayout.Bounds[i], frame[i].Bounds);
            Assert.Equal(toLayout.ParentIndices[i], frame[i].ParentIndex);
            Assert.Equal(0.5f, frame[i].Alpha, precision: 4);
        }
    }

    /// <summary>
    /// The per-frame path must stay a flat pass: no list growth, no sorting, no
    /// per-frame allocation. Ordering is resolved by the constructor, so the
    /// MemoryDiagnoser in the benchmark suite should report nothing here.
    /// </summary>
    [Fact]
    public void EvaluateDoesNotAllocateOnceWarm()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "leaving", 100),
            TreeBuilder.Folder(4, "old", TreeBuilder.File(5, "deep", 60)));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(4, "old",
                TreeBuilder.File(5, "deep", 60),
                TreeBuilder.File(6, "added", 800)),
            TreeBuilder.Space(7, "free", NodeKind.FreeSpace, 300));

        LayoutTween tween = new(LayoutOf(from, Bounds), LayoutOf(to, Bounds));

        // Sized up front so the measured loop cannot grow the list, and warmed so
        // the measurement covers steady-state per-frame work rather than first-call
        // costs that are already paid by Begin.
        List<TweenedCell> frame = new(32);
        for (int i = 0; i < 64; i++)
        {
            tween.Evaluate((i % 17) / 17f, frame);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
        {
            tween.Evaluate((i % 17) / 17f, frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
