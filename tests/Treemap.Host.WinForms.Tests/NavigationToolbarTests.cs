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
