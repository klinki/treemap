using SkiaSharp;

namespace Treemap.Skia;

/// <summary>
/// Assigns colours to nodes by category, file group, and nesting depth.
/// </summary>
/// <remarks>
/// <para>
/// Folder shading is derived from depth rather than stored per node: each level
/// lightens slightly, so nesting reads as depth without needing a colour table
/// entry per level. This is what keeps the scheme stable while a scan mutates the
/// tree underneath it.
/// </para>
/// </remarks>
public sealed class TreemapPalette
{
    private readonly SKColor[] _groupColors;
    private readonly SKColor _folderColor;
    private readonly SKColor _driveColor;
    private readonly SKColor _freeSpaceColor;
    private readonly SKColor _unknownSpaceColor;
    private readonly SKColor _missingDataColor;

    /// <summary>How much each nesting level lightens, in 0..1.</summary>
    public float DepthLightenStep { get; init; } = 0.12f;

    /// <summary>Border colour drawn around each cell.</summary>
    public SKColor BorderColor { get; init; } = new(0x20, 0x20, 0x20, 0xC0);

    public TreemapPalette(
        SKColor[]? groupColors = null,
        SKColor? folderColor = null,
        SKColor? driveColor = null,
        SKColor? freeSpaceColor = null,
        SKColor? unknownSpaceColor = null,
        SKColor? missingDataColor = null)
    {
        _groupColors = groupColors ?? DefaultGroupColors();
        _folderColor = folderColor ?? new SKColor(0x8E, 0x9A, 0xAF);
        _driveColor = driveColor ?? new SKColor(0x6E, 0x7C, 0x91);

        // Reference recording: free space is a green block, unaccounted space gray.
        _freeSpaceColor = freeSpaceColor ?? new SKColor(0x2E, 0x9E, 0x4B);
        _unknownSpaceColor = unknownSpaceColor ?? new SKColor(0x70, 0x70, 0x70);
        _missingDataColor = missingDataColor ?? new SKColor(0x5F, 0x5F, 0x5F);
    }

    public static TreemapPalette Default { get; } = new();

    private static SKColor[] DefaultGroupColors() =>
    [
        new(0x4E, 0x79, 0xA7), // 0 blue
        new(0xF2, 0x8E, 0x2B), // 1 orange
        new(0xE1, 0x57, 0x59), // 2 red
        new(0x76, 0xB7, 0xB2), // 3 teal
        new(0x59, 0xA1, 0x4F), // 4 green
        new(0xB0, 0x7A, 0xA8), // 5 purple
        new(0xE1, 0xC7, 0x4A), // 6 yellow
        new(0x9C, 0x7A, 0x5C), // 7 brown
        new(0x6B, 0x8E, 0x9E), // 8 slate
        new(0xC9, 0x5D, 0x8A), // 9 pink
    ];

    public SKColor ForGroup(int group)
    {
        if (_groupColors.Length == 0)
        {
            return _folderColor;
        }

        int index = group % _groupColors.Length;
        if (index < 0)
        {
            index += _groupColors.Length;
        }

        return _groupColors[index];
    }

    /// <summary>
    /// Resolves a cell's fill colour. Depth shading applies to folders and drives
    /// so nesting is legible; file group colours stay saturated.
    /// </summary>
    public SKColor Resolve(NodeKind kind, int colorGroup, int depth, float alpha = 1f)
    {
        SKColor baseColor = kind switch
        {
            NodeKind.Drive => Lighten(_driveColor, depth),
            NodeKind.Folder => Lighten(_folderColor, depth),
            NodeKind.FreeSpace => _freeSpaceColor,
            NodeKind.UnknownSpace => _unknownSpaceColor,
            NodeKind.MissingData => _missingDataColor,
            _ => ForGroup(colorGroup),
        };

        if (alpha >= 1f)
        {
            return baseColor;
        }

        return baseColor.WithAlpha((byte)(baseColor.Alpha * Math.Clamp(alpha, 0f, 1f)));
    }

    /// <summary>Picks black or white text for legibility against a fill.</summary>
    public static SKColor ContrastColor(SKColor background)
    {
        // Rec. 601 luma; perceptually better than a flat RGB average.
        double luma = ((0.299 * background.Red) + (0.587 * background.Green) + (0.114 * background.Blue)) / 255d;
        return luma > 0.55 ? SKColors.Black : SKColors.White;
    }

    private SKColor Lighten(SKColor color, int depth)
    {
        if (depth <= 0 || DepthLightenStep <= 0f)
        {
            return color;
        }

        float factor = Math.Clamp(DepthLightenStep * depth, 0f, 0.6f);
        return new SKColor(
            (byte)Math.Clamp(color.Red + ((255 - color.Red) * factor), 0, 255),
            (byte)Math.Clamp(color.Green + ((255 - color.Green) * factor), 0, 255),
            (byte)Math.Clamp(color.Blue + ((255 - color.Blue) * factor), 0, 255),
            color.Alpha);
    }
}
