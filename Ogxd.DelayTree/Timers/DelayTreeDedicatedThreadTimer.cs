using System.Runtime.CompilerServices;
using System.Threading;

namespace Ogxd.DelayTree;

public class DelayTreeDedicatedThreadTimer : IDelayTreeTimer
{
    private Thread? _thread;
    private long _disposed = 0;
    
    public void SetDelayTree(IDelayTree delayTree)
    {
        _thread = new Thread(() =>
        {
            SpinWait spinWait = new();
            while (Interlocked.Read(ref _disposed) == 0)
            {
                if (delayTree.Count == 0ul)
                {
                    spinWait.SpinOnce();
                    continue;
                }
                
                uint timestamp = delayTree.CurrentTimestampMs;
        
                // Fast path - no delays to collect because we know the next delay is in the future
                int delay = (int)(delayTree.NextDelayTimestampMs - timestamp);
                if (delay > 0)
                {
                    //_lastTimestamp = timestamp;
                    if (delay < 5)
                    {
                        // Fancy spinning
                        for (int i = 0; i < 100; i++)
                        {
                            Nop();
                        }
                    }
                    else
                    {
                        spinWait.SpinOnce();
                    }
                    continue;
                }
                
                delayTree.Collect(timestamp);
            }
        })
        {
            IsBackground = true,
            Name = "DelayTree Collector"
        };
        _thread.Start();
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Nop() {}

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}