namespace Treemap;

/// <summary>
/// Maps between treemap space and screen space, and owns zoom/pan state.
/// </summary>
/// <remarks>
/// <para>
/// The camera holds a scale and a translation applied to the laid-out treemap.
/// Screen-to-treemap is the exact inverse, which is what makes zoom-to-cursor and
/// hit-testing share one code path.
/// </para>
///
/// <para>
/// The treemap is laid out in "content" coordinates (origin at the content rect's
/// top-left). The camera maps that into the viewport. Keeping the two separate means
/// re-laying out at a new zoom level does not disturb the camera, and the camera does
/// not need to know how layout produced its numbers.
/// </para>
/// </remarks>
public sealed class TreemapCamera
{
    private float _zoom = 1f;
    private PointF _pan;

    /// <summary>Lowest and highest permitted zoom factors.</summary>
    public float MinZoom { get; init; } = 0.05f;

    public float MaxZoom { get; init; } = 64f;

    /// <summary>Current zoom factor. Always within [<see cref="MinZoom"/>, <see cref="MaxZoom"/>].</summary>
    public float Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    /// <summary>Screen-space translation in pixels.</summary>
    public PointF Pan
    {
        get => _pan;
        set => _pan = value;
    }

    public void Reset()
    {
        _zoom = 1f;
        _pan = default;
    }

    /// <summary>Converts a point in treemap space to screen space.</summary>
    public PointF ContentToScreen(in PointF p)
        => new((p.X * _zoom) + _pan.X, (p.Y * _zoom) + _pan.Y);

    /// <summary>Converts a screen point to treemap space.</summary>
    public PointF ScreenToContent(in PointF p)
        => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    /// <summary>Converts a content rect to screen space.</summary>
    public RectF ContentToScreen(in RectF r)
        => new(
            (r.X * _zoom) + _pan.X,
            (r.Y * _zoom) + _pan.Y,
            r.Width * _zoom,
            r.Height * _zoom);

    /// <summary>Converts a screen rect to content space.</summary>
    public RectF ScreenToContent(in RectF r)
        => new(
            (r.X - _pan.X) / _zoom,
            (r.Y - _pan.Y) / _zoom,
            r.Width / _zoom,
            r.Height / _zoom);

    /// <summary>
    /// Zooms by <paramref name="factor"/> keeping the content point under
    /// <paramref name="anchor"/> (screen coords) stationary. This is the behaviour
    /// that makes wheel-zoom feel anchored rather than drifting.
    /// </summary>
    public void ZoomAt(float factor, in PointF anchor)
    {
        float target = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - _zoom) < 1e-6f)
        {
            return;
        }

        // Solve for the pan that keeps the content point under the anchor fixed.
        PointF contentUnderAnchor = ScreenToContent(anchor);
        _zoom = target;
        _pan = new PointF(
            anchor.X - (contentUnderAnchor.X * _zoom),
            anchor.Y - (contentUnderAnchor.Y * _zoom));
    }

    /// <summary>Zooms keeping the viewport centre fixed.</summary>
    public void ZoomAtCenter(float factor, in RectF viewport)
        => ZoomAt(factor, viewport.Center);

    /// <summary>Translates the view by a screen-space delta.</summary>
    public void PanBy(float dx, float dy)
        => _pan = new PointF(_pan.X + dx, _pan.Y + dy);

    /// <summary>
    /// Fits a content rect into the viewport, preserving aspect ratio and
    /// centring. Used on resize and when focusing a subtree.
    /// </summary>
    public void FitToContent(in RectF content, in RectF viewport, float margin = 0.95f)
    {
        if (content.IsEmpty || viewport.IsEmpty)
        {
            return;
        }

        float scaleX = viewport.Width / content.Width;
        float scaleY = viewport.Height / content.Height;
        float scale = MathF.Min(scaleX, scaleY) * margin;

        _zoom = Math.Clamp(scale, MinZoom, MaxZoom);

        // Centre the scaled content within the viewport.
        float scaledW = content.Width * _zoom;
        float scaledH = content.Height * _zoom;

        _pan = new PointF(
            viewport.X + ((viewport.Width - scaledW) * 0.5f) - (content.X * _zoom),
            viewport.Y + ((viewport.Height - scaledH) * 0.5f) - (content.Y * _zoom));
    }

    /// <summary>
    /// Snapshots the current transform so it can be restored later or used as one
    /// end of a <see cref="CameraTransform"/> interpolation.
    /// </summary>
    public CameraTransform Capture() => new(_zoom, _pan);

    /// <summary>Applies an explicit transform, used by the animator.</summary>
    public void Apply(in CameraTransform transform)
    {
        _zoom = Math.Clamp(transform.Zoom, MinZoom, MaxZoom);
        _pan = transform.Pan;
    }

    public override string ToString() => $"zoom={Zoom:0.###} pan=({Pan.X:0.#}, {Pan.Y:0.#})";
}

/// <summary>Immutable camera state, used as animation endpoints.</summary>
public readonly struct CameraTransform(float zoom, PointF pan)
{
    public float Zoom { get; } = zoom;

    public PointF Pan { get; } = pan;

    public static CameraTransform Lerp(in CameraTransform a, in CameraTransform b, float t)
        => new(
            a.Zoom + ((b.Zoom - a.Zoom) * t),
            new PointF(
                a.Pan.X + ((b.Pan.X - a.Pan.X) * t),
                a.Pan.Y + ((b.Pan.Y - a.Pan.Y) * t)));
}
