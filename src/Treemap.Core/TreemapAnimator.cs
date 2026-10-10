using System.Diagnostics;

namespace Treemap;

/// <summary>
/// Drives a <see cref="LayoutTween"/> over wall-clock time and exposes the current
/// frame. Intended to be pumped once per render tick.
/// </summary>
public sealed class TreemapAnimator
{
    private readonly Stopwatch _clock = new();

    private LayoutTween? _active;
    private List<TweenedCell> _frame = [];
    private long _startTimestamp;
    private TimeSpan _duration;

    public TreemapAnimator(TimeSpan? defaultDuration = null)
    {
        _duration = defaultDuration ?? TimeSpan.FromMilliseconds(220);
        _clock.Start();
    }

    /// <summary>Duration applied to the next transition.</summary>
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

    /// <summary>Begins a transition. A zero duration snaps immediately.</summary>
    public void Begin(LayoutTween tween)
    {
        ArgumentNullException.ThrowIfNull(tween);

        _active = tween;
        _startTimestamp = _clock.ElapsedTicks;
    }

    /// <summary>Sets the frame directly with no transition.</summary>
    public void Snap(LayoutResult layout)
    {
        _active = null;
        _frame.Clear();

        for (int i = 0; i < layout.Count; i++)
        {
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
            });
        }
    }

    /// <summary>Abandons any active transition, freezing the current frame.</summary>
    public void Cancel() => _active = null;

    /// <summary>
    /// Advances the animation and re-evaluates the frame. Returns true while still
    /// animating, so the host knows whether another frame is required.
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
            return false;
        }

        return true;
    }

    private float ComputeProgress()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return 1f;
        }

        long elapsed = _clock.ElapsedTicks - _startTimestamp;
        double ratio = elapsed / _duration.Ticks;
        return (float)Math.Clamp(ratio, 0d, 1d);
    }
}
