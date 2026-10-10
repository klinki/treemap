namespace Treemap;

/// <summary>What changed since the last poll, so the host knows whether to repaint.</summary>
[Flags]
public enum ChangeKind
{
    None = 0,
    Layout = 1 << 0,
    Camera = 1 << 1,
    Hover = 1 << 2,
    Selection = 1 << 3,
    Navigation = 1 << 4,
    All = Layout | Camera | Hover | Selection | Navigation,
}

/// <summary>
/// Framework-agnostic interaction state for the treemap view.
/// </summary>
/// <remarks>
/// <para>
/// This is the layer that makes stage 4 testable without a window. Input arrives as
/// plain method calls (<see cref="PointerMoved"/>, <see cref="WheelZoom"/>, …), the
/// controller updates camera, hover and selection, and the host simply asks whether
/// anything changed before redrawing.
/// </para>
///
/// <para>
/// It deliberately knows nothing about any UI toolkit. The WinForms shell will be a
/// few hundred lines that forwards events and blits the resulting bitmap.
/// </para>
/// </remarks>
public sealed class TreemapController
{
    private readonly SnapshotPublisher _publisher;
    private readonly TreemapCamera _camera = new();
    private readonly TreemapAnimator _animator;
    private readonly List<NavigationEntry> _history = [];

    private LayoutResult _layout = LayoutResult.Empty;
    private RectF _viewport;
    private LayoutOptions _options = LayoutOptions.Default;
    private int _hoveredNodeId = -1;
    private int _selectedNodeId = -1;
    private int _focusRootNodeId = -1;
    private ChangeKind _pending = ChangeKind.All;

    public TreemapController(
        SnapshotPublisher publisher, LayoutOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        _publisher = publisher;
        _options = options ?? LayoutOptions.Default;
        _animator = new TreemapAnimator(timeProvider: timeProvider);
        _focusRootNodeId = publisher.Current.Root.Id;
        _camera.FitToContent(ContentBounds, _viewport);
    }

    /// <summary>
    /// Raised whenever state changes, so a host can schedule a repaint.
    /// Hosts that prefer polling can ignore this and call <see cref="ConsumeChanges"/>.
    /// </summary>
    public event Action<ChangeKind>? Changed;

    private void Raise(ChangeKind kind)
    {
        _pending |= kind;
        Changed?.Invoke(kind);
    }

    public TreemapCamera Camera => _camera;

    public int HoveredNodeId => _hoveredNodeId;

    public int SelectedNodeId => _selectedNodeId;

    public bool IsAnimating => _animator.IsAnimating;

    /// <summary>Current viewport in screen pixels.</summary>
    public RectF Viewport
    {
        get => _viewport;
        private set => _viewport = value;
    }

    /// <summary>
    /// The rect the treemap is laid out into, in content coordinates. This is a
    /// fixed reference size (not the viewport), so the camera's zoom of 1.0 means
    /// "fitted to the window" regardless of window size. Zoom values therefore stay
    /// in a human-sensible range near 1.0 instead of being scaled by viewport pixels.
    /// </summary>
    private static RectF ContentBounds => new(0, 0, ReferenceSize, ReferenceSize);

    /// <summary>Reference layout size in content units.</summary>
    private const float ReferenceSize = 1000f;

    /// <summary>
    /// Sets the viewport size. The camera refits whenever the size changes, so the
    /// treemap always fills the window. Call <see cref="Relayout"/> afterwards if
    /// the caller also wants the content re-laid out.
    /// </summary>
    public void SetViewport(in RectF viewport)
    {
        if (_viewport.Equals(viewport))
        {
            return;
        }

        Viewport = viewport;

        if (!viewport.IsEmpty)
        {
            _camera.FitToContent(ContentBounds, viewport);
            Raise(ChangeKind.Camera);
        }
    }

    /// <summary>
    /// Re-lays out the current snapshot. Called after a publish or option change.
    /// The focused subtree is used as the layout root, so drilling into a folder
    /// re-lays out that folder's contents across the whole content rect.
    /// </summary>
    public void Relayout(bool animate = true)
    {
        TreemapSnapshot snapshot = _publisher.Current;
        TreemapNode root = snapshot.TryGetNode(_focusRootNodeId, out TreemapNode focused)
            ? focused
            : snapshot.Root;

        LayoutResult next = SquarifyLayout.Compute(root, ContentBounds, _options);

        if (animate && !_layout.IsEmpty)
        {
            _animator.Begin(new LayoutTween(_layout, next));
        }
        else
        {
            _animator.Snap(next);
        }

        _layout = next;
        Raise(ChangeKind.Layout);
    }

    /// <summary>The current animated frame, ready to render.</summary>
    public IReadOnlyList<TweenedCell> CurrentFrame => _animator.CurrentFrame;

    /// <summary>
    /// Advances any running animation. Returns true if a repaint is needed, which
    /// includes the frame that settles the transition.
    /// </summary>
    public bool Advance()
    {
        bool repaint = _animator.Advance();
        if (repaint)
        {
            Raise(ChangeKind.Layout);
        }

        return repaint;
    }

    // ---- input ----

    /// <summary>
    /// Handles pointer movement in screen coordinates. Hover resolves against the
    /// frame currently on screen, not the layout the transition is heading for, so
    /// the highlight tracks the cells the user can actually see.
    /// </summary>
    public void PointerMoved(float x, float y)
    {
        int hit = HitAt(x, y);

        if (hit != _hoveredNodeId)
        {
            _hoveredNodeId = hit;
            Raise(ChangeKind.Hover);
        }
    }

    /// <summary>Handles a left click: select, or drill into a folder on double-click.</summary>
    public void Click(float x, float y)
    {
        int hit = HitAt(x, y);

        if (hit != _selectedNodeId)
        {
            _selectedNodeId = hit;
            Raise(ChangeKind.Selection);
        }
    }

    private int HitAt(float screenX, float screenY)
    {
        PointF content = _camera.ScreenToContent(new PointF(screenX, screenY));
        return HitTester.HitTestFrameContent(_animator.CurrentFrame, content.X, content.Y);
    }

    /// <summary>Zooms by a wheel notch, anchored at the cursor.</summary>
    public void WheelZoom(float notches, float x, float y)
    {
        // Each notch is a fixed ratio so trackpads and wheels feel consistent.
        const float perNotch = 1.15f;
        float factor = MathF.Pow(perNotch, notches);

        _camera.ZoomAt(factor, new PointF(x, y));
        Raise(ChangeKind.Camera);
    }

    /// <summary>Pans by a screen-space delta (mouse drag).</summary>
    public void DragBy(float dx, float dy)
    {
        _camera.PanBy(dx, dy);
        Raise(ChangeKind.Camera);
    }

    /// <summary>Navigates into the hovered folder and fills the viewport with it.</summary>
    public bool ZoomIntoHovered()
    {
        if (_hoveredNodeId < 0)
        {
            return false;
        }

        TreemapSnapshot snapshot = _publisher.Current;
        if (!snapshot.TryGetNode(_hoveredNodeId, out TreemapNode node) || node.Children.Count == 0)
        {
            return false;
        }

        _history.Add(new NavigationEntry(_focusRootNodeId, _camera.Capture()));
        _focusRootNodeId = _hoveredNodeId;
        Raise(ChangeKind.Navigation);
        return true;
    }

    /// <summary>Returns to the parent of the current focus root.</summary>
    public bool GoToParent()
    {
        if (_focusRootNodeId < 0)
        {
            return false;
        }

        TreemapSnapshot snapshot = _publisher.Current;
        if (!snapshot.TryGetNode(_focusRootNodeId, out TreemapNode node))
        {
            return false;
        }

        // The parent of the focused node, within the current focus subtree.
        int parent = FindParentInLayout(_focusRootNodeId);
        if (parent < 0 && _history.Count == 0)
        {
            return false;
        }

        _focusRootNodeId = parent >= 0 ? parent : _history[^1].FocusRootNodeId;
        if (parent < 0 && _history.Count > 0)
        {
            _camera.Apply(_history[^1].Camera);
            _history.RemoveAt(_history.Count - 1);
        }

        Raise(ChangeKind.Navigation);
        return true;
    }

    /// <summary>Resets the view to the whole drive.</summary>
    public void GoToRoot()
    {
        TreemapSnapshot snapshot = _publisher.Current;
        _focusRootNodeId = snapshot.Root.Id;
        _history.Clear();
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport);
        Raise(ChangeKind.Navigation);
    }

    /// <summary>Returns and clears accumulated changes since the last call.</summary>
    public ChangeKind ConsumeChanges()
    {
        ChangeKind result = _pending;
        _pending = ChangeKind.None;
        return result;
    }

    private int FindParentInLayout(int nodeId)
    {
        if (!_layout.TryGetIndex(nodeId, out int index))
        {
            return -1;
        }

        int parentIndex = _layout.ParentIndices[index];
        return parentIndex >= 0 ? _layout.NodeIds[parentIndex] : -1;
    }

    private readonly record struct NavigationEntry(int FocusRootNodeId, CameraTransform Camera);
}
