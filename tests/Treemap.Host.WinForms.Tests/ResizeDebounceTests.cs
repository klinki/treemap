using System.Reflection;
using Treemap;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers resize coalescing: every size still refits the camera and back buffer
/// immediately, only the settled size rebuilds the tree, no timer idles forever,
/// disposal is safe, and wide and portrait viewports both fill with content.
/// </summary>
public class ResizeDebounceTests
{
    [Fact]
    public void ResizeRefitsImmediatelyButRebuildsOnce() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out _);
        PumpSettled(form);

        int layouts = 0;
        form.Controller.Changed += kind =>
        {
            if ((kind & ChangeKind.Layout) != 0)
            {
                layouts++;
            }
        };

        // A burst of sizes: each one refits, none rebuilds yet.
        for (int i = 0; i < 5; i++)
        {
            form.Size = new Size(form.Size.Width + 12, form.Size.Height + 8);
        }

        Assert.Equal(0, layouts);
        Assert.True(form.HasPendingResize);

        form.FirePendingResizeForTests();

        Assert.Equal(1, layouts);
        Assert.False(form.HasPendingResize);
    });

    [Fact]
    public void NoTimerIdlesAfterSettling() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out _);
        PumpSettled(form);

        Assert.False(FrameTimer(form).Enabled);
        Assert.False(form.HasPendingResize);
        Assert.False(form.HasPendingSingleClick);
    });

    [Fact]
    public void DisposeWithPendingResizeAndClickIsSafe() => StaRunner.Run(() =>
    {
        TreemapForm form = CreateForm(out _);
        PumpSettled(form);

        form.Size = new Size(form.Size.Width + 40, form.Size.Height + 20);
        form.Dispose();

        // Firing after disposal is a no-op rather than a callback on torn-down state.
        form.FirePendingResizeForTests();
        form.FirePendingClickForTests();
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(150));
    });

    [Fact]
    public void WideAndPortraitViewportsBothFillWithContent() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out _);

        form.Size = new Size(480, 120);
        form.FirePendingResizeForTests();
        PumpSettled(form);
        Assert.True(DistinctColours(form) > 2, "wide viewport did not fill with content");

        form.Size = new Size(140, 420);
        form.FirePendingResizeForTests();
        PumpSettled(form);
        Assert.True(DistinctColours(form) > 2, "portrait viewport did not fill with content");
    });

    // ---- helpers ----

    private static TreemapForm CreateForm(out SnapshotPublisher publisher)
    {
        publisher = new SnapshotPublisher(Trees.Folder(1, "empty"));
        publisher.Publish(Trees.WithMarker(42));

        TreemapForm form = new(publisher) { Size = new Size(320, 220) };
        _ = form.Handle;
        PumpSettled(form);
        return form;
    }

    private static void PumpSettled(TreemapForm form)
        => StaRunner.PumpUntil(() => !form.Controller.IsAnimating && !form.HasPendingResize);

    private static System.Windows.Forms.Timer FrameTimer(TreemapForm form)
    {
        FieldInfo field = typeof(TreemapForm).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(TreemapForm), "_timer");
        return (System.Windows.Forms.Timer)field.GetValue(form)!;
    }

    private static int DistinctColours(TreemapForm form)
    {
        StaRunner.PumpUntil(() => !form.Controller.IsAnimating);

        using Bitmap bitmap = new(Math.Max(1, form.Width), Math.Max(1, form.Height));
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

        HashSet<int> colours = [];
        for (int y = 0; y < bitmap.Height; y += 4)
        {
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                colours.Add(bitmap.GetPixel(x, y).ToArgb());
            }
        }

        return colours.Count;
    }
}
