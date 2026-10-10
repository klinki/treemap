using System.Runtime.ExceptionServices;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Runs a test body on its own STA thread with a message pump, because handle creation
/// and posted callbacks need one. Nothing is ever shown: handles are created directly and
/// rendering is exercised through <c>DrawToBitmap</c>.
/// </summary>
internal static class StaRunner
{
    /// <summary>
    /// Bound on how long <see cref="Run"/> waits for the body. It is not a cancellation:
    /// a wedged body keeps running on its background thread and the assert is what fails.
    /// </summary>
    private static readonly TimeSpan BodyDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Bound on a single pumped condition, after which it throws.</summary>
    private static readonly TimeSpan PumpDeadline = TimeSpan.FromSeconds(10);

    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        Thread thread = new(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(BodyDeadline), "the STA test body did not finish in time");
        failure?.Throw();
    }

    /// <summary>Pumps messages until <paramref name="condition"/> holds.</summary>
    public static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + PumpDeadline;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the expected UI state was not reached before the pump deadline");
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }

        Application.DoEvents();
    }

    /// <summary>
    /// Pumps for a fixed span. Used where the assertion is that something does *not*
    /// happen, and the callback under test needs a chance to misbehave first.
    /// </summary>
    public static void PumpFor(TimeSpan duration)
    {
        DateTime deadline = DateTime.UtcNow + duration;

        do
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        while (DateTime.UtcNow < deadline);
    }
}