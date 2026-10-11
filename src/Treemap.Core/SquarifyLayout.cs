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

        int effectiveMax = EffectiveMaxDepth(options);
        List<LayoutCell> cells = new(capacity: 512);

        // One reusable scratch buffer per depth level. Depth is bounded by
        // the effective (temporarily extended) max, capped at 64, so this is
        // a fixed small allocation instead of per-node churn.
        Entry[][] buffers = new Entry[effectiveMax + 2][];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = Array.Empty<Entry>();
        }

        float totalArea = bounds.Area;

        int rootCell = -1;
        if (IsRenderable(bounds, options))
        {
            RectF header = RectF.Empty;
            RectF content = bounds;
            bool rootExpanded = root.Id == options.TemporaryExpandedNodeId;
            if (WantsHeader(hasChildren: root.Children.Count > 0, root.Kind, options)
                && IsExpanded(root, depth: 0, bounds, options, rootExpanded))
            {
                float strip = options.HeaderHeight;
                RectF remainder = new(bounds.X, bounds.Y + strip, bounds.Width, bounds.Height - strip);
                if (!remainder.IsEmpty)
                {
                    header = new RectF(bounds.X, bounds.Y, bounds.Width, strip);
                    content = remainder;
                }
            }

            if (options.InnerPadding > 0f)
            {
                content = content.InsetUniform(options.InnerPadding);
            }

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
                HeaderBounds = header,
                LogicalSize = root.LogicalSize,
                PhysicalSize = root.PhysicalSize,
            });

            if (root.Children.Count > 0 && IsExpanded(root, depth: 0, bounds, options, rootExpanded) && !content.IsEmpty)
            {
                buffers[1] = Fill(buffers[1], root.Children, options, out int rootChildCount);
                Normalize(buffers[1], rootChildCount, content.Area);
                Squarify(
                    buffers[1], rootChildCount, content, depth: 1, parentIndex: rootCell,
                    options, cells, buffers, totalArea, rootExpanded);
            }
        }

        return LayoutResult.Build(cells, rootCell);
    }

    /// <summary>
    /// Deepest depth the layout may reach: the normal budget, extended for the
    /// temporary branch only. Both branches are hard-capped at 64 so scratch
    /// buffers stay fixed-size; larger <see cref="LayoutOptions.MaxDepth"/> or
    /// <see cref="LayoutOptions.TemporaryExtraDepth"/> values saturate at 64
    /// rather than allocating unbounded buffers or recursing past them.
    /// </summary>
    internal static int EffectiveMaxDepth(LayoutOptions options)
    {
        if (options.TemporaryExpandedNodeId < 0 || options.TemporaryExtraDepth <= 0)
        {
            return Math.Min(64, options.MaxDepth);
        }

        return CappedWithExtra(options.MaxDepth, options.TemporaryExtraDepth);
    }

    private static int LimitFor(LayoutOptions options, bool inExpandedBranch)
    {
        if (inExpandedBranch && options.TemporaryExpandedNodeId >= 0 && options.TemporaryExtraDepth > 0)
        {
            return CappedWithExtra(options.MaxDepth, options.TemporaryExtraDepth);
        }

        return Math.Min(64, options.MaxDepth);
    }

    /// <summary>
    /// Base depth plus extra levels, saturated at 64 without int overflow.
    /// A base already at or above 64 returns 64 directly, so huge option values
    /// (including <see cref="int.MaxValue"/>) never overflow the addition.
    /// </summary>
    private static int CappedWithExtra(int baseDepth, int extraDepth)
    {
        if (extraDepth <= 0)
        {
            return Math.Min(64, baseDepth);
        }

        if (baseDepth >= 64)
        {
            return 64;
        }

        long sum = (long)baseDepth + extraDepth;
        return sum >= 64 ? 64 : (int)sum;
    }

    /// <summary>
    /// Headers belong to folders and drives that actually have children to show
    /// beneath them. Files and space blocks never carry one.
    /// </summary>
    private static bool WantsHeader(bool hasChildren, NodeKind kind, LayoutOptions options)
        => hasChildren
            && options.ShowHeaders
            && options.HeaderHeight > 0f
            && kind is NodeKind.Folder or NodeKind.Drive;

    /// <summary>
    /// True when a node has at least one layout-visible child with positive size.
    /// Hidden-only or zero-size children leave nothing to expand into, so the
    /// node renders collapsed with no header and no recursion.
    /// </summary>
    private static bool HasExpandableChildren(TreemapNode node, LayoutOptions options)
    {
        foreach (TreemapNode child in node.Children)
        {
            if (!IsSpaceVisible(child, options))
            {
                continue;
            }

            if (child.LayoutSize > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a node at <paramref name="depth"/> in <paramref name="rect"/> will
    /// actually show children: depth budget remains, a visible child exists, and
    /// the rect leaves usable child space after any header strip and padding.
    /// The temporary branch uses its extended limit; every other branch keeps
    /// the normal <see cref="LayoutOptions.MaxDepth"/>.
    /// </summary>
    private static bool IsExpanded(TreemapNode node, int depth, in RectF rect, LayoutOptions options, bool inExpandedBranch = false)
    {
        if (node.Children.Count == 0 || depth >= LimitFor(options, inExpandedBranch))
        {
            return false;
        }

        if (!HasExpandableChildren(node, options))
        {
            return false;
        }

        RectF inner = rect;
        if (WantsHeader(hasChildren: true, node.Kind, options))
        {
            if (rect.Height <= options.HeaderHeight)
            {
                return false;
            }

            inner = new RectF(rect.X, rect.Y + options.HeaderHeight, rect.Width, rect.Height - options.HeaderHeight);
            if (inner.IsEmpty)
            {
                return false;
            }
        }

        if (options.InnerPadding > 0f)
        {
            inner = inner.InsetUniform(options.InnerPadding);
        }

        return !inner.IsEmpty;
    }

    /// <summary>
    /// Layout-only visibility for the synthetic space blocks. Hidden kinds are
    /// skipped before normalisation, so the remaining siblings absorb their area.
    /// The snapshot tree itself is never touched.
    /// </summary>
    internal static bool IsSpaceVisible(TreemapNode node, LayoutOptions options)
    {
        if (node.Kind == NodeKind.FreeSpace)
        {
            return options.ShowFreeSpace;
        }

        if (node.Kind is NodeKind.UnknownSpace or NodeKind.MissingData)
        {
            return options.ShowUnknownSpace;
        }

        return true;
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
        float totalArea,
        bool parentInExpandedBranch = false)
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
                    bool inExpanded = parentInExpandedBranch
                        || entries[k].Node.Id == options.TemporaryExpandedNodeId;
                    Emit(entries[k].Node, cell, depth, parentIndex, options, cells, buffers, totalArea, inExpanded);

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
                    bool inExpandedRow = parentInExpandedBranch
                        || entries[k].Node.Id == options.TemporaryExpandedNodeId;
                    Emit(entries[k].Node, cell, depth, parentIndex, options, cells, buffers, totalArea, inExpandedRow);

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
        float totalArea,
        bool inExpandedBranch = false)
    {
        if (!IsRenderable(rect, options))
        {
            // Descendants are strictly smaller, so the whole subtree is culled.
            return -1;
        }

        bool hasChildren = node.Children.Count > 0;
        int limit = LimitFor(options, inExpandedBranch);

        int AddCollapsed()
        {
            int index = cells.Count;
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
                HeaderBounds = RectF.Empty,
                LogicalSize = node.LogicalSize,
                PhysicalSize = node.PhysicalSize,
            });
            return index;
        }

        // Collapsed when the depth budget is spent or there is nothing visible to
        // expand into: no header, no recursion, centred label.
        // The temporary branch uses its extended limit; other branches keep MaxDepth.
        if (!hasChildren || depth >= limit || !HasExpandableChildren(node, options))
        {
            return AddCollapsed();
        }

        // A folder header is a strip across the top of the cell; children tile
        // the remainder, so the strip never overlaps them. A cell too short to
        // leave usable child space collapses: no header, no recursion.
        RectF header = RectF.Empty;
        RectF inner = rect;
        bool wantsHeader = WantsHeader(hasChildren: true, node.Kind, options);
        if (wantsHeader)
        {
            if (rect.Height > options.HeaderHeight)
            {
                RectF remainder = new(rect.X, rect.Y + options.HeaderHeight, rect.Width, rect.Height - options.HeaderHeight);
                if (!remainder.IsEmpty)
                {
                    header = new RectF(rect.X, rect.Y, rect.Width, options.HeaderHeight);
                    inner = remainder;
                }
            }

            if (header.IsEmpty)
            {
                return AddCollapsed();
            }
        }

        if (options.InnerPadding > 0f)
        {
            inner = inner.InsetUniform(options.InnerPadding);
        }

        if (inner.IsEmpty)
        {
            return AddCollapsed();
        }

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
            HeaderBounds = header,
            LogicalSize = node.LogicalSize,
            PhysicalSize = node.PhysicalSize,
        });

        int nextDepth = depth + 1;
        if (nextDepth >= buffers.Length)
        {
            return selfIndex;
        }

        Entry[] buffer = buffers[nextDepth];
        buffer = Fill(buffer, node.Children, options, out int childCount);
        buffers[nextDepth] = buffer;
        Normalize(buffer, childCount, inner.Area);

        Squarify(
            buffer, childCount, inner, nextDepth, selfIndex, options, cells, buffers, totalArea, inExpandedBranch);

        return selfIndex;
    }

    /// <summary>
    /// Loads visible children into the scratch buffer for a depth level, growing it if
    /// needed. Returns the (possibly new) buffer — the caller must store it back
    /// into <c>buffers[depth]</c>, since Array.Resize copies on growth.
    /// </summary>
    private static Entry[] Fill(
        Entry[] buffer, IReadOnlyList<TreemapNode> children, LayoutOptions options, out int count)
    {
        if (buffer.Length < children.Count)
        {
            int capacity = Math.Max(children.Count, buffer.Length * 2);
            Array.Resize(ref buffer, capacity);
        }

        count = 0;
        for (int i = 0; i < children.Count; i++)
        {
            TreemapNode child = children[i];
            if (!IsSpaceVisible(child, options))
            {
                continue;
            }

            long size = child.LayoutSize;
            buffer[count] = new Entry { Node = child, Area = size > 0 ? size : 0d };
            count++;
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
