using System.Reflection;
using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the shell navigation toolbar: history buttons follow the controller,
/// detail buttons move the depth bounds without touching zoom, and the space
/// boxes reflow the map without rescanning.
/// </summary>
public class NavigationToolbarTests
{
    private static TreemapNode Tree()
        => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(3, "a.txt", NodeKind.File, 400, 400, null, 0),
                TreemapNode.Create(8, "b.txt", NodeKind.File, 100, 100, null, 0),
            ]),
            TreemapNode.Create(4, "other.txt", NodeKind.File, 500, 500, null, 0),
            TreemapNode.Create(6, "Free space", NodeKind.FreeSpace, 2000, 2000),
            TreemapNode.Create(7, "Unaccounted space", NodeKind.UnknownSpace, 300, 300),
        ]);

    private static ScanResult Result(string rootPath, TreemapNode root) => new()
    {
        RootPath = rootPath,
        Root = root,
        Backend = ScanBackend.Portable,
        IsComplete = true,
        FileCount = 3,
        Duration = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public void ToolbarFollowsNavigationState() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(Tree());

        Assert.False(shell.BackButton.Enabled);
        Assert.False(shell.ForwardButton.Enabled);
        Assert.False(shell.UpButton.Enabled);
        Assert.False(shell.HomeButton.Enabled);

        Assert.True(shell.View.Controller.FocusNode(2));
        shell.View.Controller.Relayout(animate: false);

        Assert.True(shell.BackButton.Enabled);
        Assert.True(shell.UpButton.Enabled);
        Assert.True(shell.HomeButton.Enabled);

        Click(shell.BackButton);
        Assert.Equal(1, shell.View.Controller.FocusRootNodeId);
        Assert.False(shell.BackButton.Enabled);
        Assert.True(shell.ForwardButton.Enabled);

        Click(shell.ForwardButton);
        Assert.Equal(2, shell.View.Controller.FocusRootNodeId);

        Click(shell.UpButton);
        Assert.Equal(1, shell.View.Controller.FocusRootNodeId);
    });

    [Fact]
    public void OptionalBlocksAreCheckedByDefault() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(Tree());

        Assert.True(shell.View.Controller.ShowFreeSpace);
        Assert.True(shell.View.Controller.ShowUnknownSpace);
        Assert.True(shell.FreeSpaceBox.Checked);
        Assert.True(shell.UnaccountedSpaceBox.Checked);
    });

    [Fact]
    public void DetailButtonsMoveDepthWithoutTouchingZoom() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(Tree());

        float zoom = shell.View.Controller.Camera.Zoom;
        int depth = shell.View.Controller.DetailDepth;

        Click(shell.MoreDetailButton);
        Assert.Equal(depth + 1, shell.View.Controller.DetailDepth);
        Assert.Equal(zoom, shell.View.Controller.Camera.Zoom, precision: 4);

        Click(shell.LessDetailButton);
        Assert.Equal(depth, shell.View.Controller.DetailDepth);
        Assert.Equal(zoom, shell.View.Controller.Camera.Zoom, precision: 4);

        while (shell.LessDetailButton.Enabled)
        {
            Click(shell.LessDetailButton);
        }

        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.False(shell.LessDetailButton.Enabled);
    });

    [Fact]
    public void FreshDefaultShowsImmediateChildrenAndEachStepMovesOneGeneration() => StaRunner.Run(() =>
    {
        // Fresh desktop/shell default: no depth override here.
        using ScanShell shell = CreateShell(DeepTree());
        PumpSettled(shell);

        // Level 1: immediate folder and file visible, grandchildren hidden.
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.False(shell.LessDetailButton.Enabled);
        Assert.True(shell.MoreDetailButton.Enabled);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 4 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 5 && c.Alpha > 0.01f);

        // One More step: next folder+file generation appears, deeper stays hidden.
        Click(shell.MoreDetailButton);
        PumpSettled(shell);
        Assert.Equal(TreemapController.MinDetailDepth + 1, shell.View.Controller.DetailDepth);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 5 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);

        // Another More step: next generation appears, deepest stays hidden.
        Click(shell.MoreDetailButton);
        PumpSettled(shell);
        Assert.Equal(TreemapController.MinDetailDepth + 2, shell.View.Controller.DetailDepth);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);

        // Less restores exactly one generation.
        Click(shell.LessDetailButton);
        PumpSettled(shell);
        Assert.Equal(TreemapController.MinDetailDepth + 1, shell.View.Controller.DetailDepth);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 5 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);

        // Less cannot go below level 1.
        while (shell.LessDetailButton.Enabled)
        {
            Click(shell.LessDetailButton);
        }

        PumpSettled(shell);
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.False(shell.LessDetailButton.Enabled);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 4 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
    });

    [Fact]
    public void RootClickKeepsFreshMapAtImmediateChildren() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(DeepTree());
        PumpSettled(shell);

        int rootId = shell.View.Controller.FocusRootNodeId;

        // A single click on the displayed root must not deepen every branch
        // while the toolbar still reports basic detail 1.
        Assert.False(shell.View.Controller.ExpandNodeTemporarily(rootId));
        Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
        shell.View.Controller.Relayout(animate: false);
        PumpSettled(shell);

        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 4 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 5 && c.Alpha > 0.01f);
    });

    [Fact]
    public void LessButtonAtMinimumClearsTemporaryExpansion() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(DeepTree());
        PumpSettled(shell);

        Assert.True(shell.View.Controller.ExpandNodeTemporarily(2));
        shell.View.Controller.Relayout(animate: false);
        PumpSettled(shell);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);

        // Less stays available while the branch expansion is active, even at
        // minimum depth, and returns the map to minimal in one step.
        Assert.True(shell.LessDetailButton.Enabled);
        Click(shell.LessDetailButton);
        PumpSettled(shell);

        Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.False(shell.LessDetailButton.Enabled);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
    });

    [Fact]
    public void StartingANewScanResetsTemporaryExpansion() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(DeepTree());
        PumpSettled(shell);

        Assert.True(shell.View.Controller.ExpandNodeTemporarily(2));
        shell.View.Controller.Relayout(animate: false);
        PumpSettled(shell);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);

        shell.StartScan(@"C:\");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        PumpSettled(shell);

        Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
    });

    [Fact]
    public void FreshScanWithDeepeningPreviewsKeepsMinimalFrameInStepWithToolbar() => StaRunner.Run(() =>
    {
        // Simulates a live scan whose previews deepen the published tree while the
        // user never clicks: shallow basis, then richer same-source previews, then
        // the final tree. Every publish carries the scan's RootPath explicitly, so
        // the controller takes the real new-source then same-source path.
        TreemapNode shallow = TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0),
            TreemapNode.Create(4, "other.txt", NodeKind.File, 3000, 3000, null, 0),
        ]);
        TreemapNode mid = TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(3, "a.txt", NodeKind.File, 1000, 1000, null, 0),
                TreemapNode.Create(5, "nested", NodeKind.Folder, 0, 0, null, 0,
                [
                    TreemapNode.Create(6, "b.txt", NodeKind.File, 1000, 1000, null, 0),
                ]),
            ]),
            TreemapNode.Create(4, "other.txt", NodeKind.File, 3000, 3000, null, 0),
        ]);
        TreemapNode full = DeepTree();

        TaskCompletionSource allowMiddle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowFinal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScanShell shell = new(
            (rootPath, publisher, options, progress, token) => Task.Run(async () =>
            {
                publisher.Publish(shallow, rootPath);
                await allowMiddle.Task.WaitAsync(token);
                publisher.Publish(mid, rootPath);
                await allowFinal.Task.WaitAsync(token);
                publisher.Publish(full, rootPath);
                return Result(rootPath, full);
            }, token),
            ScanOptions.Default with { Backend = ScanBackendPreference.Portable });

        _ = shell.Handle;
        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        shell.StartScan(@"C:\");
        AssertMinimalPreview(shallow);
        allowMiddle.SetResult();
        AssertMinimalPreview(mid);
        allowFinal.SetResult();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        PumpSettled(shell);

        void AssertMinimalPreview(TreemapNode expected)
        {
            StaRunner.PumpUntil(() => shell.View.Controller.CurrentFrame.Any(
                cell => cell.NodeId == 1 && cell.LogicalSize == expected.LogicalSize));
            PumpSettled(shell);
            Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
            Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
            Assert.False(shell.LessDetailButton.Enabled);
            Assert.DoesNotContain(shell.View.Controller.CurrentFrame,
                cell => cell.Alpha > 0.01f && cell.Depth > TreemapController.MinDetailDepth);
        }

        // Toolbar reports basic detail 1 with no temporary branch.
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
        Assert.False(shell.LessDetailButton.Enabled);
        Assert.True(shell.MoreDetailButton.Enabled);

        // Effective frame agrees: immediate children visible, deeper generations
        // hidden, and no visible cell exceeds the toolbar's depth budget.
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 4 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 5 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);

        foreach (TweenedCell cell in shell.View.Controller.CurrentFrame)
        {
            if (cell.Alpha > 0.01f)
            {
                Assert.True(
                    cell.Depth <= TreemapController.MinDetailDepth,
                    $"visible node {cell.NodeId} at depth {cell.Depth} exceeds toolbar detail {TreemapController.MinDetailDepth}");
            }
        }
    });

    [Fact]
    public void PendingClickFromBeforeScanCannotDeepenFreshSource() => StaRunner.Run(() =>
    {
        // A press latched on the old map must not expand the fresh one: the
        // deferred timer fires by id only while the source still matches.
        using ScanShell shell = new(
            (rootPath, publisher, options, progress, token) =>
            {
                publisher.Publish(DeepTree(), rootPath);
                return Task.FromResult(Result(rootPath, DeepTree()));
            },
            ScanOptions.Default with { Backend = ScanBackendPreference.Portable });

        _ = shell.Handle;
        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        shell.StartScan(@"C:\old");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        PumpSettled(shell);

        Point at = FormGeometry.CellHeaderPoint(shell.View, 2);
        MouseDown(shell.View, at);
        MouseUp(shell.View, at);
        Assert.True(shell.View.HasPendingSingleClick);

        shell.StartScan(@"C:\new");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        PumpSettled(shell);

        // Resolve the deferred click deterministically instead of waiting out
        // the real double-click interval; a source mismatch must drop it.
        shell.View.FirePendingClickForTests();
        PumpSettled(shell);

        Assert.Equal(-1, shell.View.Controller.TemporaryExpandedNodeId);
        Assert.Equal(-1, shell.View.Controller.SelectedNodeId);
        Assert.Equal(TreemapController.MinDetailDepth, shell.View.Controller.DetailDepth);
        Assert.False(shell.LessDetailButton.Enabled);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 2 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
    });

    [Fact]
    public void SpaceBoxesReflowWithoutRescanning() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(Tree());

        long version = shell.Publisher.Current.Version;
        TreemapNode root = shell.Publisher.Current.Root;
        PumpSettled(shell);

        // Optional blocks start shown. Departing cells linger one settled union
        // frame at alpha 0 (existing animator contract), so removal means no
        // VISIBLE cell, matching the renderer/hit-test threshold.
        Assert.True(shell.FreeSpaceBox.Checked);
        Assert.True(shell.UnaccountedSpaceBox.Checked);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);

        shell.FreeSpaceBox.Checked = false;
        PumpSettled(shell);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);

        shell.FreeSpaceBox.Checked = true;
        PumpSettled(shell);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);

        shell.UnaccountedSpaceBox.Checked = false;
        PumpSettled(shell);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);

        // Layout only: same snapshot, same tree, nothing rescanned.
        Assert.Equal(version, shell.Publisher.Current.Version);
        Assert.Same(root, shell.Publisher.Current.Root);
        Assert.True(shell.Publisher.Current.TryGetNode(6, out _));
    });

    // ---- helpers ----

    /// <summary>
    /// Invokes the button's actual Click wiring while the shell stays hidden.
    /// PerformClick is a no-op on a hidden, non-selectable button; calling the
    /// protected OnClick exercises the same handlers instead.
    /// </summary>
    private static void Click(Button button)
    {
        Assert.True(button.Enabled);
        MethodInfo? onClick = button.GetType().GetMethod(
            "OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onClick);
        onClick.Invoke(button, [EventArgs.Empty]);
        Application.DoEvents();
    }

    private static void PumpSettled(ScanShell shell)
        => StaRunner.PumpUntil(() => !shell.View.Controller.IsAnimating);

    private static void MouseDown(TreemapForm form, Point at)
        => Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void MouseUp(TreemapForm form, Point at)
        => Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));

    private static void Invoke(TreemapForm form, string method, MouseEventArgs args)
    {
        MethodInfo? target = typeof(TreemapForm).GetMethod(
            method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(target);
        target.Invoke(form, [args]);
        Application.DoEvents();
    }

    private static ScanShell CreateShell(TreemapNode root)
    {
        ScanShell shell = new(
            (rootPath, publisher, options, progress, token) =>
            {
                publisher.Publish(root);
                return Task.FromResult(Result(rootPath, root));
            },
            ScanOptions.Default with { Backend = ScanBackendPreference.Portable });

        _ = shell.Handle;
        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        shell.StartScan(@"C:\");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        // The fresh default shows only immediate children (level 1), so wait for
        // the immediate file rather than the depth-2 leaf the old default showed.
        StaRunner.PumpUntil(() => shell.View.Controller.CurrentFrame.Any(c => c.NodeId == 4));

        return shell;
    }

    /// <summary>
    /// Tiny nested tree for the fresh-default regression: root 1, immediate folder 2
    /// and file 4 (level 1), file 3 and subfolder 5 (level 2), file 6 and folder 7
    /// (level 3), file 8 (level 4). Sizes keep every generation large enough to
    /// survive culling once its level is shown.
    /// </summary>
    private static TreemapNode DeepTree()
        => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
        [
            TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
            [
                TreemapNode.Create(3, "a.txt", NodeKind.File, 1000, 1000, null, 0),
                TreemapNode.Create(5, "nested", NodeKind.Folder, 0, 0, null, 0,
                [
                    TreemapNode.Create(6, "b.txt", NodeKind.File, 1000, 1000, null, 0),
                    TreemapNode.Create(7, "deep", NodeKind.Folder, 0, 0, null, 0,
                    [
                        TreemapNode.Create(8, "c.txt", NodeKind.File, 1000, 1000, null, 0),
                    ]),
                ]),
            ]),
            TreemapNode.Create(4, "other.txt", NodeKind.File, 3000, 3000, null, 0),
        ]);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
