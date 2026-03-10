using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ogxd.DelayTree;

public class DelayTreeThreadPoolTimer : IDisposable
{
    private readonly Timer? _timer;
    private readonly DelayTree2<TaskCompletionSource> _delayTree = new();

    public DelayTreeThreadPoolTimer(uint intervalMs)
    {
        _timer = new Timer(_ =>
        {
            foreach (var tcs in _delayTree.Collect())
            {
                tcs.TrySetResult();
            }
        }, null, (int)intervalMs, (int)intervalMs);
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

    public void Dispose()
    {
        _timer?.Dispose();
    }
}