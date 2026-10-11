namespace Treemap;

/// <summary>Easing curves for layout transitions.</summary>
public static class Easing
{
    /// <summary>Linear. Predictable but mechanical.</summary>
    public static float Linear(float t) => t;

    /// <summary>
    /// Cubic ease-in-out. The default: fast departure, gentle settle, no overshoot.
    /// Overshoot would let rectangles visibly overlap during a transition.
    /// </summary>
    public static float CubicInOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - (float)Math.Pow(-2d * t + 2d, 3d) / 2f;
    }

    /// <summary>Quadratic ease-out: quick response, long tail. Good for hover feedback.</summary>
    public static float QuadOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return 1f - ((1f - t) * (1f - t));
    }
}

/// <summary>
/// Interpolates a treemap between two layouts.
/// </summary>
/// <remarks>
/// <para>
/// Nodes present in both layouts have their rectangles lerped. Nodes that appear
/// fade in at their final position; nodes that disappear fade out where they were.
/// This is the capability that requires holding two snapshots at once — a mutable
/// tree under a lock simply cannot express "the layout before and after".
/// </para>
///
/// <para>
/// Matching is by <see cref="TreemapNode.Id"/>, so the tree may change shape
/// entirely between snapshots as long as stable ids are used.
/// </para>
///
/// <para>
/// Draw order is decided once, when the tween is built, and does not depend on
/// progress: every cell's parent is painted before it. Sorting per frame by alpha
/// or depth could not honour that, because a fading-in parent has a lower alpha
/// than the retained child it now contains and would be painted over it.
/// </para>
/// </remarks>
public sealed class LayoutTween
{
    private readonly LayoutResult _from;
    private readonly LayoutResult _to;

    /// <summary>Node ids present in <see cref="_from"/> but not <see cref="_to"/>.</summary>
    private readonly List<int> _removedIds;

    /// <summary>
    /// The frame's cells in final draw order, with everything that does not vary
    /// between frames already resolved: which endpoint a cell reads from, its
    /// parent and its depth. Built once, so <see cref="Evaluate"/> is a flat loop
    /// that neither allocates nor sorts.
    /// </summary>
    private readonly PlanEntry[] _plan;

    /// <summary>One cell of the union of both layouts, before draw order is decided.</summary>
    private struct UnionCell
    {
        public int NodeId;

        /// <summary>Index into <see cref="_from"/>, or -1 when the cell is entering.</summary>
        public int FromIndex;

        /// <summary>Index into <see cref="_to"/>, or -1 when the cell is departing.</summary>
        public int ToIndex;

        /// <summary>Slot of this cell's effective parent, or -1 for a frame root.</summary>
        public int ParentSlot;
    }

    /// <summary>One frame cell, holding only the parts constant across progress.</summary>
    private struct PlanEntry
    {
        public int NodeId;

        /// <summary>Index into <see cref="_from"/>, or -1 when the cell is entering.</summary>
        public int FromIndex;

        /// <summary>Index into <see cref="_to"/>, or -1 when the cell is departing.</summary>
        public int ToIndex;

        /// <summary>Frame index of this cell's parent, or -1 for a frame root.</summary>
        public int ParentFrameIndex;

        /// <summary>Depth in the frame's own hierarchy, not in either endpoint's.</summary>
        public int Depth;

        public bool IsEntering => FromIndex < 0;
    }

    public LayoutTween(LayoutResult from, LayoutResult to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        _from = from;
        _to = to;

        _removedIds = [];
        for (int i = 0; i < from.Count; i++)
        {
            if (!to.TryGetIndex(from.NodeIds[i], out _))
            {
                _removedIds.Add(i);
            }
        }

        _plan = BuildPlan();
    }

    /// <summary>True when both endpoints describe the same set of cells.</summary>
    public bool IsIdentity =>
        _from.Count == _to.Count && _removedIds.Count == 0 && ReferenceEquals(_from, _to);

    public int RemovedCount => _removedIds.Count;

    /// <summary>
    /// Produces the interpolated frame at <paramref name="progress"/> (0..1).
    /// </summary>
    public void Evaluate(float progress, List<TweenedCell> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();

        float t = Easing.CubicInOut(progress);
        PlanEntry[] plan = _plan;

        for (int i = 0; i < plan.Length; i++)
        {
            // Frame position is the loop index, so ParentFrameIndex is already the
            // cell's index within the output list.
            ref readonly PlanEntry entry = ref plan[i];

            if (entry.ToIndex < 0)
            {
                // Disappeared: fade out where it was.
                int source = entry.FromIndex;
                output.Add(new TweenedCell
                {
                    NodeId = entry.NodeId,
                    Bounds = _from.Bounds[source],
                    Alpha = 1f - t,
                    Depth = entry.Depth,
                    Kind = _from.Kinds[source],
                    ColorGroup = _from.ColorGroups[source],
                    AreaFraction = _from.AreaFractions[source],
                    HasChildren = _from.HasChildren[source],
                    IsEntering = false,
                    ParentIndex = entry.ParentFrameIndex,
                    HeaderBounds = _from.HeaderBounds[source],
                    LogicalSize = _from.LogicalSizes[source],
                    PhysicalSize = _from.PhysicalSizes[source],
                });
            }
            else if (entry.IsEntering)
            {
                // Newly discovered node: fade in at its final position.
                int target = entry.ToIndex;
                output.Add(new TweenedCell
                {
                    NodeId = entry.NodeId,
                    Bounds = _to.Bounds[target],
                    Alpha = t,
                    Depth = entry.Depth,
                    Kind = _to.Kinds[target],
                    ColorGroup = _to.ColorGroups[target],
                    AreaFraction = _to.AreaFractions[target],
                    HasChildren = _to.HasChildren[target],
                    IsEntering = true,
                    ParentIndex = entry.ParentFrameIndex,
                    HeaderBounds = _to.HeaderBounds[target],
                    LogicalSize = _to.LogicalSizes[target],
                    PhysicalSize = _to.PhysicalSizes[target],
                });
            }
            else
            {
                // In both layouts: interpolate towards the target rect. The header
                // follows the interpolated bounds so it stays inside the cell at
                // every intermediate t, even when one endpoint has none.
                int source = entry.FromIndex;
                int target = entry.ToIndex;
                RectF bounds = RectF.Lerp(_from.Bounds[source], _to.Bounds[target], t);
                output.Add(new TweenedCell
                {
                    NodeId = entry.NodeId,
                    Bounds = bounds,
                    Alpha = 1f,
                    Depth = entry.Depth,
                    Kind = _to.Kinds[target],
                    ColorGroup = _to.ColorGroups[target],
                    AreaFraction = _to.AreaFractions[target],
                    HasChildren = _to.HasChildren[target],
                    IsEntering = false,
                    ParentIndex = entry.ParentFrameIndex,
                    HeaderBounds = LerpHeader(_from.HeaderBounds[source], _to.HeaderBounds[target], bounds, t),
                    LogicalSize = _to.LogicalSizes[target],
                    PhysicalSize = _to.PhysicalSizes[target],
                });
            }
        }
    }

    /// <summary>
    /// Interpolates header strips while keeping the result inside the current
    /// <paramref name="bounds"/>. Both endpoints present lerps directly; a missing
    /// endpoint grows or shrinks a top strip anchored to the current cell, so an
    /// empty rect at the global origin never drags the label off-cell mid-tween.
    /// </summary>
    private static RectF LerpHeader(in RectF from, in RectF to, in RectF bounds, float t)
    {
        bool fromEmpty = from.IsEmpty;
        bool toEmpty = to.IsEmpty;

        if (!fromEmpty && !toEmpty)
        {
            return RectF.Lerp(from, to, t);
        }

        if (fromEmpty && toEmpty)
        {
            return RectF.Empty;
        }

        float height = fromEmpty ? to.Height * t : from.Height * (1f - t);
        if (height <= 0f || bounds.IsEmpty)
        {
            return RectF.Empty;
        }

        height = MathF.Min(height, bounds.Height);
        return new RectF(bounds.X, bounds.Y, bounds.Width, height);
    }

    /// <summary>
    /// Resolves draw order, parent links and depths once, for the whole transition.
    /// </summary>
    /// <remarks>
    /// The order is a topological walk of the union of both trees. Departing cells
    /// are seeded first, each preceded by the ancestors it still shares with the
    /// frame, because a departing cell never moves: painting it after the retained
    /// child it used to contain would hide that child behind it for the whole
    /// transition. Retained and entering cells then follow in the target's own
    /// order.
    ///
    /// <para>
    /// The one case no single order can satisfy is two retained cells that swap
    /// parents between the endpoints: the frame can only honour one of the two
    /// hierarchies, and it honours the target, so at progress 0 such a cell is
    /// painted under whichever parent it is moving out of.
    /// </para>
    /// </remarks>
    private PlanEntry[] BuildPlan()
    {
        int count = _to.Count + _removedIds.Count;

        // Slots are target cells in target order, then departing cells in source
        // order. In that arrangement every cell's parent already has a smaller slot,
        // which is what makes the walk below a single visit per cell.
        UnionCell[] cells = new UnionCell[count];
        Dictionary<int, int> slotById = new(count);

        int next = 0;

        for (int i = 0; i < _to.Count; i++)
        {
            int id = _to.NodeIds[i];

            // TryGetIndex yields default(T) on a miss, which would make every
            // entering node read its geometry out of source cell 0 — and index an
            // empty source at all. Absence has to be spelled out.
            int fromIndex = _from.TryGetIndex(id, out int found) ? found : -1;

            slotById[id] = next;
            cells[next] = new UnionCell { NodeId = id, FromIndex = fromIndex, ToIndex = i };
            next++;
        }

        foreach (int i in _removedIds)
        {
            int id = _from.NodeIds[i];
            slotById[id] = next;
            cells[next] = new UnionCell { NodeId = id, FromIndex = i, ToIndex = -1 };
            next++;
        }

        // Effective parent: the target's for anything the target still owns, so an
        // entering parent is followed by the retained children it now contains, and
        // the source's for a departing cell, whose parent may itself have moved.
        for (int i = 0; i < count; i++)
        {
            int parentNodeId = cells[i].ToIndex >= 0
                ? ParentNodeId(_to, cells[i].ToIndex)
                : ParentNodeId(_from, cells[i].FromIndex);

            cells[i].ParentSlot =
                parentNodeId >= 0 && slotById.TryGetValue(parentNodeId, out int slot) ? slot : -1;
        }

        int[] order = new int[count];
        int[] frameIndex = new int[count];
        bool[] emitted = new bool[count];
        List<int> pending = [];
        int emittedCount = 0;

        void Emit(int seed)
        {
            if (emitted[seed])
            {
                return;
            }

            // Climb to the first ancestor that is not on screen yet, then emit
            // downwards from it, so a parent is never painted after its child.
            pending.Clear();
            int current = seed;
            while (!emitted[current])
            {
                pending.Add(current);
                int parent = cells[current].ParentSlot;
                if (parent < 0)
                {
                    break;
                }

                current = parent;
            }

            while (pending.Count > 0)
            {
                int slot = pending[^1];
                pending.RemoveAt(pending.Count - 1);

                emitted[slot] = true;
                frameIndex[slot] = emittedCount;
                order[emittedCount] = slot;
                emittedCount++;
            }
        }

        for (int i = 0; i < _removedIds.Count; i++)
        {
            Emit(_to.Count + i);
        }

        for (int i = 0; i < _to.Count; i++)
        {
            Emit(i);
        }

        // Remap to the emitted order. Parents come first, so parent indices and
        // depths — which must agree with the hierarchy the frame actually carries —
        // both fall out of one forward pass.
        PlanEntry[] plan = new PlanEntry[count];
        for (int i = 0; i < count; i++)
        {
            UnionCell cell = cells[order[i]];
            int parent = cell.ParentSlot;

            plan[i] = new PlanEntry
            {
                NodeId = cell.NodeId,
                FromIndex = cell.FromIndex,
                ToIndex = cell.ToIndex,
                ParentFrameIndex = parent >= 0 ? frameIndex[parent] : -1,
                Depth = parent >= 0 ? plan[frameIndex[parent]].Depth + 1 : 0,
            };
        }

        return plan;
    }

    private static int ParentNodeId(LayoutResult layout, int cellIndex)
    {
        int parent = layout.ParentIndices[cellIndex];
        return parent >= 0 ? layout.NodeIds[parent] : -1;
    }
}

/// <summary>One cell in an interpolated frame.</summary>
public readonly struct TweenedCell
{
    public required int NodeId { get; init; }

    public required RectF Bounds { get; init; }

    /// <summary>Opacity, used to fade entering and departing nodes.</summary>
    public required float Alpha { get; init; }

    public required int Depth { get; init; }

    public required NodeKind Kind { get; init; }

    public required int ColorGroup { get; init; }

    public required float AreaFraction { get; init; }

    public required bool HasChildren { get; init; }

    /// <summary>True when this node did not exist in the previous layout.</summary>
    public required bool IsEntering { get; init; }

    /// <summary>
    /// Index of this cell's parent within the same frame, or -1 for a frame root.
    /// Always expressed in the frame's own draw order rather than in either
    /// endpoint layout's, because cells move between parents mid-transition. This
    /// is what lets the renderer walk the hover path to the root without guessing
    /// from geometry: during a transition a child can sit outside the rectangle
    /// its parent is animating towards.
    /// </summary>
    public required int ParentIndex { get; init; }

    /// <summary>Header strip for folder cells, or empty for leaves and collapsed nodes.</summary>
    public RectF HeaderBounds { get; init; }

    /// <summary>Apparent size in bytes, for labels and tooltips.</summary>
    public long LogicalSize { get; init; }

    /// <summary>Allocated size in bytes, for labels and tooltips.</summary>
    public long PhysicalSize { get; init; }
}
