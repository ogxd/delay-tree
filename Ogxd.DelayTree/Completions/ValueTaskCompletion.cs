using System.Threading.Tasks;

namespace Ogxd.DelayTree.Completions;

/// <summary>
/// Completion that returns a <see cref="ValueTask"/> instead of a <see cref="Task"/>.
/// Backed by a <see cref="TaskCompletionSource"/> so multiple concurrent awaiters are supported —
/// necessary because DelayTree shares a single leaf (and therefore a single completion object)
/// across all callers whose computed timestamp falls in the same millisecond bucket.
/// </summary>
/// <remarks>
/// <para>
/// Per-leaf allocation profile is identical to <see cref="TaskCompletion"/>
/// (this wrapper + <see cref="TaskCompletionSource"/> + backing <see cref="Task"/>).
/// The benefit over <see cref="TaskCompletion"/> is in the caller:
/// <list type="bullet">
///   <item>The handle is a <see cref="ValueTask"/> struct — zero heap cost when passed or stored.</item>
///   <item><see cref="ValueTask.AsTask()"/> returns the backing <see cref="Task"/> directly with
///         no additional allocation (unlike an <c>IValueTaskSource</c>-backed <see cref="ValueTask"/>
///         whose <c>AsTask()</c> would allocate a new wrapper).</item>
/// </list>
/// </para>
/// </remarks>
public class ValueTaskCompletion : ICompletion<ValueTask>
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask CompletionHandle => new ValueTask(_tcs.Task);

    public void SetCompleted(bool dispose) => _tcs.SetResult();
}
