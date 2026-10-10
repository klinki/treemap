using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// The animator turns elapsed time into progress and owns the frame the host
/// paints. Both are driven by an injected clock here, so every assertion is exact.
/// </summary>
public class AnimatorTests
{
    private static LayoutResult Layout(TreemapNode root)
        => SquarifyLayout.Compute(root, new RectF(0, 0, 400, 400));

    /// <summary>
    /// A root whose second child grows, so node 3's rect genuinely moves between
    /// the two endpoints and progress can be observed rather than assumed.
    /// </summary>
    private static (LayoutResult From, LayoutResult To) Endpoints()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "b", 900));

        return (Layout(from), Layout(to));
    }

    private static RectF BoundsOf(LayoutResult layout, int nodeId)
    {
        Assert.True(layout.TryGetIndex(nodeId, out int index), $"node {nodeId} is not in the layout");
        return layout.Bounds[index];
    }

    private static RectF FrameBoundsOf(IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        foreach (TweenedCell cell in frame)
        {
            if (cell.NodeId == nodeId)
            {
                return cell.Bounds;
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return RectF.Empty;
    }

    /// <summary>
    /// Progress must come from the provider's own tick units. The clock here runs at
    /// 3MHz while <see cref="TimeSpan"/> counts at 10MHz, so a duration converted
    /// incorrectly — or not converted at all — lands somewhere other than a quarter,
    /// a half and one.
    /// </summary>
    [Fact]
    public void ProgressIsMeasuredInTheProvidersOwnTicks()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        // 200ms at 3MHz is 600_000 ticks, not the 2_000_000 TimeSpan ticks of 200ms.
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0.25f, animator.Progress, precision: 4);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0.5f, animator.Progress, precision: 4);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1f, animator.Progress, precision: 4);
    }

    /// <summary>
    /// One tick of a 200ms tween is a real, non-zero fraction of the way along.
    /// Integer division would truncate it to nothing and the frame would sit at its
    /// start position until the whole duration had elapsed.
    /// </summary>
    [Fact]
    public void SubTickProgressIsNotTruncatedAway()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        RectF start = BoundsOf(from, 3);
        RectF end = BoundsOf(to, 3);
        Assert.False(start.Equals(end), "the fixture must actually move node 3");

        clock.AdvanceTicks(1);

        Assert.True(animator.Progress > 0f, "a single tick of a 200ms tween must not truncate to zero");
        Assert.True(animator.Progress < 0.01f, "and it is still very close to the start");
    }

    /// <summary>
    /// Progress drives the interpolation directly: the frame the animator publishes
    /// is the exact lerp of the two endpoints at the eased progress, with no
    /// direction assumed and no snapping to either endpoint.
    /// </summary>
    [Fact]
    public void ThePublishedFrameIsTheEasedLerpOfTheEndpoints()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        clock.AdvanceTicks(1);
        Assert.True(animator.Progress > 0f, "a single tick of a 200ms tween must not truncate to zero");

        // Halfway: 100ms is 300_000 ticks at 3MHz.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0.5f, animator.Progress, precision: 4);

        Assert.True(animator.Advance());

        RectF start = BoundsOf(from, 3);
        RectF end = BoundsOf(to, 3);
        RectF published = FrameBoundsOf(animator.CurrentFrame, 3);

        Assert.Equal(RectF.Lerp(start, end, Easing.CubicInOut(animator.Progress)), published);
        Assert.False(published.Equals(start), "the frame must have left the start layout");
        Assert.False(published.Equals(end), "and must not have jumped to the target");
    }

    /// <summary>
    /// A zero duration has nothing to interpolate, so the frame handed over at
    /// <c>Begin</c> is already the settled layout and the first pump retires it.
    /// </summary>
    [Fact]
    public void ZeroDurationSnapsToTheFinalFrameImmediately()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.Zero, clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        RectF target = BoundsOf(to, 3);
        Assert.False(BoundsOf(from, 3).Equals(target), "the fixture must actually move node 3");
        Assert.Equal(target, FrameBoundsOf(animator.CurrentFrame, 3));

        Assert.True(animator.Advance(), "the settling frame still has to be painted");
        Assert.False(animator.IsAnimating);
        Assert.False(animator.Advance(), "there is nothing left to evaluate");
    }

    /// <summary>
    /// A negative duration clamps to zero rather than running the tween backwards.
    /// The clamp is asserted through the constructor as well as the setter, because
    /// those are two separate paths into the same field and only one of them used to
    /// clamp — a duration read back as negative while every read of it behaved as
    /// zero is exactly the inconsistency this guards against.
    /// </summary>
    [Fact]
    public void NegativeDurationClampsToZero()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(-5), clock);

        Assert.Equal(TimeSpan.Zero, animator.Duration);

        // Clamped to zero, so it has the zero duration's meaning throughout: the
        // settled layout at Begin, and one reported frame to paint it.
        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        RectF target = BoundsOf(to, 3);
        Assert.False(BoundsOf(from, 3).Equals(target), "the fixture must actually move node 3");
        Assert.Equal(target, FrameBoundsOf(animator.CurrentFrame, 3));

        Assert.True(animator.Advance(), "the settling frame still has to be painted");
        Assert.False(animator.IsAnimating);
        Assert.False(animator.Advance(), "there is nothing left to evaluate");
    }

    [Fact]
    public void AdvanceReportsEveryEvaluatedFrameIncludingTheLast()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(100), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(animator.Advance());
        Assert.True(animator.IsAnimating);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(animator.Advance(), "the frame that settles the tween must be reported too");
        Assert.False(animator.IsAnimating, "IsAnimating tracks running state, not this call's result");

        Assert.False(animator.Advance(), "an idle animator has nothing left to evaluate");

        // Whatever is on screen after the last pump is the target layout.
        Assert.Equal(BoundsOf(to, 3).X, FrameBoundsOf(animator.CurrentFrame, 3).X, precision: 3);
    }

    /// <summary>
    /// The frame exists from the moment the transition starts, so the first repaint
    /// is coherent rather than a stale layout followed by a jump.
    /// </summary>
    [Fact]
    public void BeginRendersTheFirstFrameAtProgressZero()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        Assert.NotEmpty(animator.CurrentFrame);
        Assert.Equal(0f, animator.Progress, precision: 5);

        foreach (TweenedCell cell in animator.CurrentFrame)
        {
            Assert.Equal(BoundsOf(from, cell.NodeId), cell.Bounds);
            Assert.Equal(1f, cell.Alpha, precision: 5);
            Assert.False(cell.IsEntering);
        }
    }

    /// <summary>
    /// A snapped frame is the layout itself, so its parent indices have to be the
    /// layout's — the renderer walks them to outline the hover path.
    /// </summary>
    [Fact]
    public void SnapPublishesTheLayoutsParentIndices()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "inner",
                TreeBuilder.File(3, "a", 100),
                TreeBuilder.File(4, "b", 200)),
            TreeBuilder.File(5, "c", 300));

        LayoutResult layout = Layout(root);

        TreemapAnimator animator = new();
        animator.Snap(layout);

        IReadOnlyList<TweenedCell> frame = animator.CurrentFrame;
        Assert.Equal(layout.Count, frame.Count);

        for (int i = 0; i < frame.Count; i++)
        {
            Assert.Equal(layout.NodeIds[i], frame[i].NodeId);
            Assert.Equal(layout.ParentIndices[i], frame[i].ParentIndex);
            Assert.True(frame[i].ParentIndex < i, "a parent is always painted before its child");
        }

        Assert.False(animator.IsAnimating);
        Assert.Equal(1f, animator.Progress, precision: 5);
    }

    /// <summary>Cancelling abandons the transition without disturbing the frame.</summary>
    [Fact]
    public void CancelFreezesTheFrameInPlace()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock);

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.True(animator.Advance());

        float frozenX = FrameBoundsOf(animator.CurrentFrame, 3).X;

        animator.Cancel();
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.False(animator.IsAnimating);
        Assert.False(animator.Advance());
        Assert.Equal(frozenX, FrameBoundsOf(animator.CurrentFrame, 3).X);
    }

    /// <summary>A duration set after construction is honoured by the next transition.</summary>
    [Fact]
    public void DurationCanBeChangedBetweenTransitions()
    {
        TestTimeProvider clock = new();
        TreemapAnimator animator = new(TimeSpan.FromMilliseconds(200), clock)
        {
            Duration = TimeSpan.FromMilliseconds(50),
        };

        (LayoutResult from, LayoutResult to) = Endpoints();
        animator.Begin(new LayoutTween(from, to));

        clock.Advance(TimeSpan.FromMilliseconds(25));
        Assert.Equal(0.5f, animator.Progress, precision: 4);
    }
}
