using Treemap;
using Xunit;

namespace Treemap.Core.Tests;

public class CameraTests
{
    [Fact]
    public void ScreenToContentIsInverseOfContentToScreen()
    {
        TreemapCamera camera = new() { Zoom = 2.5f, Pan = new PointF(37f, -12f) };

        PointF original = new(120f, 80f);
        PointF screen = camera.ContentToScreen(original);
        PointF roundTrip = camera.ScreenToContent(screen);

        Assert.Equal(original.X, roundTrip.X, precision: 3);
        Assert.Equal(original.Y, roundTrip.Y, precision: 3);
    }

    [Fact]
    public void RectRoundTripIsInverse()
    {
        TreemapCamera camera = new() { Zoom = 1.75f, Pan = new PointF(10f, 20f) };

        RectF original = new(5f, 7f, 30f, 40f);
        RectF roundTrip = camera.ScreenToContent(camera.ContentToScreen(original));

        Assert.Equal(original.X, roundTrip.X, precision: 3);
        Assert.Equal(original.Y, roundTrip.Y, precision: 3);
        Assert.Equal(original.Width, roundTrip.Width, precision: 3);
        Assert.Equal(original.Height, roundTrip.Height, precision: 3);
    }

    [Fact]
    public void ZoomAtKeepsAnchorPointStationary()
    {
        TreemapCamera camera = new();
        PointF anchor = new(400f, 300f);

        PointF before = camera.ScreenToContent(anchor);
        camera.ZoomAt(2f, anchor);
        PointF after = camera.ScreenToContent(anchor);

        Assert.Equal(before.X, after.X, precision: 3);
        Assert.Equal(before.Y, after.Y, precision: 3);
    }

    [Fact]
    public void ZoomIsClampedToLimits()
    {
        TreemapCamera camera = new() { MinZoom = 0.5f, MaxZoom = 4f };

        camera.ZoomAt(1000f, new PointF(0, 0));
        Assert.Equal(4f, camera.Zoom, precision: 3);

        camera.ZoomAt(0.0001f, new PointF(0, 0));
        Assert.Equal(0.5f, camera.Zoom, precision: 3);
    }

    [Fact]
    public void PanByMovesViewWithoutChangingZoom()
    {
        TreemapCamera camera = new() { Zoom = 2f };
        float zoom = camera.Zoom;

        camera.PanBy(25f, -10f);

        Assert.Equal(zoom, camera.Zoom, precision: 3);
        Assert.Equal(25f, camera.Pan.X, precision: 3);
        Assert.Equal(-10f, camera.Pan.Y, precision: 3);
    }

    [Fact]
    public void FitToContentCentresAndFits()
    {
        TreemapCamera camera = new();
        RectF viewport = new(0, 0, 800, 600);
        RectF content = new(0, 0, 100, 100);

        camera.FitToContent(content, viewport, margin: 1f);

        // Square content in a wider viewport: limited by height.
        Assert.Equal(6f, camera.Zoom, precision: 2);

        RectF onScreen = camera.ContentToScreen(content);

        // Horizontally centred.
        float centreOffset = (onScreen.Left + (onScreen.Width * 0.5f)) - (viewport.Left + (viewport.Width * 0.5f));
        Assert.Equal(0f, centreOffset, precision: 2);

        // Vertically fills.
        Assert.Equal(viewport.Height, onScreen.Height, precision: 2);
    }

    [Fact]
    public void FitToContentRespectsMargin()
    {
        TreemapCamera camera = new();
        RectF viewport = new(0, 0, 800, 600);

        camera.FitToContent(new RectF(0, 0, 100, 100), viewport, margin: 0.5f);

        Assert.Equal(3f, camera.Zoom, precision: 2);
    }

    [Fact]
    public void CaptureAndApplyRoundTrip()
    {
        TreemapCamera camera = new() { Zoom = 3.3f, Pan = new PointF(11f, 22f) };
        CameraTransform captured = camera.Capture();

        camera.Zoom = 1f;
        camera.Pan = default;

        camera.Apply(captured);

        Assert.Equal(3.3f, camera.Zoom, precision: 3);
        Assert.Equal(11f, camera.Pan.X, precision: 3);
        Assert.Equal(22f, camera.Pan.Y, precision: 3);
    }

    [Fact]
    public void CameraTransformLerpInterpolatesBothComponents()
    {
        CameraTransform a = new(1f, new PointF(0f, 0f));
        CameraTransform b = new(3f, new PointF(20f, 40f));

        CameraTransform mid = CameraTransform.Lerp(a, b, 0.5f);

        Assert.Equal(2f, mid.Zoom, precision: 3);
        Assert.Equal(10f, mid.Pan.X, precision: 3);
        Assert.Equal(20f, mid.Pan.Y, precision: 3);
    }

    [Fact]
    public void ResetRestoresIdentity()
    {
        TreemapCamera camera = new() { Zoom = 5f, Pan = new PointF(50f, 50f) };
        camera.Reset();

        Assert.Equal(1f, camera.Zoom, precision: 3);
        Assert.Equal(0f, camera.Pan.X, precision: 3);
        Assert.Equal(0f, camera.Pan.Y, precision: 3);
    }
}
