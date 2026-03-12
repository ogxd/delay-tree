namespace Ogxd.DelayTree.Completions;

public interface ICompletion<out T>
{
    T CompletionHandle { get; }

    void SetCompleted(bool dispose);
}