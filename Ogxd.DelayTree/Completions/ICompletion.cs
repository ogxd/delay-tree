namespace Ogxd.DelayTree;

public interface ICompletion<out T>
{
    T CompletionHandle { get; }

    void SetCompleted(bool dispose);
}