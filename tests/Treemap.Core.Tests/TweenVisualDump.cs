using Treemap;
using Xunit;
using Xunit.Abstractions;

namespace Treemap.Core.Tests;

/// <summary>
/// Prints an animation strip so the tween can be judged visually.
/// </summary>
public class TweenVisualDump
{
    private readonly ITestOutputHelper _output;

    public TweenVisualDump(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpTweenFrames()
    {
        // A file grows substantially between scans; the treemap should ease into it.
        TreemapNode from = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "alpha.bin", 100),
            TreeBuilder.File(3, "beta.bin", 100),
            TreeBuilder.File(4, "gamma.bin", 100));

        TreemapNode to = TreeBuilder.Folder(1, "root",
            TreeBuilder.File(2, "alpha.bin", 100),
            TreeBuilder.File(3, "beta.bin", 100),
            TreeBuilder.File(4, "gamma.bin", 2400));

        RectF bounds = new(0, 0, 60, 14);
        LayoutResult fromLayout = SquarifyLayout.Compute(from, bounds);
        LayoutResult toLayout = SquarifyLayout.Compute(to, bounds);

        LayoutTween tween = new(fromLayout, toLayout);
        List<TweenedCell> frame = [];

        float[] steps = [0f, 0.15f, 0.3f, 0.45f, 0.6f, 0.8f, 1f];

        foreach (float step in steps)
        {
            tween.Evaluate(step, frame);
            Render(frame, step, bounds);
        }
    }

    private void Render(List<TweenedCell> frame, float progress, RectF bounds)
    {
        const int cols = 60;
        const int rows = 14;

        char[,] grid = new char[rows, cols];

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                grid[r, c] = ' ';
            }
        }

        // Map node id -> glyph, stable across frames.
        var glyphs = new Dictionary<int, char>();
        foreach (TweenedCell cell in frame)
        {
            if (!glyphs.ContainsKey(cell.NodeId))
            {
                glyphs[cell.NodeId] = (char)('a' + (glyphs.Count % 26));
            }
        }

        foreach (TweenedCell cell in frame.OrderBy(c => c.Depth))
        {
            if (cell.Alpha < 0.15f)
            {
                continue;
            }

            char g = glyphs[cell.NodeId];

            int x0 = Math.Clamp((int)MathF.Round(cell.Bounds.Left), 0, cols);
            int y0 = Math.Clamp((int)MathF.Round(cell.Bounds.Top), 0, rows);
            int x1 = Math.Clamp((int)MathF.Round(cell.Bounds.Right), 0, cols);
            int y1 = Math.Clamp((int)MathF.Round(cell.Bounds.Bottom), 0, rows);

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    grid[y, x] = g;
                }
            }
        }

        float eased = Easing.CubicInOut(progress);
        _output.WriteLine($"progress={progress:0.00} (eased={eased:0.00})");

        for (int r = 0; r < rows; r++)
        {
            string line = new string(Enumerable.Range(0, cols).Select(c => grid[r, c]).ToArray());
            _output.WriteLine("  |" + line + "|");
        }

        _output.WriteLine(string.Empty);
    }
}
