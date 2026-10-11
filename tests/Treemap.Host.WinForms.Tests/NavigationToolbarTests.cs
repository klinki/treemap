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
    public void SpaceBoxesReflowWithoutRescanning() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(Tree());

        long version = shell.Publisher.Current.Version;
        TreemapNode root = shell.Publisher.Current.Root;
        PumpSettled(shell);

        // Optional blocks start hidden, as in the reference recording.
        // Departing cells linger one settled union frame at alpha 0 (existing
        // animator contract), so removal means no VISIBLE cell, matching the
        // renderer/hit-test threshold.
        Assert.False(shell.FreeSpaceBox.Checked);
        Assert.False(shell.UnaccountedSpaceBox.Checked);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);

        shell.FreeSpaceBox.Checked = true;
        PumpSettled(shell);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);

        shell.FreeSpaceBox.Checked = false;
        PumpSettled(shell);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);

        shell.UnaccountedSpaceBox.Checked = true;
        PumpSettled(shell);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);

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
        StaRunner.PumpUntil(() => shell.View.Controller.CurrentFrame.Any(c => c.NodeId == 3));

        return shell;
    }

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
