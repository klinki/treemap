using Treemap;
using Xunit;
using Xunit.Abstractions;

namespace Treemap.Core.Tests;

/// <summary>
/// Renders an ASCII treemap so layout behaviour can be eyeballed directly.
/// Useful when a numeric invariant passes but the result still looks wrong.
/// </summary>
public class AsciiLayoutDump
{
    private readonly ITestOutputHelper _output;

    public AsciiLayoutDump(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpSquarifiedLayout()
    {
        TreemapNode root = TreeBuilder.Folder(1, "root",
            TreeBuilder.Folder(2, "src",
                TreeBuilder.File(3, "Program.cs", 1200),
                TreeBuilder.File(4, "Engine.cs", 3400),
                TreeBuilder.File(5, "Util.cs", 800)),
            TreeBuilder.Folder(6, "assets",
                TreeBuilder.File(7, "logo.png", 8800),
                TreeBuilder.File(8, "font.ttf", 4500),
                TreeBuilder.File(9, "icon.svg", 300)),
            TreeBuilder.Folder(10, "docs",
                TreeBuilder.File(11, "readme.md", 600),
                TreeBuilder.File(12, "guide.pdf", 2200)));

        RectF bounds = new(0, 0, 64, 22);

        // A small inner padding reveals nesting: without it, a node's children tile
        // its rect exactly and completely hide it, which is correct but unreadable.
        LayoutResult layout = SquarifyLayout.Compute(root, bounds, new LayoutOptions
        {
            InnerPadding = 1f,
        });

        const int cols = 64;
        const int rows = 22;

        char[,] grid = new char[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                grid[r, c] = ' ';
            }
        }

        // Render one depth level at a time: fill the whole canvas with the shallowest
        // cells, then let the NEXT level overwrite only its own parent's footprint.
        // Painting deepest-first would let the root (depth 0) cover the entire canvas.
        const int renderDepth = 2;

        var topLevel = new Dictionary<int, char>();
        int nextLetter = 0;

        // Assign a glyph to every depth-1 cell up front.
        for (int i = 0; i < layout.Count; i++)
        {
            if (layout.Depths[i] == 1)
            {
                topLevel[layout.NodeIds[i]] = (char)('A' + (nextLetter++ % 26));
            }
        }

        // Level-by-level fill, root excluded.
        for (int level = 1; level <= renderDepth; level++)
        {
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout.Depths[i] != level)
                {
                    continue;
                }

                char glyph = level == 1
                    ? topLevel[layout.NodeIds[i]]
                    : '#';

                RectF b = layout.Bounds[i];
                int x0 = Math.Max(0, (int)MathF.Round(b.Left));
                int y0 = Math.Max(0, (int)MathF.Round(b.Top));
                int x1 = Math.Min(cols, (int)MathF.Round(b.Right));
                int y1 = Math.Min(rows, (int)MathF.Round(b.Bottom));

                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        grid[y, x] = glyph;
                    }
                }
            }
        }

        _output.WriteLine("Squarified treemap (A.. = depth 1, # = depth 2, . = depth 3, : = depth 4):");
        _output.WriteLine(new string('+', cols) + "+");
        for (int r = 0; r < rows; r++)
        {
            string line = new string(Enumerable.Range(0, cols).Select(c => grid[r, c]).ToArray());
            _output.WriteLine("|" + line + "|");
        }

        _output.WriteLine(new string('+', cols) + "+");

        for (int i = 0; i < layout.Count; i++)
        {
            RectF b = layout.Bounds[i];
            double aspect = b.Height <= 0 ? 0 : Math.Max(b.Width / b.Height, b.Height / b.Width);
            _output.WriteLine($"  depth={layout.Depths[i]} id={layout.NodeIds[i],3}  {b}  aspect={aspect:0.00}");
        }

        Assert.True(layout.Count > 0);
    }
}
