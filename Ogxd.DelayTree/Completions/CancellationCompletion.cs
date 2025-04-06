using System.Threading;

namespace Ogxd.DelayTree;

public class CancellationCompletion : ICompletion<CancellationToken>
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public CancellationToken CompletionHandle => _cancellationTokenSource.Token;

    public void SetCompleted(bool dispose)
    {
        _cancellationTokenSource.Cancel();
        if (dispose)
        {
            _cancellationTokenSource.Dispose();
        }
    }
}