using BenchmarkDotNet.Attributes;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

/// <summary>
/// Measures accuracy (wall time per sequential delay) for each timer backend.
/// DelayTree's advantage is high concurrency, but accuracy still matters.
/// </summary>
[MemoryDiagnoser(false)]
[ThreadingDiagnoser]
public class DelayTreeAccuracyBenchmark
{
    [Params(1, 10)]
    public int Delay { get; set; }

    [Benchmark(Baseline = true, OperationsPerInvoke = 100)]
    public async Task Task_Delay()
    {
        for (int i = 0; i < 100; i++)
            await Task.Delay(Delay);
    }

    private DelayTree<TaskCompletion, Task>? _hybrid;

    [GlobalSetup(Target = nameof(DelayTree_Hybrid))]
    public void Hybrid_Setup() =>
        _hybrid = new DelayTree<TaskCompletion, Task>(16, new DelayTreeHybridTimer());

    [GlobalCleanup(Target = nameof(DelayTree_Hybrid))]
    public void Hybrid_Cleanup() { _hybrid!.Dispose(); _hybrid = null; }

    [Benchmark(OperationsPerInvoke = 100)]
    public async Task DelayTree_Hybrid()
    {
        for (int i = 0; i < 100; i++)
            await _hybrid!.Delay((uint)Delay);
    }
}
