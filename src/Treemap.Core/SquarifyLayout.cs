namespace Treemap;

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk, 2000).
///
/// <para>
/// Children are sorted by descending area and packed into rows. A row is extended
/// while doing so improves its worst aspect ratio; once the next item would make
/// it worse the row is committed and a new one begins. Most cells therefore land
/// near 1:1, which reads far better than the extreme slivers slice-and-dice
/// produces.
/// </para>
///
/// <para>Child areas are normalised so they exactly tile the parent's content rect,
/// which keeps the recursion self-correcting and makes drift impossible.</para>
///
/// <para>Pure geometry with no platform or renderer dependencies.</para>
/// </summary>
public static class SquarifyLayout
{
    /// <summary>Working item. A struct so the hot loop does not allocate per node.</summary>
    private struct Entry
    {
        public TreemapNode Node;
        public double Area;
    }

    public static LayoutResult Compute(TreemapNode root, RectF bounds, LayoutOptions? options = null)
    {
        options ??= LayoutOptions.Default;

        if (root is null || bounds.IsEmpty || options.MaxDepth <= 0)
        {
            return LayoutResult.Empty;
        }

        List<LayoutCell> cells = new(capacity: 512);

        // One reusable scratch buffer per depth level. Depth is bounded by
        // MaxDepth, so this is a fixed small allocation instead of per-node churn.
        Entry[][] buffers = new Entry[options.MaxDepth + 2][];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = Array.Empty<Entry>();
        }

        float totalArea = bounds.Area;

        int rootCell = -1;
        if (IsRenderable(bounds, options))
        {
            rootCell = cells.Count;
            cells.Add(new LayoutCell
            {
                NodeId = root.Id,
                Bounds = bounds,
                Depth = 0,
                Kind = root.Kind,
                ColorGroup = root.ColorGroup,
                AreaFraction = 1f,
                HasChildren = root.Children.Count > 0,
                ParentIndex = -1,
            });
        }

        if (root.Children.Count > 0)
        {
            RectF content = options.InnerPadding > 0f ? bounds.InsetUniform(options.InnerPadding) : bounds;
            if (!content.IsEmpty)
            {
                buffers[1] = Fill(buffers[1], root.Children, out int rootChildCount);
                Normalize(buffers[1], rootChildCount, content.Area);
                Squarify(
                    buffers[1], rootChildCount, content, depth: 1, parentIndex: rootCell,
                    options, cells, buffers, totalArea);
            }
        }

        return LayoutResult.Build(cells, rootCell);
    }

    /// <summary>Lays out a sorted, normalised run of children inside <paramref name="rect"/>.</summary>
    private static void Squarify(
        Entry[] entries,
        int count,
        RectF rect,
        int depth,
        int parentIndex,
        LayoutOptions options,
        List<LayoutCell> cells,
        Entry[][] buffers,
        float totalArea)
    {
        RectF remaining = rect;
        int i = 0;

        while (i < count && !remaining.IsEmpty)
        {
            double shortest = remaining.ShortestSide;
            if (shortest <= 0d)
            {
                break;
            }

            // --- grow the row while its worst aspect ratio does not degrade ---
            int rowStart = i;
            double rowSum = 0d;
            double rowMin = double.MaxValue;
            double rowMax = 0d;
            double bestWorst = double.MaxValue;

            while (i < count)
            {
                double area = entries[i].Area;
                double candSum = rowSum + area;
                double candMin = Math.Min(rowMin, area);
                double candMax = Math.Max(rowMax, area);
                double candWorst = Worst(candSum, candMin, candMax, shortest);

                if (i > rowStart && candWorst > bestWorst)
                {
                    break;
                }

                rowSum = candSum;
                rowMin = candMin;
                rowMax = candMax;
                bestWorst = candWorst;
                i++;
            }

            int rowEnd = i;
            if (rowEnd <= rowStart || rowSum <= 0d)
            {
                break;
            }

            // --- commit the row along the shorter side ---
            if (remaining.Width >= remaining.Height)
            {
                // Column on the left edge; members stack vertically.
                float columnWidth = (float)(rowSum / remaining.Height);

                float y = remaining.Y;
                for (int k = rowStart; k < rowEnd; k++)
                {
                    float height = (float)(entries[k].Area / Math.Max(columnWidth, 1e-9d));
                    height = MathF.Min(height, remaining.Bottom - y);

                    RectF cell = new(remaining.X, y, columnWidth, height);
                    Emit(entries[k].Node, cell, depth, parentIndex, options, cells, buffers, totalArea);

                    y += height;
                    if (y >= remaining.Bottom - 1e-6f)
                    {
                        break;
                    }
                }

                remaining = new RectF(
                    remaining.X + columnWidth, remaining.Y, remaining.Width - columnWidth, remaining.Height);
            }
            else
            {
                // Row across the top edge; members stack horizontally.
                float rowHeight = (float)(rowSum / remaining.Width);

                float x = remaining.X;
                for (int k = rowStart; k < rowEnd; k++)
                {
                    float width = (float)(entries[k].Area / Math.Max(rowHeight, 1e-9d));
                    width = MathF.Min(width, remaining.Right - x);

                    RectF cell = new(x, remaining.Y, width, rowHeight);
                    Emit(entries[k].Node, cell, depth, parentIndex, options, cells, buffers, totalArea);

                    x += width;
                    if (x >= remaining.Right - 1e-6f)
                    {
                        break;
                    }
                }

                remaining = new RectF(
                    remaining.X, remaining.Y + rowHeight, remaining.Width, remaining.Height - rowHeight);
            }
        }
    }

    /// <summary>Records one cell and recurses into its children. Returns the cell's index.</summary>
    private static int Emit(
        TreemapNode node,
        RectF rect,
        int depth,
        int parentIndex,
        LayoutOptions options,
        List<LayoutCell> cells,
        Entry[][] buffers,
        float totalArea)
    {
        if (!IsRenderable(rect, options))
        {
            // Descendants are strictly smaller, so the whole subtree is culled.
            return -1;
        }

        bool hasChildren = node.Children.Count > 0;

        int selfIndex = cells.Count;
        cells.Add(new LayoutCell
        {
            NodeId = node.Id,
            Bounds = rect,
            Depth = depth,
            Kind = node.Kind,
            ColorGroup = node.ColorGroup,
            AreaFraction = totalArea > 0f ? rect.Area / totalArea : 0f,
            HasChildren = hasChildren,
            ParentIndex = parentIndex,
        });

        if (!hasChildren || depth >= options.MaxDepth)
        {
            return selfIndex;
        }

        RectF inner = options.InnerPadding > 0f ? rect.InsetUniform(options.InnerPadding) : rect;
        if (inner.IsEmpty)
        {
            return selfIndex;
        }

        int nextDepth = depth + 1;
        if (nextDepth >= buffers.Length)
        {
            return selfIndex;
        }

        Entry[] buffer = buffers[nextDepth];
        buffer = Fill(buffer, node.Children, out int childCount);
        buffers[nextDepth] = buffer;
        Normalize(buffer, childCount, inner.Area);

        Squarify(
            buffer, childCount, inner, nextDepth, selfIndex, options, cells, buffers, totalArea);

        return selfIndex;
    }

    /// <summary>
    /// Loads children into the scratch buffer for a depth level, growing it if
    /// needed. Returns the (possibly new) buffer — the caller must store it back
    /// into <c>buffers[depth]</c>, since Array.Resize copies on growth.
    /// </summary>
    private static Entry[] Fill(
        Entry[] buffer, IReadOnlyList<TreemapNode> children, out int count)
    {
        count = children.Count;
        if (buffer.Length < count)
        {
            int capacity = Math.Max(count, buffer.Length * 2);
            Array.Resize(ref buffer, capacity);
        }

        for (int i = 0; i < count; i++)
        {
            TreemapNode child = children[i];
            long size = child.LayoutSize;
            buffer[i] = new Entry { Node = child, Area = size > 0 ? size : 0d };
        }

        return buffer;
    }

    /// <summary>Scales logical sizes to pixel areas filling <paramref name="rectArea"/>.</summary>
    private static void Normalize(Entry[] buffer, int count, float rectArea)
    {
        double sum = 0d;
        for (int i = 0; i < count; i++)
        {
            sum += buffer[i].Area;
        }

        if (sum <= 0d || rectArea <= 0f)
        {
            return;
        }

        double scale = rectArea / sum;
        for (int i = 0; i < count; i++)
        {
            buffer[i].Area *= scale;
        }

        Array.Sort(buffer, 0, count, AreaComparer.Instance);
    }

    private static bool IsRenderable(in RectF rect, LayoutOptions options)
        => rect.Width >= options.MinCellSize || rect.Height >= options.MinCellSize;

    /// <summary>
    /// Worst (most elongated) aspect ratio achievable within a row.
    ///
    /// <para>
    /// For a row of total area <c>S</c> laid across a side of length <c>w</c>, the
    /// extreme stretch is set by the smallest and largest members. Minimising this
    /// over row composition is what keeps cells near square.
    /// </para>
    /// </summary>
    private static double Worst(double sum, double min, double max, double w)
    {
        if (sum <= 0d || min <= 0d || w <= 0d)
        {
            return double.MaxValue;
        }

        double sumSquared = sum * sum;
        double wSquared = w * w;

        double wide = (wSquared * max) / sumSquared;
        double tall = sumSquared / (wSquared * min);

        return Math.Max(wide, tall);
    }

    private sealed class AreaComparer : IComparer<Entry>
    {
        public static AreaComparer Instance { get; } = new();

        public int Compare(Entry x, Entry y) => y.Area.CompareTo(x.Area);
    }
}
