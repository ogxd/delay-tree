using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ogxd.DelayTree;

public class DelayTreeDedicatedThreadTimer : IDisposable
{
    private readonly Thread? _thread;
    private readonly DelayTree2<TaskCompletionSource> _delayTree = new();
    private long _disposed = 0;

    public DelayTreeDedicatedThreadTimer()
    {
        _thread = new Thread(() =>
        {
            SpinWait spinWait = new();
            while (Interlocked.Read(ref _disposed) == 0)
            {
                if (_delayTree.Count == 0ul)
                {
                    spinWait.SpinOnce();
                    continue;
                }
                
                uint timestamp = _delayTree.CurrentTimestampMs;
        
                // Fast path - no delays to collect because we know the next delay is in the future
                int delay = (int)(_delayTree.NextDelayTimestampMs - timestamp);
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
                
                foreach (var tcs in _delayTree.Collect())
                {
                    tcs.TrySetResult();
                }
            }
        })
        {
            IsBackground = true,
            Name = "DelayTree Collector"
        };
        _thread.Start();
    }
    
    public Task Delay(uint delayMilliseconds)
    {
        if (delayMilliseconds == 0)
        {
            return Task.CompletedTask;
        }
        
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _delayTree.Add(tcs, delayMilliseconds);
        return tcs.Task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Nop() {}

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}