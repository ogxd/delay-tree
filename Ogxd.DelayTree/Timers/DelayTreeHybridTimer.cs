using System.Threading;

namespace Ogxd.DelayTree.Timers;

/// <summary>
/// A timer that sleeps until close to the next deadline, then tight-spins for the final millisecond.
/// This achieves sub-millisecond accuracy without burning CPU continuously:
/// CPU is only used during the brief spin window near each deadline.
/// When a new shorter delay is added, the sleep is interrupted via a signal.
/// </summary>
public class DelayTreeHybridTimer : IDelayTreeTimer
{
    private Thread? _thread;
    private long _disposed = 0;
    // Signaled when a new delay is added that is earlier than the current next deadline.
    // Allows the sleeping thread to wake up and re-evaluate.
    private readonly ManualResetEventSlim _wakeUp = new(false);

    public void SetDelayTree(IDelayTree delayTree)
    {
        _thread = new Thread(() =>
        {
            while (Interlocked.Read(ref _disposed) == 0)
            {
                if (delayTree.Count == 0ul)
                {
                    // Nothing pending: wait until signaled by an Add
                    _wakeUp.Wait(10);
                    _wakeUp.Reset();
                    continue;
                }

                uint timestamp = delayTree.CurrentTimestampMs;
                int delay = (int)(delayTree.NextDelayTimestampMs - timestamp);

                if (delay > 1)
                {
                    // Sleep until 1ms before the deadline.
                    // Use the wake-up event so an earlier Add can interrupt the sleep.
                    _wakeUp.Wait(delay - 1);
                    _wakeUp.Reset();
                }
                else if (delay > 0)
                {
                    // Final millisecond: spin with enough iterations for ~µs precision
                    // without burning as much CPU as SpinWait(16) in a tight loop
                    Thread.SpinWait(1000);
                }
                else
                {
                    delayTree.Collect(timestamp);
                }
            }
        })
        {
            IsBackground = true,
            Name = "DelayTree Hybrid Collector"
        };
        _thread.Start();
    }

    public void NotifyEarlierDelay() => _wakeUp.Set();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _wakeUp.Set();   // unblock the thread so it sees _disposed and exits
            _thread?.Join(); // wait for it to fully exit before disposing the event
            _wakeUp.Dispose();
        }
    }
}
