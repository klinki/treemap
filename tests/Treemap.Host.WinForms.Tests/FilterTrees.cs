using Treemap;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Small trees with distinguishable filenames for the filter tests.
/// </summary>
internal static class FilterTrees
{
    public static TreemapNode Root() => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
    [
        TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "a.mp4", NodeKind.File, 1000, 1000, null, 0),
            TreemapNode.Create(8, "b.txt", NodeKind.File, 100, 100, null, 0),
        ]),
        TreemapNode.Create(4, "other.mp4", NodeKind.File, 500, 500, null, 0),
        TreemapNode.Create(6, "Free space", NodeKind.FreeSpace, 2000, 2000),
        TreemapNode.Create(7, "Unaccounted space", NodeKind.UnknownSpace, 300, 300),
    ]);

    public static TreemapNode RootWithThreeMp4() => TreemapNode.Create(1, "C:", NodeKind.Drive, 0, 0, null, 0,
    [
        TreemapNode.Create(2, "sub", NodeKind.Folder, 0, 0, null, 0,
        [
            TreemapNode.Create(3, "a.mp4", NodeKind.File, 1000, 1000, null, 0),
            TreemapNode.Create(9, "c.mp4", NodeKind.File, 2000, 2000, null, 0),
        ]),
        TreemapNode.Create(4, "other.mp4", NodeKind.File, 500, 500, null, 0),
    ]);
}
