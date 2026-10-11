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
    /// <summary>Shallowest visible descendant depth the detail controls allow.</summary>
    public const int MinDetailDepth = 1;

    /// <summary>Deepest visible descendant depth the detail controls allow.</summary>
    public const int MaxDetailDepth = 64;

    /// <summary>Bound on retained navigation entries, so previews cannot grow history without limit.</summary>
    private const int MaxHistoryEntries = 100;

    private readonly SnapshotPublisher _publisher;
    private readonly TreemapCamera _camera = new();
    private readonly TreemapAnimator _animator;
    private readonly List<NavigationEntry> _backStack = [];
    private readonly List<NavigationEntry> _forwardStack = [];

    private LayoutResult _layout = LayoutResult.Empty;
    private RectF _viewport;
    private LayoutOptions _options = LayoutOptions.Default;
    private int _hoveredNodeId = -1;
    private int _selectedNodeId = -1;
    private int _focusRootNodeId = -1;
    private string? _focusSource;
    private ChangeKind _pending = ChangeKind.All;

    private int _tooltipNodeId = -1;
    private long _tooltipVersion = -1;
    private string? _tooltipText;

    public TreemapController(
        SnapshotPublisher publisher, LayoutOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        _publisher = publisher;
        _options = options ?? LayoutOptions.Default;
        _animator = new TreemapAnimator(timeProvider: timeProvider);
        _focusRootNodeId = publisher.Current.Root.Id;
        _focusSource = publisher.Current.RootPath;
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

    /// <summary>Node id the current layout is rooted at. Drilling changes it.</summary>
    public int FocusRootNodeId => _focusRootNodeId;

    /// <summary>Current layout options. Detail depth and space filters live here.</summary>
    public LayoutOptions Options => _options;

    /// <summary>Visible descendant depth, carried by <see cref="LayoutOptions.MaxDepth"/>.</summary>
    public int DetailDepth => _options.MaxDepth;

    public bool CanIncreaseDetail => _options.MaxDepth < MaxDetailDepth;

    public bool CanDecreaseDetail => _options.MaxDepth > MinDetailDepth;

    /// <summary>Whether the free-space block takes part in layout. The snapshot is untouched.</summary>
    public bool ShowFreeSpace => _options.ShowFreeSpace;

    /// <summary>Whether the unaccounted-space block takes part in layout. The snapshot is untouched.</summary>
    public bool ShowUnknownSpace => _options.ShowUnknownSpace;

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public bool CanGoHome
    {
        get
        {
            if (_forwardStack.Count > 0)
            {
                return true;
            }

            if (_focusRootNodeId != _publisher.Current.Root.Id)
            {
                return true;
            }

            return IsCameraOffFit();
        }
    }

    /// <summary>Whether the focused node has a true parent in the whole snapshot.</summary>
    public bool CanGoUp
    {
        get
        {
            TreemapSnapshot snapshot = _publisher.Current;
            return snapshot.TryGetNode(_focusRootNodeId, out _)
                && snapshot.TryGetParent(_focusRootNodeId, out _);
        }
    }

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
    /// A changed source <see cref="TreemapSnapshot.RootPath"/> resets
    /// focus/selection/hover/camera/history and the tooltip cache, so a new scan
    /// never lands on a coincidentally reused id; same-source previews keep focus.
    /// Otherwise stale history entries are pruned, and a focus that no longer
    /// exists resets to the snapshot root.
    /// </summary>
    public void Relayout(bool animate = true)
    {
        TreemapSnapshot snapshot = _publisher.Current;

        if (!SourcesMatch(_focusSource, snapshot.RootPath))
        {
            _focusSource = snapshot.RootPath;
            _focusRootNodeId = snapshot.Root.Id;
            _backStack.Clear();
            _forwardStack.Clear();
            _hoveredNodeId = -1;
            _selectedNodeId = -1;
            _tooltipNodeId = -1;
            _tooltipVersion = -1;
            _tooltipText = null;
            _camera.Reset();
            _camera.FitToContent(ContentBounds, _viewport);
            Raise(ChangeKind.Navigation | ChangeKind.Camera | ChangeKind.Hover | ChangeKind.Selection);
        }
        else
        {
            PruneHistory(snapshot);

            if (!snapshot.TryGetNode(_focusRootNodeId, out _))
            {
                _focusRootNodeId = snapshot.Root.Id;
                _backStack.Clear();
                _forwardStack.Clear();
                _camera.Reset();
                _camera.FitToContent(ContentBounds, _viewport);
            }
        }

        snapshot.TryGetNode(_focusRootNodeId, out TreemapNode? focusNode);
        focusNode ??= snapshot.Root;

        LayoutResult next = SquarifyLayout.Compute(focusNode, ContentBounds, _options);

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
    public bool ZoomIntoHovered() => _hoveredNodeId >= 0 && FocusNode(_hoveredNodeId);

    /// <summary>
    /// Focuses <paramref name="nodeId"/>, pushing the current view onto the back
    /// stack and clearing forward history. Only nodes with children can be focused;
    /// focusing the already-focused node is a no-op. The newly focused subtree is
    /// fitted to the viewport; the previous camera is kept in history.
    /// </summary>
    public bool FocusNode(int nodeId)
    {
        if (nodeId == _focusRootNodeId)
        {
            return false;
        }

        TreemapSnapshot snapshot = _publisher.Current;
        if (!snapshot.TryGetNode(nodeId, out TreemapNode node) || node.Children.Count == 0)
        {
            return false;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = nodeId;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport);
        Raise(ChangeKind.Navigation | ChangeKind.Camera);
        return true;
    }

    /// <summary>
    /// Moves one level to the true parent resolved from the whole snapshot, not
    /// from the current focused layout or the history stack. Branches: forward
    /// history is cleared. The parent subtree is fitted to the viewport.
    /// </summary>
    public bool GoToParent()
    {
        TreemapSnapshot snapshot = _publisher.Current;
        if (!snapshot.TryGetNode(_focusRootNodeId, out _)
            || !snapshot.TryGetParent(_focusRootNodeId, out int parentId))
        {
            return false;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = parentId;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport);
        Raise(ChangeKind.Navigation | ChangeKind.Camera);
        return true;
    }

    /// <summary>Steps back through navigation history, restoring focus and camera.</summary>
    public bool GoBack()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        _forwardStack.Add(new NavigationEntry(_focusRootNodeId, _camera.Capture()));
        NavigationEntry entry = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);

        _focusRootNodeId = entry.FocusRootNodeId;
        _camera.Apply(entry.Camera);
        Raise(ChangeKind.Navigation);
        return true;
    }

    /// <summary>Steps forward through navigation history, restoring focus and camera.</summary>
    public bool GoForward()
    {
        if (_forwardStack.Count == 0)
        {
            return false;
        }

        _backStack.Add(new NavigationEntry(_focusRootNodeId, _camera.Capture()));
        NavigationEntry entry = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);

        _focusRootNodeId = entry.FocusRootNodeId;
        _camera.Apply(entry.Camera);
        Raise(ChangeKind.Navigation);
        return true;
    }

    /// <summary>
    /// Resets the view to the whole drive, refitting the camera even when already
    /// at the root. Branches: forward history is cleared. Returns false when
    /// already home with a fitted camera and empty forward history.
    /// </summary>
    public bool GoToRoot()
    {
        TreemapSnapshot snapshot = _publisher.Current;
        int rootId = snapshot.Root.Id;

        if (_focusRootNodeId == rootId)
        {
            bool fitted = !IsCameraOffFit();
            if (_forwardStack.Count == 0 && fitted)
            {
                return false;
            }

            _forwardStack.Clear();
            _camera.Reset();
            _camera.FitToContent(ContentBounds, _viewport);
            Raise(ChangeKind.Navigation | ChangeKind.Camera);
            return true;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = rootId;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport);
        Raise(ChangeKind.Navigation | ChangeKind.Camera);
        return true;
    }

    /// <summary>
    /// Shows one more descendant level without touching camera zoom or rescanning.
    /// </summary>
    public bool IncreaseDetail()
    {
        if (!CanIncreaseDetail)
        {
            return false;
        }

        _options = _options.With(maxDepth: _options.MaxDepth + 1);
        Raise(ChangeKind.Layout);
        return true;
    }

    /// <summary>Shows one fewer descendant level without touching camera zoom.</summary>
    public bool DecreaseDetail()
    {
        if (!CanDecreaseDetail)
        {
            return false;
        }

        _options = _options.With(maxDepth: _options.MaxDepth - 1);
        Raise(ChangeKind.Layout);
        return true;
    }

    /// <summary>Replaces the layout options. The caller relayouts afterwards.</summary>
    public void SetLayoutOptions(LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        Raise(ChangeKind.Layout);
    }

    /// <summary>
    /// Layout-only visibility for the synthetic space blocks. Hiding them reflows
    /// the map; the snapshot, scan counts and totals are preserved. The caller
    /// relayouts afterwards; nothing here rescans.
    /// </summary>
    public void SetSpaceVisibility(bool showFreeSpace, bool showUnknownSpace)
    {
        if (showFreeSpace == _options.ShowFreeSpace && showUnknownSpace == _options.ShowUnknownSpace)
        {
            return;
        }

        _options = _options.With(showFreeSpace: showFreeSpace, showUnknownSpace: showUnknownSpace);
        Raise(ChangeKind.Layout);
    }

    /// <summary>
    /// Tooltip text for a node: its snapshot path plus logical/allocated size.
    /// Read from the captured snapshot only, never the filesystem, and cached per
    /// node and snapshot version so mouse movement does not rebuild the path.
    /// </summary>
    public string GetTooltipText(int nodeId)
    {
        TreemapSnapshot snapshot = _publisher.Current;

        if (nodeId == _tooltipNodeId && snapshot.Version == _tooltipVersion && _tooltipText is not null)
        {
            return _tooltipText;
        }

        string text = string.Empty;
        if (nodeId >= 0 && snapshot.TryGetNode(nodeId, out TreemapNode? node))
        {
            string path = snapshot.GetPath(nodeId);
            text = node.PhysicalSize > 0 && node.PhysicalSize != node.LogicalSize
                ? $"{path}\n{SizeFormat.FormatBytes(node.LogicalSize)} logical, {SizeFormat.FormatBytes(node.PhysicalSize)} allocated"
                : $"{path}\n{SizeFormat.FormatBytes(SizeFormat.DisplaySize(node.LogicalSize, node.PhysicalSize))}";
        }

        _tooltipNodeId = nodeId;
        _tooltipVersion = snapshot.Version;
        _tooltipText = text;
        return text;
    }

    /// <summary>Returns and clears accumulated changes since the last call.</summary>
    public ChangeKind ConsumeChanges()
    {
        ChangeKind result = _pending;
        _pending = ChangeKind.None;
        return result;
    }

    /// <summary>
    /// Records the current view for Back, skipping a push when the top already
    /// holds it so repeated navigation cannot stack duplicates.
    /// </summary>
    private void PushBack()
    {
        if (_backStack.Count > 0 && _backStack[^1].FocusRootNodeId == _focusRootNodeId)
        {
            return;
        }

        _backStack.Add(new NavigationEntry(_focusRootNodeId, _camera.Capture()));
        if (_backStack.Count > MaxHistoryEntries)
        {
            _backStack.RemoveAt(0);
        }
    }

    /// <summary>
    /// Drops history entries whose nodes are gone from the new snapshot, so a
    /// rescan never lands Back on a coincidentally matching id, and trims the
    /// stacks to their bound.
    /// </summary>
    private void PruneHistory(TreemapSnapshot snapshot)
    {
        _backStack.RemoveAll(entry => !snapshot.TryGetNode(entry.FocusRootNodeId, out _));
        _forwardStack.RemoveAll(entry => !snapshot.TryGetNode(entry.FocusRootNodeId, out _));

        while (_backStack.Count > MaxHistoryEntries)
        {
            _backStack.RemoveAt(0);
        }

        while (_forwardStack.Count > MaxHistoryEntries)
        {
            _forwardStack.RemoveAt(0);
        }
    }

    private static bool SourcesMatch(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Match scanner path identity: case-insensitive on Windows,
        // case-sensitive everywhere else.
        return OperatingSystem.IsWindows()
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>True when the camera differs from a fresh fit of the viewport.</summary>
    private bool IsCameraOffFit()
    {
        if (_viewport.IsEmpty)
        {
            return false;
        }

        TreemapCamera fitted = new();
        fitted.FitToContent(ContentBounds, _viewport);
        const float zoomTol = 1e-4f;
        const float panTol = 0.5f;
        return MathF.Abs(_camera.Zoom - fitted.Zoom) > zoomTol
            || MathF.Abs(_camera.Pan.X - fitted.Pan.X) > panTol
            || MathF.Abs(_camera.Pan.Y - fitted.Pan.Y) > panTol;
    }

    private readonly record struct NavigationEntry(int FocusRootNodeId, CameraTransform Camera);
}
