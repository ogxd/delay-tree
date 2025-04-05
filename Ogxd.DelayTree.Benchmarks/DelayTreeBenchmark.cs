using BenchmarkDotNet.Attributes;
using HWT;

namespace Ogxd.DelayTree.Benchmarks;

[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class DelayTreeBenchmark
{
    private DelayTree<TaskCompletion, Task> _delayTree;
    private HashedWheelTimer _wheelTimer;
    private int[] _delays;

    [Params(1000000)]
    public int Recursions { get; set; }

    [Params(10)]
    public int Delay { get; set; }

    [GlobalSetup]
    public void PrecomputeDelays()
    {
        // Precompute all these delays to avoid any overhead in the benchmark
        _delays = Enumerable.Range(0, Recursions).Select(_ => Random.Shared.Next((int)(0.8d * Delay), (int)(1.2d * Delay))).ToArray();
    }

    [GlobalSetup(Target = nameof(DelayTree_Delay))]
    public void Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(_ => Random.Shared.Next((int)(0.8d * Delay), (int)(1.2d * Delay))).ToArray();
        _delayTree = new(16, 20);
    }

    [GlobalCleanup(Target = nameof(DelayTree_Delay))]
    public void Cleanup()
    {
        _delayTree.Dispose();
        _delayTree = null;
    }

    [Benchmark(Baseline = true)]
    public async Task Task_Delay()
    {
        Task[] tasks = _delays.Select(Task.Delay).ToArray();

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task DelayTree_Delay()
    {
        Task[] tasks = _delays.Select(t => _delayTree.Delay((uint)t)).ToArray();

        await Task.WhenAll(tasks);
    }
    
    [GlobalSetup(Target = nameof(HashedWheelTimer_Delay))]
    public void SetupHashedWheelTimer()
    {
        _delays = Enumerable.Range(0, Recursions).Select(_ => Random.Shared.Next((int)(0.8d * Delay), (int)(1.2d * Delay))).ToArray();
        _wheelTimer = new HashedWheelTimer( tickDuration: TimeSpan.FromSeconds(1)
            , ticksPerWheel: 100000
            , maxPendingTimeouts: 0);
    }

    [GlobalCleanup(Target = nameof(HashedWheelTimer_Delay))]
    public void CleanupHashedWheelTimer()
    {
        _wheelTimer = null;
    }
    
    [Benchmark]
    public async Task HashedWheelTimer_Delay()
    {
        Task[] tasks = _delays.Select(t => DelayWheelTimer((uint)t)).ToArray();
        
        await Task.WhenAll(tasks);
    }
    
    private async Task DelayWheelTimer(uint delay)
    {
        await _wheelTimer.Delay((uint)delay);
    }
}
