using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using HWT;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

// | Method                 | Recursions | Delay | Mean      | Error     | StdDev    | Median    | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |----------:|----------:|----------:|----------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |  16.24 ms |  0.285 ms |  0.400 ms |  16.39 ms |  1.00 |    0.03 |           10000.0000 |         139.8750 |   14.886 ms |   1719.16 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 10    |  22.38 ms |  0.445 ms |  1.058 ms |  22.39 ms |  1.38 |    0.07 |           10000.0000 |           0.0625 |   12.440 ms |   4062.88 KB |        2.36 |
// | DelayTree_ThreadPool   | 10000      | 10    |  19.21 ms |  0.383 ms |  0.393 ms |  19.27 ms |  1.18 |    0.04 |               1.9688 |           0.2500 |    1.361 ms |    328.21 KB |        0.19 |
// | DelayTree_Dedicated    | 10000      | 10    |  11.00 ms |  0.002 ms |  0.002 ms |  11.00 ms |  0.68 |    0.02 |                    - |                - |    2.882 ms |    343.01 KB |        0.20 |
// | DelayTree_Hybrid       | 10000      | 10    |  12.04 ms |  0.235 ms |  0.399 ms |  12.15 ms |  0.74 |    0.03 |                    - |           0.1406 |    1.172 ms |    333.72 KB |        0.19 |
// |                        |            |       |           |           |           |           |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    | 870.21 ms | 32.442 ms | 95.657 ms | 883.66 ms |  1.01 |    0.17 |         1000000.0000 |        4213.0000 | 1964.247 ms | 171894.95 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 10    | 849.26 ms | 16.533 ms | 28.074 ms | 843.74 ms |  0.99 |    0.13 |         1000000.0000 |                - | 1186.259 ms | 406262.67 KB |        2.36 |
// | DelayTree_ThreadPool   | 1000000    | 10    |  86.81 ms |  1.734 ms |  4.717 ms |  88.43 ms |  0.10 |    0.01 |               8.3333 |           0.1667 |   40.446 ms |  11077.49 KB |        0.06 |
// | DelayTree_Dedicated    | 1000000    | 10    |  73.32 ms |  0.890 ms |  0.695 ms |  73.09 ms |  0.09 |    0.01 |                    - |           0.1429 |   56.981 ms |   8419.35 KB |        0.05 |
// | DelayTree_Hybrid       | 1000000    | 10    |  99.93 ms |  3.217 ms |  8.915 ms | 100.14 ms |  0.12 |    0.02 |                    - |                - |   94.882 ms |   8391.53 KB |        0.05 |

/// <summary>
/// Throughput benchmark: schedule N concurrent delays and wait for all to complete.
/// Measures total wall time and memory for different timer backends.
/// </summary>
[MemoryDiagnoser(false)]
[CpuDiagnoser]
[ThreadingDiagnoser]
public class DelayTreeBenchmark
{
    private int[] _delays = null!;

    [Params(10_000, 1_000_000)]
    public int Recursions { get; set; }

    [Params(10)]
    public int Delay { get; set; }

    private int GetRandomDelay(int _) =>
        (int)(Delay * (0.8 + Random.Shared.NextDouble() * 0.4));

    [GlobalSetup]
    public void Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    }

    // ── Baseline: Task.Delay ──────────────────────────────────────────────────

    // [Benchmark(Baseline = true, OperationsPerInvoke = 1)]
    // public async Task Task_Delay()
    // {
    //     Task[] tasks = _delays.Select(Task.Delay).ToArray();
    //     await Task.WhenAll(tasks);
    // }

    // ── HashedWheelTimer ─────────────────────────────────────────────────────
    //
    // private HashedWheelTimer? _wheelTimer;
    //
    // [GlobalSetup(Target = nameof(HashedWheelTimer_Delay))]
    // public void HashedWheelTimer_Setup()
    // {
    //     _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    //     // Tick=20ms × 3277 ticks ≈ 65.5 s max (matches DelayTree 16-bit depth)
    //     _wheelTimer = new HashedWheelTimer(
    //         tickDuration: TimeSpan.FromMilliseconds(20),
    //         ticksPerWheel: 3277,
    //         maxPendingTimeouts: 0);
    // }
    //
    // [GlobalCleanup(Target = nameof(HashedWheelTimer_Delay))]
    // public void HashedWheelTimer_Cleanup()
    // {
    //     _wheelTimer = null;
    // }
    //
    // [Benchmark(OperationsPerInvoke = 1)]
    // public async Task HashedWheelTimer_Delay()
    // {
    //     Task[] tasks = _delays.Select(d => WrapWheelTimer((uint)d)).ToArray();
    //     await Task.WhenAll(tasks);
    // }
    //
    // private async Task WrapWheelTimer(uint delay)
    // {
    //     await _wheelTimer!.Delay(delay);
    // }

    // ── DelayTree (ThreadPool timer, 10ms tick) ───────────────────────────────

    // private DelayTree<TaskCompletion, Task>? _delayTreeThreadPool;
    //
    // [GlobalSetup(Target = nameof(DelayTree_ThreadPool))]
    // public void DelayTree_ThreadPool_Setup()
    // {
    //     _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    //     _delayTreeThreadPool = new DelayTree<TaskCompletion, Task>(32, new DelayTreeThreadPoolTimer(10));
    // }
    //
    // [GlobalCleanup(Target = nameof(DelayTree_ThreadPool))]
    // public void DelayTree_ThreadPool_Cleanup()
    // {
    //     _delayTreeThreadPool!.Dispose();
    //     _delayTreeThreadPool = null;
    // }
    //
    // [Benchmark(OperationsPerInvoke = 1)]
    // public async Task DelayTree_ThreadPool()
    // {
    //     Task[] tasks = _delays.Select(d => _delayTreeThreadPool!.Delay((uint)d)).ToArray();
    //     await Task.WhenAll(tasks);
    // }
    //
    // // ── DelayTree (dedicated spinning thread) ─────────────────────────────────
    //
    // private DelayTree<TaskCompletion, Task>? _delayTreeDedicated;
    //
    // [GlobalSetup(Target = nameof(DelayTree_Dedicated))]
    // public void DelayTree_Dedicated_Setup()
    // {
    //     _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    //     _delayTreeDedicated = new DelayTree<TaskCompletion, Task>(32, new DelayTreeDedicatedThreadTimer());
    // }
    //
    // [GlobalCleanup(Target = nameof(DelayTree_Dedicated))]
    // public void DelayTree_Dedicated_Cleanup()
    // {
    //     _delayTreeDedicated!.Dispose();
    //     _delayTreeDedicated = null;
    // }
    //
    // [Benchmark(OperationsPerInvoke = 1)]
    // public async Task DelayTree_Dedicated()
    // {
    //     Task[] tasks = _delays.Select(d => _delayTreeDedicated!.Delay((uint)d)).ToArray();
    //     await Task.WhenAll(tasks);
    // }
    //
    // ── DelayTree (hybrid) ─────────────────────────────────

    private DelayTree<TaskCompletion, Task>? _delayTreeHybrid;

    [GlobalSetup(Target = nameof(DelayTree_Hybrid))]
    public void DelayTree_Hybrid_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _delayTreeHybrid = new DelayTree<TaskCompletion, Task>(12, new DelayTreeHybridTimer());
    }

    [GlobalCleanup(Target = nameof(DelayTree_Hybrid))]
    public void DelayTree_Hybrid_Cleanup()
    {
        _delayTreeHybrid!.Dispose();
        _delayTreeHybrid = null;
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public async Task DelayTree_Hybrid()
    {
        Task[] tasks = _delays.Select(d => _delayTreeHybrid!.Delay((uint)d)).ToArray();
        await Task.WhenAll(tasks);
    }
}
