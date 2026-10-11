using System.Diagnostics;
using System.Drawing.Imaging;
using SkiaSharp;
using Treemap;
using Treemap.Skia;

namespace Treemap.Host.WinForms;

/// <summary>
/// A self-contained treemap view: forwards WinForms input to a <see cref="TreemapController"/>,
/// lets <see cref="TreemapRenderer"/> rasterise a frame into an off-screen back buffer, and
/// blits that buffer once per paint.
/// </summary>
/// <remarks>
/// <para>
/// The host owns no layout or drawing policy of its own. Everything the libraries already
/// decide - squarified geometry, colours, label fitting, hit testing, animation timing - is
/// driven purely by the calls made from the <c>On*</c> overrides below.
/// </para>
///
/// <para>
/// Input coordinates are WinForms client coordinates, which is exactly the space
/// <see cref="TreemapController"/> expects: it converts screen to content space through the
/// camera before hit testing, and the same camera transform is applied to the canvas here (see
/// <see cref="ApplyCamera"/>). No translation is needed in either direction.
/// </para>
///
/// <para>
/// The back buffer is a GDI+ <see cref="Bitmap"/> locked for the duration of a frame, with the
/// <see cref="SKBitmap"/> bound directly over its pixels. Skia therefore rasterises straight
/// into the blit source: there is no intermediate copy and no SkiaSharp.Views dependency.
/// </para>
///
/// <para>
/// The view also subscribes to <see cref="SnapshotPublisher.Published"/>, so a producer only
/// has to call <c>Publish</c> from any background thread to drive the UI. Notifications are
/// coalesced into a single posted callback that always consumes the newest snapshot, and the
/// 16 ms timer runs only while a transition is in flight.
/// </para>
/// </remarks>
public sealed class TreemapForm : Control
{
    /// <summary>Backdrop behind the treemap, so untouched pixels match the window chrome.</summary>
    private static readonly SKColor Background = new(0x1E, 0x1E, 0x1E);

    /// <summary>The one pixel format used on both sides of the handoff.</summary>
    private const PixelFormat BackBufferFormat = PixelFormat.Format32bppPArgb;

    /// <summary>Pixels a drag may travel before it stops counting as a click.</summary>
    private const int ClickSlop = 3;

    private readonly TreemapController _controller;
    private readonly TreemapRenderer _renderer;
    private readonly RenderState _renderState;
    private readonly SnapshotPublisher _publisher;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private readonly System.Windows.Forms.Timer _clickTimer;
    private readonly ToolTip _toolTip = new()
    {
        AutoPopDelay = 10_000,
        InitialDelay = 400,
        ReshowDelay = 100,
    };

    private int _tooltipNodeId = -2;
    private long _tooltipVersion = -1;
    private TreemapSnapshot? _tooltipSnapshot;
    private string? _tooltipFilter;

    private Bitmap? _backBuffer;
    private SKBitmap? _bitmap;
    private SKImageInfo _bitmapInfo;

    private int _publishPending;
    private int _updateScheduled;

    // Deferred single-click expansion: MouseDown latches the hit id, MouseUp on a
    // clean release starts the double-click interval, and the timer fires the
    // expansion by id. A double-click navigates to the latched folder instead.
    // A blank press latches id -1 with the flag set, so releasing it clears the
    // prior selection and expansion.
    private bool _hasPendingClick;
    private int _pendingClickId = -1;
    private string? _pendingSource;
    private string? _pendingFilter;
    private int _pendingFocusRoot;

    // Read from the publishing thread as well as the UI thread.
    private volatile bool _disposed;
    private bool _initialized;
    private bool _dragging;
    private bool _movedWhileDown;
    private Point _dragOrigin;
    private Point _lastMouse;

    public TreemapForm(SnapshotPublisher publisher, Func<int, string>? nameResolver = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        _publisher = publisher;

        _controller = new TreemapController(publisher, new LayoutOptions
        {
            MinCellSize = 2f,
            InnerPadding = 1.5f,

            // Reference style: readable top-level layers with folder headers, and
            // the synthetic space blocks shown until the user hides them.
            MaxDepth = 4,
            ShowHeaders = true,
            ShowFreeSpace = true,
            ShowUnknownSpace = true,
        });

        _renderer = new TreemapRenderer(TreemapPalette.Default)
        {
            NameResolver = nameResolver,
        };

        _renderState = new RenderState
        {
            HighlightShadow = true,
            ShowLabels = true,
            MinFontSize = 6f,
            BaseFontSize = 12f,
            FontFamily = "Segoe UI",
        };

        // AllPaintingInWmPaint is deliberately NOT set: it suppresses the WM_PAINT -> OnPaint
        // dispatch that this control paints from. UserPaint + OptimizedDoubleBuffer gives the
        // same "we own every pixel" behaviour without losing the paint callback.
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Opaque |
            ControlStyles.Selectable,
            true);

        BackColor = Color.FromArgb(Background.Alpha, Background.Red, Background.Green, Background.Blue);
        TabStop = true;
        Cursor = Cursors.Hand;

        // Both subscriptions below can fire from the very next line on, so the timer has to
        // exist before either of them is attached.
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;

        // One-shot aspect-ratio relayout after a resize burst settles. The camera
        // refits immediately in SyncViewport; this only re-lays out once the user
        // stops dragging the window edge, so every pixel does not rebuild the tree.
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _resizeTimer.Tick += OnResizeTimerTick;

        // Deferred single click. Fires once when the double-click interval passes
        // without a second press; a double-click stops it and navigates instead.
        _clickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        _clickTimer.Tick += OnClickTimerTick;

        _controller.Changed += OnControllerChanged;
        _publisher.Published += OnPublished;

        SyncViewport();
        _controller.Relayout(animate: false);
        _initialized = true;
        SyncTimer();
    }

    /// <summary>The controller, exposed so a shell can drive navigation or re-publish snapshots.</summary>
    public TreemapController Controller => _controller;

    // ---- sizing ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // The style and property assignments in the constructor raise this, and the
        // constructor does the initial sizing itself once everything exists.
        if (!_initialized)
        {
            return;
        }

        // A resize refits the back buffer and camera immediately; the content
        // relayout for the settled aspect ratio follows once the burst stops
        // (see the one-shot timer below), so every pixel does not rebuild the
        // tree or restart a running transition.
        SyncViewport();
        RestartResizeTimer();
    }

    /// <summary>
    /// Restarts the one-shot aspect-ratio relayout. Only the last tick after the
    /// burst rebuilds the tree; every intermediate size just refits the camera.
    /// </summary>
    private void RestartResizeTimer()
    {
        if (_disposed)
        {
            return;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();

        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        // A resize moves every cell on screen, so a latched click location no
        // longer means what it did on MouseDown.
        CancelPendingClick();

        // The camera already refit on every OnResize; this catches the layout up
        // to the settled aspect ratio without animating.
        _controller.Relayout(animate: false);
    }

    /// <summary>
    /// Keeps the back buffer and the controller viewport in step with the control size.
    /// The camera refits whenever the size changes, so the treemap keeps filling the
    /// window without the caller having to ask for a relayout.
    /// </summary>
    private void SyncViewport()
    {
        EnsureBackBuffer();
        _controller.SetViewport(new RectF(0, 0, Width, Height));
        Invalidate();
    }

    private void EnsureBackBuffer()
    {
        int width = Math.Max(1, Width);
        int height = Math.Max(1, Height);

        if (_backBuffer is not null && _backBuffer.Width == width && _backBuffer.Height == height)
        {
            return;
        }

        Bitmap buffer = new(width, height, BackBufferFormat);

        // Bgra8888 on both sides keeps the Skia pixels byte-identical to the GDI+ DIB, so the
        // handoff is a straight copy with no swizzle and no channel reordering.
        SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        SKBitmap replacement = new(info);

        Bitmap? previousBuffer = _backBuffer;
        SKBitmap? previousBitmap = _bitmap;

        _backBuffer = buffer;
        _bitmap = replacement;
        _bitmapInfo = info;

        // The Skia view is a borrowed pointer into the GDI+ DIB, so it has to die first.
        previousBitmap?.Dispose();
        previousBuffer?.Dispose();
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_backBuffer is null || _bitmap is null)
        {
            e.Graphics.Clear(BackColor);
            return;
        }

        RenderFrame();
        e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
    }

    /// <summary>Rasterises one frame of the current animation state into the back buffer.</summary>
    private void RenderFrame()
    {
        Debug.Assert(_backBuffer is not null && _bitmap is not null);
        if (_backBuffer is null || _bitmap is null)
        {
            return;
        }

        _renderState.HoveredNodeId = _controller.HoveredNodeId;
        _renderState.SelectedNodeId = _controller.SelectedNodeId;

        BitmapData data = _backBuffer.LockBits(
            new Rectangle(0, 0, _backBuffer.Width, _backBuffer.Height),
            ImageLockMode.ReadWrite,
            BackBufferFormat);

        try
        {
            if (!_bitmap.InstallPixels(_bitmapInfo, data.Scan0, data.Stride))
            {
                throw new InvalidOperationException("Skia could not bind the treemap back buffer.");
            }

            using SKCanvas canvas = new(_bitmap);
            canvas.Clear(Background);

            // A filter that matches nothing keeps an empty 0-byte root projection.
            // The branches are exclusive: either the explicit message alone in
            // device coordinates, or the map as normal - never the message
            // painted over the root fill and labels.
            // Fixed text: the user pattern is unbounded and must not be painted
            // across the map.
            if (!string.IsNullOrEmpty(_controller.FilterPattern) && _controller.DisplayFileCount == 0)
            {
                using SKTypeface typeface = SKTypeface.FromFamilyName("Segoe UI");
                using SKFont font = new() { Typeface = typeface, Size = 14 };
                using SKPaint paint = new()
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };

                const string message = "No files match the filter.";
                float x = Math.Max(12, (Width - font.MeasureText(message, paint)) / 2);
                canvas.DrawText(message, x, Height / 2f, SKTextAlign.Left, font, paint);
            }
            else
            {
                ApplyCamera(canvas);
                _renderer.Draw(canvas, _controller.CurrentFrame, _renderState);
            }

            canvas.Flush();
        }
        finally
        {
            _backBuffer.UnlockBits(data);
        }
    }

    /// <summary>
    /// Applies the camera. Cell geometry draws in content coordinates; without this the
    /// treemap would always be a 1x1 unit square pinned to the top-left corner. Labels
    /// are mapped to device pixels inside the renderer, so they stay readable at any zoom.
    /// </summary>
    private void ApplyCamera(SKCanvas canvas)
    {
        TreemapCamera camera = _controller.Camera;

        // Skia post-multiplies, so Translate-then-Scale maps content to content*zoom + pan,
        // which is exactly TreemapCamera.ContentToScreen. The reverse order would also scale
        // the pan, making a drag move by dx*zoom on screen and breaking hit-test symmetry.
        canvas.Translate(camera.Pan.X, camera.Pan.Y);
        canvas.Scale(camera.Zoom);
    }

    // ---- render loop ----

    private void OnControllerChanged(ChangeKind kind)
    {
        SyncTimer();
        // A filter or layout change can alter the hovered folder's totals without
        // any mouse movement, so refresh the cached tooltip text here too.
        if ((kind & (ChangeKind.Layout | ChangeKind.Navigation | ChangeKind.Selection | ChangeKind.Hover)) != 0)
        {
            UpdateTooltip();
        }

        Invalidate();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool repaint = _controller.Advance();

        // Advance reports the settling frame too, so repaint first and only then decide
        // whether the timer still has a reason to exist.
        if (repaint)
        {
            Invalidate();
        }

        SyncTimer();
    }

    /// <summary>
    /// Runs the timer only while a transition is in flight. An idle timer would wake the
    /// UI 60 times a second to do nothing.
    /// </summary>
    private void SyncTimer()
    {
        if (_disposed)
        {
            return;
        }

        if (_controller.IsAnimating)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    // ---- publication ----

    /// <summary>
    /// Runs on whichever thread called <c>Publish</c>. It only sets a flag and posts one
    /// callback; no UI is touched here.
    /// </summary>
    private void OnPublished()
    {
        Volatile.Write(ref _publishPending, 1);
        ScheduleSnapshotUpdate();
    }

    /// <summary>
    /// Posts a single callback for a burst of publishes, so a scanner publishing a hundred
    /// snapshots between two paints causes one relayout, not a hundred.
    /// </summary>
    private void ScheduleSnapshotUpdate()
    {
        if (_disposed || !IsHandleCreated || Volatile.Read(ref _publishPending) == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _updateScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(ApplyPendingSnapshot));
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _updateScheduled, 0);
        }
        catch (InvalidOperationException) when (_disposed || !IsHandleCreated)
        {
            // The handle went away between the check above and the post. OnHandleCreated
            // will repost once it exists again. Any other invalid-operation is a real
            // fault and is left to propagate.
            Interlocked.Exchange(ref _updateScheduled, 0);
        }
    }

    /// <summary>
    /// Consumes the newest snapshot. Notifications may arrive out of version order, so the
    /// frame is read from the publisher rather than from the notification itself.
    /// </summary>
    private void ApplyPendingSnapshot()
    {
        // Clear the scheduled flag first: a publish arriving from here on re-arms both
        // flags and posts its own callback, which is what keeps it from being lost.
        Interlocked.Exchange(ref _updateScheduled, 0);

        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        // Handle recreation can resurrect a post for a notification that was already
        // consumed. Relayouting on that would restart any transition in flight.
        if (Interlocked.Exchange(ref _publishPending, 0) == 0)
        {
            return;
        }

        // The relayout below already lays out at the settled size, so a queued
        // aspect-ratio tick would only rebuild the same tree twice.
        _resizeTimer.Stop();

        // While a transition is still running, settle onto the newest snapshot
        // without starting another tween: a live scan publishing every few hundred
        // milliseconds would otherwise restart the animation on every preview and
        // keep the frame timer awake for the whole scan.
        _controller.Relayout(animate: !_controller.IsAnimating);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Covers a publish that arrived before the handle existed, and one whose callback
        // was dropped when an earlier handle was destroyed.
        ScheduleSnapshotUpdate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // A post that has not been dispatched dies with the handle, so hand the schedule
        // flag back rather than leaving publications permanently unconsumed.
        Interlocked.Exchange(ref _updateScheduled, 0);
        base.OnHandleDestroyed(e);
    }

    // ---- pointer ----

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);

        // Without focus the control never sees WM_MOUSEWHEEL or key messages, so hovering
        // is the only chance to opt in before the user clicks.
        Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        switch (e.Button)
        {
            case MouseButtons.Left:
                // The press latches the hit id from the current frame but starts
                // no timer while the button is held: holding past the
                // double-click interval must not select or expand anything.
                // A fresh single press (Clicks == 1) on a different cell drops
                // the first latch and keeps the latest hit; the second press of
                // a double-click arrives with Clicks >= 2 and must preserve the
                // first latch even when a same-source preview moved a different
                // node underneath the same coordinates in between.
                if (_hasPendingClick)
                {
                    // A new press supersedes the deferred interval: a clean
                    // release restarts it, and a double-click cancels it below.
                    // Stopping here keeps the first timer from firing between
                    // the second press and its double-click.
                    _clickTimer.Stop();
                    if (e.Clicks < 2)
                    {
                        int secondHit = HitTestAt(e.X, e.Y);
                        if (secondHit != _pendingClickId)
                        {
                            CancelPendingClick();
                            LatchPendingClick(e.X, e.Y);
                        }
                    }
                }
                else
                {
                    LatchPendingClick(e.X, e.Y);
                }

                _dragging = true;
                _movedWhileDown = false;
                _dragOrigin = e.Location;
                _lastMouse = e.Location;
                Capture = true;
                break;

            case MouseButtons.Middle:
                GoToRoot();
                break;

            case MouseButtons.Right:
                GoToParent();
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            int dx = e.X - _lastMouse.X;
            int dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;

            if (Math.Abs(e.X - _dragOrigin.X) > ClickSlop || Math.Abs(e.Y - _dragOrigin.Y) > ClickSlop)
            {
                _movedWhileDown = true;

                // A drag is a pan, not a click: drop the latched press so the
                // release does not expand anything.
                CancelPendingClick();
            }

            if (dx != 0 || dy != 0)
            {
                _controller.DragBy(dx, dy);
            }
        }
        else
        {
            _controller.PointerMoved(e.X, e.Y);
            UpdateTooltip();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        _controller.PointerMoved(-1, -1);
        UpdateTooltip();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Capture = false;

            // A drag ending elsewhere selects nothing; its latch was already
            // dropped on move. A clean release starts the deferred-click
            // interval - selection happens when it fires, not here, so a
            // double-click can still navigate first.
            if (!_movedWhileDown && _hasPendingClick && !_disposed)
            {
                _clickTimer.Stop();
                _clickTimer.Start();
            }
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Navigate to the folder the FIRST press latched, not the cursor's
        // current cell: the first press has not expanded anything yet, so the
        // layout - and the latched id - are still the ones the user aimed at.
        // The second press on the same cell kept the first latch; cancel the
        // deferred timer so the trailing release selects nothing extra.
        if (!_hasPendingClick)
        {
            return;
        }

        int latchedId = _pendingClickId;
        string? latchedSource = _pendingSource;
        string? latchedFilter = _pendingFilter;
        int latchedFocusRoot = _pendingFocusRoot;
        CancelPendingClick();

        // The second MouseDown already re-armed the drag state; drop it so the trailing
        // MouseUp does not also fire a selection.
        _dragging = false;
        Capture = false;
        _movedWhileDown = true;

        if (latchedId >= 0 && IsPendingValid(latchedId, latchedSource, latchedFilter, latchedFocusRoot))
        {
            if (_controller.FocusNode(latchedId))
            {
                _resizeTimer.Stop();
                _controller.Relayout(animate: true);
            }
        }
    }

    /// <summary>
    /// Hit-tests in content space against the frame on screen, so the latched id
    /// is the cell the user actually aimed at.
    /// </summary>
    private int HitTestAt(float x, float y)
    {
        PointF content = _controller.Camera.ScreenToContent(new PointF(x, y));
        return HitTester.HitTestFrameContent(_controller.CurrentFrame, content.X, content.Y);
    }

    private void LatchPendingClick(float x, float y)
    {
        _hasPendingClick = true;
        _pendingClickId = HitTestAt(x, y);
        _pendingSource = _publisher.Current.RootPath;
        _pendingFilter = _controller.FilterPattern;
        _pendingFocusRoot = _controller.FocusRootNodeId;
    }

    private void CancelPendingClick()
    {
        _clickTimer.Stop();
        _hasPendingClick = false;
        _pendingClickId = -1;
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        FirePendingClick();
    }

    private void FirePendingClick()
    {
        // Never while the button is still held: the timer only starts on a
        // clean release, and a test hook firing early must not select either.
        if (!_hasPendingClick || _dragging)
        {
            return;
        }

        int id = _pendingClickId;
        string? source = _pendingSource;
        string? filter = _pendingFilter;
        int focusRoot = _pendingFocusRoot;
        CancelPendingClick();

        if (_disposed)
        {
            return;
        }

        // A new scan source, filter or navigation since MouseDown means the
        // latched id may name a different node or nothing at all; drop it
        // rather than expanding something the user never clicked. Same-source
        // previews keep stable ids, so the latch survives those as long as the
        // node still exists in the current display projection - geometry may
        // have moved meanwhile, but the id still names the same folder.
        if (!IsPendingValid(id, source, filter, focusRoot))
        {
            return;
        }

        // Fires by latched id, never by stale coordinates: selects the node
        // (files and blank clicks clear the prior expansion) and temporarily
        // expands a folder in place. The root, history and camera stay put.
        // A queued aspect-ratio tick would rebuild the same tree twice, but
        // only when the click actually changed the expansion and relayouts.
        // A leaf or blank click leaves the layout alone, so a pending resize
        // must stay alive to catch the layout up to the new aspect.
        if (_controller.ExpandNodeTemporarily(id))
        {
            _resizeTimer.Stop();
            _controller.Relayout(animate: true);
        }
    }

    private bool IsPendingValid(int id, string? source, string? filter, int focusRoot)
    {
        if (_disposed)
        {
            return false;
        }

        if (!SourcesMatch(_publisher.Current.RootPath, source))
        {
            return false;
        }

        if (!string.Equals(_controller.FilterPattern, filter, StringComparison.Ordinal))
        {
            return false;
        }

        if (_controller.FocusRootNodeId != focusRoot)
        {
            return false;
        }

        // A blank press carries no node; it clears the prior selection and
        // expansion once the source, filter and focus still match.
        if (id < 0)
        {
            return true;
        }

        return _controller.DisplaySnapshot.TryGetNode(id, out _);
    }

    private static bool SourcesMatch(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Same identity rule as the controller: case-insensitive on Windows,
        // case-sensitive everywhere else.
        return OperatingSystem.IsWindows()
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Focus();

        int notches = Math.Sign(e.Delta);
        if (notches != 0)
        {
            _controller.WheelZoom(notches, e.X, e.Y);
        }
    }

    // ---- keyboard ----

    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Back or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.KeyCode)
        {
            // Back steps through navigation history; the actual parent stays on
            // right-click, the Up key and the toolbar.
            case Keys.Back:
            case Keys.Left:
                e.Handled = GoBack();
                break;

            case Keys.Right:
                e.Handled = GoForward();
                break;

            case Keys.Up:
                e.Handled = GoToParent();
                break;

            case Keys.Home:
                GoToRoot();
                e.Handled = true;
                break;

            case Keys.Add:
                if (_controller.ZoomIntoHovered())
                {
                    _controller.Relayout(animate: true);
                    e.Handled = true;
                }

                break;
        }
    }

    /// <summary>
    /// Refreshes the hover tooltip from the display projection only: path plus
    /// logical/allocated size, with no filesystem I/O. Rebuilt only when the
    /// hovered node, the publisher version, the display projection reference or
    /// the filter changes - applying or clearing a filter alters folder totals
    /// without publishing anything new.
    /// </summary>
    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        int hovered = _controller.HoveredNodeId;
        long version = _publisher.Current.Version;
        TreemapSnapshot display = _controller.DisplaySnapshot;
        string filter = _controller.FilterPattern;
        if (hovered == _tooltipNodeId
            && version == _tooltipVersion
            && ReferenceEquals(display, _tooltipSnapshot)
            && string.Equals(filter, _tooltipFilter, StringComparison.Ordinal))
        {
            return;
        }

        _tooltipNodeId = hovered;
        _tooltipVersion = version;
        _tooltipSnapshot = display;
        _tooltipFilter = filter;
        _toolTip.SetToolTip(this, hovered >= 0 ? _controller.GetTooltipText(hovered) : string.Empty);
    }

    private bool GoToParent()
    {
        if (!_controller.GoToParent())
        {
            return false;
        }

        _controller.Relayout(animate: true);
        return true;
    }

    private bool GoBack()
    {
        if (!_controller.GoBack())
        {
            return false;
        }

        _controller.Relayout(animate: true);
        return true;
    }

    private bool GoForward()
    {
        if (!_controller.GoForward())
        {
            return false;
        }

        _controller.Relayout(animate: true);
        return true;
    }

    private void GoToRoot()
    {
        if (_controller.GoToRoot())
        {
            _controller.Relayout(animate: false);
        }
    }

    // ---- teardown ----

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _publisher.Published -= OnPublished;
            _controller.Changed -= OnControllerChanged;

            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _resizeTimer.Stop();
            _resizeTimer.Tick -= OnResizeTimerTick;
            _resizeTimer.Dispose();
            _clickTimer.Stop();
            _clickTimer.Tick -= OnClickTimerTick;
            _clickTimer.Dispose();
            _renderer.Dispose();
            _toolTip.Dispose();

            _bitmap?.Dispose();
            _bitmap = null;
            _backBuffer?.Dispose();
            _backBuffer = null;
        }

        base.Dispose(disposing);
    }

    // ---- state the tests read ----

    internal int PendingClickId => _pendingClickId;

    internal bool HasPendingSingleClick => _hasPendingClick;

    internal bool HasPendingResize => _resizeTimer.Enabled;

    internal bool IsClickTimerRunningForTests => _clickTimer.Enabled;

    internal string TooltipTextForTests => _toolTip.GetToolTip(this) ?? string.Empty;

    /// <summary>
    /// Fires the deferred single click synchronously, so tests do not wait out
    /// the real double-click interval. No-op while the button is still held.
    /// </summary>
    internal void FirePendingClickForTests() => FirePendingClick();

    /// <summary>
    /// Fires the coalesced aspect-ratio relayout synchronously.
    /// </summary>
    internal void FirePendingResizeForTests() => OnResizeTimerTick(this, EventArgs.Empty);
}
