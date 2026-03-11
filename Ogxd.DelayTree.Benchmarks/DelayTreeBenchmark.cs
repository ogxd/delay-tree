using BenchmarkDotNet.Attributes;
using HWT;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

// | Method                 | Recursions | Delay | Mean        | Error     | StdDev     | Median      | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |------------:|----------:|-----------:|------------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |    16.59 ms |  0.322 ms |   0.451 ms |    16.59 ms |  1.00 |    0.04 |           10000.0000 |          26.3438 |   17.157 ms |   1719.16 KB |        1.00 |                                                                                                       
// | HashedWheelTimer_Delay | 10000      | 10    |    21.21 ms |  0.416 ms |   0.761 ms |    21.13 ms |  1.28 |    0.06 |           10000.0000 |           0.0313 |    9.883 ms |   4062.99 KB |        2.36 |
// | DelayTree_Hybrid       | 10000      | 10    |    12.80 ms |  0.099 ms |   0.088 ms |    12.81 ms |  0.77 |    0.02 |           10000.0000 |           0.0313 |    9.066 ms |     644.5 KB |        0.37 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 10000      | 1000  | 1,199.82 ms |  0.703 ms |   0.587 ms | 1,199.90 ms |  1.00 |    0.00 |           10000.0000 |         265.0000 |  167.389 ms |   1724.15 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 1000  | 1,214.08 ms |  2.672 ms |   2.500 ms | 1,215.33 ms |  1.01 |    0.00 |           10000.0000 |                - |   21.510 ms |   4066.82 KB |        2.36 |
// | DelayTree_Hybrid       | 10000      | 1000  | 1,199.38 ms |  0.411 ms |   0.364 ms | 1,199.28 ms |  1.00 |    0.00 |           10000.0000 |                - |  179.673 ms |    718.65 KB |        0.42 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    |   633.18 ms | 45.527 ms | 134.236 ms |   607.39 ms |  1.04 |    0.31 |         1000000.0000 |        2504.0000 |  805.467 ms | 171884.28 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 10    |   873.34 ms | 17.015 ms |  20.255 ms |   871.88 ms |  1.44 |    0.29 |         1000000.0000 |                - |  890.999 ms | 406266.28 KB |        2.36 |
// | DelayTree_Hybrid       | 1000000    | 10    |    82.71 ms |  1.511 ms |   1.413 ms |    82.97 ms |  0.14 |    0.03 |           18946.8333 |           0.3333 |   56.535 ms |   8853.59 KB |        0.05 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 1000  | 1,960.97 ms | 39.079 ms | 100.173 ms | 1,973.58 ms |  1.00 |    0.07 |         1000000.0000 |        3254.0000 | 1733.959 ms | 171888.01 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 1000  | 2,187.35 ms | 43.439 ms | 123.933 ms | 2,144.95 ms |  1.12 |    0.09 |         1000000.0000 |                - | 1480.355 ms | 406263.95 KB |        2.36 |
// | DelayTree_Hybrid       | 1000000    | 1000  | 1,276.22 ms |  1.010 ms |   0.896 ms | 1,275.94 ms |  0.65 |    0.03 |         1000000.0000 |                - |  917.083 ms |  64422.46 KB |        0.37 |

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

    [Params(10, 1000)]
    public int Delay { get; set; }

    private int GetRandomDelay(int _) => (int)(Delay * (0.8 + Random.Shared.NextDouble() * 0.4));

    [GlobalSetup]
    public void Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
    }

    #region Task.Delay
    
    [Benchmark(Baseline = true, OperationsPerInvoke = 1)]
    public async Task Task_Delay()
    {
        Task[] tasks = _delays.Select(Task.Delay).ToArray();
        await Task.WhenAll(tasks);
    }
    
    #endregion

    #region HashedWheelTimer
    
    private HashedWheelTimer? _wheelTimer;
    
    [GlobalSetup(Target = nameof(HashedWheelTimer_Delay))]
    public void HashedWheelTimer_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        // Tick=20ms × 3277 ticks ≈ 65.5 s max (matches DelayTree 16-bit depth)
        _wheelTimer = new HashedWheelTimer(
            tickDuration: TimeSpan.FromMilliseconds(20),
            ticksPerWheel: 3277,
            maxPendingTimeouts: 0);
    }
    
    [GlobalCleanup(Target = nameof(HashedWheelTimer_Delay))]
    public void HashedWheelTimer_Cleanup()
    {
        _wheelTimer = null;
    }
    
    [Benchmark(OperationsPerInvoke = 1)]
    public async Task HashedWheelTimer_Delay()
    {
        Task[] tasks = _delays.Select(d => WrapWheelTimer((uint)d)).ToArray();
        await Task.WhenAll(tasks);
    }
    
    private async Task WrapWheelTimer(uint delay)
    {
        await _wheelTimer!.Delay(delay);
    }
    
    #endregion

    #region DelayTree

    private DelayTree<TaskCompletion, Task>? _delayTreeHybrid;

    [GlobalSetup(Target = nameof(DelayTree_Hybrid))]
    public void DelayTree_Hybrid_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _delayTreeHybrid = new DelayTree<TaskCompletion, Task>(16, new DelayTreeHybridTimer());
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

    #endregion
}
