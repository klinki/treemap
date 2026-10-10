using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

public class TweenTests
{
    private static LayoutResult LayoutOf(TreemapNode root, RectF bounds)
        => SquarifyLayout.Compute(root, bounds);

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

    [Fact]
    public void FrameIsDrawOrderedDeepestLast()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(3, "sub", TreeBuilder.File(4, "deep", 50)));

        RectF bounds = new(0, 0, 400, 400);
        LayoutTween tween = new(LayoutOf(from, bounds), LayoutOf(to, bounds));

        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);

        for (int i = 1; i < frame.Count; i++)
        {
            Assert.True(
                frame[i - 1].Depth <= frame[i].Depth,
                "frame must be ordered by ascending depth for correct painting");
        }
    }
}
