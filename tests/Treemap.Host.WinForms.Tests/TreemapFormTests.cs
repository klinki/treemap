using System.Reflection;
using Treemap;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the WinForms host's contract with a producer: a background <c>Publish</c> is
/// enough to drive the view, bursts collapse into one relayout of the newest snapshot, a
/// resize does no layout work, and the frame timer exists only while a transition runs.
/// </summary>
public class TreemapFormTests
{
    /// <summary>Bound on any thread a test hands work to, so a stuck producer fails fast.</summary>
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void BackgroundPublishReachesTheFrameThroughTheMessageLoop() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        Assert.NotEmpty(form.Controller.CurrentFrame);
        Assert.False(HasNode(form, 42));

        Thread worker = new(() => publisher.Publish(Trees.WithMarker(42)));
        worker.Start();
        Assert.True(worker.Join(WorkerDeadline), "the publishing thread did not finish");

        // The frame is still the initial one: only the posted callback rebuilds it.
        StaRunner.PumpUntil(() => HasNode(form, 42));
    });

    [Fact]
    public void PublishBeforeTheHandleExistsIsNotLost() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Trees.Folder(1, "empty"));

        using TreemapForm form = new(publisher) { Size = new Size(240, 160) };
        Assert.False(form.IsHandleCreated);

        publisher.Publish(Trees.WithMarker(42));
        Assert.False(HasNode(form, 42));

        _ = form.Handle;
        StaRunner.PumpUntil(() => HasNode(form, 42));
    });

    [Fact]
    public void ABurstOfPublishesCoalescesIntoOneRelayoutOfTheNewestSnapshot() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        // Only publication-driven relayouts are counted. One of those starts a
        // transition, and the frame it leaves behind still has zero-alpha entering
        // cells; timer ticks come later, with the transition already under way. Counting
        // every Layout change instead would depend on how many 16 ms ticks the pump fits
        // in before the next check.
        int publicationRelayouts = 0;

        form.Controller.Changed += kind =>
        {
            if ((kind & ChangeKind.Layout) == 0)
            {
                return;
            }

            bool atTransitionStart = form.Controller.CurrentFrame.Any(cell => cell.IsEntering && cell.Alpha <= 0f);
            if (atTransitionStart)
            {
                publicationRelayouts++;
            }
        };

        const int Burst = 20;
        Thread worker = new(() =>
        {
            for (int i = 0; i < Burst; i++)
            {
                publisher.Publish(Trees.WithMarker(100 + i));
            }

            publisher.Publish(Trees.WithMarker(999));
        });

        worker.Start();
        Assert.True(worker.Join(WorkerDeadline), "the publishing thread did not finish");

        // The newest snapshot is in the frame. Its entering cells are still transparent
        // at t=0, which is fine: presence in the frame is the proof it was consumed.
        StaRunner.PumpUntil(() => HasNode(form, 999));

        Assert.Equal(1, publicationRelayouts);

        for (int i = 0; i < Burst; i++)
        {
            Assert.False(HasNode(form, 100 + i), $"snapshot {100 + i} was still on screen");
        }
    });

    [Fact]
    public void ARecreatedHandleStillDeliversAPendingPublication() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        publisher.Publish(Trees.WithMarker(42));
        Assert.False(HasNode(form, 42));

        // Cycle the handle while the publication is still waiting to be posted or
        // dispatched: whatever the old handle drops has to be re-posted by the new one.
        typeof(Control).GetMethod("DestroyHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Assert.False(form.IsHandleCreated);

        _ = form.Handle;
        StaRunner.PumpUntil(() => HasNode(form, 42));
    });

    [Fact]
    public void PublishingWhileTheUpdateCallbackRunsIsNotLost() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        // A Changed handler fires from inside the posted callback, so this publishes
        // exactly while the callback is resetting its own scheduling state.
        bool republished = false;
        form.Controller.Changed += _ =>
        {
            if (!republished)
            {
                republished = true;
                publisher.Publish(Trees.WithMarker(77));
            }
        };

        publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => HasNode(form, 77));

        // The second publication was consumed, so once the transition settles the frame
        // comes from its snapshot and the superseded one has faded out.
        StaRunner.PumpUntil(() => !form.Controller.IsAnimating);
        Assert.DoesNotContain(form.Controller.CurrentFrame, cell => cell.NodeId == 42 && cell.Alpha > 0.01f);
    });

    [Fact]
    public void ResizeRefitsTheCameraWithoutRelayoutingOrStoppingTheAnimation() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => form.Controller.IsAnimating);

        ChangeKind observed = ChangeKind.None;
        form.Controller.Changed += kind => observed |= kind;

        form.Size = new(form.Size.Width + 60, form.Size.Height + 40);

        Assert.True((observed & ChangeKind.Camera) != 0, $"resize reported {observed}");
        Assert.False((observed & ChangeKind.Layout) != 0, $"resize reported {observed}");
        Assert.True(form.Controller.IsAnimating, "resize interrupted the running transition");
    });

    [Fact]
    public void TheFrameTimerRunsOnlyWhileATransitionIsInFlight() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);
        System.Windows.Forms.Timer timer = TimerOf(form);

        Assert.False(timer.Enabled);

        publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => form.Controller.IsAnimating);
        Assert.True(timer.Enabled);

        StaRunner.PumpUntil(() => !form.Controller.IsAnimating);
        Assert.False(timer.Enabled);
    });

    [Fact]
    public void DisposalWithAPendingCallbackAndALaterPublishIsSafe() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Trees.Folder(1, "empty"));
        TreemapForm form = new(publisher) { Size = new Size(240, 160) };
        _ = form.Handle;

        Thread worker = new(() => publisher.Publish(Trees.WithMarker(42)));
        worker.Start();
        Assert.True(worker.Join(WorkerDeadline), "the publishing thread did not finish");

        // The posted callback is still queued when the view goes away.
        form.Dispose();
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        publisher.Publish(Trees.WithMarker(43));
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(2L, publisher.Current.Version);
        Assert.False(HasNode(form, 43));
    });

    [Fact]
    public void PublishingWhileTheViewIsBeingDisposedIsSafe() => StaRunner.Run(() =>
    {
        SnapshotPublisher publisher = new(Trees.Folder(1, "empty"));
        using ManualResetEventSlim notificationCaptured = new();
        using ManualResetEventSlim resumeNotification = new();

        // Pause an already captured multicast notification before it reaches the view.
        publisher.Published += () =>
        {
            notificationCaptured.Set();
            if (!resumeNotification.Wait(WorkerDeadline))
            {
                throw new TimeoutException("the disposal test did not release the notification");
            }
        };

        using TreemapForm form = new(publisher) { Size = new Size(240, 160) };
        _ = form.Handle;
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try { publisher.Publish(Trees.WithMarker(42)); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };

        worker.Start();
        try
        {
            Assert.True(notificationCaptured.Wait(WorkerDeadline), "notification was not captured");
            form.Dispose();
        }
        finally
        {
            resumeNotification.Set();
            Assert.True(worker.Join(WorkerDeadline), "the publishing thread did not finish");
        }

        Assert.Null(failure);
        Assert.Equal(1L, publisher.Current.Version);
        Assert.False(HasNode(form, 42));
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(50));
    });

    [Fact]
    public void PublishedTreeRendersToPixels() => StaRunner.Run(() =>
    {
        using TreemapForm form = CreateForm(out SnapshotPublisher publisher);

        publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => HasNode(form, 42));

        // Paint the settled frame: at t=0 the entering cells carry Alpha 0, so painting
        // before the transition ends would draw a nearly empty treemap.
        StaRunner.PumpUntil(() => !form.Controller.IsAnimating);

        using Bitmap bitmap = new(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

        HashSet<int> colours = [];
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                colours.Add(bitmap.GetPixel(x, y).ToArgb());
            }
        }

        Assert.True(colours.Count > 2, $"expected a painted treemap, got {colours.Count} distinct colours");
    });

    // ---- helpers ----

    private static TreemapForm CreateForm(out SnapshotPublisher publisher)
    {
        publisher = new SnapshotPublisher(Trees.Folder(1, "empty"));

        TreemapForm form = new(publisher) { Size = new Size(240, 160) };

        // Creates the window handle without showing anything, which is what BeginInvoke
        // needs as a posting target.
        _ = form.Handle;
        return form;
    }

    private static bool HasNode(TreemapForm form, int nodeId)
        => form.Controller.CurrentFrame.Any(cell => cell.NodeId == nodeId);

    /// <summary>
    /// Reads the private timer so the "only while animating" invariant can be asserted
    /// without adding production API that exists only for tests.
    /// </summary>
    private static System.Windows.Forms.Timer TimerOf(TreemapForm form)
    {
        FieldInfo field = typeof(TreemapForm).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(TreemapForm), "_timer");

        return (System.Windows.Forms.Timer)field.GetValue(form)!;
    }
}