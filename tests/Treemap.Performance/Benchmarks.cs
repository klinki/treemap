using BenchmarkDotNet.Attributes;
using SkiaSharp;
using Treemap;
using Treemap.Skia;

namespace Treemap.Performance;

/// <summary>
/// Builds each tree on demand and retains a bounded number of them, so no benchmark
/// pays to build a tree it is not measuring.
/// </summary>
/// <remarks>
/// Trees are evicted oldest-first once <c>MaxRetained</c> is reached, and freed when
/// the cache is disposed.
/// </remarks>
public sealed class TreeCache : IDisposable
{
    private readonly Dictionary<(int Files, int Seed), TreemapNode> _cache = [];
    private readonly Stack<(int Files, int Seed)> _eviction = new();

    /// <summary>Maximum trees retained concurrently. Two is enough for before/after pairs.</summary>
    private const int MaxRetained = 2;

    public TreemapNode Get(int fileCount, int seed = 12345)
    {
        if (fileCount > PerfConfig.MaxFileCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileCount),
                fileCount,
                $"Exceeds PerfConfig.MaxFileCount ({PerfConfig.MaxFileCount}). Raise the cap " +
                "deliberately; large trees are the main source of memory pressure here.");
        }

        (int Files, int Seed) key = (fileCount, seed);

        if (_cache.TryGetValue(key, out TreemapNode? cached))
        {
            return cached;
        }

        TreemapNode built = SyntheticTree.Build(fileCount, seed);

        // Evict oldest entries until there is room for the new one.
        while (_cache.Count >= MaxRetained && _eviction.Count > 0)
        {
            (int Files, int Seed) oldest = _eviction.Pop();
            _cache.Remove(oldest);
        }

        _cache[key] = built;
        _eviction.Push(key);
        return built;
    }

    public void Dispose() => _cache.Clear();
}

/// <summary>
/// Layout throughput: the squarified treemap over large trees.
/// </summary>
/// <remarks>
/// Sizes stop at 100K nodes, which is the largest case that stays inside the memory
/// budget these benchmarks run under. <c>PerfConfig.MaxFileCount</c> is the hard cap.
/// </remarks>
[Config(typeof(PerfConfig))]
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    private readonly TreeCache _trees = new();
    private TreemapNode _tree = null!;

    private static RectF Viewport => new(0, 0, 1920, 1080);

    [GlobalCleanup]
    public void Cleanup() => _trees.Dispose();

    [Benchmark(Baseline = true, Description = "Layout 10K nodes")]
    public LayoutResult Layout10K()
    {
        _tree = _trees.Get(10_000);
        return SquarifyLayout.Compute(_tree, Viewport);
    }

    [Benchmark(Description = "Layout 50K nodes")]
    public LayoutResult Layout50K()
    {
        _tree = _trees.Get(50_000);
        return SquarifyLayout.Compute(_tree, Viewport);
    }

    [Benchmark(Description = "Layout 100K nodes")]
    public LayoutResult Layout100K()
    {
        _tree = _trees.Get(100_000);
        return SquarifyLayout.Compute(_tree, Viewport);
    }

    /// <summary>
    /// High detail keeps more cells alive, so culling does less work and the renderer
    /// has more to draw. Worst realistic case.
    /// </summary>
    [Benchmark(Description = "Layout 50K nodes, high detail")]
    public LayoutResult Layout50KHighDetail()
    {
        _tree = _trees.Get(50_000);
        return SquarifyLayout.Compute(_tree, Viewport, LayoutOptions.HighDetail);
    }
}

/// <summary>
/// Render throughput: painting an already-laid-out frame to an SKCanvas.
/// </summary>
[Config(typeof(PerfConfig))]
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;

    private readonly TreeCache _trees = new();
    private TreemapNode _tree = null!;

    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;
    private TreemapRenderer _renderer = null!;

    private LayoutResult _layout = null!;
    private List<TweenedCell> _frame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new SKBitmap(Width, Height);
        _canvas = new SKCanvas(_bitmap);
        _renderer = new TreemapRenderer { NameResolver = id => $"node-{id}.dll" };

        // Sized to keep the resident layout well under the heap cap PerfConfig sets.
        _tree = _trees.Get(50_000);
        _layout = SquarifyLayout.Compute(_tree, new RectF(0, 0, Width, Height), new LayoutOptions { InnerPadding = 1f });
        _frame = RenderBenchmarksShared.ToFrame(_layout);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _renderer.Dispose();
        _trees.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Render 1920x1080, labels on")]
    public void RenderWithLabels()
        => _renderer.DrawLayout(_canvas, _layout, new RenderState { ShowLabels = true });

    [Benchmark(Description = "Render 1920x1080, labels off")]
    public void RenderWithoutLabels()
        => _renderer.DrawLayout(_canvas, _layout, new RenderState { ShowLabels = false });

    /// <summary>The animated path, which is what actually runs at 60fps during a transition.</summary>
    [Benchmark(Description = "Render tweened frame (animation path)")]
    public void RenderTweenedFrame()
        => _renderer.Draw(_canvas, _frame, new RenderState { ShowLabels = true });
}

internal static class RenderBenchmarksShared
{
    internal static List<TweenedCell> ToFrame(LayoutResult layout)
    {
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
                ParentIndex = layout.ParentIndices[i],
            });
        }

        return frame;
    }
}

/// <summary>
/// Interaction latency: hit-testing and camera transforms, both per-frame operations.
/// </summary>
[Config(typeof(PerfConfig))]
[MemoryDiagnoser]
public class InteractionBenchmarks
{
    private readonly TreeCache _trees = new();
    private LayoutResult _layout = null!;
    private TreemapCamera _camera = null!;
    private float _seed = 0.5f;

    [GlobalSetup]
    public void Setup()
    {
        TreemapNode tree = _trees.Get(50_000);
        _layout = SquarifyLayout.Compute(tree, new RectF(0, 0, 1920, 1080), new LayoutOptions { InnerPadding = 1f });
        _camera = new TreemapCamera { Zoom = 1f };
    }

    [GlobalCleanup]
    public void Cleanup() => _trees.Dispose();

    [Benchmark(Baseline = true, Description = "HitTest deepest cell")]
    public int HitTest()
    {
        // Vary the probe so the branch predictor cannot memoise the answer.
        _seed = (_seed + 0.6180339887f) % 1f;
        return HitTester.HitTest(_layout, _seed * 1920f, ((_seed * 7919f) % 1f) * 1080f);
    }

    [Benchmark(Description = "HitTest skipping space cells")]
    public int HitTestContent()
    {
        _seed = (_seed + 0.6180339887f) % 1f;
        return HitTester.HitTestContent(_layout, _seed * 1920f, ((_seed * 7919f) % 1f) * 1080f);
    }

    [Benchmark(Description = "Camera screen->content (per frame)")]
    public PointF CameraInverse() => _camera.ScreenToContent(new PointF(_seed * 1920f, 480f));

    [Benchmark(Description = "ZoomAt anchored (per frame)")]
    public void CameraZoom() => _camera.ZoomAt(1.01f, new PointF(960f, 540f));
}

/// <summary>
/// Animation cost: evaluating a layout tween, which runs every frame during a transition.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PerfConfig))]
public class TweenBenchmarks
{
    private LayoutTween _tween = null!;
    private readonly List<TweenedCell> _frame = [];

    [GlobalSetup]
    public void Setup()
    {
        // Two 20K trees held briefly as a before/after pair, then released.
        TreemapNode before = SyntheticTree.Build(20_000, seed: 1);
        TreemapNode after = SyntheticTree.Build(20_000, seed: 2);

        RectF viewport = new(0, 0, 1920, 1080);
        LayoutOptions options = new() { InnerPadding = 1f };

        // Draw order, parent links and depths are resolved by the constructor, so
        // the steady-state cost below is a single pass over the cell list.
        _tween = new LayoutTween(
            SquarifyLayout.Compute(before, viewport, options),
            SquarifyLayout.Compute(after, viewport, options));
    }

    /// <summary>
    /// The hot path the MemoryDiagnoser is aimed at. The tween resolves its draw
    /// order once at construction, so <c>Evaluate</c> is expected to report no
    /// per-call allocation; a non-zero figure here means ordering or sorting crept
    /// back into the per-frame path.
    /// </summary>
    [Benchmark(Baseline = true, Description = "LayoutTween.Evaluate (per frame)")]
    public int Evaluate()
    {
        _tween.Evaluate(0.5f, _frame);
        return _frame.Count;
    }
}

/// <summary>
/// Snapshot publication: the scan-side hot loop that feeds the render thread.
/// </summary>
[Config(typeof(PerfConfig))]
[MemoryDiagnoser]
public class SnapshotBenchmarks
{
    private readonly TreeCache _trees = new();
    private TreemapNode _tree = null!;
    private SnapshotPublisher _publisher = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tree = _trees.Get(50_000);
        _publisher = new SnapshotPublisher(_tree);
    }

    [GlobalCleanup]
    public void Cleanup() => _trees.Dispose();

    [Benchmark(Baseline = true, Description = "Publish 50K-node snapshot")]
    public TreemapSnapshot Publish() => _publisher.Publish(_tree);

    [Benchmark(Description = "Read Current snapshot")]
    public TreemapSnapshot ReadCurrent() => _publisher.Current;
}
