using System.IO.Enumeration;

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
    private string _tooltipFilter = string.Empty;
    private string? _tooltipText;

    private string _filterPattern = string.Empty;
    private TreemapSnapshot? _displaySource;
    private string _displayFilter = string.Empty;
    private TreemapSnapshot? _displayCache;
    private long _displayFileCount;

    private int _temporaryExpandedNodeId = -1;

    /// <summary>Extra descendant levels granted to the temporarily expanded branch.</summary>
    private const int TemporaryExtraLevels = 2;

    public TreemapController(
        SnapshotPublisher publisher, LayoutOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        _publisher = publisher;
        _options = WithoutTemporary(options ?? LayoutOptions.Default);
        _animator = new TreemapAnimator(timeProvider: timeProvider);
        _focusRootNodeId = publisher.Current.Root.Id;
        _focusSource = publisher.Current.RootPath;
        _camera.FitToContent(ContentBounds, _viewport, FitMargin);
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

    /// <summary>
    /// Less stays enabled while a temporary branch expansion is active, even at
    /// the minimum depth, so the toolbar can always return the map to minimal.
    /// </summary>
    public bool CanDecreaseDetail => _options.MaxDepth > MinDetailDepth || _temporaryExpandedNodeId >= 0;

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
            if (_temporaryExpandedNodeId >= 0)
            {
                return true;
            }

            if (_forwardStack.Count > 0)
            {
                return true;
            }

            if (_focusRootNodeId != DisplaySnapshot.Root.Id)
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
            TreemapSnapshot snapshot = DisplaySnapshot;
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
    /// The rect the treemap is laid out into, in content coordinates. The height is
    /// a fixed reference size while the width follows the current viewport aspect,
    /// so a fitted camera (margin 1) fills wide and portrait windows instead of
    /// letterboxing a square. Zoom 1.0 still means "fitted to the window".
    /// </summary>
    private RectF ContentBounds => ContentBoundsFor(_viewport);

    /// <summary>Reference layout height in content units; width follows the viewport.</summary>
    private const float ReferenceHeight = 1000f;

    /// <summary>Camera fit margin, close to 1 so the map fills the viewport.</summary>
    private const float FitMargin = 1f;

    /// <summary>
    /// Content rect for a viewport: reference height with matching reference width.
    /// Empty viewports fall back to the 1000x1000 square.
    /// </summary>
    public static RectF ContentBoundsFor(RectF viewport)
    {
        if (viewport.IsEmpty || viewport.Height <= 0f || viewport.Width <= 0f)
        {
            return new RectF(0, 0, ReferenceHeight, ReferenceHeight);
        }

        float width = ReferenceHeight * viewport.Width / viewport.Height;
        if (!(width > 0f) || float.IsNaN(width) || float.IsInfinity(width))
        {
            return new RectF(0, 0, ReferenceHeight, ReferenceHeight);
        }

        return new RectF(0, 0, width, ReferenceHeight);
    }

    /// <summary>
    /// Sets the viewport size. The camera refits whenever the size changes, so the
    /// treemap always fills the window. The host debounces the matching aspect-ratio
    /// relayout and calls <see cref="Relayout"/> once the size settles; this method
    /// never rebuilds the tree itself.
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
            _camera.FitToContent(ContentBounds, viewport, FitMargin);
            Raise(ChangeKind.Camera);
        }
    }

    /// <summary>Active filename filter, or empty when no filter applies.</summary>
    public string FilterPattern => _filterPattern;

    /// <summary>
    /// Projected snapshot the map displays: the published tree filtered by
    /// <see cref="FilterPattern"/>, or the published snapshot itself when clear.
    /// Cached by source reference and normalized expression; rebuilt per preview.
    /// </summary>
    public TreemapSnapshot DisplaySnapshot
    {
        get
        {
            if (_filterPattern.Length == 0)
            {
                return _publisher.Current;
            }

            EnsureDisplayCache();
            return _displayCache ?? _publisher.Current;
        }
    }

    /// <summary>Matching file count over the display projection.</summary>
    public long DisplayFileCount
    {
        get
        {
            if (_filterPattern.Length == 0)
            {
                EnsureUnfilteredCount();
                return _displayFileCount;
            }

            EnsureDisplayCache();
            return _displayFileCount;
        }
    }

    /// <summary>Folder temporarily showing extra levels in place, or -1.</summary>
    public int TemporaryExpandedNodeId => _temporaryExpandedNodeId;

    /// <summary>
    /// Sets the filename filter. Blank or <c>*</c> clears it. Filename glob with
    /// <c>*</c> and <c>?</c>, case-insensitive on basenames only. Returns whether
    /// the normalized filter changed; the caller relayouts as with option changes.
    /// </summary>
    public bool ApplyFileFilter(string? pattern)
    {
        string normalized = NormalizeFilter(pattern);
        if (string.Equals(normalized, _filterPattern, StringComparison.Ordinal))
        {
            return false;
        }

        _filterPattern = normalized;
        _temporaryExpandedNodeId = -1;
        _tooltipNodeId = -1;
        _tooltipVersion = -1;
        _tooltipFilter = string.Empty;
        _tooltipText = null;
        TreemapSnapshot display = DisplaySnapshot;
        ChangeKind kind = ChangeKind.Layout | ChangeKind.Selection;
        if (_selectedNodeId >= 0 && !display.TryGetNode(_selectedNodeId, out _))
        {
            _selectedNodeId = -1;
        }

        if (_hoveredNodeId >= 0 && !display.TryGetNode(_hoveredNodeId, out _))
        {
            _hoveredNodeId = -1;
            kind |= ChangeKind.Hover;
        }

        Raise(kind);
        return true;
    }

    /// <summary>
    /// Selects <paramref name="nodeId"/> and grants its branch two extra levels.
    /// Files and blank (-1) clear the prior expansion. The displayed root never
    /// takes the expansion: expanding it would deepen every branch while the
    /// toolbar still reports basic detail, so a root click only selects it and
    /// clears any prior branch expansion. Returns whether the
    /// expansion changed, in which case the caller relayouts.
    /// </summary>
    public bool ExpandNodeTemporarily(int nodeId)
    {
        TreemapSnapshot display = DisplaySnapshot;
        int requested = nodeId >= 0 && display.TryGetNode(nodeId, out _) ? nodeId : -1;
        bool isFolder = requested >= 0
            && display.TryGetNode(requested, out TreemapNode? node)
            && node.Children.Count > 0;
        bool isDisplayRoot = requested >= 0 && requested == _focusRootNodeId;
        int next = isFolder && !isDisplayRoot ? requested : -1;

        if (requested != _selectedNodeId)
        {
            _selectedNodeId = requested;
            Raise(ChangeKind.Selection);
        }

        if (next == _temporaryExpandedNodeId)
        {
            return false;
        }

        _temporaryExpandedNodeId = next;
        return true;
    }

    /// <summary>
    /// Re-lays out the display projection. Called after a publish or option change.
    /// The focused subtree is used as the layout root, so drilling into a folder
    /// re-lays out that folder's contents across the aspect-aware content rect.
    /// A changed source <see cref="TreemapSnapshot.RootPath"/> resets
    /// focus/selection/hover/camera/history/temporary expansion and the tooltip cache,
    /// so a new scan never lands on a coincidentally reused id; same-source previews
    /// keep focus and retain a still-valid temporary expansion.
    /// Focus, parents and history resolve against the display projection, so a
    /// filtered-out focus safely resets to the display root.
    /// </summary>
    public void Relayout(bool animate = true)
    {
        TreemapSnapshot source = _publisher.Current;

        if (!SourcesMatch(_focusSource, source.RootPath))
        {
            _focusSource = source.RootPath;
            _temporaryExpandedNodeId = -1;
            _displaySource = null;
            _displayCache = null;
            TreemapSnapshot display = DisplaySnapshot;
            _focusRootNodeId = display.Root.Id;
            _backStack.Clear();
            _forwardStack.Clear();
            _hoveredNodeId = -1;
            _selectedNodeId = -1;
            _tooltipNodeId = -1;
            _tooltipVersion = -1;
            _tooltipFilter = string.Empty;
            _tooltipText = null;
            _camera.Reset();
            _camera.FitToContent(ContentBounds, _viewport, FitMargin);
            Raise(ChangeKind.Navigation | ChangeKind.Camera | ChangeKind.Hover | ChangeKind.Selection);
        }
        else
        {
            TreemapSnapshot display = DisplaySnapshot;
            PruneHistory(display);

            if (!display.TryGetNode(_focusRootNodeId, out _))
            {
                _focusRootNodeId = display.Root.Id;
                _backStack.Clear();
                _forwardStack.Clear();
                _camera.Reset();
                _camera.FitToContent(ContentBounds, _viewport, FitMargin);
            }

            if (_temporaryExpandedNodeId >= 0
                && (!display.TryGetNode(_temporaryExpandedNodeId, out TreemapNode? expanded)
                    || expanded.Children.Count == 0))
            {
                _temporaryExpandedNodeId = -1;
            }

            ChangeKind pruned = ChangeKind.None;
            if (_selectedNodeId >= 0 && !display.TryGetNode(_selectedNodeId, out _))
            {
                _selectedNodeId = -1;
                pruned |= ChangeKind.Selection;
            }

            if (_hoveredNodeId >= 0 && !display.TryGetNode(_hoveredNodeId, out _))
            {
                _hoveredNodeId = -1;
                pruned |= ChangeKind.Hover;
            }

            if (pruned != ChangeKind.None)
            {
                Raise(pruned);
            }
        }

        TreemapSnapshot current = DisplaySnapshot;
        current.TryGetNode(_focusRootNodeId, out TreemapNode? focusNode);
        focusNode ??= current.Root;

        LayoutOptions effective = _temporaryExpandedNodeId >= 0
            ? WithoutTemporary(_options).With(
                temporaryExpandedNodeId: _temporaryExpandedNodeId,
                temporaryExtraDepth: TemporaryExtraLevels)
            : WithoutTemporary(_options);
        LayoutResult next = SquarifyLayout.Compute(focusNode, ContentBounds, effective);

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

    /// <summary>
    /// Handles a left click: selects the cell under the cursor and temporarily
    /// expands a clicked folder two levels in place. The displayed root, camera
    /// and history never change. Relayouts once when the expansion changed.
    /// </summary>
    public void Click(float x, float y)
    {
        int hit = HitAt(x, y);
        if (ExpandNodeTemporarily(hit))
        {
            Relayout(animate: false);
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
    /// Clears any temporary in-place expansion. Resolves against the display projection.
    /// </summary>
    public bool FocusNode(int nodeId)
    {
        if (nodeId == _focusRootNodeId)
        {
            return false;
        }

        TreemapSnapshot snapshot = DisplaySnapshot;
        if (!snapshot.TryGetNode(nodeId, out TreemapNode node) || node.Children.Count == 0)
        {
            return false;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = nodeId;
        _temporaryExpandedNodeId = -1;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport, FitMargin);
        Raise(ChangeKind.Navigation | ChangeKind.Camera);
        return true;
    }

    /// <summary>
    /// Moves one level to the true parent resolved from the display projection, not
    /// from the current focused layout or the history stack. Branches: forward
    /// history is cleared. The parent subtree is fitted to the viewport.
    /// Clears any temporary in-place expansion.
    /// </summary>
    public bool GoToParent()
    {
        TreemapSnapshot snapshot = DisplaySnapshot;
        if (!snapshot.TryGetNode(_focusRootNodeId, out _)
            || !snapshot.TryGetParent(_focusRootNodeId, out int parentId))
        {
            return false;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = parentId;
        _temporaryExpandedNodeId = -1;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport, FitMargin);
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
        _temporaryExpandedNodeId = -1;
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
        _temporaryExpandedNodeId = -1;
        _camera.Apply(entry.Camera);
        Raise(ChangeKind.Navigation);
        return true;
    }

    /// <summary>
    /// Resets the view to the whole drive, refitting the camera even when already
    /// at the root. Branches: forward history is cleared. Returns false when
    /// already home with a fitted camera, empty forward history and no temporary
    /// detail. Clears any temporary in-place expansion.
    /// The caller relayouts afterwards when temporary detail was cleared.
    /// </summary>
    public bool GoToRoot()
    {
        TreemapSnapshot snapshot = DisplaySnapshot;
        int rootId = snapshot.Root.Id;

        if (_focusRootNodeId == rootId)
        {
            bool fitted = !IsCameraOffFit();
            if (_forwardStack.Count == 0 && fitted && _temporaryExpandedNodeId < 0)
            {
                return false;
            }

            _forwardStack.Clear();
            _temporaryExpandedNodeId = -1;
            _camera.Reset();
            _camera.FitToContent(ContentBounds, _viewport, FitMargin);
            Raise(ChangeKind.Navigation | ChangeKind.Camera);
            return true;
        }

        PushBack();
        _forwardStack.Clear();
        _focusRootNodeId = rootId;
        _temporaryExpandedNodeId = -1;
        _camera.Reset();
        _camera.FitToContent(ContentBounds, _viewport, FitMargin);
        Raise(ChangeKind.Navigation | ChangeKind.Camera);
        return true;
    }

    /// <summary>
    /// Shows one more descendant level without touching camera zoom or rescanning.
    /// Clears any temporary branch expansion, so global detail never stacks on
    /// top of a focused branch. Returns true when the depth or the expansion
    /// changed, in which case the caller relayouts.
    /// </summary>
    public bool IncreaseDetail()
    {
        bool cleared = _temporaryExpandedNodeId >= 0;
        if (cleared)
        {
            _temporaryExpandedNodeId = -1;
        }

        bool deepened = false;
        if (CanIncreaseDetail)
        {
            _options = _options.With(maxDepth: _options.MaxDepth + 1);
            deepened = true;
        }

        if (cleared || deepened)
        {
            Raise(ChangeKind.Layout);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Shows one fewer descendant level without touching camera zoom.
    /// Clears any temporary branch expansion first, so Less at the minimum
    /// depth still returns an expanded map to minimal instead of doing nothing.
    /// </summary>
    public bool DecreaseDetail()
    {
        bool cleared = _temporaryExpandedNodeId >= 0;
        if (cleared)
        {
            _temporaryExpandedNodeId = -1;
        }

        bool shallowed = false;
        if (_options.MaxDepth > MinDetailDepth)
        {
            _options = _options.With(maxDepth: _options.MaxDepth - 1);
            shallowed = true;
        }

        if (cleared || shallowed)
        {
            Raise(ChangeKind.Layout);
            return true;
        }

        return false;
    }

    /// <summary>Replaces the layout options. The caller relayouts afterwards.</summary>
    /// <remarks>Any temporary-expansion fields on <paramref name="options"/> are
    /// stripped: only the controller's own <see cref="TemporaryExpandedNodeId"/>
    /// drives the override, so clearing it cannot leave external values behind.</remarks>
    public void SetLayoutOptions(LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = WithoutTemporary(options);
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
    /// Tooltip text for a node: its display path plus logical/allocated size.
    /// Read from the display projection only, never the filesystem, and cached per
    /// node, snapshot version and filter so mouse movement does not rebuild the path.
    /// </summary>
    public string GetTooltipText(int nodeId)
    {
        TreemapSnapshot source = _publisher.Current;
        TreemapSnapshot snapshot = DisplaySnapshot;

        if (nodeId == _tooltipNodeId
            && source.Version == _tooltipVersion
            && string.Equals(_filterPattern, _tooltipFilter, StringComparison.Ordinal)
            && _tooltipText is not null)
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
        _tooltipVersion = source.Version;
        _tooltipFilter = _filterPattern;
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

    private static string NormalizeFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        string trimmed = pattern.Trim();
        if (trimmed.Length == 0 || trimmed == "*")
        {
            return string.Empty;
        }

        return trimmed;
    }

    private void EnsureUnfilteredCount()
    {
        TreemapSnapshot source = _publisher.Current;
        if (ReferenceEquals(_displaySource, source) && _displayFilter.Length == 0 && _filterPattern.Length == 0)
        {
            return;
        }

        _displaySource = source;
        _displayFilter = string.Empty;
        _displayCache = null;
        _displayFileCount = CountFiles(source.Root);
    }

    private void EnsureDisplayCache()
    {
        TreemapSnapshot source = _publisher.Current;
        if (ReferenceEquals(_displaySource, source)
            && string.Equals(_displayFilter, _filterPattern, StringComparison.Ordinal)
            && _displayCache is not null)
        {
            return;
        }

        (TreemapNode Root, long FileCount) projected = ProjectForFilter(source.Root, _filterPattern);
        _displayCache = new TreemapSnapshot(
            projected.Root, source.Version, source.Timestamp, source.RootPath);
        _displaySource = source;
        _displayFilter = _filterPattern;
        _displayFileCount = projected.FileCount;
    }

    private static long CountFiles(TreemapNode root)
    {
        long count = 0;
        Stack<TreemapNode> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            TreemapNode node = stack.Pop();
            if (node.Kind == NodeKind.File)
            {
                count++;
            }
            else
            {
                foreach (TreemapNode child in node.Children)
                {
                    stack.Push(child);
                }
            }
        }

        return count;
    }

    private static (TreemapNode Root, long FileCount) ProjectForFilter(TreemapNode root, string pattern)
    {
        long files = 0;
        TreemapNode? projected = ProjectNode(root, pattern, isRoot: true, ref files);
        projected ??= TreemapNode.Create(
            root.Id, root.Name, root.Kind, 0, 0, root.LastWriteTime, root.ColorGroup);
        return (projected, files);
    }

    private static TreemapNode? ProjectNode(TreemapNode node, string pattern, bool isRoot, ref long files)
    {
        if (node.IsSpace)
        {
            return null;
        }

        if (node.Kind == NodeKind.File)
        {
            if (!GlobMatches(node.Name, pattern))
            {
                return null;
            }

            files++;
            return node;
        }

        List<TreemapNode>? kept = null;
        long logical = 0;
        long physical = 0;
        foreach (TreemapNode child in node.Children)
        {
            TreemapNode? match = ProjectNode(child, pattern, isRoot: false, ref files);
            if (match is null)
            {
                continue;
            }

            kept ??= [];
            kept.Add(match);
            logical = AddSaturating(logical, match.LogicalSize);
            physical = AddSaturating(physical, match.PhysicalSize);
        }

        if (kept is null)
        {
            return isRoot ? TreemapNode.Create(
                node.Id, node.Name, node.Kind, 0, 0, node.LastWriteTime, node.ColorGroup) : null;
        }

        return TreemapNode.Create(
            node.Id, node.Name, node.Kind, logical, physical,
            node.LastWriteTime, node.ColorGroup, kept);
    }

    private static long AddSaturating(long a, long b)
    {
        if (b > 0 && a > long.MaxValue - b)
        {
            return long.MaxValue;
        }

        return a + b;
    }

    /// <summary>
    /// Filename glob with <c>*</c> (any run) and <c>?</c> (one char),
    /// case-insensitive, basename only. Uses the framework matcher.
    /// </summary>
    private static bool GlobMatches(string name, string pattern)
        => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    /// <summary>
    /// Returns options without any temporary-expansion override, copying when
    /// needed so the shared <see cref="LayoutOptions.Default"/> is never mutated.
    /// </summary>
    private static LayoutOptions WithoutTemporary(LayoutOptions options)
        => options.TemporaryExpandedNodeId == -1 && options.TemporaryExtraDepth == 0
            ? options
            : options.With(temporaryExpandedNodeId: -1, temporaryExtraDepth: 0);

    /// <summary>True when the camera differs from a fresh fit of the viewport.</summary>
    private bool IsCameraOffFit()
    {
        if (_viewport.IsEmpty)
        {
            return false;
        }

        TreemapCamera fitted = new();
        fitted.FitToContent(ContentBounds, _viewport, FitMargin);
        const float zoomTol = 1e-4f;
        const float panTol = 0.5f;
        return MathF.Abs(_camera.Zoom - fitted.Zoom) > zoomTol
            || MathF.Abs(_camera.Pan.X - fitted.Pan.X) > panTol
            || MathF.Abs(_camera.Pan.Y - fitted.Pan.Y) > panTol;
    }

    private readonly record struct NavigationEntry(int FocusRootNodeId, CameraTransform Camera);
}
