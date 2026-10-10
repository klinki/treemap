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
/// </remarks>
public sealed class LayoutTween
{
    private readonly LayoutResult _from;
    private readonly LayoutResult _to;

    /// <summary>Node ids present in <see cref="_from"/> but not <see cref="_to"/>.</summary>
    private readonly List<int> _removedIds;

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

        // --- nodes present in the target: lerp, or fade in if newly added ---
        for (int i = 0; i < _to.Count; i++)
        {
            int id = _to.NodeIds[i];

            if (_from.TryGetIndex(id, out int fromIndex))
            {
                RectF a = _from.Bounds[fromIndex];
                RectF b = _to.Bounds[i];

                output.Add(new TweenedCell
                {
                    NodeId = id,
                    Bounds = RectF.Lerp(a, b, t),
                    Alpha = 1f,
                    Depth = _to.Depths[i],
                    Kind = _to.Kinds[i],
                    ColorGroup = _to.ColorGroups[i],
                    AreaFraction = _to.AreaFractions[i],
                    HasChildren = _to.HasChildren[i],
                    IsEntering = false,
                });
            }
            else
            {
                // Newly discovered node: fade in at its final position.
                output.Add(new TweenedCell
                {
                    NodeId = id,
                    Bounds = _to.Bounds[i],
                    Alpha = t,
                    Depth = _to.Depths[i],
                    Kind = _to.Kinds[i],
                    ColorGroup = _to.ColorGroups[i],
                    AreaFraction = _to.AreaFractions[i],
                    HasChildren = _to.HasChildren[i],
                    IsEntering = true,
                });
            }
        }

        // --- nodes that disappeared: fade out in place ---
        for (int k = 0; k < _removedIds.Count; k++)
        {
            int i = _removedIds[k];
            output.Add(new TweenedCell
            {
                NodeId = _from.NodeIds[i],
                Bounds = _from.Bounds[i],
                Alpha = 1f - t,
                Depth = _from.Depths[i],
                Kind = _from.Kinds[i],
                ColorGroup = _from.ColorGroups[i],
                AreaFraction = _from.AreaFractions[i],
                HasChildren = _from.HasChildren[i],
                IsEntering = false,
            });
        }

        // Draw order: deeper cells last so nested content sits on top.
        output.Sort(static (a, b) =>
        {
            int byAlpha = b.Alpha.CompareTo(a.Alpha);
            return byAlpha != 0 ? byAlpha : a.Depth.CompareTo(b.Depth);
        });
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
}
