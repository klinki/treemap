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

    /// <summary>Minimum font size in device pixels; smaller labels are dropped.</summary>
    public float MinFontSize { get; init; } = 6f;

    /// <summary>Starting font size in device pixels, shrunk to fit the mapped cell.</summary>
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
    private readonly SKPaint _ancestorPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false, Color = SKColors.White.WithAlpha(44) };
    private readonly SKPaint _shadowPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 0x66) };
    private readonly SKPaint _textPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    // Scratch buffers reused between frames, so the settled and hover paths do not
    // allocate per draw.
    private readonly List<TweenedCell> _settledFrame = [];
    private readonly List<int> _hoverPath = [];

    private SKTypeface? _typeface;
    private SKFont _font = new();

    public TreemapRenderer(TreemapPalette? palette = null)
    {
        _palette = palette ?? TreemapPalette.Default;
        _borderPaint.Color = _palette.BorderColor;
    }

    /// <summary>
    /// Renders a frame of tweened cells. Cells are expected in draw order (parents
    /// before children), which <see cref="LayoutTween"/> prepares once per tween.
    /// </summary>
    public void Draw(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, RenderState state)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);

        // Pass 1: fills, back to front so nested children cover their parent. The
        // hovered cell is the deepest drawn match, found up front so its fill can
        // be brightened inline and the ancestor pass below needs only the chain.
        int hoveredIndex = -1;

        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].Alpha > 0.01f && frame[i].NodeId == state.HoveredNodeId)
            {
                hoveredIndex = i;
            }
        }

        for (int i = 0; i < frame.Count; i++)
        {
            TweenedCell cell = frame[i];
            if (cell.Alpha <= 0.01f)
            {
                continue;
            }

            SKColor fill = _palette.Resolve(cell.Kind, cell.ColorGroup, cell.Depth, cell.Alpha);
            _fillPaint.Color = i == hoveredIndex ? Brighten(fill) : fill;
            canvas.DrawRect(ToSkia(cell.Bounds), _fillPaint);
        }

        // Pass 1b: ancestor chain. Only the reserved header strip of each
        // ancestor is shaded, so unrelated subtrees keep their fills; outlines
        // still mark the full chain. The chain is walked once via ParentIndex.
        if (hoveredIndex >= 0)
        {
            List<int> path = HitTester.FramePathToRoot(frame, hoveredIndex, _hoverPath);
            for (int i = path.Count - 1; i >= 1; i--)
            {
                TweenedCell ancestor = frame[path[i]];
                if (ancestor.Alpha > 0.01f && !ancestor.HeaderBounds.IsEmpty)
                {
                    canvas.DrawRect(ToSkia(ancestor.HeaderBounds), _ancestorPaint);
                }
            }
        }

        // Pass 2: shadow under the highlighted element, then borders, then the
        // ancestor outline. Shadow first so borders sit crisply on top.
        if (hoveredIndex >= 0 && state.HighlightShadow)
        {
            DrawShadow(canvas, frame[hoveredIndex].Bounds);
        }

        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].Alpha <= 0.01f)
            {
                continue;
            }

            canvas.DrawRect(ToSkia(frame[i].Bounds), _borderPaint);
        }

        if (hoveredIndex >= 0)
        {
            DrawHoverOutline(canvas, frame, hoveredIndex);
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

        Draw(canvas, SettledFrame(layout), state);
    }

    /// <summary>
    /// Projects a layout into a frame. The list is reused, but it is repopulated on
    /// every call: <see cref="LayoutResult"/> exposes its arrays as public mutable
    /// fields, so a layout is not actually immutable and a cached projection could
    /// outlive an edit to it and draw stale geometry.
    /// </summary>
    private IReadOnlyList<TweenedCell> SettledFrame(LayoutResult layout)
    {
        _settledFrame.Clear();
        if (_settledFrame.Capacity < layout.Count)
        {
            _settledFrame.Capacity = layout.Count;
        }

        for (int i = 0; i < layout.Count; i++)
        {
            _settledFrame.Add(new TweenedCell
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
                ParentIndex = layout.ParentIndices[i],
                HeaderBounds = layout.HeaderBounds[i],
                LogicalSize = layout.LogicalSizes[i],
                PhysicalSize = layout.PhysicalSizes[i],
            });
        }

        return _settledFrame;
    }

    /// <summary>Lightens a fill towards white for the hovered cell.</summary>
    private static SKColor Brighten(SKColor color)
    {
        const float amount = 0.35f;
        return new SKColor(
            (byte)(color.Red + ((255 - color.Red) * amount)),
            (byte)(color.Green + ((255 - color.Green) * amount)),
            (byte)(color.Blue + ((255 - color.Blue) * amount)),
            color.Alpha);
    }

    /// <summary>
    /// Outlines the hovered cell and its ancestors. Ancestry comes from each cell's
    /// explicit <see cref="TweenedCell.ParentIndex"/> rather than geometric containment: a
    /// tweened child can sit well outside the rectangle its parent is animating
    /// towards, and containment would silently drop the highlight exactly when a
    /// tree is being restructured.
    /// </summary>
    private void DrawHoverOutline(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, int hoveredIndex)
    {
        RectF hovered = frame[hoveredIndex].Bounds;
        if (hovered.IsEmpty)
        {
            return;
        }

        List<int> path = HitTester.FramePathToRoot(frame, hoveredIndex, _hoverPath);

        // Outermost first, then the hovered cell, so nested outlines layer correctly.
        for (int i = path.Count - 1; i >= 1; i--)
        {
            canvas.DrawRect(ToSkia(frame[path[i]].Bounds), _highlightPaint);
        }

        canvas.DrawRect(ToSkia(hovered), _highlightPaint);
    }

    private void DrawShadow(SKCanvas canvas, in RectF bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        // The highlight shadow must not darken the hovered interior: the offset
        // rect overlaps it almost entirely, so it is clipped to the outside.
        const float offset = 3f;
        canvas.Save();
        canvas.ClipRect(ToSkia(bounds), SKClipOperation.Difference);
        canvas.DrawRect(
            ToSkia(new RectF(bounds.X + offset, bounds.Y + offset, bounds.Width, bounds.Height)),
            _shadowPaint);
        canvas.Restore();
    }

    /// <summary>
    /// Draws a label only when the cell can actually fit it. Folder headers carry
    /// name plus size on one line in their strip; leaves, collapsed folders and
    /// space blocks stack name above size when tall enough, so a long
    /// name truncates on its own line instead of pushing the size away. Text is
    /// clipped to its region so it never spills into a neighbour.
    /// </summary>
    /// <remarks>
    /// Font sizes are device pixels, but cells arrive in content coordinates under
    /// the camera transform. Each label rect is mapped through the canvas matrix
    /// and painted with an identity matrix, so text stays readable at any zoom.
    /// Cell geometry, hit testing and resize behaviour are untouched.
    /// </remarks>
    private void DrawLabels(SKCanvas canvas, IReadOnlyList<TweenedCell> frame, RenderState state)
    {
        SKMatrix matrix = canvas.TotalMatrix;

        canvas.Save();
        canvas.ResetMatrix();
        try
        {
            foreach (TweenedCell cell in frame)
            {
                if (cell.Alpha <= 0.05f)
                {
                    continue;
                }

                string name = NameFor(cell);
                string size = SizeFormat.FormatBytes(SizeFormat.DisplaySize(cell.LogicalSize, cell.PhysicalSize));

                if (!cell.HeaderBounds.IsEmpty)
                {
                    DrawHeaderLabel(canvas, state, cell, name, size, MapRect(matrix, ToSkia(cell.HeaderBounds)));
                }
                else
                {
                    DrawLeafLabel(canvas, state, cell, name, size, MapRect(matrix, ToSkia(cell.Bounds)));
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawHeaderLabel(SKCanvas canvas, RenderState state, TweenedCell cell, string name, string size, SKRect mapped)
    {
        if (mapped.Height < 10f)
        {
            return;
        }

        float available = mapped.Width - 8f;
        if (available <= 0f)
        {
            return;
        }

        if (state.BaseFontSize < state.MinFontSize)
        {
            return;
        }

        // Guarded before use: Math.Clamp throws when the lower bound exceeds the
        // upper one, which a small strip or a large caller MinFontSize can cause.
        float upper = mapped.Height * 0.75f;
        if (upper < state.MinFontSize)
        {
            return;
        }

        SKColor fill = _palette.Resolve(cell.Kind, cell.ColorGroup, cell.Depth, cell.Alpha);
        float fontSize = Math.Min(state.BaseFontSize, upper);

        string label = FitLabel($"{name}  {size}", state, ref fontSize, available);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        DrawMappedText(canvas, state, label, fontSize, TreemapPalette.ContrastColor(fill),
            cell.Alpha, mapped, x: mapped.Left + 4f, centred: false);
    }

    private void DrawLeafLabel(SKCanvas canvas, RenderState state, TweenedCell cell, string name, string size, SKRect mapped)
    {
        float minDimension = MathF.Min(mapped.Width, mapped.Height);
        if (minDimension < 20f)
        {
            return;
        }

        float available = mapped.Width - 6f;
        if (available <= 0f)
        {
            return;
        }

        if (state.BaseFontSize < state.MinFontSize)
        {
            return;
        }

        SKColor fill = _palette.Resolve(cell.Kind, cell.ColorGroup, cell.Depth, cell.Alpha);
        SKColor contrast = TreemapPalette.ContrastColor(fill);
        float centreX = mapped.Left + (mapped.Width * 0.5f);

        // Two centred lines when tall enough. Name and size fit independently so
        // a long name truncates on its own line and never suppresses the size.
        // The lines form one compact block centred in the cell, not one line per
        // half, so they stay adjacent in tall cells.
        float halfHeight = mapped.Height * 0.5f;
        float twoLineUpper = halfHeight * 0.8f;
        if (twoLineUpper >= state.MinFontSize)
        {
            float nameSize = Math.Min(state.BaseFontSize, twoLineUpper);
            float sizeSize = Math.Min(state.BaseFontSize, twoLineUpper);
            string nameFit = FitLabel(name, state, ref nameSize, available);
            string sizeFit = FitLabel(size, state, ref sizeSize, available);

            if (!string.IsNullOrEmpty(sizeFit))
            {
                if (!string.IsNullOrEmpty(nameFit))
                {
                    DrawCompactTwoLines(canvas, state, nameFit, nameSize, sizeFit, sizeSize, contrast,
                        cell.Alpha, mapped, centreX);
                }
                else
                {
                    DrawMappedText(canvas, state, sizeFit, sizeSize, contrast,
                        cell.Alpha, mapped, x: centreX, centred: true);
                }

                return;
            }
        }

        float singleUpper = mapped.Height * 0.7f;
        if (singleUpper < state.MinFontSize)
        {
            return;
        }

        float fontSize = Math.Min(state.BaseFontSize, singleUpper);

        string label = FitLabel($"{name}  {size}", state, ref fontSize, available);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        DrawMappedText(canvas, state, label, fontSize, contrast,
            cell.Alpha, mapped, x: centreX, centred: true);
    }

    private void DrawMappedText(
        SKCanvas canvas, RenderState state, string label, float fontSize, SKColor color,
        float alpha, SKRect mapped, float x, bool centred)
    {
        ConfigureFont(state, fontSize);

        _font.GetFontMetrics(out SKFontMetrics metrics);
        float baseline = mapped.Top + ((mapped.Height - (metrics.Descent - metrics.Ascent)) * 0.5f) - metrics.Ascent;

        canvas.Save();
        canvas.ClipRect(mapped);

        _textPaint.Color = color.WithAlpha((byte)(color.Alpha * alpha));
        canvas.DrawText(label, x, baseline, centred ? SKTextAlign.Center : SKTextAlign.Left, _font, _textPaint);

        canvas.Restore();
    }

    private void DrawCompactTwoLines(
        SKCanvas canvas, RenderState state, string name, float nameSize,
        string size, float sizeSize, SKColor color, float alpha,
        SKRect mapped, float centreX)
    {
        ConfigureFont(state, nameSize);
        _font.GetFontMetrics(out SKFontMetrics nameMetrics);
        float nameHeight = nameMetrics.Descent - nameMetrics.Ascent;

        ConfigureFont(state, sizeSize);
        _font.GetFontMetrics(out SKFontMetrics sizeMetrics);
        float sizeHeight = sizeMetrics.Descent - sizeMetrics.Ascent;

        float gap = MathF.Min(4f, mapped.Height * 0.05f);
        float total = nameHeight + gap + sizeHeight;
        float startY = mapped.Top + ((mapped.Height - total) * 0.5f);

        canvas.Save();
        canvas.ClipRect(mapped);

        _textPaint.Color = color.WithAlpha((byte)(color.Alpha * alpha));

        ConfigureFont(state, nameSize);
        canvas.DrawText(name, centreX, startY - nameMetrics.Ascent,
            SKTextAlign.Center, _font, _textPaint);

        ConfigureFont(state, sizeSize);
        canvas.DrawText(size, centreX, startY + nameHeight + gap - sizeMetrics.Ascent,
            SKTextAlign.Center, _font, _textPaint);

        canvas.Restore();
    }

    private static SKRect MapRect(in SKMatrix matrix, SKRect rect)
    {
        MapPoint(matrix, rect.Left, rect.Top, out float x0, out float y0);
        MapPoint(matrix, rect.Right, rect.Top, out float x1, out float y1);
        MapPoint(matrix, rect.Right, rect.Bottom, out float x2, out float y2);
        MapPoint(matrix, rect.Left, rect.Bottom, out float x3, out float y3);

        return new SKRect(
            MathF.Min(MathF.Min(x0, x1), MathF.Min(x2, x3)),
            MathF.Min(MathF.Min(y0, y1), MathF.Min(y2, y3)),
            MathF.Max(MathF.Max(x0, x1), MathF.Max(x2, x3)),
            MathF.Max(MathF.Max(y0, y1), MathF.Max(y2, y3)));
    }

    private static void MapPoint(in SKMatrix matrix, float x, float y, out float mappedX, out float mappedY)
    {
        float w = (x * matrix.Persp0) + (y * matrix.Persp1) + matrix.Persp2;
        float tx = (x * matrix.ScaleX) + (y * matrix.SkewX) + matrix.TransX;
        float ty = (x * matrix.SkewY) + (y * matrix.ScaleY) + matrix.TransY;

        if (w != 0f && w != 1f)
        {
            tx /= w;
            ty /= w;
        }

        mappedX = tx;
        mappedY = ty;
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

    private static SKRect ToSkia(in RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public void Dispose()
    {
        _fillPaint.Dispose();
        _borderPaint.Dispose();
        _highlightPaint.Dispose();
        _ancestorPaint.Dispose();
        _shadowPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
        _typeface?.Dispose();
    }
}
