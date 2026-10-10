using Treemap;

namespace Treemap.Core.Tests;

/// <summary>Small helpers for assembling test trees.</summary>
internal static class TreeBuilder
{
    public static TreemapNode File(int id, string name, long size, int colorGroup = 0)
        => TreemapNode.Create(id, name, NodeKind.File, size, size, null, colorGroup);

    public static TreemapNode Folder(int id, string name, params TreemapNode[] children)
        => TreemapNode.Create(id, name, NodeKind.Folder, 0, 0, null, 0, children);

    public static TreemapNode Drive(int id, string name, params TreemapNode[] children)
        => TreemapNode.Create(id, name, NodeKind.Drive, 0, 0, null, 0, children);

    public static TreemapNode Space(int id, string name, NodeKind kind, long size)
        => TreemapNode.Create(id, name, kind, size, size, null, 0);

    public static TreemapNode[] Children(params TreemapNode[] nodes) => nodes;
}
