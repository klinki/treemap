using SkiaSharp;
using Treemap;

namespace Treemap.Skia;

/// <summary>Visual state the renderer draws on top of the treemap.</summary>
public sealed class RenderState
{
    /// <summary>Node id currently under the cursor.</summary>
    public int HoveredNodeId { get; set; } = -1;

    /// <summary>Node id that is selected/pinned.</summary>
    public int SelectedNodeId { get; set; } = -1;

    /// <summary>Draw a drop shadow beneath the highlighted element.</summary>
    public bool HighlightShadow { get; init; } = true;

    /// <summary>Draw labels.</summary>
    public bool ShowLabels { get; init; } = true;

    /// <summary>Minimum font size in pixels; smaller labels are dropped.</summary>
    public float MinFontSize { get; init; } = 6f;

    public float BaseFontSize { get; init; } = 12f;

    public string FontFamily { get; init; } = "Segoe UI";
}

/// <summary>
/// Draws a laid-out treemap onto an <see cref="SKCanvas"/>.
/// </summary>
/// <remarks>
/// <para>
/// Fills cells, draws borders, fits labels, and highlights the hover path. The
/// whole treemap is rasterised into a bitmap for the host to display.
/// </para>
/// </remarks>
public sealed class TreemapRenderer : IDisposable
{
    private readonly TreemapPalette _palette;
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _borderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = false };
    private readonly SKPaint _highlightPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = false, Color = SKColors.White };
    private readonly SKPaint _shadowPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 0x66) };
    private readonly SKPaint _textPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private SKTypeface? _typeface;
    private SKFont _font = new();

    public TreemapRenderer(TreemapPalette? palette = null)
    {
        _palette = palette ?? TreemapPalette.Default;
        _borderPaint.Color = _palette.BorderColor;
    }

    /// <summary>
    /// Renders a frame of tweened cells. Cells are expected in draw order
    /// (shallow to deep), which <see cref="LayoutTween"/> guarantees.
    /// </summary>
    public void Draw(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, RenderState state)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);

        // Pass 1: fills, back to front so nested children cover their parent.
        foreach (TweenedCell cell in frame)
        {
            if (cell.Alpha <= 0.01f)
            {
                continue;
            }

            _fillPaint.Color = _palette.Resolve(cell.Kind, cell.ColorGroup, cell.Depth, cell.Alpha);
            canvas.DrawRect(ToSkia(cell.Bounds), _fillPaint);
        }

        // Pass 2: shadow under the highlighted element, then borders, then the
        // ancestor outline. Shadow first so borders sit crisply on top.
        if (state.HighlightShadow && state.HoveredNodeId >= 0)
        {
            DrawShadow(canvas, frame, state.HoveredNodeId);
        }

        foreach (TweenedCell cell in frame)
        {
            if (cell.Alpha <= 0.01f)
            {
                continue;
            }

            canvas.DrawRect(ToSkia(cell.Bounds), _borderPaint);
        }

        if (state.HoveredNodeId >= 0)
        {
            DrawHoverOutline(canvas, frame, state.HoveredNodeId);
        }

        // Pass 3: labels last so they sit above every fill.
        if (state.ShowLabels)
        {
            DrawLabels(canvas, frame, state);
        }
    }

    /// <summary>Renders a settled layout with no animation.</summary>
    public void DrawLayout(SKCanvas canvas, LayoutResult layout, RenderState state)
    {
        ArgumentNullException.ThrowIfNull(layout);

        List<TweenedCell> frame = new(layout.Count);
        for (int i = 0; i < layout.Count; i++)
        {
            frame.Add(new TweenedCell
            {
                NodeId = layout.NodeIds[i],
                Bounds = layout.Bounds[i],
                Alpha = 1f,
                Depth = layout.Depths[i],
                Kind = layout.Kinds[i],
                ColorGroup = layout.ColorGroups[i],
                AreaFraction = layout.AreaFractions[i],
                HasChildren = layout.HasChildren[i],
                IsEntering = false,
            });
        }

        Draw(canvas, frame, state);
    }

    /// <summary>
    /// Outlines the hovered cell's ancestors. Uses geometric containment because a tweened
    /// frame carries no parent links.
    /// </summary>
    private void DrawHoverOutline(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        RectF hovered = FindBounds(frame, nodeId);
        if (hovered.IsEmpty)
        {
            return;
        }

        int hoveredDepth = FindDepth(frame, nodeId);

        foreach (TweenedCell cell in frame)
        {
            if (cell.NodeId == nodeId || cell.Depth >= hoveredDepth)
            {
                continue;
            }

            if (Contains(cell.Bounds, hovered))
            {
                canvas.DrawRect(ToSkia(cell.Bounds), _highlightPaint);
            }
        }

        canvas.DrawRect(ToSkia(hovered), _highlightPaint);
    }

    private void DrawShadow(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        RectF bounds = FindBounds(frame, nodeId);
        if (bounds.IsEmpty)
        {
            return;
        }

        // The highlighted element is always an axis-aligned rect, so its shadow is
        // simply that rect offset by a few pixels. No path needed.
        const float offset = 3f;
        canvas.DrawRect(
            ToSkia(new RectF(bounds.X + offset, bounds.Y + offset, bounds.Width, bounds.Height)),
            _shadowPaint);
    }

    /// <summary>
    /// Draws a label only when the cell can actually fit it.
    /// </summary>
    private void DrawLabels(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, RenderState state)
    {
        foreach (TweenedCell cell in frame)
        {
            if (cell.Alpha <= 0.05f || cell.Kind is NodeKind.FreeSpace or NodeKind.UnknownSpace)
            {
                continue;
            }

            float minDimension = MathF.Min(cell.Bounds.Width, cell.Bounds.Height);
            if (minDimension < 20f)
            {
                continue;
            }

            float available = cell.Bounds.Width - 6f;
            if (available <= 0f)
            {
                continue;
            }

            SKColor fill = _palette.Resolve(cell.Kind, cell.ColorGroup, cell.Depth, cell.Alpha);
            SKColor textColor = TreemapPalette.ContrastColor(fill);

            // Start from the base size and shrink until the text fits.
            float fontSize = Math.Clamp(state.BaseFontSize * 0.75f, state.MinFontSize, cell.Bounds.Height * 0.7f);
            if (fontSize < state.MinFontSize)
            {
                continue;
            }

            string label = FitLabel(NameFor(cell), state, ref fontSize, available);
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            ConfigureFont(state, fontSize);

            _font.GetFontMetrics(out SKFontMetrics metrics);
            float baseline = cell.Bounds.Y + ((cell.Bounds.Height - (metrics.Descent - metrics.Ascent)) * 0.5f) - metrics.Ascent;

            _textPaint.Color = textColor.WithAlpha((byte)(textColor.Alpha * cell.Alpha));
            canvas.DrawText(label, cell.Bounds.X + 3f, baseline, SKTextAlign.Left, _font, _textPaint);
        }
    }

    /// <summary>
    /// Shrinks the font until <paramref name="text"/> fits, or truncates it.
    /// Returns an empty string when even the minimum size cannot fit.
    /// </summary>
    private string FitLabel(string text, RenderState state, ref float fontSize, float maxWidth)
    {
        ConfigureFont(state, fontSize);

        if (_font.MeasureText(text, _textPaint) <= maxWidth)
        {
            return text;
        }

        // Try smaller sizes down to the floor.
        for (float size = fontSize - 1f; size >= state.MinFontSize; size -= 1f)
        {
            ConfigureFont(state, size);
            if (_font.MeasureText(text, _textPaint) <= maxWidth)
            {
                fontSize = size;
                return text;
            }
        }

        // Still too wide: truncate with an ellipsis at the minimum size.
        ConfigureFont(state, state.MinFontSize);
        fontSize = state.MinFontSize;

        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = string.Concat(text.AsSpan(0, len), "…");
            if (_font.MeasureText(candidate, _textPaint) <= maxWidth)
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private void ConfigureFont(RenderState state, float size)
    {
        _typeface ??= SKTypeface.FromFamilyName(state.FontFamily);
        _font.Typeface = _typeface;
        _font.Size = size;
    }

    /// <summary>
    /// Resolves display text for a cell. The renderer only carries ids, so the host
    /// supplies a lookup; without one, the id is shown.
    /// </summary>
    public Func<int, string>? NameResolver { get; set; }

    private string NameFor(TweenedCell cell)
        => NameResolver?.Invoke(cell.NodeId) ?? cell.NodeId.ToString();

    private static RectF FindBounds(IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        foreach (TweenedCell cell in frame)
        {
            if (cell.NodeId == nodeId)
            {
                return cell.Bounds;
            }
        }

        return RectF.Empty;
    }

    private static int FindDepth(IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        foreach (TweenedCell cell in frame)
        {
            if (cell.NodeId == nodeId)
            {
                return cell.Depth;
            }
        }

        return -1;
    }

    private static bool Contains(in RectF outer, in RectF inner)
        => outer.Left <= inner.Left && outer.Top <= inner.Top
        && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    private static SKRect ToSkia(in RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public void Dispose()
    {
        _fillPaint.Dispose();
        _borderPaint.Dispose();
        _highlightPaint.Dispose();
        _shadowPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
        _typeface?.Dispose();
    }
}
