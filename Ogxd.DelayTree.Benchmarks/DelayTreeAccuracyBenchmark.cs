using BenchmarkDotNet.Attributes;

namespace Ogxd.DelayTree.Benchmarks;

[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class DelayTreeAccuracyBenchmark
{
    private DelayTree<TaskCompletion, Task> _delayTree;

    [Params(1, /*2, 5, 10,*/ 1000)]
    public int Delay { get; set; }

    [GlobalSetup(Target = nameof(DelayTree_Delay))]
    public void Setup()
    {
        _delayTree = new(16, 20);
    }

    [GlobalCleanup(Target = nameof(DelayTree_Delay))]
    public void Cleanup()
    {
        _delayTree.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Task_Delay()
    {
        await Task.Delay(Delay);
    }

    [Benchmark]
    public async Task DelayTree_Delay()
    {
        await Task.Delay(Delay);
        // await _delayTree.Delay((uint)Delay);
    }
}
