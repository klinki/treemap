using System.Reflection;
using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the filename filter row: typing alone applies nothing, Apply and Enter
/// recalculate the map from matching files with counts and totals, Clear restores,
/// no matches is explicit, the filter survives new previews, stays independent of
/// the space toggles, and the row still fits at the smallest window.
/// </summary>
public class FilterRowTests
{
    [Fact]
    public void TypingAloneAppliesNothing() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(string.Empty, shell.View.Controller.FilterPattern ?? string.Empty);
        Assert.Equal(string.Empty, shell.FilterInfoText);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);
    });

    [Fact]
    public void ApplyRecalculatesCountsAndTotalsAndOmitsSpaceBlocks() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);

        Assert.Equal("*.mp4", shell.View.Controller.FilterPattern);
        Assert.Equal(2L, shell.View.Controller.DisplayFileCount);
        Assert.Equal(1500L, shell.View.Controller.DisplaySnapshot.Root.LogicalSize);
        Assert.Equal(1500L, shell.View.Controller.DisplaySnapshot.Root.PhysicalSize);
        Assert.Contains("2", shell.FilterInfoText, StringComparison.Ordinal);
        Assert.Contains("*.mp4", shell.FilterInfoText, StringComparison.Ordinal);

        // The summary uses the same binary formatter as the map labels and
        // tooltips, for both logical and allocated totals.
        Assert.Contains(SizeFormat.FormatBytes(1500L), shell.FilterInfoText, StringComparison.Ordinal);

        // Matching files stay, the rest is gone from the map but not the scan.
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 3 && c.Alpha > 0.01f);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 4 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);

        // Filtered views omit the volume-space blocks even with both boxes checked.
        Assert.True(shell.FreeSpaceBox.Checked);
        Assert.True(shell.UnaccountedSpaceBox.Checked);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
        Assert.DoesNotContain(shell.View.Controller.CurrentFrame, c => c.NodeId == 7 && c.Alpha > 0.01f);

        // The published tree is untouched and nothing rescanned.
        Assert.True(shell.Publisher.Current.TryGetNode(8, out _));
        Assert.True(shell.Publisher.Current.TryGetNode(6, out _));

        // 1500 bytes formats identically under the old decimal formatter, so
        // republish with a 300,000,000-byte match: the summary must read the
        // binary value, not the old decimal one. The active filter survives
        // the new preview, as covered by TheFilterSurvivesNewPreviews.
        shell.Publisher.Publish(BigRoot());
        StaRunner.PumpUntil(() => shell.View.Controller.DisplayFileCount == 1);
        Assert.Contains(SizeFormat.FormatBytes(300_000_000L), shell.FilterInfoText, StringComparison.Ordinal);
        Assert.DoesNotContain("300 MB", shell.FilterInfoText, StringComparison.Ordinal);
    });

    [Fact]
    public void NoMatchesIsExplicit() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mkv";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);

        Assert.Equal(0L, shell.View.Controller.DisplayFileCount);
        Assert.Contains("No files match", shell.FilterInfoText, StringComparison.Ordinal);
        Assert.Contains("*.mkv", shell.FilterInfoText, StringComparison.Ordinal);

        // The empty view renders only the message: a corner pixel well away
        // from the centered text must be the bare background, proving the
        // empty 0-byte root fill is absent.
        Assert.True(shell.View.Width > 10 && shell.View.Height > 10);
        using Bitmap bitmap = new(shell.View.Width, shell.View.Height);
        shell.View.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        Assert.Equal(shell.View.BackColor.ToArgb(), bitmap.GetPixel(5, 5).ToArgb());
    });

    [Fact]
    public void ClearRestoresTheMapAndSpaceChoices() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);
        Assert.Equal(2L, shell.View.Controller.DisplayFileCount);

        Click(shell.ClearFilterButton);
        PumpSettled(shell);

        Assert.Equal(string.Empty, shell.View.Controller.FilterPattern ?? string.Empty);
        Assert.Equal(string.Empty, shell.FilterInfoText);
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 8 && c.Alpha > 0.01f);

        // Clearing restores the user's own space visibility choices.
        Assert.Contains(shell.View.Controller.CurrentFrame, c => c.NodeId == 6 && c.Alpha > 0.01f);
    });

    [Fact]
    public void BlankAndStarClearTheFilter() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);
        Assert.Equal("*.mp4", shell.View.Controller.FilterPattern);

        shell.FilterBox.Text = "*";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);

        Assert.Equal(string.Empty, shell.View.Controller.FilterPattern ?? string.Empty);
        Assert.Equal(string.Empty, shell.FilterInfoText);
    });

    [Fact]
    public void EnterAppliesWithoutDinging() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        KeyEventArgs args = new(Keys.Enter);
        MethodInfo handler = typeof(ScanShell).GetMethod(
            "OnFilterKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(shell, [shell.FilterBox, args]);
        Application.DoEvents();
        PumpSettled(shell);

        Assert.True(args.Handled);
        Assert.True(args.SuppressKeyPress);
        Assert.Equal("*.mp4", shell.View.Controller.FilterPattern);
        Assert.Equal(2L, shell.View.Controller.DisplayFileCount);
    });

    [Fact]
    public void TheFilterSurvivesNewPreviews() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);
        Assert.Equal(2L, shell.View.Controller.DisplayFileCount);

        shell.Publisher.Publish(FilterTrees.RootWithThreeMp4());
        StaRunner.PumpUntil(() => shell.View.Controller.DisplayFileCount == 3);

        Assert.Equal("*.mp4", shell.View.Controller.FilterPattern);
        Assert.Contains("3", shell.FilterInfoText, StringComparison.Ordinal);
    });

    [Fact]
    public void SpaceTogglesStayIndependentAndRescanNothing() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);

        long version = shell.Publisher.Current.Version;

        shell.FreeSpaceBox.Checked = false;
        PumpSettled(shell);
        shell.FreeSpaceBox.Checked = true;
        PumpSettled(shell);

        Assert.Equal("*.mp4", shell.View.Controller.FilterPattern);
        Assert.Equal(2L, shell.View.Controller.DisplayFileCount);
        Assert.Equal(version, shell.Publisher.Current.Version);
    });

    [Fact]
    public void HoverTooltipFollowsTheFilterWithoutANewPublish() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());
        PumpSettled(shell);

        // Hover the folder; the tooltip reads the display projection, no disk IO.
        MouseMove(shell, FormGeometry.CellHeaderPoint(shell.View, 2));
        string before = shell.View.TooltipTextForTests;
        Assert.NotEmpty(before);

        long version = shell.Publisher.Current.Version;

        shell.FilterBox.Text = "*.mp4";
        Click(shell.ApplyFilterButton);
        PumpSettled(shell);

        // Same snapshot version, but the folder totals changed with the filter.
        Assert.Equal(version, shell.Publisher.Current.Version);
        string filtered = shell.View.TooltipTextForTests;
        Assert.NotEmpty(filtered);
        Assert.NotEqual(before, filtered);

        Click(shell.ClearFilterButton);
        PumpSettled(shell);

        Assert.Equal(version, shell.Publisher.Current.Version);
        Assert.Equal(before, shell.View.TooltipTextForTests);
    });

    [Fact]
    public void AtTheSmallestWindowTheFilterRowStillFits() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(FilterTrees.Root());

        shell.Size = shell.MinimumSize;
        shell.PerformLayout();
        Application.DoEvents();

        foreach (Control control in shell.FilterRow.Controls)
        {
            Rectangle parent = new(Point.Empty, control.Parent!.ClientSize);
            Assert.True(
                parent.Contains(control.Bounds),
                $"'{control.Text}' ({control.GetType().Name}) at {control.Bounds} escaped {parent}.");
        }

        Assert.True(shell.ApplyFilterButton.Width > 0 && shell.ClearFilterButton.Width > 0);
    });

    // ---- helpers ----

    /// <summary>
    /// A tree whose single matching file is large enough to separate the binary
    /// map formatter from the old decimal one (286.1 MB vs 300 MB).
    /// </summary>
    private static TreemapNode BigRoot() => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
    [
        TreemapNode.Create(3, "big.mp4", NodeKind.File, 300_000_000, 300_000_000, null, 0),
        TreemapNode.Create(8, "b.txt", NodeKind.File, 100, 100, null, 0),
    ]);

    private static void MouseMove(ScanShell shell, Point at)
    {
        MethodInfo handler = typeof(TreemapForm).GetMethod(
            "OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(shell.View, [new MouseEventArgs(MouseButtons.Left, 0, at.X, at.Y, 0)]);
        Application.DoEvents();
    }

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
        ScanResult result = new()
        {
            RootPath = @"C:\",
            Root = root,
            Backend = ScanBackend.Portable,
            IsComplete = true,
            FileCount = 3,
            Duration = TimeSpan.FromMilliseconds(10),
        };

        ScanShell shell = new(
            (rootPath, publisher, options, progress, token) =>
            {
                publisher.Publish(root);
                return Task.FromResult(result);
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
        // Filter assertions need depth-2 leaves visible.
        shell.View.Controller.SetLayoutOptions(shell.View.Controller.Options.With(maxDepth: 2));
        shell.View.Controller.Relayout(animate: false);
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
