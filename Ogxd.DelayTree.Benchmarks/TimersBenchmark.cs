using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using HWT;

namespace Ogxd.DelayTree.Benchmarks;

[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class TimersBenchmark
{
    private int[] _delays;

    [Params(10000, 1000000)]
    public int Recursions { get; set; }

    [Params(1)]
    public int Delay { get; set; }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetRandomDelay(int _) => Random.Shared.Next((int)Math.Max(1, 0.8d * Delay), (int)(1.2d * Delay));

    [GlobalSetup]
    public void Setup()
    {
        // Precompute all these delays to avoid any overhead in the benchmark
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    }
    
    [Benchmark(Baseline = true, OperationsPerInvoke = 10)]
    public async Task Task_DedicatedThread()
    {
        for (int i = 0; i < 10; i++)
        {
            Task[] tasks = _delays.Select(Task.Delay).ToArray();
            await Task.WhenAll(tasks);
        }
    }
    
    [Benchmark(OperationsPerInvoke = 10)]
    public async Task DelayTree_DedicatedThread()
    {
        var delayTree = new DelayTree<TaskCompletion, Task>(16, new DelayTreeDedicatedThreadTimer());
        for (int i = 0; i < 10; i++)
        {
            Task[] tasks = _delays.Select(t => delayTree!.Delay((uint)t)).ToArray();
            await Task.WhenAll(tasks);
        }
    }
    
    [Benchmark(OperationsPerInvoke = 10)]
    public async Task DelayTree_ThreadPoolTimer16()
    {
        var delayTree = new DelayTree<TaskCompletion, Task>(16, new DelayTreeThreadPoolTimer(16));
        for (int i = 0; i < 10; i++)
        {
            Task[] tasks = _delays.Select(t => delayTree!.Delay((uint)t)).ToArray();
            await Task.WhenAll(tasks);
        }
    }
    
    // [Benchmark(OperationsPerInvoke = 10)]
    // public async Task DelayTree_ThreadPoolTimer1()
    // {
    //     var delayTree = new DelayTree<TaskCompletion, Task>(16, new DelayTreeThreadPoolTimer(1));
    //     for (int i = 0; i < 10; i++)
    //     {
    //         Task[] tasks = _delays.Select(t => delayTree!.Delay((uint)t)).ToArray();
    //         await Task.WhenAll(tasks);
    //     }
    // }
}