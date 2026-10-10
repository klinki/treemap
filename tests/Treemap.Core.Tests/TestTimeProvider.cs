namespace Treemap.Core.Tests;

/// <summary>
/// A hand-driven clock, so animation tests are exact instead of sleep-based.
/// </summary>
/// <remarks>
/// The default frequency is deliberately not 10MHz, because the point of these
/// tests is that progress must be derived from whatever tick frequency the
/// provider reports rather than from <see cref="TimeSpan"/>'s.
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    private long _ticks;

    public TestTimeProvider(long timestampFrequency = 3_000_000) => TimestampFrequency = timestampFrequency;

    public override long TimestampFrequency { get; }

    public override long GetTimestamp() => _ticks;

    /// <summary>Moves the clock forward by whole provider ticks.</summary>
    public void AdvanceTicks(long ticks) => _ticks += ticks;

    /// <summary>Moves the clock forward by a wall-clock duration.</summary>
    public void Advance(TimeSpan delta)
        => AdvanceTicks((long)Math.Round(delta.TotalSeconds * TimestampFrequency));
}
