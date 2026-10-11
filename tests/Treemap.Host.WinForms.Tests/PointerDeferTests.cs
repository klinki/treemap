using System.Reflection;
using Treemap;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers deferred single-click expansion: MouseDown latches, MouseUp starts the
/// double-click interval, the timer fires by latched id, a double-click navigates
/// to the exact latched folder, fast presses land on the latest node, drags and
/// held presses select nothing, and a stale latch never lands on a new tree.
/// </summary>
public class PointerDeferTests
{
    private static TreemapNode Tree() => TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0,
    [
        TreemapNode.Create(2, "folderA", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "a1.txt", NodeKind.File, 400, 400, null, 0),
            TreemapNode.Create(8, "a2.txt", NodeKind.File, 100, 100, null, 0),
        ]),
        TreemapNode.Create(5, "folderB", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(6, "b1.txt", NodeKind.File, 500, 500, null, 0),
        ]),
    ]);

    /// <summary>
    /// Same ids, reweighted so folderA and folderB swap on-screen positions.
    /// Firing by id must still expand folderA.
    /// </summary>
    private static TreemapNode ReweightedTree() => TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0,
    [
        TreemapNode.Create(2, "folderA", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "a1.txt", NodeKind.File, 50, 50, null, 0),
            TreemapNode.Create(8, "a2.txt", NodeKind.File, 50, 50, null, 0),
        ]),
        TreemapNode.Create(5, "folderB", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(6, "b1.txt", NodeKind.File, 5000, 5000, null, 0),
        ]),
    ]);

    private static TreemapNode TreeWithoutFolderA() => TreemapNode.Create(1, "root", NodeKind.Folder, 0, 0, null, 0,
    [
        TreemapNode.Create(5, "folderB", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(6, "b1.txt", NodeKind.File, 500, 500, null, 0),
        ]),
    ]);

    [Fact]
    public void FirstPressDefersAndFiringExpandsInPlace() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        Assert.True(form.HasPendingSingleClick);
        Assert.False(form.IsClickTimerRunningForTests);

        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);
        Assert.True(form.IsClickTimerRunningForTests);

        // Deferred: latched but nothing selected or expanded yet.
        Assert.Equal(-1, form.Controller.SelectedNodeId);

        form.FirePendingClickForTests();
        PumpSettled(form);

        Assert.Equal(2, form.Controller.SelectedNodeId);
        Assert.Equal(2, form.Controller.TemporaryExpandedNodeId);
        Assert.Equal(1, form.Controller.FocusRootNodeId);
    });

    [Fact]
    public void BlankClickClearsThePriorSelectionAndExpansion() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        MouseDown(form, FormGeometry.CellHeaderPoint(form, 2), clicks: 1);
        MouseUp(form, FormGeometry.CellHeaderPoint(form, 2));
        form.FirePendingClickForTests();
        PumpSettled(form);
        Assert.Equal(2, form.Controller.TemporaryExpandedNodeId);

        // A genuine blank press latches id -1 with the flag set, then clears.
        // Negative coordinates fall outside every cell, so the hit is blank.
        Point blank = new(-10, -10);
        MouseDownAt(form, blank);
        MouseUpAt(form, blank);
        Assert.True(form.HasPendingSingleClick);
        Assert.Equal(-1, form.PendingClickId);

        form.FirePendingClickForTests();
        PumpSettled(form);

        Assert.Equal(-1, form.Controller.SelectedNodeId);
        Assert.Equal(-1, form.Controller.TemporaryExpandedNodeId);
    });

    [Fact]
    public void SelectingAnotherNodeClearsThePriorExpansion() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        Point first = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, first, clicks: 1);
        MouseUp(form, first);
        form.FirePendingClickForTests();
        PumpSettled(form);
        Assert.Equal(2, form.Controller.TemporaryExpandedNodeId);

        Point second = FormGeometry.CellHeaderPoint(form, 5);
        MouseDown(form, second, clicks: 1);
        MouseUp(form, second);
        form.FirePendingClickForTests();
        PumpSettled(form);

        Assert.Equal(5, form.Controller.SelectedNodeId);
        Assert.Equal(5, form.Controller.TemporaryExpandedNodeId);
    });

    [Fact]
    public void DoubleClickNavigatesToTheLatchedFolder() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        // Actual WinForms ordering: Down1, Up1, Down2 (clicks 2), DoubleClick, Up2.
        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        MouseDown(form, at, clicks: 2);
        MouseDoubleClick(form, at);
        MouseUp(form, at);

        Assert.False(form.HasPendingSingleClick);
        Assert.Equal(2, form.Controller.FocusRootNodeId);
        Assert.Equal(-1, form.Controller.TemporaryExpandedNodeId);
    });

    [Fact]
    public void DoubleClickPreservesFirstLatchWhenPreviewMovesCellsUnderneath() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Tree());
        using TreemapForm form = CreateForm(publisher);
        PumpSettled(form);

        // Actual WinForms ordering: Down1, Up1, Down2 (clicks 2), DoubleClick, Up2.
        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);
        Assert.Equal(2, form.PendingClickId);

        // Same-source reweighted preview: cells move but stable ids stay valid.
        publisher.Publish(ReweightedTree());
        StaRunner.PumpUntil(() => form.Controller.CurrentFrame.Count > 0);
        PumpSettled(form);

        // Prove the race: the original screen point no longer hits folder A.
        PointF content = form.Controller.Camera.ScreenToContent(new PointF(at.X, at.Y));
        int nowHit = HitTester.HitTestFrameContent(form.Controller.CurrentFrame, content.X, content.Y);
        Assert.NotEqual(2, nowHit);

        // Second press of the double-click must not re-latch the node now
        // underneath the same coordinates.
        MouseDown(form, at, clicks: 2);
        Assert.True(form.HasPendingSingleClick);
        Assert.Equal(2, form.PendingClickId);

        MouseDoubleClick(form, at);
        MouseUp(form, at);

        Assert.False(form.HasPendingSingleClick);
        Assert.False(form.IsClickTimerRunningForTests);
        Assert.Equal(2, form.Controller.FocusRootNodeId);
        Assert.Equal(-1, form.Controller.SelectedNodeId);
        Assert.Equal(-1, form.Controller.TemporaryExpandedNodeId);
    });

    [Fact]
    public void DifferentFastPressesLandOnTheLatestNode() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        Point first = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, first, clicks: 1);
        MouseUp(form, first);

        // A second press on another cell cancels the first latch and keeps the
        // latest hit; the first expansion never fires before the retest.
        Point second = FormGeometry.CellHeaderPoint(form, 5);
        MouseDown(form, second, clicks: 1);
        MouseUp(form, second);

        Assert.Equal(-1, form.Controller.SelectedNodeId);
        Assert.True(form.HasPendingSingleClick);
        Assert.Equal(5, form.PendingClickId);

        form.FirePendingClickForTests();
        Assert.Equal(5, form.Controller.SelectedNodeId);
    });

    [Fact]
    public void HoldingLongerThanDoubleClickTimeSelectsNothingWhileHeld() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);

        // Held past the double-click interval: the timer never started, so no
        // selection or expansion happens while the button is down.
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime + 250));
        Application.DoEvents();
        Assert.False(form.IsClickTimerRunningForTests);
        Assert.Equal(-1, form.Controller.SelectedNodeId);
        Assert.Equal(-1, form.Controller.TemporaryExpandedNodeId);

        // A test hook firing while held must not select either.
        form.FirePendingClickForTests();
        Assert.Equal(-1, form.Controller.SelectedNodeId);

        // A clean release arms the interval and fires normally.
        MouseUp(form, at);
        Assert.True(form.IsClickTimerRunningForTests);
        form.FirePendingClickForTests();
        Assert.Equal(2, form.Controller.SelectedNodeId);
    });

    [Fact]
    public void ADragCancelsTheLatchAndSelectsNothing() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        Assert.True(form.HasPendingSingleClick);

        MouseMove(form, new Point(at.X + 40, at.Y + 40));
        Assert.False(form.HasPendingSingleClick);

        MouseUp(form, new Point(at.X + 40, at.Y + 40));
        form.FirePendingClickForTests();

        Assert.Equal(-1, form.Controller.SelectedNodeId);
    });

    [Fact]
    public void SameSourcePreviewMovingGeometryStillExpandsTheLatchedFolder() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Tree());
        using TreemapForm form = CreateForm(publisher);
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);

        // Same source, reweighted: cells move but stable ids stay valid.
        publisher.Publish(ReweightedTree());
        StaRunner.PumpUntil(() => form.Controller.CurrentFrame.Count > 0);
        PumpSettled(form);

        form.FirePendingClickForTests();
        PumpSettled(form);

        Assert.Equal(2, form.Controller.SelectedNodeId);
        Assert.Equal(2, form.Controller.TemporaryExpandedNodeId);
    });

    [Fact]
    public void ANodeExcludedFromTheNewPreviewDropsPending() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Tree());
        using TreemapForm form = CreateForm(publisher);
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);

        publisher.Publish(TreeWithoutFolderA());
        StaRunner.PumpUntil(() => form.Controller.CurrentFrame.Count > 0);
        PumpSettled(form);

        form.FirePendingClickForTests();

        Assert.Equal(-1, form.Controller.SelectedNodeId);
        Assert.False(form.HasPendingSingleClick);
    });

    [Fact]
    public void AStaleLatchNeverLandsOnANewTree() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Tree());
        using TreemapForm form = CreateForm(publisher);
        PumpSettled(form);

        Point at = FormGeometry.CellHeaderPoint(form, 2);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);

        publisher.Publish(Tree(), rootPath: @"D:\other");
        StaRunner.PumpUntil(() => form.Controller.CurrentFrame.Count > 0);
        PumpSettled(form);

        form.FirePendingClickForTests();

        Assert.Equal(-1, form.Controller.SelectedNodeId);
    });

    [Fact]
    public void PendingLeafClickDoesNotCancelPendingResize() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(Tree());
        PumpSettled(form);

        // Leaf press: latches the file but grants no temporary expansion.
        Point at = FormGeometry.CellCenter(form, 3);
        MouseDown(form, at, clicks: 1);
        MouseUp(form, at);
        Assert.True(form.HasPendingSingleClick);
        Assert.Equal(3, form.PendingClickId);

        // Resize before the deferred click fires. The camera refits at once;
        // the aspect-ratio rebuild is still pending.
        form.Size = new Size(680, 200);
        Assert.True(form.HasPendingResize);

        // Test hook fires the click deterministically before the 100ms
        // resize tick. A leaf changes no expansion, so the resize must stay
        // alive to catch the layout up to the new aspect.
        form.FirePendingClickForTests();

        // Let the pending resize settle through the real timer.
        PumpSettled(form);

        Assert.Equal(3, form.Controller.SelectedNodeId);
        Assert.Equal(-1, form.Controller.TemporaryExpandedNodeId);
        Assert.Equal(1, form.Controller.FocusRootNodeId);
        Assert.False(form.HasPendingResize);
        Assert.False(form.Controller.IsAnimating);

        TweenedCell root = form.Controller.CurrentFrame.First(c => c.NodeId == 1);
        RectF screen = form.Controller.Camera.ContentToScreen(root.Bounds);
        Assert.True(
            Math.Abs(screen.Width - form.Width) < 2f,
            $"root width {screen.Width} did not fill viewport width {form.Width} after resize settled");
        Assert.True(
            Math.Abs(screen.Height - form.Height) < 2f,
            $"root height {screen.Height} did not fill viewport height {form.Height} after resize settled");
    });

    // ---- helpers ----

    private static TreemapForm CreateForm(TreemapNode root) => CreateForm(new SnapshotPublisher(root));

    private static TreemapForm CreateForm(SnapshotPublisher publisher)
    {
        TreemapForm form = new(publisher) { Size = new Size(480, 320) };
        _ = form.Handle;
        PumpSettled(form);
        return form;
    }

    private static void PumpSettled(TreemapForm form)
        => StaRunner.PumpUntil(() => !form.Controller.IsAnimating && !form.HasPendingResize);

    private static void MouseDown(TreemapForm form, Point at, int clicks = 1)
        => Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, clicks, at.X, at.Y, 0));

    private static void MouseDownAt(TreemapForm form, Point at)
        => Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void MouseUp(TreemapForm form, Point at)
        => Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void MouseUpAt(TreemapForm form, Point at)
        => Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void MouseMove(TreemapForm form, Point at)
        => Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void MouseDoubleClick(TreemapForm form, Point at)
        => Invoke(form, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, at.X, at.Y, 0));

    private static void Invoke(TreemapForm form, string method, MouseEventArgs args)
    {
        MethodInfo target = typeof(TreemapForm).GetMethod(
            method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(TreemapForm), method);
        target.Invoke(form, [args]);
        Application.DoEvents();
    }
}
