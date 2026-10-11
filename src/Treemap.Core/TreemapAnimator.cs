namespace Treemap;

/// <summary>
/// Drives a <see cref="LayoutTween"/> over wall-clock time and exposes the current
/// frame. Intended to be pumped once per render tick.
/// </summary>
/// <remarks>
/// Time comes from a <see cref="TimeProvider"/>, so tests can drive it exactly
/// instead of sleeping, and so progress is derived from whatever tick frequency
/// the provider reports rather than assuming <see cref="TimeSpan"/>'s 10MHz.
/// </remarks>
public sealed class TreemapAnimator
{
    private readonly TimeProvider _time;
    private readonly List<TweenedCell> _frame = [];

    private LayoutTween? _active;
    private long _startTimestamp;
    private TimeSpan _duration;

    public TreemapAnimator(TimeSpan? defaultDuration = null, TimeProvider? timeProvider = null)
    {
        // Routed through the property so the clamp cannot be bypassed: assigning the
        // field directly would let a negative default survive construction, while
        // every later read would treat it as zero. A negative duration and a zero
        // duration mean the same thing here, so both settle on the same frame.
        Duration = defaultDuration ?? TimeSpan.FromMilliseconds(220);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Duration applied to the next transition. Negative values clamp to zero rather
    /// than running the tween backwards.
    /// </summary>
    public TimeSpan Duration
    {
        get => _duration;
        set => _duration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>True while a transition is in flight.</summary>
    public bool IsAnimating => _active is not null;

    /// <summary>Progress of the active transition, 0..1. 1 when idle.</summary>
    public float Progress => _active is null ? 1f : ComputeProgress();

    /// <summary>The latest evaluated frame. Stable reference until the next pump.</summary>
    public IReadOnlyList<TweenedCell> CurrentFrame => _frame;

    /// <summary>
    /// Begins a transition and evaluates its first frame, so whatever the host
    /// paints next is coherent: the start layout for a normal duration, and the
    /// settled layout when the duration is zero.
    /// </summary>
    public void Begin(LayoutTween tween)
    {
        ArgumentNullException.ThrowIfNull(tween);

        _active = tween;
        _startTimestamp = _time.GetTimestamp();

        tween.Evaluate(_duration <= TimeSpan.Zero ? 1f : 0f, _frame);
    }

    /// <summary>Sets the frame directly with no transition.</summary>
    public void Snap(LayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _active = null;
        _frame.Clear();

        if (_frame.Capacity < layout.Count)
        {
            _frame.Capacity = layout.Count;
        }

        for (int i = 0; i < layout.Count; i++)
        {
            // The frame is the layout, cell for cell, so the layout's parent indices
            // are already indices into this frame.
            _frame.Add(new TweenedCell
            {
                NodeId = layout.NodeIds[i],
                Bounds = layout.Bounds[i],
                Alpha = 1f,
                Depth = layout.Depths[i],
                Kind = layout.Kinds[i],
                ColorGroup = layout.ColorGroups[i],
                AreaFraction = layout.AreaFractions[i],
                HasChildren = layout.HasChildren[i],
                IsEntering = false,
                ParentIndex = layout.ParentIndices[i],
                HeaderBounds = layout.HeaderBounds[i],
                LogicalSize = layout.LogicalSizes[i],
                PhysicalSize = layout.PhysicalSizes[i],
            });
        }
    }

    /// <summary>Abandons any active transition, freezing the current frame.</summary>
    public void Cancel() => _active = null;

    /// <summary>
    /// Advances the animation and re-evaluates the frame. Returns true whenever a
    /// frame was evaluated — including the one that completes the tween, since that
    /// is the frame showing the settled layout and it has to be painted.
    /// <see cref="IsAnimating"/> reports whether any frames remain.
    /// </summary>
    public bool Advance()
    {
        if (_active is null)
        {
            return false;
        }

        float progress = ComputeProgress();
        _active.Evaluate(progress, _frame);

        if (progress >= 1f)
        {
            _active = null;
        }

        return true;
    }

    private float ComputeProgress()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return 1f;
        }

        long elapsed = _time.GetTimestamp() - _startTimestamp;
        if (elapsed <= 0)
        {
            return 0f;
        }

        // GetTimestamp counts in the provider's own frequency, which is not
        // TimeSpan's 10MHz: Stopwatch reports 1GHz ticks on Unix. Convert the
        // duration into those units, then divide as a double — integer division
        // truncates the first few milliseconds of every transition to no progress
        // at all, and dividing mismatched units finishes it instantly instead.
        double durationTicks = _duration.Ticks * ((double)_time.TimestampFrequency / TimeSpan.TicksPerSecond);
        if (durationTicks <= 0d)
        {
            return 1f;
        }

        return (float)Math.Clamp(elapsed / durationTicks, 0d, 1d);
    }
}
