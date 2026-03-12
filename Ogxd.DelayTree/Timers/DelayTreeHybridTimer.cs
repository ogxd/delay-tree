using System;
using System.Threading;

namespace Ogxd.DelayTree.Timers;

public class DelayTreeHybridTimer : IDelayTreeTimer
{
    private Thread? _thread;
    private long _disposed = 0;
    private const int MaxPauseMs = 10;

    public void SetDelayTree(IDelayTree delayTree)
    {
        _thread = new Thread(() =>
        {
            int pauseTimeMs = 0;
            while (Interlocked.Read(ref _disposed) == 0)
            {
                if (delayTree.Count == 0ul)
                {
                    // In case there is no delay, progressively increase wait time, up to 10ms
                    pauseTimeMs = Math.Clamp(pauseTimeMs + 1, 1, MaxPauseMs);
                    Thread.Sleep(pauseTimeMs);
                    continue;
                }

                uint timestamp = delayTree.CurrentTimestampMs;
                int delay = (int)(delayTree.NextDelayTimestampMs - timestamp);
                pauseTimeMs = 0;

                if (delay > 0)
                {
                    // Sleep until deadline, but not more than 10ms
                    Thread.Sleep(Math.Clamp(delay, 1, MaxPauseMs));
                    continue;
                }

                // Delay has passed, time to collect
                delayTree.Collect(timestamp);
            }
        })
        {
            IsBackground = true,
            Name = "DelayTree Hybrid Collector"
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _thread?.Join(); // wait for it to fully exit before disposing the event
        }
    }
}
