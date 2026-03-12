using BenchmarkDotNet.Attributes;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

/// <summary>
/// Measures accuracy (wall time per sequential delay) for each timer backend.
/// DelayTree's advantage is high concurrency, but accuracy still matters.
/// </summary>
public class DelayTreeAccuracyBenchmark
{
    [Params(1, 10)]
    public int Delay { get; set; }

    [Benchmark(Baseline = true, OperationsPerInvoke = 100)]
    public async Task Task_Delay()
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(Delay);
        }
    }

    private DelayTree<TaskCompletion, Task>? _hybrid;

    [GlobalSetup(Target = nameof(DelayTree))]
    public void DelayTree_Setup()
    {
        _hybrid = new DelayTree<TaskCompletion, Task>(16, new DelayTreeHybridTimer());
    }

    [GlobalCleanup(Target = nameof(DelayTree))]
    public void DelayTree_Cleanup()
    {
        _hybrid!.Dispose(); _hybrid = null;
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public async Task DelayTree()
    {
        for (int i = 0; i < 100; i++)
        {
            await _hybrid!.Delay((uint)Delay);
        }
    }
}
