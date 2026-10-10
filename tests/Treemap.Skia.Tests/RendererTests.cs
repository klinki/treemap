using SkiaSharp;
using Treemap;
using Treemap.Skia;
using Xunit;

namespace Treemap.Skia.Tests;

public class RendererTests
{
    private static TreemapNode SampleTree()
    {
        TreemapNode leafA = TreemapNode.Create(10, "a.dll", NodeKind.File, 5_000, 5_000, null, 1);
        TreemapNode leafB = TreemapNode.Create(11, "b.dll", NodeKind.File, 3_000, 3_000, null, 2);
        TreemapNode folder = TreemapNode.Create(9, "inner", NodeKind.Folder, 0, 0, null, 0, [leafA, leafB]);
        TreemapNode free = TreemapNode.Create(8, "free", NodeKind.FreeSpace, 4_000, 4_000);

        return TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0, [folder, free]);
    }

    private static LayoutResult Layout() =>
        SquarifyLayout.Compute(SampleTree(), new RectF(0, 0, 400, 300), new LayoutOptions { InnerPadding = 1f });

    [Fact]
    public void RendersWithoutThrowing()
    {
        using SKBitmap bitmap = new(400, 300);
        using SKCanvas canvas = new(bitmap);
        using TreemapRenderer renderer = new();

        renderer.DrawLayout(canvas, Layout(), new RenderState());
    }

    [Fact]
    public void RendersPixelsAcrossTheCanvas()
    {
        using SKBitmap bitmap = new(400, 300);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        using TreemapRenderer renderer = new();
        renderer.DrawLayout(canvas, Layout(), new RenderState());

        // Count distinct colours: a blank canvas would have one.
        HashSet<SKColor> seen = [];
        for (int y = 0; y < 300; y += 3)
        {
            for (int x = 0; x < 400; x += 3)
            {
                seen.Add(bitmap.GetPixel(x, y));
            }
        }

        Assert.True(seen.Count > 3, $"expected a varied treemap, got {seen.Count} distinct colours");
    }

    [Fact]
    public void HoverHighlightChangesOutput()
    {
        LayoutResult layout = Layout();
        Assert.True(layout.TryGetIndex(10, out int hoverCell));

        using TreemapRenderer renderer = new();

        using SKBitmap plain = new(400, 300);
        using SKCanvas plainCanvas = new(plain);
        renderer.DrawLayout(plainCanvas, layout, new RenderState { HoveredNodeId = -1 });

        using SKBitmap hovered = new(400, 300);
        using SKCanvas hoveredCanvas = new(hovered);
        renderer.DrawLayout(hoveredCanvas, layout, new RenderState { HoveredNodeId = 10 });

        // Compare the hovered cell's own pixels. A white outline replaces the cell's
        // fill/border colour, so at least one pixel must differ.
        RectF bounds = layout.Bounds[hoverCell];
        int differing = 0;

        for (int y = Math.Max(0, (int)bounds.Top); y < Math.Min(300, (int)bounds.Bottom); y++)
        {
            for (int x = Math.Max(0, (int)bounds.Left); x < Math.Min(400, (int)bounds.Right); x++)
            {
                if (plain.GetPixel(x, y) != hovered.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 0, "hover highlight should change pixels inside the hovered cell");
    }

    [Fact]
    public void LabelsCanBeDisabled()
    {
        LayoutResult layout = Layout();

        using SKBitmap withLabels = new(400, 300);
        using SKCanvas a = new(withLabels);
        using TreemapRenderer renderer = new();
        renderer.DrawLayout(a, layout, new RenderState { ShowLabels = true });

        using SKBitmap withoutLabels = new(400, 300);
        using SKCanvas b = new(withoutLabels);
        renderer.DrawLayout(b, layout, new RenderState { ShowLabels = false });

        Assert.NotEqual(CountDistinct(withLabels), CountDistinct(withoutLabels));
    }

    [Fact]
    public void EmptyLayoutRendersCleanly()
    {
        using SKBitmap bitmap = new(200, 200);
        using SKCanvas canvas = new(bitmap);
        using TreemapRenderer renderer = new();

        renderer.DrawLayout(canvas, LayoutResult.Empty, new RenderState());
    }

    [Fact]
    public void EncodesToPng()
    {
        using SKBitmap bitmap = new(200, 200);
        using SKCanvas canvas = new(bitmap);
        using TreemapRenderer renderer = new();
        renderer.DrawLayout(canvas, Layout(), new RenderState());

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

        Assert.True(data.Size > 0);
        Assert.False(data.IsEmpty);

        // PNG magic number, confirming real encoded output rather than an empty blob.
        ReadOnlySpan<byte> bytes = data.Span;
        Assert.True(bytes.Length > 8);
        Assert.Equal((byte)0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public void PaletteResolvesDistinctColoursPerGroup()
    {
        TreemapPalette palette = TreemapPalette.Default;

        SKColor a = palette.Resolve(NodeKind.File, 0, 1);
        SKColor b = palette.Resolve(NodeKind.File, 1, 1);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FreeSpaceUsesItsOwnColour()
    {
        TreemapPalette palette = TreemapPalette.Default;

        SKColor free = palette.Resolve(NodeKind.FreeSpace, 0, 1);
        SKColor file = palette.Resolve(NodeKind.File, 0, 1);

        Assert.NotEqual(free, file);
    }

    [Fact]
    public void AlphaIsAppliedToFills()
    {
        TreemapPalette palette = TreemapPalette.Default;

        SKColor opaque = palette.Resolve(NodeKind.File, 0, 1, alpha: 1f);
        SKColor faded = palette.Resolve(NodeKind.File, 0, 1, alpha: 0.5f);

        // 255 * 0.5 = 127.5, truncated to 127 by the byte cast.
        int expectedHalf = opaque.Alpha / 2;
        Assert.True(
            Math.Abs(expectedHalf - faded.Alpha) <= 1,
            $"expected ~{expectedHalf}, got {faded.Alpha}");
        Assert.True(faded.Alpha < opaque.Alpha, "faded cell must be more transparent");
    }

    [Fact]
    public void ContrastPicksReadableTextColour()
    {
        Assert.Equal(SKColors.Black, TreemapPalette.ContrastColor(SKColors.White));
        Assert.Equal(SKColors.White, TreemapPalette.ContrastColor(SKColors.Black));
    }

    [Fact]
    public void TweenedFrameRenders()
    {
        TreemapNode before = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0,
            [TreemapNode.Create(2, "x", NodeKind.File, 100, 100)]);

        TreemapNode after = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0,
            [
                TreemapNode.Create(2, "x", NodeKind.File, 100, 100),
                TreemapNode.Create(3, "y", NodeKind.File, 900, 900),
            ]);

        RectF bounds = new(0, 0, 400, 300);
        LayoutTween tween = new(
            SquarifyLayout.Compute(before, bounds),
            SquarifyLayout.Compute(after, bounds));

        using SKBitmap bitmap = new(400, 300);
        using SKCanvas canvas = new(bitmap);
        using TreemapRenderer renderer = new();

        List<TweenedCell> frame = [];
        for (int step = 0; step <= 4; step++)
        {
            tween.Evaluate(step / 4f, frame);
            renderer.Draw(canvas, frame, new RenderState());
        }

        Assert.True(CountDistinct(bitmap) > 3);
    }

    /// <summary>
    /// The canvas transform must reproduce <see cref="TreemapCamera.ContentToScreen"/>
    /// exactly, or hit-testing will disagree with what is drawn.
    ///
    /// <para>
    /// Skia concatenates onto the CTM, so the calls must be Translate-then-Scale.
    /// The reverse order also scales the pan, making a drag move the view by
    /// <c>dx * zoom</c> and breaking hit-test symmetry. This test pins that contract.
    /// </para>
    /// </summary>
    [Fact]
    public void TranslateThenScaleMatchesCameraProjection()
    {
        const int size = 400;
        const float zoom = 2f;
        const float panX = 30f;
        const float panY = 40f;

        TreemapCamera camera = new() { Zoom = zoom, Pan = new PointF(panX, panY) };

        RectF contentRect = new(100, 100, 10, 10);
        RectF expected = camera.ContentToScreen(contentRect);

        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        // Order matters: Translate, then Scale.
        canvas.Translate(panX, panY);
        canvas.Scale(zoom);

        using SKPaint paint = new() { Color = SKColors.White };
        canvas.DrawRect(new SKRect(contentRect.Left, contentRect.Top, contentRect.Right, contentRect.Bottom), paint);

        int minX = int.MaxValue, minY = int.MaxValue;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (bitmap.GetPixel(x, y).Red > 128)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                }
            }
        }

        Assert.Equal(expected.Left, minX, precision: 1);
        Assert.Equal(expected.Top, minY, precision: 1);
    }

    /// <summary>
    /// Pins the wrong ordering's actual result, so the inversion cannot silently
    /// return. This is the specific mistake that makes "drag moves faster when
    /// zoomed in".
    /// </summary>
    [Fact]
    public void ScaleThenTranslateWouldScaleThePan()
    {
        using SKBitmap bitmap = new(400, 400);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        canvas.Scale(2f);
        canvas.Translate(30f, 0f);

        using SKPaint paint = new() { Color = SKColors.White };
        canvas.DrawRect(new SKRect(100, 100, 110, 110), paint);

        int minX = int.MaxValue;
        for (int y = 0; y < 400; y++)
        {
            for (int x = 0; x < 400; x++)
            {
                if (bitmap.GetPixel(x, y).Red > 128 && x < minX)
                {
                    minX = x;
                }
            }
        }

        // Correct order yields 100*2 + 30 = 230; the wrong order yields 260.
        Assert.Equal(260, minX);
    }

    private static int CountDistinct(SKBitmap bitmap)
    {
        HashSet<SKColor> seen = [];
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                seen.Add(bitmap.GetPixel(x, y));
            }
        }

        return seen.Count;
    }

    // ---- hover ancestry ----

    private static TweenedCell FrameCell(int id, RectF bounds, int depth, int parent)
        => new()
        {
            NodeId = id,
            Bounds = bounds,
            Alpha = 1f,
            Depth = depth,
            Kind = NodeKind.Folder,
            ColorGroup = 0,
            AreaFraction = 1f,
            HasChildren = true,
            IsEntering = false,
            ParentIndex = parent,
        };

    private static int IndexOfFrame(IReadOnlyList<TweenedCell> frame, int nodeId)
    {
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].NodeId == nodeId)
            {
                return i;
            }
        }

        Assert.Fail($"node {nodeId} is not in the frame");
        return -1;
    }

    private static int CountWhite(SKBitmap bitmap, in RectF region, in RectF exclude = default)
    {
        int count = 0;
        int x0 = Math.Max(0, (int)MathF.Round(region.Left));
        int y0 = Math.Max(0, (int)MathF.Round(region.Top));
        int x1 = Math.Min(bitmap.Width, (int)MathF.Round(region.Right));
        int y1 = Math.Min(bitmap.Height, (int)MathF.Round(region.Bottom));

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (exclude.Contains(x + 0.5f, y + 0.5f))
                {
                    continue;
                }

                if (bitmap.GetPixel(x, y) == SKColors.White)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKBitmap RenderFrame(
        TreemapRenderer renderer, IReadOnlyList<TweenedCell> frame, int hoveredNodeId, int size, bool shadow = false)
    {
        SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        renderer.Draw(canvas, frame, new RenderState
        {
            HoveredNodeId = hoveredNodeId,
            HighlightShadow = shadow,
            ShowLabels = false,
        });

        return bitmap;
    }

    /// <summary>
    /// A cell that geometrically contains the hovered one is not necessarily an
    /// ancestor of it. Mid-transition, where a child has been reparented, it very
    /// definitely is not — so containment must not drive the highlight.
    /// </summary>
    [Fact]
    public void HoverOutlineSkipsAContainerThatIsNotAnAncestor()
    {
        RectF container = new(0, 0, 120, 120);
        RectF child = new(40, 40, 40, 40);

        // child sits inside container, but its parent index says it has none.
        List<TweenedCell> frame =
        [
            FrameCell(1, container, depth: 0, parent: -1),
            FrameCell(2, child, depth: 1, parent: -1),
        ];

        using TreemapRenderer renderer = new();
        using SKBitmap hovered = RenderFrame(renderer, frame, hoveredNodeId: 2, size: 160);

        Assert.True(CountWhite(hovered, child) > 0, "the hovered cell itself must be outlined");
        Assert.Equal(0, CountWhite(hovered, container, child.Inset(-4f, -4f)));
    }

    /// <summary>
    /// The complementary case: the declared ancestor is drawn where its bounds do
    /// not contain the child at all, because the two are animating towards different
    /// rectangles. The outline has to follow the relationship, not the geometry.
    /// </summary>
    [Fact]
    public void HoverOutlineFollowsAnAncestorThatDoesNotContainTheChild()
    {
        RectF ancestor = new(10, 10, 100, 100);
        RectF child = new(150, 20, 40, 40);

        Assert.False(
            ancestor.Left <= child.Left && ancestor.Top <= child.Top
            && ancestor.Right >= child.Right && ancestor.Bottom >= child.Bottom,
            "the fixture must place the child outside its ancestor");

        List<TweenedCell> frame =
        [
            FrameCell(1, ancestor, depth: 0, parent: -1),
            FrameCell(2, child, depth: 1, parent: 0),
        ];

        using TreemapRenderer renderer = new();
        using SKBitmap plain = RenderFrame(renderer, frame, hoveredNodeId: -1, size: 200);
        using SKBitmap hovered = RenderFrame(renderer, frame, hoveredNodeId: 2, size: 200);

        Assert.Equal(0, CountWhite(plain, ancestor));
        Assert.True(CountWhite(hovered, ancestor) > 0, "the declared ancestor must be outlined anyway");
    }

    /// <summary>
    /// A cell that has faded out is not drawn, so a stale hover id must not leave a
    /// floating outline and shadow over whatever replaced it.
    /// </summary>
    [Fact]
    public void HoverOnACellThatHasFadedOutDrawsNothing()
    {
        RectF ghost = new(20, 20, 60, 60);
        RectF replacement = new(0, 0, 100, 100);

        List<TweenedCell> frame =
        [
            FrameCell(1, replacement, depth: 0, parent: -1),
            new()
            {
                NodeId = 2,
                Bounds = ghost,
                Alpha = 0f,
                Depth = 1,
                Kind = NodeKind.File,
                ColorGroup = 1,
                AreaFraction = 0.1f,
                HasChildren = false,
                IsEntering = false,
                ParentIndex = 0,
            },
        ];

        using TreemapRenderer renderer = new();
        using SKBitmap plain = RenderFrame(renderer, frame, hoveredNodeId: -1, size: 140, shadow: true);
        using SKBitmap hovered = RenderFrame(renderer, frame, hoveredNodeId: 2, size: 140, shadow: true);

        for (int y = 0; y < 140; y++)
        {
            for (int x = 0; x < 140; x++)
            {
                Assert.True(
                    plain.GetPixel(x, y) == hovered.GetPixel(x, y),
                    $"pixel ({x},{y}) changed for a cell that was not drawn");
            }
        }
    }

    /// <summary>
    /// The pixel consequence of draw order: at progress 0 the retained leaf is
    /// painted after the departing folder that used to contain it, so it is the
    /// leaf's colour on screen rather than the folder's.
    /// </summary>
    [Fact]
    public void DepartingParentIsNotPaintedOverItsRetainedChild()
    {
        TreemapNode before = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0,
            [
                TreemapNode.Create(2, "wrap", NodeKind.Folder, 0, 0, null, 0,
                    [TreemapNode.Create(3, "leaf.bin", NodeKind.File, 500, 500, null, 3)]),
            ]);

        TreemapNode after = TreemapNode.Create(1, "root", NodeKind.Drive, 0, 0, null, 0,
            [TreemapNode.Create(3, "leaf.bin", NodeKind.File, 500, 500, null, 3)]);

        RectF bounds = new(0, 0, 120, 120);
        LayoutOptions options = new() { InnerPadding = 1f };

        LayoutTween tween = new(
            SquarifyLayout.Compute(before, bounds, options),
            SquarifyLayout.Compute(after, bounds, options));

        List<TweenedCell> frame = [];
        tween.Evaluate(0f, frame);

        int departing = IndexOfFrame(frame, 2);
        int leaf = IndexOfFrame(frame, 3);
        Assert.True(departing < leaf, "the departing folder must be painted first");

        using SKBitmap bitmap = new(120, 120);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        using TreemapRenderer renderer = new();
        renderer.Draw(canvas, frame, new RenderState { ShowLabels = false });

        RectF leafBounds = frame[leaf].Bounds;
        int cx = (int)(leafBounds.X + (leafBounds.Width / 2f));
        int cy = (int)(leafBounds.Y + (leafBounds.Height / 2f));

        TreemapPalette palette = TreemapPalette.Default;

        SKColor leafColour = palette.Resolve(
            frame[leaf].Kind, frame[leaf].ColorGroup, frame[leaf].Depth, frame[leaf].Alpha);

        SKColor folderColour = palette.Resolve(
            frame[departing].Kind, frame[departing].ColorGroup, frame[departing].Depth, frame[departing].Alpha);

        // The two are genuinely different, so the assertion below can only pass if
        // the leaf was painted on top rather than hidden behind its old parent.
        Assert.NotEqual(leafColour, folderColour);
        Assert.Equal(leafColour, bitmap.GetPixel(cx, cy));
    }
}
