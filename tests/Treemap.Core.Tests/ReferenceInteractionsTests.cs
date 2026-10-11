using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

/// <summary>
/// Reference-interaction regressions: folder headers, leaf/space labels data,
/// layout-only space filters, history navigation with true parents, bounded
/// detail depth and snapshot-safe history.
/// </summary>
public class ReferenceInteractionsTests
{
    private static TreemapNode SampleTree()
        => TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "docs",
                TreeBuilder.File(3, "a.txt", 300),
                TreeBuilder.File(4, "b.txt", 100)),
            TreeBuilder.File(5, "pagefile.sys", 600),
            TreeBuilder.Space(6, "Free space", NodeKind.FreeSpace, 900),
            TreeBuilder.Space(7, "Unaccounted space", NodeKind.UnknownSpace, 100));

    private static TreemapController NewController(
        SnapshotPublisher publisher, LayoutOptions? options = null)
    {
        TreemapController controller = new(publisher, options);
        controller.SetViewport(new RectF(0, 0, 800, 600));
        controller.Relayout(animate: false);
        return controller;
    }

    [Fact]
    public void HeadersAreReservedAboveChildren()
    {
        LayoutOptions options = new() { ShowHeaders = true, HeaderHeight = 22f };
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300), options);

        Assert.True(layout.TryGetIndex(2, out int folder));
        RectF header = layout.HeaderBounds[folder];
        Assert.False(header.IsEmpty);
        Assert.Equal(layout.Bounds[folder].X, header.X, precision: 2);
        Assert.Equal(layout.Bounds[folder].Top, header.Top, precision: 2);

        // Every laid-out child starts below the strip, so headers never overlap content.
        foreach (int child in new[] { 3, 4 })
        {
            Assert.True(layout.TryGetIndex(child, out int index));
            Assert.True(layout.Bounds[index].Top >= header.Bottom - 0.01f);
        }
    }

    [Fact]
    public void HeadersStayOffByDefaultForLibraryCallers()
    {
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300));

        for (int i = 0; i < layout.Count; i++)
        {
            Assert.True(layout.HeaderBounds[i].IsEmpty);
        }
    }

    [Fact]
    public void CellsCarrySizesForLabelsAndTooltips()
    {
        LayoutOptions options = new() { ShowHeaders = true };
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300), options);

        Assert.True(layout.TryGetIndex(3, out int leaf));
        Assert.Equal(300, layout.LogicalSizes[leaf]);
        Assert.Equal(300, layout.PhysicalSizes[leaf]);

        Assert.True(layout.TryGetIndex(1, out int root));
        Assert.False(layout.HeaderBounds[root].IsEmpty);
    }

    [Fact]
    public void HidingFreeSpaceReflowsLayoutWithoutTouchingTheSnapshot()
    {
        TreemapNode root = SampleTree();
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);

        TreemapController controller = NewController(publisher);
        long version = publisher.Current.Version;

        controller.SetSpaceVisibility(showFreeSpace: false, showUnknownSpace: true);
        controller.Relayout(animate: false);

        Assert.DoesNotContain(controller.CurrentFrame, c => c.NodeId == 6);
        Assert.Contains(controller.CurrentFrame, c => c.NodeId == 7);

        // The remaining siblings absorb the hidden block's area.
        TweenedCell file = controller.CurrentFrame.Single(c => c.NodeId == 5);
        Assert.True(file.AreaFraction > 0.2f, $"expected a larger share, got {file.AreaFraction}");

        // No rescan: same snapshot, same totals, tree intact.
        Assert.Equal(version, publisher.Current.Version);
        Assert.True(publisher.Current.TryGetNode(6, out _));
    }

    [Fact]
    public void SpaceTogglesAreIndependent()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        controller.SetSpaceVisibility(showFreeSpace: true, showUnknownSpace: false);
        controller.Relayout(animate: false);

        Assert.Contains(controller.CurrentFrame, c => c.NodeId == 6);
        Assert.DoesNotContain(controller.CurrentFrame, c => c.NodeId == 7);
    }

    [Fact]
    public void UpMovesToTheTrueParentAfterAJump()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Folder(2, "a",
                TreeBuilder.Folder(3, "b",
                    TreeBuilder.File(4, "f.txt", 100))));
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);
        TreemapController controller = NewController(publisher);

        // Jump two levels at once: history only knows the root.
        Assert.True(controller.FocusNode(3));
        Assert.Equal(3, controller.FocusRootNodeId);

        Assert.True(controller.GoToParent());
        Assert.Equal(2, controller.FocusRootNodeId);
    }

    [Fact]
    public void BackAndForwardRestoreFocusAndCamera()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        controller.WheelZoom(1, 400, 300);
        controller.DragBy(25, 10);
        float zoom = controller.Camera.Zoom;
        PointF pan = controller.Camera.Pan;

        Assert.True(controller.FocusNode(2));
        controller.Relayout(animate: false);

        Assert.True(controller.GoBack());
        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.Equal(zoom, controller.Camera.Zoom, precision: 3);
        Assert.Equal(pan.X, controller.Camera.Pan.X, precision: 2);

        Assert.True(controller.CanGoForward);
        Assert.True(controller.GoForward());
        Assert.Equal(2, controller.FocusRootNodeId);
        Assert.False(controller.CanGoForward);
    }

    [Fact]
    public void NewNavigationClearsForwardHistory()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));
        Assert.True(controller.GoBack());
        Assert.True(controller.CanGoForward);

        Assert.True(controller.FocusNode(2));
        Assert.False(controller.CanGoForward);
    }

    [Fact]
    public void FocusingTheSameNodeDoesNotDuplicateHistory()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));
        Assert.False(controller.FocusNode(2));

        Assert.True(controller.GoBack());
        Assert.False(controller.CanGoBack);
        Assert.True(controller.GoForward());
        Assert.False(controller.CanGoForward);
    }

    [Fact]
    public void NewScanWithTheSameRootIdResetsStaleFocus()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));
        Assert.True(controller.CanGoBack);

        // A fresh scan reuses root id 1 but the drilled folder is gone.
        TreemapNode fresh = TreeBuilder.Drive(1, "D:",
            TreeBuilder.File(9, "other.txt", 500));
        publisher.Publish(fresh);
        controller.Relayout(animate: false);

        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.False(controller.CanGoBack);
        Assert.False(controller.CanGoForward);
        Assert.Contains(controller.CurrentFrame, c => c.NodeId == 9);
    }

    [Fact]
    public void DetailChangesDepthWithoutTouchingZoom()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(
            publisher, new LayoutOptions { MaxDepth = 4 });

        controller.WheelZoom(1, 400, 300);
        float zoom = controller.Camera.Zoom;

        Assert.True(controller.IncreaseDetail());
        Assert.Equal(5, controller.DetailDepth);
        Assert.Equal(zoom, controller.Camera.Zoom, precision: 4);

        Assert.True(controller.DecreaseDetail());
        Assert.True(controller.DecreaseDetail());
        Assert.Equal(3, controller.DetailDepth);
        Assert.Equal(zoom, controller.Camera.Zoom, precision: 4);
    }

    [Fact]
    public void DetailIsClampedAtBothLimits()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(
            publisher, new LayoutOptions { MaxDepth = TreemapController.MinDetailDepth });

        Assert.False(controller.DecreaseDetail());
        Assert.False(controller.CanDecreaseDetail);

        for (int i = 0; i < 200 && controller.CanIncreaseDetail; i++)
        {
            Assert.True(controller.IncreaseDetail());
        }

        Assert.Equal(TreemapController.MaxDetailDepth, controller.DetailDepth);
        Assert.False(controller.IncreaseDetail());
    }

    [Fact]
    public void TooltipCarriesPathAndSizeFromTheSnapshot()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        string text = controller.GetTooltipText(3);
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
        Assert.Contains("docs", text, StringComparison.Ordinal);
        Assert.Contains("300", text, StringComparison.Ordinal);

        // Cached per node and snapshot version: verify reuse before the
        // invalid-node query replaces the one-entry cache.
        Assert.Same(text, controller.GetTooltipText(3));

        Assert.Equal(string.Empty, controller.GetTooltipText(-1));
    }

    [Fact]
    public void AncestorsWalkParentIndices()
    {
        LayoutOptions options = new() { ShowHeaders = true };
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300), options);

        Assert.True(layout.TryGetIndex(3, out int leaf));
        List<int> path = HitTester.PathToRoot(layout, leaf);

        Assert.Equal(3, layout.NodeIds[path[0]]);
        Assert.Equal(2, layout.NodeIds[path[1]]);
        Assert.Equal(1, layout.NodeIds[path[2]]);
        Assert.Equal(3, path.Count);
    }

    // ---- supervisor round 2: header collapse ----

    [Fact]
    public void DepthCollapsedFoldersHaveNoHeaderAndDoNotRecurse()
    {
        LayoutOptions options = new() { ShowHeaders = true, HeaderHeight = 22f, MaxDepth = 1 };
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300), options);

        Assert.True(layout.TryGetIndex(2, out int folder));
        Assert.True(layout.HeaderBounds[folder].IsEmpty);
        Assert.False(layout.TryGetIndex(3, out _));
        Assert.False(layout.TryGetIndex(4, out _));
    }

    [Fact]
    public void TinyHeightTreeCollapsesToTheRoot()
    {
        LayoutOptions options = new() { ShowHeaders = true, HeaderHeight = 22f };
        LayoutResult layout = SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 10), options);

        Assert.Equal(1, layout.Count);
        Assert.True(layout.HeaderBounds[0].IsEmpty);
    }

    [Fact]
    public void HiddenOnlyChildrenCollapseWithoutARecurse()
    {
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            TreeBuilder.Space(6, "Free space", NodeKind.FreeSpace, 900));
        LayoutOptions options = new() { ShowHeaders = true, ShowFreeSpace = false };
        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 400, 300), options);

        Assert.Equal(1, layout.Count);
        Assert.True(layout.HeaderBounds[0].IsEmpty);
    }

    [Fact]
    public void ZeroSizeChildrenCollapseWithoutAHeader()
    {
        // Explicit sizes keep the folder alive as a sibling while its only child
        // carries no area, so the collapse path (not zero-area culling) is exercised.
        TreemapNode emptyFolder = TreemapNode.Create(
            2, "empty", NodeKind.Folder, 500, 500, null, 0,
            [TreeBuilder.File(3, "zero.txt", 0)]);
        TreemapNode root = TreeBuilder.Drive(1, "C:",
            emptyFolder,
            TreeBuilder.File(4, "real.txt", 500));
        LayoutOptions options = new() { ShowHeaders = true };
        LayoutResult layout = SquarifyLayout.Compute(root, new RectF(0, 0, 400, 300), options);

        Assert.True(layout.TryGetIndex(2, out int folder));
        Assert.True(layout.HeaderBounds[folder].IsEmpty);
        Assert.False(layout.TryGetIndex(3, out _));
    }

    // ---- supervisor round 2: home and camera fit ----

    [Fact]
    public void HomeRefitsCameraAtRoot()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        float fittedZoom = controller.Camera.Zoom;
        controller.WheelZoom(3, 400, 300);
        controller.DragBy(50, 50);
        Assert.True(controller.CanGoHome);

        Assert.True(controller.GoToRoot());
        Assert.Equal(fittedZoom, controller.Camera.Zoom, precision: 3);
        Assert.False(controller.CanGoHome);
        Assert.False(controller.GoToRoot());
    }

    [Fact]
    public void DrillDownAndUpFitTheNewSubtree()
    {
        SnapshotPublisher publisher = new(SampleTree());
        publisher.Publish(publisher.Current.Root);
        TreemapController controller = NewController(publisher);

        float fittedZoom = controller.Camera.Zoom;
        controller.WheelZoom(3, 400, 300);

        Assert.True(controller.FocusNode(2));
        Assert.Equal(fittedZoom, controller.Camera.Zoom, precision: 3);

        controller.WheelZoom(2, 400, 300);
        Assert.True(controller.GoToParent());
        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.Equal(fittedZoom, controller.Camera.Zoom, precision: 3);
    }

    // ---- supervisor round 2: root-source safety ----

    private static TreemapNode SameIdTree(string folderName)
        => TreeBuilder.Drive(1, "root",
            TreeBuilder.Folder(2, folderName,
                TreeBuilder.File(3, "a.txt", 100)));

    [Fact]
    public void DifferentSourcesWithSameIdsResetFocusAndHistory()
    {
        SnapshotPublisher publisher = new(SameIdTree("docs"), rootPath: @"C:\Alpha");
        publisher.Publish(publisher.Current.Root, rootPath: @"C:\Alpha");
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));
        Assert.True(controller.CanGoBack);

        publisher.Publish(SameIdTree("docs"), rootPath: @"D:\Beta");
        controller.Relayout(animate: false);

        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.False(controller.CanGoBack);
        Assert.False(controller.CanGoForward);
    }

    [Fact]
    public void SameSourcePreviewRetainsFocus()
    {
        SnapshotPublisher publisher = new(SameIdTree("docs"), rootPath: @"C:\Alpha");
        publisher.Publish(publisher.Current.Root, rootPath: @"C:\Alpha");
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));

        publisher.Publish(SameIdTree("docs"), rootPath: @"C:\Alpha");
        controller.Relayout(animate: false);

        Assert.Equal(2, controller.FocusRootNodeId);
        Assert.True(controller.CanGoBack);
    }

    [Fact]
    public void GetPathUsesTheRootSource()
    {
        TreemapNode root = SameIdTree("docs");
        TreemapSnapshot snapshot = TreemapSnapshot.Create(root, 1, rootPath: @"C:\Alpha");

        Assert.Equal(@"C:\Alpha", snapshot.GetPath(1));
        string leaf = snapshot.GetPath(3);
        Assert.Contains(@"C:\Alpha", leaf, StringComparison.Ordinal);
        Assert.Contains("a.txt", leaf, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySnapshotsKeepDisplayPaths()
    {
        TreemapSnapshot snapshot = TreemapSnapshot.Create(SampleTree(), 1);

        Assert.Equal("C:/docs/a.txt", snapshot.GetPath(3));
    }

    // ---- supervisor round 2: tween header containment ----

    [Fact]
    public void TweenHeadersStayInsideTheirCells()
    {
        TreemapNode tree = SampleTree();
        RectF bounds = new(0, 0, 400, 300);
        LayoutResult from = SquarifyLayout.Compute(tree, bounds, new LayoutOptions { ShowHeaders = false });
        LayoutResult to = SquarifyLayout.Compute(tree, bounds, new LayoutOptions { ShowHeaders = true });

        LayoutTween tween = new(from, to);
        List<TweenedCell> frame = [];

        foreach (float progress in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            tween.Evaluate(progress, frame);
            foreach (TweenedCell cell in frame)
            {
                if (cell.HeaderBounds.IsEmpty)
                {
                    continue;
                }

                Assert.True(cell.HeaderBounds.Left >= cell.Bounds.Left - 0.01f);
                Assert.True(cell.HeaderBounds.Top >= cell.Bounds.Top - 0.01f);
                Assert.True(cell.HeaderBounds.Right <= cell.Bounds.Right + 0.01f);
                Assert.True(cell.HeaderBounds.Bottom <= cell.Bounds.Bottom + 0.01f);
            }
        }
    }

    // ---- supervisor round 3: explicit root sources, platform matching ----

    [Fact]
    public void ExplicitNullPublishRestoresNullSource()
    {
        TreemapNode empty = TreeBuilder.Drive(1, "empty",
            TreeBuilder.File(9, "p.txt", 10));
        SnapshotPublisher publisher = new(empty);
        TreemapSnapshot initial = publisher.Current;
        Assert.Null(initial.RootPath);

        publisher.Publish(SameIdTree("docs"), rootPath: @"C:\Alpha");
        Assert.Equal(@"C:\Alpha", publisher.Current.RootPath);

        // Cancellation/failure restore passes the prior (null) source explicitly.
        publisher.Publish(initial.Root, initial.RootPath);
        Assert.Null(publisher.Current.RootPath);
    }

    [Fact]
    public void PublishWithoutSourceDoesNotInheritThePreviousSource()
    {
        SnapshotPublisher publisher = new(SameIdTree("docs"), rootPath: @"C:\Alpha");
        publisher.Publish(SameIdTree("docs"));
        Assert.Null(publisher.Current.RootPath);
    }

    [Fact]
    public void SourceCaseDifferenceResetsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SnapshotPublisher publisher = new(SameIdTree("docs"), rootPath: @"C:\Alpha");
        publisher.Publish(publisher.Current.Root, rootPath: @"C:\Alpha");
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));

        publisher.Publish(SameIdTree("docs"), rootPath: @"C:\ALPHA");
        controller.Relayout(animate: false);

        Assert.Equal(1, controller.FocusRootNodeId);
        Assert.False(controller.CanGoBack);
    }

    [Fact]
    public void SourceCaseDifferenceRetainsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SnapshotPublisher publisher = new(SameIdTree("docs"), rootPath: @"C:\Alpha");
        publisher.Publish(publisher.Current.Root, rootPath: @"C:\Alpha");
        TreemapController controller = NewController(publisher);

        Assert.True(controller.FocusNode(2));

        publisher.Publish(SameIdTree("docs"), rootPath: @"C:\ALPHA");
        controller.Relayout(animate: false);

        Assert.Equal(2, controller.FocusRootNodeId);
        Assert.True(controller.CanGoBack);
    }
}
