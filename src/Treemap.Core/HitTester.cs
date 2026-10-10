namespace Treemap;

/// <summary>
/// Maps a screen point to the deepest visible cell beneath it.
///
/// <para>
/// Layout is produced in draw order (parents before children, siblings in stable
/// order), so the deepest hit is found by scanning backwards and returning the
/// first match.
/// </para>
/// </summary>
public static class HitTester
{
    /// <summary>
    /// Returns the index of the deepest cell containing the point, or -1 if none.
    /// </summary>
    public static int HitTest(in LayoutResult layout, float x, float y)
    {
        for (int i = layout.Count - 1; i >= 0; i--)
        {
            if (layout.Bounds[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the deepest node id under the point, ignoring space cells
    /// (free/unknown/missing) so hover lands on real content.
    /// </summary>
    public static int HitTestContent(in LayoutResult layout, float x, float y)
    {
        for (int i = layout.Count - 1; i >= 0; i--)
        {
            RectF b = layout.Bounds[i];
            if (!b.Contains(x, y))
            {
                continue;
            }

            if (layout.Kinds[i] is NodeKind.FreeSpace or NodeKind.UnknownSpace or NodeKind.MissingData)
            {
                // Keep descending: a space cell has no children, so this is a miss,
                // but a folder drawn above it would have been found first.
                continue;
            }

            return layout.NodeIds[i];
        }

        return -1;
    }

    /// <summary>
    /// Collects the cell and all its ancestors, for the highlight-path affordance.
    /// </summary>
    public static List<int> PathToRoot(in LayoutResult layout, int cellIndex, List<int>? reuse = null)
    {
        List<int> path = reuse ?? [];
        path.Clear();

        if (cellIndex < 0 || cellIndex >= layout.Count)
        {
            return path;
        }

        int current = cellIndex;
        while (current >= 0)
        {
            path.Add(current);
            current = layout.ParentIndices[current];
        }

        return path;
    }
}
