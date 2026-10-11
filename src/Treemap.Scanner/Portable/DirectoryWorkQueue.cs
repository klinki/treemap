namespace Treemap.Scanning;

/// <summary>Hands directories to a fixed pool of workers, and stops when there is no reason to continue.</summary>
/// <remarks>
/// Deliberately not one task per directory: a large volume discovers tens of thousands of
/// them, and a task each would make the scheduler's queues the largest thing in the
/// process. This keeps a fixed number of workers holding one open enumeration each, with
/// the backlog as plain queue entries.
/// </remarks>
internal sealed class DirectoryWorkQueue
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(50);

    // Monitor rather than Lock: this needs wait and pulse, which Lock does not offer.
    private readonly Queue<Work> _pending = [];
    private readonly object _gate = new();

    private int _scanning;
    private bool _stopped;

    public void Enqueue(DirectoryInfo directory, int depth)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _pending.Enqueue(new Work(directory, depth));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Ends the walk and wakes every waiting worker so none is parked on a queue nobody
    /// will fill. Used when the budget runs out or a worker died in a way that must not be
    /// papered over.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Takes the next directory, blocking until one arrives. Returns false once the queue
    /// is empty, the walk has been stopped, or no worker is still scanning - the last of
    /// those being the only condition under which the walk is genuinely finished.
    /// </summary>
    public bool TryTake(ScanCollector collector, CancellationToken cancellationToken, out Work work)
    {
        // Checked before the backlog, not only while waiting: a walk with thousands of
        // queued directories behind it must stop at the next one, not drain the lot.
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            while (true)
            {
                if (_stopped || collector.ShouldStop())
                {
                    work = default;
                    return false;
                }

                if (_pending.Count > 0)
                {
                    work = _pending.Dequeue();
                    _scanning++;
                    return true;
                }

                if (_scanning == 0)
                {
                    work = default;
                    return false;
                }

                Monitor.Wait(_gate, IdleWait);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Reports that a worker is done with the directory it took.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (--_scanning == 0)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    public readonly record struct Work(DirectoryInfo Directory, int Depth);
}