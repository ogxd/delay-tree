using System.Threading;

namespace Ogxd.DelayTree.Completions;

public class CancellationCompletion : ICompletion<CancellationToken>
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public CancellationToken CompletionHandle => _cancellationTokenSource.Token;

    public void SetCompleted(bool dispose)
    {
        // CancellationTokenSource.Cancel() invokes registered callbacks synchronously,
        // which would block the timer thread. Queue to thread pool like RunContinuationsAsynchronously.
        var cts = _cancellationTokenSource;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (source, shouldDispose) = state;
            source.Cancel();
            if (shouldDispose)
                source.Dispose();
        }, (cts, dispose), preferLocal: false);
    }
}