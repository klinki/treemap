using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// The controller is the framework-agnostic interaction layer, so all of it is
/// testable without a window. These tests exercise hover, zoom, pan and navigation.
/// </summary>
public class ControllerTests
{
    private static SnapshotPublisher BuildPublisher(out Dictionary<int, string> names)
    {
        names = new Dictionary<int, string>();

        TreemapNode leafA = Named(names, 1, "small.bin", NodeKind.File, 100, 0);
        TreemapNode leafB = Named(names, 2, "big.bin", NodeKind.File, 9_000, 1);
        TreemapNode inner = TreemapNode.Create(3, "inner", NodeKind.Folder, 0, 0, null, 0, [leafA, leafB]);

        TreemapNode other = Named(names, 4, "notes.txt", NodeKind.File, 500, 2);
        TreemapNode root = TreemapNode.Create(9, "root", NodeKind.Drive, 0, 0, null, 0, [inner, other]);

        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        return publisher;
    }

    private static TreemapNode Named(
        Dictionary<int, string> names, int id, string name, NodeKind kind, long size, int group)
    {
        names[id] = name;
        return TreemapNode.Create(id, name, kind, size, size, null, group);
    }

    private static TreemapController NewController(out SnapshotPublisher publisher)
    {
        publisher = BuildPublisher(out _);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);
        return controller;
    }

    [Fact]
    public void HoverTracksPointerOverContent()
    {
        TreemapController controller = NewController(out _);

        // Sweep the viewport; the hovered id must eventually become a real node.
        bool sawNode = false;
        for (float x = 10; x < 790 && !sawNode; x += 10)
        {
            for (float y = 10; y < 590 && !sawNode; y += 10)
            {
                controller.PointerMoved(x, y);
                if (controller.HoveredNodeId > 0)
                {
                    sawNode = true;
                }
            }
        }

        Assert.True(sawNode, "sweeping the viewport should hover some node");
    }

    [Fact]
    public void HoverChangeFiresEvent()
    {
        TreemapController controller = NewController(out _);
        controller.ConsumeChanges();

        ChangeKind? seen = null;
        controller.Changed += kind => seen = kind;

        controller.PointerMoved(400, 300);

        Assert.True(seen.HasValue, "Changed should fire on pointer movement");
        Assert.True(seen!.Value.HasFlag(ChangeKind.Hover));
    }

    [Fact]
    public void WheelZoomChangesCamera()
    {
        TreemapController controller = NewController(out _);
        controller.ConsumeChanges();

        float before = controller.Camera.Zoom;
        controller.WheelZoom(1, 400, 300);

        // Content uses reference height 1000 with aspect-matched width, so zoom
        // starts near the "fitted" value and scrolling up genuinely zooms in.
        Assert.True(controller.Camera.Zoom > before, "scrolling up should zoom in");

        ChangeKind changes = controller.ConsumeChanges();
        Assert.True(changes.HasFlag(ChangeKind.Camera));
    }

    [Fact]
    public void DragPansTheCamera()
    {
        TreemapController controller = NewController(out _);
        controller.ConsumeChanges();

        // The camera is refitted (centred) on SetViewport, so pan is an absolute
        // transform rather than a delta. Assert the relative movement instead.
        PointF before = controller.Camera.Pan;
        controller.DragBy(30f, 20f);
        PointF after = controller.Camera.Pan;

        Assert.Equal(before.X + 30f, after.X, precision: 2);
        Assert.Equal(before.Y + 20f, after.Y, precision: 2);

        ChangeKind changes = controller.ConsumeChanges();
        Assert.True(changes.HasFlag(ChangeKind.Camera));
    }

    [Fact]
    public void ClickSelectsNode()
    {
        // Click a real leaf cell so selection lands on content rather than a
        // space block (which hit-testing skips) or a blank margin. A folder
        // bounds center hits its deepest visible child, so folders are not
        // clickable by bounds center without headers.
        TreemapController controller = NewController(out _);
        controller.ConsumeChanges();

        PointF at = ScreenCenterOf(controller, 2);
        controller.Click(at.X, at.Y);

        ChangeKind changes = controller.ConsumeChanges();
        Assert.True(changes.HasFlag(ChangeKind.Selection));
        Assert.Equal(2, controller.SelectedNodeId);
    }

    private static PointF ScreenCenterOf(TreemapController controller, int nodeId)
    {
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == nodeId)
            {
                PointF centre = new(
                    cell.Bounds.X + cell.Bounds.Width / 2,
                    cell.Bounds.Y + cell.Bounds.Height / 2);
                return controller.Camera.ContentToScreen(centre);
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return default;
    }

    [Fact]
    public void ZoomIntoHoveredRequiresAFolder()
    {
        TreemapController controller = NewController(out _);

        // Hover a leaf file: it has no children, so drilling in must fail.
        bool hoveredFolder = false;
        for (float x = 5; x < 795 && !hoveredFolder; x += 5)
        {
            for (float y = 5; y < 595 && !hoveredFolder; y += 5)
            {
                controller.PointerMoved(x, y);
                if (controller.HoveredNodeId == 3)
                {
                    hoveredFolder = true;
                    break;
                }
            }
        }

        if (hoveredFolder)
        {
            Assert.True(controller.ZoomIntoHovered(), "folder with children should be enterable");
            Assert.True(controller.ConsumeChanges().HasFlag(ChangeKind.Navigation));
        }
    }

    [Fact]
    public void GoToRootResetsCamera()
    {
        TreemapController controller = NewController(out _);
        controller.WheelZoom(3, 400, 300);
        controller.DragBy(50, 50);

        controller.GoToRoot();

        Assert.True(controller.ConsumeChanges().HasFlag(ChangeKind.Navigation));

        // After fitting, pan should be a recentring value, not the dragged one.
        Assert.NotEqual(50f, controller.Camera.Pan.X, precision: 1);
    }

    [Fact]
    public void RelayoutAnimatesBetweenTrees()
    {
        SnapshotPublisher publisher = BuildPublisher(out _);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        Assert.False(controller.IsAnimating, "first layout should snap");

        // Publish a changed tree and relayout with animation.
        TreemapNode changed = TreemapNode.Create(9, "root", NodeKind.Drive, 0, 0, null, 0,
            [
                TreemapNode.Create(1, "small.bin", NodeKind.File, 100, 100, null, 0),
                TreemapNode.Create(2, "big.bin", NodeKind.File, 99_000, 99_000, null, 1),
                TreemapNode.Create(4, "notes.txt", NodeKind.File, 500, 500, null, 2),
            ]);

        publisher.Publish(changed);
        controller.Relayout(animate: true);

        Assert.True(controller.IsAnimating, "relayout with animate=true should start a tween");

        // Pump until settled.
        for (int i = 0; i < 500 && controller.Advance(); i++)
        {
            Thread.Sleep(1);
        }

        Assert.False(controller.IsAnimating);
    }

    [Fact]
    public void ConsumeChangesClearsState()
    {
        TreemapController controller = NewController(out _);

        ChangeKind first = controller.ConsumeChanges();
        Assert.NotEqual(ChangeKind.None, first);

        ChangeKind second = controller.ConsumeChanges();
        Assert.Equal(ChangeKind.None, second);
    }

    [Fact]
    public void FittedZoomStaysNearUnity()
    {
        // Regression guard: the content rect uses reference height 1000 with
        // aspect-matched width, so a fitted camera sits near zoom 1.0. An earlier
        // 1x1 content rect scaled the zoom to hundreds and pinned it against MaxZoom.
        TreemapController controller = NewController(out _);

        Assert.InRange(controller.Camera.Zoom, 0.2f, 2.0f);
    }

    [Fact]
    public void ResizeRefitsCamera()
    {
        TreemapController controller = NewController(out _);
        controller.WheelZoom(2, 400, 300);
        controller.ConsumeChanges();

        controller.SetViewport(new RectF(0, 0, 1600, 1200));

        Assert.True(
            controller.ConsumeChanges().HasFlag(ChangeKind.Camera),
            "a viewport change should notify the host");
    }

    private static bool HoverNode(TreemapController controller, int nodeId)
    {
        for (float x = 5; x < 795; x += 5)
        {
            for (float y = 5; y < 595; y += 5)
            {
                controller.PointerMoved(x, y);
                if (controller.HoveredNodeId == nodeId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void ZoomIntoFolderLaysOutThatFoldersContents()
    {
        TreemapController controller = NewController(out _);

        if (!HoverNode(controller, 3))
        {
            // Layout shifted; the state is unreachable, so nothing to assert.
            return;
        }

        Assert.True(controller.ZoomIntoHovered());
        controller.Relayout(animate: false);

        // The focused folder becomes the layout root, so its own id is present and
        // the drive root is not.
        Assert.Contains(controller.CurrentFrame, c => c.NodeId == 3);
        Assert.DoesNotContain(controller.CurrentFrame, c => c.NodeId == 9);
    }

    [Fact]
    public void GoToParentRestoresTheBroaderView()
    {
        TreemapController controller = NewController(out _);

        if (!HoverNode(controller, 3))
        {
            return;
        }

        controller.ZoomIntoHovered();
        controller.Relayout(animate: false);
        controller.GoToParent();
        controller.Relayout(animate: false);

        Assert.Contains(controller.CurrentFrame, c => c.NodeId == 9);
    }

    [Fact]
    public void CurrentFrameIsNonEmptyAfterRelayout()
    {
        TreemapController controller = NewController(out _);

        Assert.NotEmpty(controller.CurrentFrame);
    }

    // ---- frame-relative input ----

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

    private static RectF LayoutBoundsOf(LayoutResult layout, int nodeId)
    {
        Assert.True(layout.TryGetIndex(nodeId, out int index), $"node {nodeId} is not in the layout");
        return layout.Bounds[index];
    }

    /// <summary>A point inside <paramref name="inside"/> but outside <paramref name="outside"/>.</summary>
    private static (float X, float Y) PointInButNotIn(RectF inside, RectF outside)
    {
        for (float y = inside.Top + 1; y < inside.Bottom; y += 1)
        {
            for (float x = inside.Left + 1; x < inside.Right; x += 1)
            {
                if (!outside.Contains(x, y))
                {
                    return (x, y);
                }
            }
        }

        Assert.Fail("the two rectangles coincide, so the fixture never moves the cell");
        return default;
    }

    /// <summary>
    /// Pointer and click must resolve against the frame that is on screen. Testing
    /// the target layout instead makes hover land on a cell the user cannot see yet
    /// — and miss entirely where the frame is showing something else — so both
    /// hover and selection follow the animation.
    /// </summary>
    [Fact]
    public void PointerAndClickFollowTheAnimatedFrameNotTheTargetLayout()
    {
        TestTimeProvider clock = new();
        SnapshotPublisher publisher = BuildPublisher(out _);

        TreemapController controller = new(publisher, timeProvider: clock);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        // A new sibling inside the existing folder pushes node 2 into a different
        // rectangle and introduces a cell that does not exist on screen yet.
        TreemapNode changed = TreemapNode.Create(9, "root", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "inner", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(1, "small.bin", NodeKind.File, 100, 100, null, 0),
                TreemapNode.Create(2, "big.bin", NodeKind.File, 9_000, 9_000, null, 1),
                TreemapNode.Create(5, "extra.bin", NodeKind.File, 8_000, 8_000, null, 2),
            ]),
            TreemapNode.Create(4, "notes.txt", NodeKind.File, 500, 500, null, 2),
        ]);

        publisher.Publish(changed);
        controller.Relayout(animate: true);

        // The controller lays out into its aspect-aware content rect, so the
        // target can be reproduced exactly here.
        LayoutResult target = SquarifyLayout.Compute(
            changed, TreemapController.ContentBoundsFor(new RectF(0, 0, 800, 600)));

        RectF onScreen = FrameBoundsOf(controller.CurrentFrame, 2);
        RectF targetRect = LayoutBoundsOf(target, 2);
        Assert.False(onScreen.Equals(targetRect), "the fixture must actually move node 2");

        (float x, float y) = PointInButNotIn(onScreen, targetRect);

        // The old behaviour: the target layout does not have node 2 here at all.
        Assert.NotEqual(2, HitTester.HitTestContent(target, x, y));

        // The new cell is present in the frame but not yet visible, so it must not
        // steal the pointer either.
        Assert.Equal(0f, controller.CurrentFrame.Single(c => c.NodeId == 5).Alpha);
        Assert.NotEqual(5, HitTester.HitTestFrameContent(controller.CurrentFrame, x, y));

        PointF screen = controller.Camera.ContentToScreen(new PointF(x, y));

        controller.PointerMoved(screen.X, screen.Y);
        Assert.Equal(2, controller.HoveredNodeId);

        controller.Click(screen.X, screen.Y);
        Assert.Equal(2, controller.SelectedNodeId);
        Assert.True(controller.ConsumeChanges().HasFlag(ChangeKind.Selection));
    }

    /// <summary>
    /// Once the transition settles, hover follows the cell to the rectangle it
    /// actually ended up in. The settling frame has to be pumped, which is what
    /// makes <c>Advance</c> report the final evaluation.
    /// </summary>
    [Fact]
    public void HoverFollowsTheFrameOnceTheTransitionSettles()
    {
        TestTimeProvider clock = new();
        SnapshotPublisher publisher = BuildPublisher(out _);

        TreemapController controller = new(publisher, timeProvider: clock);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        publisher.Publish(TreemapNode.Create(9, "root", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "inner", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(1, "small.bin", NodeKind.File, 100, 100, null, 0),
                TreemapNode.Create(2, "big.bin", NodeKind.File, 9_000, 9_000, null, 1),
            ]),
            TreemapNode.Create(4, "notes.txt", NodeKind.File, 50_000, 50_000, null, 2),
        ]));

        controller.Relayout(animate: true);
        Assert.True(controller.IsAnimating);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(controller.Advance(), "the settling frame must be reported so it gets painted");
        Assert.False(controller.IsAnimating);

        RectF settled = FrameBoundsOf(controller.CurrentFrame, 2);
        PointF inside = controller.Camera.ContentToScreen(new PointF(settled.X + 1f, settled.Y + 1f));

        controller.PointerMoved(inside.X, inside.Y);
        Assert.Equal(2, controller.HoveredNodeId);
    }
}

/// <summary>
/// Hit-testing has to answer for the frame on screen, so it inherits the frame's
/// own draw order, parent links and opacity rather than a settled layout's.
/// </summary>
public class FrameHitTestTests
{
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
    /// Builds one cell of a frame whose rectangles, alphas and draw order are declared
    /// outright, so the expected answers follow from the fixture rather than from the
    /// code under test.
    /// </summary>
    private static TweenedCell Cell(int nodeId, RectF bounds, float alpha, NodeKind kind, int parentIndex)
        => new()
        {
            NodeId = nodeId,
            Bounds = bounds,
            Alpha = alpha,
            Depth = parentIndex < 0 ? 0 : 1,
            Kind = kind,
            ColorGroup = 0,
            AreaFraction = 0.25f,
            HasChildren = false,
            IsEntering = false,
            ParentIndex = parentIndex,
        };

    /// <summary>
    /// A cell that has left the layout fades out where it was, so for part of the
    /// transition the pointer is still over its old rectangle. What the frame answers
    /// with there follows from the declared draw order rather than from anything about
    /// the fade: the departing cell is painted before the retained cell that grows over
    /// it, so the retained cell — opaque and painted later — is what the overlap
    /// resolves to, and the departing cell is the answer only where nothing covers it.
    /// </summary>
    [Fact]
    public void CellsThatHaveFadedOutAreNotHit()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.File(3, "leaving", 400));

        TreemapNode to = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));

        LayoutTween tween = new(
            SquarifyLayout.Compute(from, Bounds),
            SquarifyLayout.Compute(to, Bounds));

        List<TweenedCell> frame = [];

        tween.Evaluate(0.5f, frame);

        TweenedCell gone = frame.Single(c => c.NodeId == 3);
        TweenedCell retained = frame.Single(c => c.NodeId == 2);

        // Declared draw order, asserted because both answers below rest on it: a
        // departing cell is painted before the retained cell that grows over it, which
        // is what stops it hiding that cell for the whole transition.
        Assert.True(
            IndexOfFrame(frame, 3) < IndexOfFrame(frame, 2),
            "a departing cell is painted before the retained cell that grows over it");

        Assert.True(gone.Alpha > 0.01f, "it is still painted halfway through");
        Assert.True(
            retained.Bounds.Intersects(gone.Bounds),
            "halfway through, the retained cell has grown over the departing one");

        // Two stated probes inside the departing rectangle, either side of the retained
        // cell's left edge. Deliberately not a search for an exclusive point: the root
        // covers every point in the frame, so no such point exists, and hunting for one
        // would assert nothing about the behaviour being tested.
        float y = gone.Bounds.Center.Y;
        float uncovered = gone.Bounds.Left + ((retained.Bounds.Left - gone.Bounds.Left) * 0.5f);
        float covered = retained.Bounds.Left + ((retained.Bounds.Right - retained.Bounds.Left) * 0.5f);

        Assert.True(gone.Bounds.Contains(uncovered, y));
        Assert.False(retained.Bounds.Contains(uncovered, y), "this probe has to sit clear of the retained cell");
        Assert.Equal(3, HitTester.HitTestFrameContent(frame, uncovered, y));

        Assert.True(gone.Bounds.Contains(covered, y), "the retained cell has to cover this probe");
        Assert.Equal(2, HitTester.HitTestFrameContent(frame, covered, y));

        // Once it is gone the pointer finds whatever replaced it, never the ghost —
        // including over the part of its old rectangle that is now solid.
        tween.Evaluate(1f, frame);

        TweenedCell faded = frame.Single(c => c.NodeId == 3);
        TweenedCell settled = frame.Single(c => c.NodeId == 2);

        Assert.Equal(0f, faded.Alpha);
        Assert.Equal(gone.Bounds, faded.Bounds);   // a departing cell never moves
        Assert.True(
            faded.Bounds.Intersects(settled.Bounds),
            "and it ends up underneath the cell that replaced it");

        Assert.Equal(2, HitTester.HitTestFrameContent(frame, uncovered, y));
        Assert.Equal(2, HitTester.HitTestFrameContent(frame, covered, y));
        Assert.NotEqual(IndexOfFrame(frame, 3), HitTester.HitTestFrame(frame, uncovered, y));
        Assert.NotEqual(IndexOfFrame(frame, 3), HitTester.HitTestFrame(frame, covered, y));
    }

    /// <summary>
    /// The cutoff is about what the user can actually see, so it is pinned against a
    /// frame whose rectangles, alphas and draw order are declared outright: a cell at
    /// zero, one exactly on the cutoff, one clearly visible, and a space block, over a
    /// common area so that the declared order alone decides the answer.
    /// </summary>
    [Fact]
    public void CellsAtOrBelowTheVisibleCutoffAreIgnored()
    {
        RectF left = new(0, 0, 100, 200);
        RectF right = new(100, 0, 100, 200);

        List<TweenedCell> frame =
        [
            Cell(10, new RectF(0, 0, 200, 200), 1f, NodeKind.Folder, -1),
            Cell(13, left, 0.5f, NodeKind.File, 0),
            Cell(12, left, 0.01f, NodeKind.File, 0),
            Cell(11, left, 0f, NodeKind.File, 0),
            Cell(14, right, 1f, NodeKind.FreeSpace, 0),
        ];

        // List order is the draw order, and the deepest drawn cell wins.
        Assert.Equal<int>([10, 13, 12, 11, 14], frame.Select(c => c.NodeId));

        // Invisible cells follow the visible file in draw order, so they must be skipped.
        Assert.Equal(13, HitTester.HitTestFrameContent(frame, 50, 100));

        // Neither the fully faded cell nor the one sitting exactly on the cutoff is
        // ever the answer, though both are painted over the point.
        Assert.NotEqual(IndexOfFrame(frame, 11), HitTester.HitTestFrame(frame, 50, 100));
        Assert.NotEqual(IndexOfFrame(frame, 12), HitTester.HitTestFrame(frame, 50, 100));
        Assert.NotEqual(11, HitTester.HitTestFrameContent(frame, 50, 100));
        Assert.NotEqual(12, HitTester.HitTestFrameContent(frame, 50, 100));

        // Over the free-space block: found, because it is drawn, but not reported as
        // content, so the pointer falls through to the root behind it.
        Assert.Equal(4, HitTester.HitTestFrame(frame, 150, 100));
        Assert.Equal(10, HitTester.HitTestFrameContent(frame, 150, 100));
    }

    [Fact]
    public void SpaceCellsAreFoundButNotReportedAsContent()
    {
        TreemapNode tree = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Space(3, "free", NodeKind.FreeSpace, 400));

        LayoutResult layout = SquarifyLayout.Compute(tree, Bounds);
        LayoutTween tween = new(layout, layout);

        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);

        int spaceIndex = IndexOfFrame(frame, 3);
        PointF centre = frame[spaceIndex].Bounds.Center;

        Assert.Equal(spaceIndex, HitTester.HitTestFrame(frame, centre.X, centre.Y));
        Assert.NotEqual(3, HitTester.HitTestFrameContent(frame, centre.X, centre.Y));
    }

    [Fact]
    public void DeepestDrawnCellWins()
    {
        TreemapNode tree = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "a", 100),
            TreeBuilder.Folder(3, "folder",
                TreeBuilder.File(4, "deep", 300),
                TreeBuilder.File(5, "deeper", 200)));

        LayoutResult layout = SquarifyLayout.Compute(tree, Bounds);
        LayoutTween tween = new(layout, layout);

        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);

        PointF centre = frame[IndexOfFrame(frame, 4)].Bounds.Center;
        Assert.Equal(4, HitTester.HitTestFrameContent(frame, centre.X, centre.Y));
    }

    /// <summary>
    /// Ancestry follows the frame's parent indices, which survive a reparent that
    /// geometry alone could not express.
    /// </summary>
    [Fact]
    public void FramePathToRootWalksParentIndices()
    {
        TreemapNode from = TreeBuilder.Folder(1, "root", TreeBuilder.File(2, "a", 100));
        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(3, "wrap", TreeBuilder.File(2, "a", 100)));

        LayoutTween tween = new(
            SquarifyLayout.Compute(from, Bounds),
            SquarifyLayout.Compute(to, Bounds));

        List<TweenedCell> frame = [];
        tween.Evaluate(0.5f, frame);

        List<int> path = HitTester.FramePathToRoot(frame, IndexOfFrame(frame, 2));

        Assert.Equal<int>(
            [IndexOfFrame(frame, 2), IndexOfFrame(frame, 3), IndexOfFrame(frame, 1)],
            path);
    }
}
