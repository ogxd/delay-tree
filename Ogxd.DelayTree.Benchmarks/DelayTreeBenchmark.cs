using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using HWT;

namespace Ogxd.DelayTree.Benchmarks;

/*
[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class DelayTreeBenchmark
{
    private int[] _delays;

    [Params(1000000)]
    public int Recursions { get; set; }

    [Params(10)]
    public int Delay { get; set; }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetRandomDelay(int _) => Random.Shared.Next((int)(0.8d * Delay), (int)(1.2d * Delay));

    [GlobalSetup]
    public void Baseline_Setup()
    {
        // Precompute all these delays to avoid any overhead in the benchmark
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    }

    [Benchmark(Baseline = true)]
    public async Task Task_Delay()
    {
        Task[] tasks = _delays.Select(Task.Delay).ToArray();

        await Task.WhenAll(tasks);
    }
    
    
    // Hashed Wheel Timer
    private HashedWheelTimer? _wheelTimer;
    
    [GlobalSetup(Target = nameof(HashedWheelTimer_Delay))]
    public void HashedWheelTimer_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        // The delay tree here is configured with a bit depth of 16, thus can take up to 65536ms delays.
        // The wheel timer is configured with a tick duration of 20ms and 3277 ticks per wheel, thus can take up to 65540ms delays (so it's comparable).
        _wheelTimer = new HashedWheelTimer(tickDuration: TimeSpan.FromMilliseconds(20), ticksPerWheel: 3277, maxPendingTimeouts: 0);
    }

    [GlobalCleanup(Target = nameof(HashedWheelTimer_Delay))]
    public void HashedWheelTimer_Cleanup()
    {
        _wheelTimer = null;
    }
    
    [Benchmark]
    public async Task HashedWheelTimer_Delay()
    {
        Task[] tasks = _delays.Select(t => DelayWheelTimer((uint)t)).ToArray();
        
        await Task.WhenAll(tasks);
    }
    
    // The wheel timer returns a TimedAwaiter, so we convert it into a task to be able to use Task.WhenAll (not optimal)
    private async Task DelayWheelTimer(uint delay)
    {
        await _wheelTimer!.Delay(delay);
    }
    
    
    // Delay Tree
    private DelayTree<TaskCompletion, Task>? _delayTree;

    [GlobalSetup(Target = nameof(DelayTree_Delay))]
    public void DelayTree_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _delayTree = new(16, 20);
    }
    
    [GlobalCleanup(Target = nameof(DelayTree_Delay))]
    public void DelayTree_Cleanup()
    {
        _delayTree!.Dispose();
        _delayTree = null;
    }
    
    [Benchmark]
    public async Task DelayTree_Delay()
    {
        Task[] tasks = _delays.Select(t => _delayTree!.Delay((uint)t)).ToArray();

        await Task.WhenAll(tasks);
    }
}
*/