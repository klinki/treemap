using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Aspect-aware content bounds: reference height 1000 with matching width,
/// fitted camera fills wide and portrait viewports, hit testing stays exact,
/// and SetViewport never rebuilds the tree itself.
/// </summary>
public class AspectBoundsTests
{
    private static SnapshotPublisher Publisher()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "a",
                TreeBuilder.File(3, "x.bin", 600),
                TreeBuilder.File(4, "y.bin", 400)),
            TreeBuilder.File(5, "z.bin", 500));
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        return publisher;
    }

    private static TreemapController Controller(RectF viewport)
    {
        TreemapController controller = new(Publisher());
        controller.SetViewport(viewport);
        controller.Relayout(animate: false);
        return controller;
    }

    [Fact]
    public void WideViewportWidensContentBounds()
    {
        RectF bounds = TreemapController.ContentBoundsFor(new RectF(0, 0, 1600, 900));

        Assert.Equal(1000f, bounds.Height, precision: 2);
        Assert.Equal(1000f * 1600f / 900f, bounds.Width, precision: 1);
    }

    [Fact]
    public void PortraitViewportNarrowsContentBounds()
    {
        RectF bounds = TreemapController.ContentBoundsFor(new RectF(0, 0, 600, 800));

        Assert.Equal(1000f, bounds.Height, precision: 2);
        Assert.Equal(750f, bounds.Width, precision: 1);
    }

    [Fact]
    public void EmptyViewportFallsBackToSquare()
    {
        RectF bounds = TreemapController.ContentBoundsFor(RectF.Empty);

        Assert.Equal(1000f, bounds.Width, precision: 2);
        Assert.Equal(1000f, bounds.Height, precision: 2);
    }

    [Fact]
    public void FittedCameraFillsWideViewport()
    {
        RectF viewport = new(0, 0, 1600, 900);
        TreemapController controller = Controller(viewport);

        RectF content = TreemapController.ContentBoundsFor(viewport);
        RectF onScreen = controller.Camera.ContentToScreen(content);

        Assert.Equal(viewport.Width, onScreen.Width, precision: 1);
        Assert.Equal(viewport.Height, onScreen.Height, precision: 1);
        Assert.Equal(viewport.X, onScreen.X, precision: 1);
        Assert.Equal(viewport.Y, onScreen.Y, precision: 1);
    }

    [Fact]
    public void FittedCameraFillsPortraitViewport()
    {
        RectF viewport = new(0, 0, 600, 800);
        TreemapController controller = Controller(viewport);

        RectF content = TreemapController.ContentBoundsFor(viewport);
        RectF onScreen = controller.Camera.ContentToScreen(content);

        Assert.Equal(viewport.Width, onScreen.Width, precision: 1);
        Assert.Equal(viewport.Height, onScreen.Height, precision: 1);
    }

    [Fact]
    public void HitTestingTracksCellsInWideAndPortraitWindows()
    {
        // Default layout has no folder headers, so expanded parents (1, 2) are
        // fully covered by their children: a parent bounds center hits the
        // deepest visible child there. Only leaves keep exclusive centers.
        foreach (RectF viewport in new[] { new RectF(0, 0, 1600, 900), new RectF(0, 0, 600, 800) })
        {
            TreemapController controller = Controller(viewport);

            foreach (int expected in new[] { 3, 4, 5 })
            {
                TweenedCell cell = VisibleCell(controller, expected);
                PointF screen = controller.Camera.ContentToScreen(cell.Bounds.Center);

                controller.PointerMoved(screen.X, screen.Y);
                Assert.Equal(expected, controller.HoveredNodeId);
            }
        }

        // With headers enabled each expanded folder keeps an exclusive strip,
        // so the header center must hit the folder itself in both aspects.
        foreach (RectF viewport in new[] { new RectF(0, 0, 1600, 900), new RectF(0, 0, 600, 800) })
        {
            TreemapController controller = HeaderController(viewport);

            foreach (int expected in new[] { 1, 2 })
            {
                TweenedCell cell = VisibleCell(controller, expected);
                Assert.False(cell.HeaderBounds.IsEmpty, $"node {expected} should have a header");
                PointF screen = controller.Camera.ContentToScreen(cell.HeaderBounds.Center);

                controller.PointerMoved(screen.X, screen.Y);
                Assert.Equal(expected, controller.HoveredNodeId);
            }
        }
    }

    private static TreemapController HeaderController(RectF viewport)
    {
        TreemapController controller = new(Publisher(), new LayoutOptions { ShowHeaders = true });
        controller.SetViewport(viewport);
        controller.Relayout(animate: false);
        return controller;
    }

    private static TweenedCell VisibleCell(TreemapController controller, int nodeId)
    {
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == nodeId && cell.Alpha > 0.01f)
            {
                return cell;
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return default;
    }

    [Fact]
    public void SetViewportRefitsCameraWithoutRelayingOut()
    {
        TreemapController controller = Controller(new RectF(0, 0, 800, 600));
        controller.ConsumeChanges();

        controller.SetViewport(new RectF(0, 0, 1600, 900));

        ChangeKind changes = controller.ConsumeChanges();
        Assert.True(changes.HasFlag(ChangeKind.Camera));
        Assert.False(changes.HasFlag(ChangeKind.Layout));
    }
}

/// <summary>
/// Temporary in-place expansion: one branch shows two extra levels, selection of
/// another node clears it, files and blank clear it, navigation and filter edits
/// clear it, and same-source previews retain it only while valid.
/// </summary>
public class TemporaryExpansionTests
{
    private static TreemapNode Tree() => TreeBuilder.Drive(1, "C:",
        TreeBuilder.Folder(2, "folderA",
            TreeBuilder.Folder(20, "deepA",
                TreeBuilder.File(21, "a1.bin", 400),
                TreeBuilder.File(22, "a2.bin", 400))),
        TreeBuilder.Folder(3, "folderB",
            TreeBuilder.Folder(30, "deepB",
                TreeBuilder.File(31, "b1.bin", 400))));

    private static TreemapController Controller()
    {
        SnapshotPublisher publisher = new(Tree());
        publisher.Publish(Tree());
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = 2 });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);
        return controller;
    }

    private static bool FrameHas(TreemapController controller, int nodeId)
    {
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == nodeId)
            {
                return true;
            }
        }

        return false;
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

    private static PointF ScreenHeaderCenterOf(TreemapController controller, int nodeId)
    {
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == nodeId)
            {
                Assert.False(cell.HeaderBounds.IsEmpty, $"node {nodeId} should have a header");
                return controller.Camera.ContentToScreen(cell.HeaderBounds.Center);
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return default;
    }

    [Fact]
    public void ExpansionShowsOnlyTheClickedBranch()
    {
        TreemapController controller = Controller();

        Assert.False(FrameHas(controller, 21));
        Assert.False(FrameHas(controller, 31));

        Assert.True(controller.ExpandNodeTemporarily(2));
        controller.Relayout(animate: false);

        Assert.Equal(2, controller.TemporaryExpandedNodeId);
        Assert.Equal(2, controller.DetailDepth);
        Assert.True(FrameHas(controller, 21));
        Assert.False(FrameHas(controller, 31));
    }

    [Fact]
    public void ClickingAnotherFolderMovesTheExpansion()
    {
        TreemapController controller = Controller();

        Assert.True(controller.ExpandNodeTemporarily(2));
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 21));

        Assert.True(controller.ExpandNodeTemporarily(3));
        controller.Relayout(animate: false);

        Assert.Equal(3, controller.TemporaryExpandedNodeId);
        Assert.False(FrameHas(controller, 21));
        Assert.True(FrameHas(controller, 31));
    }

    [Fact]
    public void FileAndBlankClicksClearTheExpansion()
    {
        TreemapController controller = Controller();
        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 21));

        Assert.True(controller.ExpandNodeTemporarily(21));
        Assert.Equal(21, controller.SelectedNodeId);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        controller.Relayout(animate: false);
        Assert.False(FrameHas(controller, 21));

        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 21));

        Assert.True(controller.ExpandNodeTemporarily(-1));
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
    }

    [Fact]
    public void ClickingDisplayRootClearsInsteadOfExpandingEverywhere()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "folderA",
                TreeBuilder.Folder(20, "deepA",
                    TreeBuilder.File(21, "a1.bin", 400))),
            TreeBuilder.Folder(3, "folderB",
                TreeBuilder.Folder(30, "deepB",
                    TreeBuilder.File(31, "b1.bin", 400))));
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = TreemapController.MinDetailDepth });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        // Fresh basic detail shows only immediate children.
        Assert.Equal(TreemapController.MinDetailDepth, controller.DetailDepth);
        Assert.False(FrameHas(controller, 20));
        Assert.False(FrameHas(controller, 30));

        // A root click on the fresh map grants nothing and reports no change.
        Assert.False(controller.ExpandNodeTemporarily(1));
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        controller.Relayout(animate: false);
        Assert.False(FrameHas(controller, 20));
        Assert.False(FrameHas(controller, 30));

        // A root click after a branch expansion clears back to minimal
        // instead of deepening every branch while the toolbar says basic 1.
        Assert.True(controller.ExpandNodeTemporarily(2));
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 21));

        Assert.True(controller.ExpandNodeTemporarily(1));
        Assert.Equal(1, controller.SelectedNodeId);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        controller.Relayout(animate: false);
        Assert.False(FrameHas(controller, 20));
        Assert.False(FrameHas(controller, 30));
        Assert.Equal(TreemapController.MinDetailDepth, controller.DetailDepth);
    }

    [Fact]
    public void DetailButtonsClearTemporaryExpansion()
    {
        TreemapNode root = Tree();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = TreemapController.MinDetailDepth });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 21));

        // Less at the minimum still returns to minimal by clearing the branch.
        Assert.True(controller.CanDecreaseDetail);
        Assert.True(controller.DecreaseDetail());
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        Assert.Equal(TreemapController.MinDetailDepth, controller.DetailDepth);
        controller.Relayout(animate: false);
        Assert.False(FrameHas(controller, 21));

        // More clears the branch while adding its single global generation.
        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);
        Assert.True(controller.IncreaseDetail());
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        Assert.Equal(TreemapController.MinDetailDepth + 1, controller.DetailDepth);
    }

    [Fact]
    public void DefaultDetailShowsOnlyImmediateChildrenBeforeAndAfterPublish()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "folderA",
                TreeBuilder.Folder(20, "deepA",
                    TreeBuilder.File(21, "a1.bin", 400))),
            TreeBuilder.File(4, "top.bin", 400));
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(
            publisher, new LayoutOptions { MaxDepth = TreemapController.MinDetailDepth });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        Assert.Equal(TreemapController.MinDetailDepth, controller.DetailDepth);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        Assert.True(FrameHas(controller, 2));
        Assert.True(FrameHas(controller, 4));
        Assert.False(FrameHas(controller, 20));
        Assert.False(FrameHas(controller, 21));

        publisher.Publish(TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "folderA",
                TreeBuilder.Folder(20, "deepA",
                    TreeBuilder.File(21, "a1.bin", 400))),
            TreeBuilder.File(4, "top.bin", 400)));
        controller.Relayout(animate: false);

        Assert.Equal(TreemapController.MinDetailDepth, controller.DetailDepth);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        Assert.True(FrameHas(controller, 2));
        Assert.True(FrameHas(controller, 4));
        Assert.False(FrameHas(controller, 20));
        Assert.False(FrameHas(controller, 21));
    }

    [Fact]
    public void ClickSelectsExpandsAndKeepsFocusCameraHistory()
    {
        TreemapController controller = Controller();
        controller.SetLayoutOptions(new LayoutOptions { MaxDepth = 2, ShowHeaders = true });
        controller.Relayout(animate: false);
        float zoom = controller.Camera.Zoom;
        PointF pan = controller.Camera.Pan;

        // Without headers the folder interior is covered by its children, so
        // click the header strip where the folder itself is really the hit.
        PointF at = ScreenHeaderCenterOf(controller, 2);
        controller.PointerMoved(at.X, at.Y);
        Assert.Equal(2, controller.HoveredNodeId);
        controller.Click(at.X, at.Y);

        Assert.Equal(2, controller.SelectedNodeId);
        Assert.Equal(2, controller.TemporaryExpandedNodeId);
        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.Equal(zoom, controller.Camera.Zoom, precision: 4);
        Assert.Equal(pan.X, controller.Camera.Pan.X, precision: 2);
        Assert.False(controller.CanGoBack);
        Assert.True(FrameHas(controller, 21));
    }

    [Fact]
    public void NavigationClearsTheExpansion()
    {
        TreemapController controller = Controller();
        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);

        Assert.True(controller.FocusNode(3));
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
    }

    [Fact]
    public void SameSourcePreviewRetainsValidExpansion()
    {
        TreemapNode root = Tree();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = 2 });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);

        publisher.Publish(Tree());
        controller.Relayout(animate: false);

        Assert.Equal(2, controller.TemporaryExpandedNodeId);
        Assert.True(FrameHas(controller, 21));
    }

    [Fact]
    public void SameSourcePreviewDropsRemovedExpansion()
    {
        TreemapNode root = Tree();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = 2 });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ExpandNodeTemporarily(2);
        controller.Relayout(animate: false);

        TreemapNode pruned = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(3, "folderB",
                TreeBuilder.Folder(30, "deepB",
                    TreeBuilder.File(31, "b1.bin", 400))));
        publisher.Publish(pruned);
        controller.Relayout(animate: false);

        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
    }

    [Fact]
    public void DeepChainsStayBounded()
    {
        TreemapNode leaf = TreeBuilder.File(1000, "leaf", 100);
        for (int depth = 70; depth >= 2; depth--)
        {
            leaf = TreeBuilder.Folder(depth, $"d{depth}", leaf);
        }

        TreemapNode root = TreeBuilder.Drive(1, "C:", leaf);
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = 4 });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        Assert.True(controller.ExpandNodeTemporarily(2));
        controller.Relayout(animate: false);

        int maxDepth = 0;
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            maxDepth = Math.Max(maxDepth, cell.Depth);
        }

        Assert.True(maxDepth <= 64, $"depth {maxDepth} exceeded the bound");
    }
}

/// <summary>
/// Filename filter projection: glob matching, recalculated totals, ancestors,
/// space-block omission, empty roots, cache identity and tooltip/count behaviour.
/// </summary>
public class FileFilterTests
{
    private static TreemapNode Root() => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
    [
        TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "a.mp4", NodeKind.File, 1000, 1000, null, 0),
            TreemapNode.Create(8, "b.txt", NodeKind.File, 100, 100, null, 0),
        ]),
        TreemapNode.Create(4, "other.mp4", NodeKind.File, 500, 500, null, 0),
        TreemapNode.Create(6, "Free space", NodeKind.FreeSpace, 2000, 2000),
        TreemapNode.Create(7, "Unaccounted space", NodeKind.UnknownSpace, 300, 300),
    ]);

    private static TreemapController Controller(TreemapNode root, string? source = null)
    {
        SnapshotPublisher publisher = new(root, rootPath: source);
        publisher.Publish(root, rootPath: source);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);
        return controller;
    }

    private static bool FrameHas(TreemapController controller, int nodeId)
    {
        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == nodeId && cell.Alpha > 0.01f)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void ApplyRecalculatesTotalsAndOmitsSpaceBlocks()
    {
        TreemapController controller = Controller(Root());

        Assert.True(controller.ApplyFileFilter("*.mp4"));
        Assert.Equal("*.mp4", controller.FilterPattern);
        controller.Relayout(animate: false);

        Assert.Equal(2L, controller.DisplayFileCount);
        Assert.Equal(1500L, controller.DisplaySnapshot.Root.LogicalSize);
        Assert.Equal(1500L, controller.DisplaySnapshot.Root.PhysicalSize);

        Assert.True(FrameHas(controller, 3));
        Assert.True(FrameHas(controller, 4));
        Assert.False(FrameHas(controller, 8));
        Assert.False(FrameHas(controller, 6));
        Assert.False(FrameHas(controller, 7));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndQuestionMarkWorks()
    {
        TreemapController upper = Controller(Root());
        upper.ApplyFileFilter("*.MP4");
        upper.Relayout(animate: false);
        Assert.Equal(2L, upper.DisplayFileCount);
        Assert.True(upper.DisplaySnapshot.TryGetNode(3, out _));

        TreemapController single = Controller(Root());
        single.ApplyFileFilter("?.mp4");
        single.Relayout(animate: false);
        Assert.Equal(1L, single.DisplayFileCount);

        TreemapController miss = Controller(Root());
        miss.ApplyFileFilter("*.mkv");
        miss.Relayout(animate: false);
        Assert.Equal(0L, miss.DisplayFileCount);
    }

    [Fact]
    public void AncestorsAndStableIdsSurvive()
    {
        TreemapController controller = Controller(Root());
        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);

        Assert.True(controller.DisplaySnapshot.TryGetNode(2, out TreemapNode? folder));
        Assert.Equal("sub", folder.Name);
        Assert.Equal(NodeKind.Folder, folder.Kind);
        Assert.True(controller.DisplaySnapshot.TryGetNode(3, out _));

        string path = controller.DisplaySnapshot.GetPath(3);
        Assert.Contains("sub", path, StringComparison.Ordinal);
        Assert.Contains("a.mp4", path, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMatchKeepsAnEmptyRoot()
    {
        TreemapController controller = Controller(Root());
        controller.ApplyFileFilter("*.mkv");
        controller.Relayout(animate: false);

        Assert.Equal(0L, controller.DisplayFileCount);
        Assert.Equal(1, controller.DisplaySnapshot.Root.Id);
        Assert.Empty(controller.DisplaySnapshot.Root.Children);
        Assert.Equal(0L, controller.DisplaySnapshot.Root.LogicalSize);
    }

    [Fact]
    public void BlankAndStarClearAndRestoreTheExactSnapshot()
    {
        TreemapNode root = Root();
        SnapshotPublisher publisher = new(root);
        TreemapSnapshot published = publisher.Publish(root);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);
        Assert.Equal(2L, controller.DisplayFileCount);

        Assert.True(controller.ApplyFileFilter("*"));
        Assert.Equal(string.Empty, controller.FilterPattern);
        Assert.Same(published, publisher.Current);
        Assert.Same(publisher.Current, controller.DisplaySnapshot);
        controller.Relayout(animate: false);
        Assert.True(FrameHas(controller, 8));
        Assert.True(FrameHas(controller, 6));

        controller.ApplyFileFilter("*.mp4");
        Assert.Equal(2L, controller.DisplayFileCount);
        Assert.True(controller.ApplyFileFilter("  "));
        Assert.Equal(string.Empty, controller.FilterPattern);
    }

    [Fact]
    public void ReapplyingTheSamePatternReportsNoChange()
    {
        TreemapController controller = Controller(Root());

        Assert.True(controller.ApplyFileFilter("*.mp4"));
        Assert.False(controller.ApplyFileFilter("*.mp4"));
    }

    [Fact]
    public void DisplaySnapshotIsCachedUntilPreviewOrEdit()
    {
        TreemapController controller = Controller(Root());
        controller.ApplyFileFilter("*.mp4");

        TreemapSnapshot first = controller.DisplaySnapshot;
        Assert.Same(first, controller.DisplaySnapshot);
        Assert.Equal(controller.DisplayFileCount, controller.DisplayFileCount);
    }

    [Fact]
    public void NewPreviewRebuildsTheProjection()
    {
        TreemapNode root = Root();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ApplyFileFilter("*.mp4");
        TreemapSnapshot before = controller.DisplaySnapshot;
        Assert.Equal(2L, controller.DisplayFileCount);

        TreemapNode grown = TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(3, "a.mp4", NodeKind.File, 1000, 1000, null, 0),
                TreemapNode.Create(9, "c.mp4", NodeKind.File, 2000, 2000, null, 0),
            ]),
            TreemapNode.Create(4, "other.mp4", NodeKind.File, 500, 500, null, 0),
        ]);
        publisher.Publish(grown);
        controller.Relayout(animate: false);

        Assert.Equal("*.mp4", controller.FilterPattern);
        Assert.Equal(3L, controller.DisplayFileCount);
        Assert.NotSame(before, controller.DisplaySnapshot);
    }

    [Fact]
    public void FilteredOutFocusResetsToDisplayRoot()
    {
        TreemapController controller = Controller(Root());
        Assert.True(controller.FocusNode(2));

        controller.ApplyFileFilter("*.mkv");
        controller.Relayout(animate: false);

        Assert.Equal(controller.DisplaySnapshot.Root.Id, controller.FocusRootNodeId);
    }

    [Fact]
    public void TooltipComesFromTheDisplayProjection()
    {
        TreemapController controller = Controller(Root());
        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);

        string first = controller.GetTooltipText(3);
        Assert.Contains("a.mp4", first, StringComparison.Ordinal);
        Assert.Same(first, controller.GetTooltipText(3));

        controller.ApplyFileFilter("*.txt");
        controller.Relayout(animate: false);
        string after = controller.GetTooltipText(8);
        Assert.Contains("b.txt", after, StringComparison.Ordinal);
        Assert.NotSame(first, after);
    }

    [Fact]
    public void ZeroByteMatchesStayInProjectionButNotInLayout()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "sub",
                TreeBuilder.File(3, "empty.mp4", 0),
                TreeBuilder.File(4, "full.mp4", 1000)));
        TreemapController controller = Controller(root);

        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);

        Assert.Equal(2L, controller.DisplayFileCount);
        Assert.True(controller.DisplaySnapshot.TryGetNode(3, out _));
        Assert.False(FrameHas(controller, 3));
        Assert.True(FrameHas(controller, 4));
    }

    [Fact]
    public void LargeSumsSaturateInsteadOfOverflowing()
    {
        long big = long.MaxValue - 10;
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "sub",
                TreemapNode.Create(3, "a.mp4", NodeKind.File, big, big, null, 0),
                TreemapNode.Create(4, "b.mp4", NodeKind.File, 20, 20, null, 0)));
        TreemapController controller = Controller(root);

        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);

        Assert.Equal(long.MaxValue, controller.DisplaySnapshot.Root.LogicalSize);
        Assert.Equal(long.MaxValue, controller.DisplaySnapshot.Root.PhysicalSize);
    }

    [Fact]
    public void VersionAndSourceMetadataSurviveProjection()
    {
        TreemapNode root = Root();
        SnapshotPublisher publisher = new(root, rootPath: @"C:\scan");
        TreemapSnapshot source = publisher.Publish(root, rootPath: @"C:\scan");
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ApplyFileFilter("*.mp4");

        Assert.Equal(source.Version, controller.DisplaySnapshot.Version);
        Assert.Equal(@"C:\scan", controller.DisplaySnapshot.RootPath);
    }

    [Fact]
    public void FilterClearsSelectionAndHoverWhenNodesVanish()
    {
        TreemapController controller = Controller(Root());
        controller.ApplyFileFilter("*.mp4");
        controller.Relayout(animate: false);

        controller.ExpandNodeTemporarily(3);
        Assert.Equal(3, controller.SelectedNodeId);

        foreach (TweenedCell cell in controller.CurrentFrame)
        {
            if (cell.NodeId == 3)
            {
                PointF screen = controller.Camera.ContentToScreen(cell.Bounds.Center);
                controller.PointerMoved(screen.X, screen.Y);
                break;
            }
        }

        Assert.Equal(3, controller.HoveredNodeId);

        controller.ApplyFileFilter("*.txt");
        controller.Relayout(animate: false);

        Assert.False(controller.DisplaySnapshot.TryGetNode(3, out _));
        Assert.NotEqual(3, controller.SelectedNodeId);
        Assert.NotEqual(3, controller.HoveredNodeId);
    }

    [Fact]
    public void PreviewRemovingSelectedNodeClearsSelection()
    {
        TreemapNode root = Root();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        controller.ExpandNodeTemporarily(4);
        Assert.Equal(4, controller.SelectedNodeId);

        TreemapNode pruned = TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(3, "a.mp4", NodeKind.File, 1000, 1000, null, 0),
            ]),
        ]);
        publisher.Publish(pruned);
        controller.Relayout(animate: false);

        Assert.Equal(-1, controller.SelectedNodeId);
        Assert.Equal(-1, controller.HoveredNodeId);
    }

    [Fact]
    public void InvalidExpansionRequestNormalizesToBlank()
    {
        TreemapController controller = Controller(Root());

        Assert.False(controller.ExpandNodeTemporarily(9999));
        Assert.Equal(-1, controller.SelectedNodeId);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
    }

    [Fact]
    public void HomeClearsTemporaryDetailAtFittedRoot()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "folderA",
                TreeBuilder.Folder(20, "deepA",
                    TreeBuilder.File(21, "a1.bin", 400),
                    TreeBuilder.File(22, "a2.bin", 400))));
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = new(publisher, new LayoutOptions { MaxDepth = 2 });
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);

        Assert.True(controller.ExpandNodeTemporarily(2));
        controller.Relayout(animate: false);
        Assert.Equal(2, controller.TemporaryExpandedNodeId);

        Assert.True(controller.CanGoHome);
        Assert.True(controller.GoToRoot());
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        controller.Relayout(animate: false);
        Assert.Equal(-1, controller.TemporaryExpandedNodeId);
        Assert.False(controller.CanGoHome);
        Assert.False(controller.GoToRoot());
    }

    [Fact]
    public void ExternalTemporaryOptionsDoNotSurviveClear()
    {
        TreemapController controller = Controller(Root());
        controller.SetLayoutOptions(new LayoutOptions { MaxDepth = 4, TemporaryExpandedNodeId = 2, TemporaryExtraDepth = 5 });

        Assert.Equal(-1, controller.Options.TemporaryExpandedNodeId);
        Assert.Equal(0, controller.Options.TemporaryExtraDepth);
        Assert.Equal(4, controller.Options.MaxDepth);
        Assert.Equal(-1, LayoutOptions.Default.TemporaryExpandedNodeId);
        Assert.Equal(0, LayoutOptions.Default.TemporaryExtraDepth);
    }
}

/// <summary>
/// Deep narrow-tree regressions: oversized depth options must saturate at the
/// documented 64-level hard cap without indexing past scratch buffers, with or
/// without a temporary override and with huge extra-depth values.
/// </summary>
public class DeepDepthCapTests
{
    private static TreemapNode Chain(int levels)
    {
        TreemapNode leaf = TreeBuilder.File(1000 + levels, "leaf.bin", 100);
        for (int depth = levels; depth >= 2; depth--)
        {
            leaf = TreeBuilder.Folder(depth, $"d{depth}", leaf);
        }

        return TreeBuilder.Drive(1, "C:", leaf);
    }

    private static LayoutOptions Plain(int maxDepth) => new()
    {
        MinCellSize = 0f,
        MaxDepth = maxDepth,
        InnerPadding = 0f,
        ShowHeaders = false,
    };

    private static int MaxCellDepth(LayoutResult layout)
    {
        int max = 0;
        for (int i = 0; i < layout.Count; i++)
        {
            max = Math.Max(max, layout.Depths[i]);
        }

        return max;
    }

    private static readonly RectF Bounds = new(0, 0, 1000, 1000);

    [Fact]
    public void OversizedMaxDepthStaysBounded()
    {
        TreemapNode root = Chain(90);

        foreach (int maxDepth in new[] { 128, int.MaxValue })
        {
            LayoutResult layout = SquarifyLayout.Compute(root, Bounds, Plain(maxDepth));
            Assert.True(MaxCellDepth(layout) <= 64, $"MaxDepth {maxDepth} exceeded the 64 cap");
        }
    }

    [Fact]
    public void TemporaryOverrideWithHugeExtraStaysBounded()
    {
        TreemapNode root = Chain(90);

        foreach (int extra in new[] { 2, int.MaxValue })
        {
            LayoutOptions options = Plain(int.MaxValue).With(
                temporaryExpandedNodeId: 2, temporaryExtraDepth: extra);
            LayoutResult layout = SquarifyLayout.Compute(root, Bounds, options);
            Assert.True(MaxCellDepth(layout) <= 64, $"extra {extra} exceeded the 64 cap");
        }

        LayoutOptions atLimit = Plain(64).With(
            temporaryExpandedNodeId: 2, temporaryExtraDepth: int.MaxValue);
        LayoutResult limited = SquarifyLayout.Compute(root, Bounds, atLimit);
        Assert.True(MaxCellDepth(limited) <= 64, "base 64 plus huge extra exceeded the 64 cap");
    }

    [Fact]
    public void BranchAtDepthLimitDoesNotEnlargeUnrelatedBranches()
    {
        TreemapNode deepLeaf = TreeBuilder.File(100, "deep.bin", 100);
        for (int depth = 60; depth >= 2; depth--)
        {
            deepLeaf = TreeBuilder.Folder(1000 + depth, $"deep{depth}", deepLeaf);
        }

        TreemapNode root = TreeBuilder.Drive(1, "C:",
            deepLeaf,
            TreeBuilder.Folder(2000, "shallow",
                TreeBuilder.File(2001, "s1.bin", 100),
                TreeBuilder.File(2002, "s2.bin", 100)));
        RectF bounds = new(0, 0, 1000, 1000);
        LayoutOptions plain = new()
        {
            MinCellSize = 0f,
            MaxDepth = 64,
            InnerPadding = 0f,
            ShowHeaders = false,
        };
        LayoutResult before = SquarifyLayout.Compute(root, bounds, plain);
        LayoutResult after = SquarifyLayout.Compute(
            root, bounds, plain.With(temporaryExpandedNodeId: 1001, temporaryExtraDepth: 2));

        Assert.True(MaxCellDepth(after) <= 64, "temporary branch exceeded the 64 cap");
        Assert.True(after.TryGetIndex(2000, out _), "unrelated branch must remain laid out");
    }
}
