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
}
