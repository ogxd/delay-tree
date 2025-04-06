using System.Threading.Tasks;

namespace Ogxd.DelayTree;

public class TaskCompletion : ICompletion<Task>
{
    private readonly TaskCompletionSource _taskCompletionSource = new();

    public Task CompletionHandle => _taskCompletionSource.Task;

    public void SetCompleted(bool dispose)
    {
        _taskCompletionSource.SetResult();
    }
}