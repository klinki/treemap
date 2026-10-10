namespace Treemap;

/// <summary>Tuning knobs for the layout pass.</summary>
public sealed class LayoutOptions
{
    /// <summary>
    /// Cells smaller than this on both axes are dropped along with their subtree.
    /// Descendants are strictly smaller than their parent, so culling is sound.
    /// </summary>
    public float MinCellSize { get; init; } = 1.0f;

    /// <summary>
    /// Hard recursion guard. Prevents stack exhaustion on pathological trees;
    /// also bounds work when a scan is still populating the model.
    /// </summary>
    public int MaxDepth { get; init; } = 48;

    /// <summary>
    /// Inset applied to each node's rect before laying out its children, in pixels.
    /// Creates the visible gap between a folder block and its contents.
    /// </summary>
    public float InnerPadding { get; init; }

    public static LayoutOptions Default { get; } = new();

    /// <summary>
    /// "More detail" preset: smaller minimum cell, so more
    /// of the tree survives culling.
    /// </summary>
    public static LayoutOptions HighDetail { get; } = new() { MinCellSize = 0.75f, MaxDepth = 64 };

    /// <summary>"Less detail" preset.</summary>
    public static LayoutOptions LowDetail { get; } = new() { MinCellSize = 4.0f, MaxDepth = 32 };
}

/// <summary>One laid-out node, in draw order.</summary>
public readonly struct LayoutCell
{
    public required int NodeId { get; init; }

    public required RectF Bounds { get; init; }

    public required int Depth { get; init; }

    public required NodeKind Kind { get; init; }

    public required int ColorGroup { get; init; }

    /// <summary>Fraction of the root rect's area, useful for level-of-detail decisions.</summary>
    public required float AreaFraction { get; init; }

    public required bool HasChildren { get; init; }

    /// <summary>
    /// Index of this cell's parent, or -1 for the root. Lets the renderer walk up
    /// to the root for the ancestor highlight path without re-deriving the tree.
    /// </summary>
    public required int ParentIndex { get; init; }
}

/// <summary>
/// Flat, cache-friendly layout result. Parallel arrays avoid per-cell object
/// overhead, which matters when a full-disk scan yields hundreds of thousands
/// of nodes and the renderer walks them every frame.
/// </summary>
public sealed class LayoutResult
{
    private readonly Dictionary<int, int> _index;

    private LayoutResult(
        int[] nodeIds,
        RectF[] bounds,
        int[] depths,
        NodeKind[] kinds,
        int[] colorGroups,
        float[] areaFractions,
        bool[] hasChildren,
        int[] parentIndices,
        Dictionary<int, int> index,
        int rootCell)
    {
        NodeIds = nodeIds;
        Bounds = bounds;
        Depths = depths;
        Kinds = kinds;
        ColorGroups = colorGroups;
        AreaFractions = areaFractions;
        HasChildren = hasChildren;
        ParentIndices = parentIndices;
        _index = index;
        RootCell = rootCell;
    }

    public int[] NodeIds { get; }

    public RectF[] Bounds { get; }

    public int[] Depths { get; }

    public NodeKind[] Kinds { get; }

    public int[] ColorGroups { get; }

    public float[] AreaFractions { get; }

    public bool[] HasChildren { get; }

    /// <summary>Parent cell index per cell, or -1 for the root.</summary>
    public int[] ParentIndices { get; }

    /// <summary>Index of the root node's cell, or -1 for an empty layout.</summary>
    public int RootCell { get; }

    public int Count => NodeIds.Length;

    public bool IsEmpty => NodeIds.Length == 0;

    public static LayoutResult Empty { get; } = new(
        [], [], [], [], [], [], [], [], new Dictionary<int, int>(), -1);

    public LayoutCell GetCell(int i) => new()
    {
        NodeId = NodeIds[i],
        Bounds = Bounds[i],
        Depth = Depths[i],
        Kind = Kinds[i],
        ColorGroup = ColorGroups[i],
        AreaFraction = AreaFractions[i],
        HasChildren = HasChildren[i],
        ParentIndex = ParentIndices[i],
    };

    public bool TryGetIndex(int nodeId, out int index) => _index.TryGetValue(nodeId, out index);

    /// <summary>Enumerates cells in draw order without allocating.</summary>
    public ReadOnlySpan<LayoutCell> AsSpan()
    {
        LayoutCell[] cells = new LayoutCell[Count];
        for (int i = 0; i < Count; i++)
        {
            cells[i] = GetCell(i);
        }

        return cells;
    }

    internal static LayoutResult Build(
        List<LayoutCell> cells, int rootCell)
    {
        int n = cells.Count;
        int[] nodeIds = new int[n];
        RectF[] bounds = new RectF[n];
        int[] depths = new int[n];
        NodeKind[] kinds = new NodeKind[n];
        int[] colorGroups = new int[n];
        float[] areaFractions = new float[n];
        bool[] hasChildren = new bool[n];
        int[] parentIndices = new int[n];

        Dictionary<int, int> index = new(n);
        for (int i = 0; i < n; i++)
        {
            LayoutCell c = cells[i];
            nodeIds[i] = c.NodeId;
            bounds[i] = c.Bounds;
            depths[i] = c.Depth;
            kinds[i] = c.Kind;
            colorGroups[i] = c.ColorGroup;
            areaFractions[i] = c.AreaFraction;
            hasChildren[i] = c.HasChildren;
            parentIndices[i] = c.ParentIndex;
            index[c.NodeId] = i;
        }

        return new LayoutResult(
            nodeIds, bounds, depths, kinds, colorGroups, areaFractions, hasChildren,
            parentIndices, index, rootCell);
    }

    /// <summary>
    /// Enumerates a cell's ancestors from nearest parent to root.
    /// Drives the "highlight the path to the root" affordance.
    /// </summary>
    public AncestorEnumerable AncestorsOf(int cellIndex)
        => new(this, cellIndex);

    public readonly struct AncestorEnumerable(LayoutResult layout, int start)
    {
        public AncestorEnumerator GetEnumerator() => new(layout, start);
    }

    public struct AncestorEnumerator(LayoutResult layout, int current)
    {
        public bool MoveNext()
        {
            if (current < 0)
            {
                return false;
            }

            current = layout.ParentIndices[current];
            return current >= 0;
        }

        public readonly int Current => current;
    }
}
