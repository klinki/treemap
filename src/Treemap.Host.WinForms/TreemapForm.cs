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
    private readonly System.Windows.Forms.Timer _timer;

    private Bitmap? _backBuffer;
    private SKBitmap? _bitmap;
    private SKImageInfo _bitmapInfo;

    private bool _disposed;
    private bool _dragging;
    private bool _movedWhileDown;
    private Point _dragOrigin;
    private Point _lastMouse;

    public TreemapForm(SnapshotPublisher publisher, Func<int, string>? nameResolver = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        _controller = new TreemapController(publisher, new LayoutOptions
        {
            MinCellSize = 2f,
            InnerPadding = 1.5f,
            MaxDepth = 48,
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

        _controller.Changed += OnControllerChanged;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;

        EnsureBackBuffer();
        SyncViewport(animate: false);
        _timer.Start();
    }

    /// <summary>The controller, exposed so a shell can drive navigation or re-publish snapshots.</summary>
    public TreemapController Controller => _controller;

    // ---- sizing ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncViewport(animate: false);
    }

    /// <summary>
    /// Keeps the back buffer, the controller viewport and the layout in step. Resizing re-fits
    /// the camera once (the controller only fits when the viewport was previously unset) and
    /// relayouts without animation, because a resize is not a transition the user asked for.
    /// </summary>
    private void SyncViewport(bool animate)
    {
        EnsureBackBuffer();
        _controller.SetViewport(new RectF(0, 0, Width, Height));
        _controller.Relayout(animate);
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
            ApplyCamera(canvas);
            _renderer.Draw(canvas, _controller.CurrentFrame, _renderState);
            canvas.Flush();
        }
        finally
        {
            _backBuffer.UnlockBits(data);
        }
    }

    /// <summary>
    /// Applies the camera. The renderer draws in content coordinates only; without this the
    /// treemap would always be a 1x1 unit square pinned to the top-left corner.
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

    private void OnControllerChanged(ChangeKind kind) => Invalidate();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_controller.Advance())
        {
            Invalidate();
        }
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
            }

            if (dx != 0 || dy != 0)
            {
                _controller.DragBy(dx, dy);
            }
        }
        else
        {
            _controller.PointerMoved(e.X, e.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Capture = false;

            // A drag that ended over a different cell must not also select it.
            if (!_movedWhileDown)
            {
                _controller.Click(e.X, e.Y);
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

        // The second MouseDown already re-armed the drag state; drop it so the trailing
        // MouseUp does not also fire a selection.
        _dragging = false;
        Capture = false;

        if (_controller.ZoomIntoHovered())
        {
            _controller.Relayout(animate: true);
        }
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
            case Keys.Back:
            case Keys.Left:
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

    private bool GoToParent()
    {
        if (!_controller.GoToParent())
        {
            return false;
        }

        _controller.Relayout(animate: true);
        return true;
    }

    private void GoToRoot()
    {
        _controller.GoToRoot();
        _controller.Relayout(animate: false);
    }

    // ---- teardown ----

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();

            _controller.Changed -= OnControllerChanged;
            _renderer.Dispose();

            _bitmap?.Dispose();
            _bitmap = null;
            _backBuffer?.Dispose();
            _backBuffer = null;
        }

        base.Dispose(disposing);
    }
}
