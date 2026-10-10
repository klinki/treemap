namespace Treemap;

/// <summary>
/// Maps a screen point to the deepest visible cell beneath it.
///
/// <para>
/// Layout is produced in draw order (parents before children, siblings in stable
/// order), so the deepest hit is found by scanning backwards and returning the
/// first match.
/// </para>
///
/// <para>
/// The frame overloads exist for the same reason: a tweened frame is what is on
/// screen, and it has its own draw order, so pointer input must be resolved
/// against it rather than against the layout the transition is heading for.
/// </para>
/// </summary>
public static class HitTester
{
    /// <summary>
    /// Alpha at or below which a cell counts as not drawn. Matches the renderer's
    /// fill cutoff, so hover never lands on something the user cannot see.
    /// </summary>
    private const float VisibleAlpha = 0.01f;

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

    /// <summary>
    /// Frame-space counterpart of <see cref="HitTest"/>: the index of the deepest
    /// visible cell under the point, or -1.
    /// </summary>
    public static int HitTestFrame(IReadOnlyList<TweenedCell> frame, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(frame);

        for (int i = frame.Count - 1; i >= 0; i--)
        {
            TweenedCell cell = frame[i];
            if (cell.Alpha <= VisibleAlpha)
            {
                continue;
            }

            if (cell.Bounds.Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Frame-space counterpart of <see cref="HitTestContent"/>: the deepest visible
    /// node id under the point, skipping space cells and cells that have faded out.
    /// </summary>
    public static int HitTestFrameContent(IReadOnlyList<TweenedCell> frame, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(frame);

        for (int i = frame.Count - 1; i >= 0; i--)
        {
            TweenedCell cell = frame[i];
            if (cell.Alpha <= VisibleAlpha)
            {
                continue;
            }

            if (!cell.Bounds.Contains(x, y))
            {
                continue;
            }

            if (cell.Kind is NodeKind.FreeSpace or NodeKind.UnknownSpace or NodeKind.MissingData)
            {
                // Same reasoning as the layout path: a space cell has no children, so
                // this is a miss, and anything meaningful was drawn before it.
                continue;
            }

            return cell.NodeId;
        }

        return -1;
    }

    /// <summary>
    /// Frame-space counterpart of <see cref="PathToRoot"/>: the cell and its
    /// ancestors, nearest parent first.
    /// </summary>
    public static List<int> FramePathToRoot(IReadOnlyList<TweenedCell> frame, int cellIndex, List<int>? reuse = null)
    {
        List<int> path = reuse ?? [];
        path.Clear();

        if (cellIndex < 0 || cellIndex >= frame.Count)
        {
            return path;
        }

        int current = cellIndex;
        while (current >= 0)
        {
            path.Add(current);
            current = frame[current].ParentIndex;
        }

        return path;
    }
}
