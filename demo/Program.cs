using SkiaSharp;
using Treemap;
using Treemap.Skia;

namespace Demo;

/// <summary>
/// Renders sample treemaps to PNG so the visual output can be inspected.
/// </summary>
internal static class Program
{
    private static int _nextId = 1;

    /// <summary>Node id to display name, for the renderer's label fitting.</summary>
    private static readonly Dictionary<int, string> Names = [];

    private static TreemapNode NodeFile(string name, long size, int colorGroup = 0)
    {
        int id = _nextId++;
        Names[id] = name;
        return TreemapNode.Create(id, name, NodeKind.File, size, size, DateTimeOffset.UnixEpoch, colorGroup);
    }

    private static TreemapNode Folder(string name, params TreemapNode[] children)
    {
        int id = _nextId++;
        Names[id] = name;
        return TreemapNode.Create(id, name, NodeKind.Folder, 0, 0, null, 0, children);
    }

    private static TreemapNode Space(string name, NodeKind kind, long size)
    {
        int id = _nextId++;
        Names[id] = name;
        return TreemapNode.Create(id, name, kind, size, size, null, 0);
    }

    private static TreemapNode Drive(string name, params TreemapNode[] children)
    {
        int id = _nextId++;
        Names[id] = name;
        return TreemapNode.Create(id, name, NodeKind.Drive, 0, 0, null, 0, children);
    }

    private static void Main()
    {
        string outputDir = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(outputDir);

        Names.Clear();
        TreemapNode drive = BuildDrive();
        int hoverId = Names.First(n => n.Value.Contains("SpaceSniffer2", StringComparison.OrdinalIgnoreCase)).Key;

        RenderSample("01-drive.png", drive, hoverNodeId: -1);
        RenderSample("02-hover.png", drive, hoverNodeId: hoverId);
        RenderSample("03-after-growth.png", BuildAnimatedTarget(), hoverNodeId: -1);

        Console.WriteLine($"Wrote PNGs to {outputDir}");
    }

    private static void RenderSample(string fileName, TreemapNode root, int hoverNodeId)
    {
        const int width = 900;
        const int height = 560;

        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Black);

        using TreemapRenderer renderer = new()
        {
            NameResolver = id => Names.TryGetValue(id, out string? n) ? n : id.ToString(),
        };

        LayoutResult layout = SquarifyLayout.Compute(
            root,
            new RectF(0, 0, width, height),
            new LayoutOptions { InnerPadding = 1.5f, MinCellSize = 2f });

        RenderState state = new()
        {
            HoveredNodeId = hoverNodeId,
            ShowLabels = true,
        };

        renderer.DrawLayout(canvas, layout, state);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

        string path = Path.Combine(AppContext.BaseDirectory, "output", fileName);
        using FileStream fs = System.IO.File.Create(path);
        data.SaveTo(fs);

        Console.WriteLine($"  {fileName}: {layout.Count} cells");

        for (int i = 0; i < layout.Count; i++)
        {
            if (layout.Depths[i] <= 1)
            {
                Console.WriteLine(
                    $"      depth={layout.Depths[i]} id={layout.NodeIds[i],3} " +
                    $"{layout.Kinds[i],-13} {layout.Bounds[i]} " +
                    $"area={layout.Bounds[i].Area / (width * (float)height):P1}");
            }
        }
    }

    private static TreemapNode BuildDrive()
        => Drive("Local Disk (C:)",
            Folder("Windows",
                Folder("System32",
                    NodeFile("kernel32.dll", 1_240_000, 1),
                    NodeFile("ntdll.dll", 2_180_000, 1),
                    NodeFile("drivers.acl", 0, 1)),
                NodeFile("explorer.exe", 5_100_000, 1),
                NodeFile("notepad.exe", 201_000, 1)),
            Folder("Program Files",
                Folder("Microsoft",
                    NodeFile("edge.exe", 3_400_000, 2),
                    NodeFile("teams.exe", 1_900_000, 2)),
                Folder("Git",
                    NodeFile("git.exe", 3_600_000, 3),
                    NodeFile("bash.exe", 1_100_000, 3))),
            Folder("Users\\david",
                Folder("Documents",
                    NodeFile("report.docx", 480_000, 4),
                    NodeFile("notes.txt", 42_000, 4),
                    NodeFile("budget.xlsx", 156_000, 4)),
                Folder("Projects",
                    Folder("sniffer",
                        NodeFile("SpaceSniffer2.exe", 8_691_097, 5),
                        NodeFile("treemap.dll", 2_400_000, 5)),
                    NodeFile("README.md", 18_000, 4)),
                Space("AppData", NodeKind.MissingData, 14_000_000)),
            Folder("temp",
                NodeFile("cache.tmp", 640_000, 6),
                NodeFile("log.txt", 22_000, 4)),
            Space("Unallocated", NodeKind.FreeSpace, 18_000_000));

    /// <summary>A tree representing the "after" state of an animation, for comparison.</summary>
    private static TreemapNode BuildAnimatedTarget()
        => Drive("Local Disk (C:)",
            Folder("Windows",
                Folder("System32",
                    NodeFile("kernel32.dll", 1_240_000, 1),
                    NodeFile("ntdll.dll", 2_180_000, 1)),
                NodeFile("explorer.exe", 5_100_000, 1)),
            Folder("Users\\david",
                Folder("Projects",
                    Folder("sniffer",
                        NodeFile("SpaceSniffer2.exe", 8_691_097, 5),
                        NodeFile("treemap.dll", 2_400_000, 5)),
                    NodeFile("build.log", 60_000_000, 6)),
                Folder("Documents",
                    NodeFile("report.docx", 480_000, 4))),
            Space("Unallocated", NodeKind.FreeSpace, 18_000_000));
}
