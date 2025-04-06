using BenchmarkDotNet.Attributes;

namespace Ogxd.DelayTree.Benchmarks;

[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class DelayTreeAccuracyBenchmark
{
    private DelayTree<TaskCompletion, Task>? _delayTree;

    [Params(1, /*2, 5,*/ 10)]
    public int Delay { get; set; }

    [GlobalSetup(Target = nameof(DelayTree_Delay))]
    public void Setup()
    {
        _delayTree = new(16);
    }

    [GlobalCleanup(Target = nameof(DelayTree_Delay))]
    public void Cleanup()
    {
        _delayTree!.Dispose();
        _delayTree = null;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 100)]
    public async Task Task_Delay()
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(Delay);
        }
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public async Task DelayTree_Delay()
    {
        for (int i = 0; i < 100; i++)
        {
            await _delayTree!.Delay((uint)Delay);
        }
    }
}
