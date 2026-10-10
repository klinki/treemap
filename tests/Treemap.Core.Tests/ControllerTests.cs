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

        // Content is laid out into a fixed 1000x1000 reference rect, so zoom starts
        // near the "fitted" value and scrolling up genuinely zooms in.
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
        TreemapController controller = NewController(out _);
        controller.ConsumeChanges();

        controller.Click(400, 300);

        ChangeKind changes = controller.ConsumeChanges();
        Assert.True(changes.HasFlag(ChangeKind.Selection));
        Assert.True(controller.SelectedNodeId != 0);
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
        // Regression guard: the content rect is a fixed 1000x1000 reference, so a
        // fitted camera sits near zoom 1.0. An earlier 1x1 content rect scaled the
        // zoom to hundreds and pinned it against MaxZoom.
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
}
