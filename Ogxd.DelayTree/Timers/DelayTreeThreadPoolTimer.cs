using System.Threading;

namespace Ogxd.DelayTree;

public class DelayTreeThreadPoolTimer(uint intervalMs) : IDelayTreeTimer
{
    private Timer? _timer;

    public void SetDelayTree(IDelayTree delayTree)
    {
        _timer = new Timer(_ => delayTree.Collect(delayTree.CurrentTimestampMs), null, (int)intervalMs, (int)intervalMs);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}