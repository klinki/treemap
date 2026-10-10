using Treemap;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Small trees for the host tests. Each <see cref="WithMarker"/> builds a tree whose leaf
/// id identifies it, so "which snapshot is on screen" is a single frame lookup.
/// </summary>
internal static class Trees
{
    public static TreemapNode Folder(int id, string name, params TreemapNode[] children)
        => TreemapNode.Create(id, name, NodeKind.Folder, 0, 0, null, 0, children);

    public static TreemapNode File(int id, string name, long size)
        => TreemapNode.Create(id, name, NodeKind.File, size, size, null, 0);

    public static TreemapNode WithMarker(int markerId)
        => Folder(1, "root", File(markerId, "marker", 900), File(markerId + 1000, "rest", 100));
}