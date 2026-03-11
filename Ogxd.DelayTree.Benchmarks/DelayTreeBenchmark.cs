using BenchmarkDotNet.Attributes;
using HWT;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

// | Method                 | Recursions | Delay | Mean        | Error     | StdDev     | Median      | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |------------:|----------:|-----------:|------------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |    17.78 ms |  0.354 ms |   0.793 ms |    17.89 ms |  1.00 |    0.07 |           10000.0000 |          58.8438 |   26.813 ms |   1719.16 KB |        1.00 |                                                                                                       
// | HashedWheelTimer_Delay | 10000      | 10    |    24.22 ms |  0.542 ms |   1.598 ms |    24.22 ms |  1.37 |    0.11 |           10000.0000 |           0.0313 |   13.240 ms |   4063.23 KB |        2.36 |
// | DelayTree_Hybrid       | 10000      | 10    |    12.91 ms |  0.056 ms |   0.053 ms |    12.91 ms |  0.73 |    0.04 |           10000.0000 |                - |   10.165 ms |    641.95 KB |        0.37 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 10000      | 1000  | 1,199.97 ms |  0.539 ms |   0.504 ms | 1,199.80 ms |  1.00 |    0.00 |           10000.0000 |         186.0000 |  207.701 ms |   1723.01 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 1000  | 1,212.88 ms |  3.441 ms |   3.219 ms | 1,213.50 ms |  1.01 |    0.00 |           10000.0000 |           1.0000 |   33.182 ms |   4066.82 KB |        2.36 |
// | DelayTree_Hybrid       | 10000      | 1000  | 1,199.40 ms |  0.351 ms |   0.274 ms | 1,199.34 ms |  1.00 |    0.00 |           10000.0000 |                - |  259.295 ms |    831.61 KB |        0.48 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    |   782.96 ms | 51.636 ms | 152.250 ms |   833.03 ms |  1.05 |    0.34 |         1000000.0000 |        4211.0000 | 1514.669 ms | 171894.03 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 10    |   825.60 ms | 16.287 ms |  14.438 ms |   829.03 ms |  1.11 |    0.29 |         1000000.0000 |                - | 1068.231 ms | 406285.09 KB |        2.36 |
// | DelayTree_Hybrid       | 1000000    | 10    |    86.20 ms |  1.711 ms |   4.768 ms |    87.62 ms |  0.12 |    0.03 |           20625.8333 |           0.3333 |   98.683 ms |   8953.55 KB |        0.05 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 1000  | 2,110.77 ms | 41.335 ms |  67.914 ms | 2,131.69 ms |  1.00 |    0.05 |          999998.0000 |        4509.0000 | 2505.495 ms |  171887.1 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 1000  | 1,933.52 ms | 30.355 ms |  32.479 ms | 1,924.92 ms |  0.92 |    0.03 |         1000000.0000 |                - | 1535.045 ms | 406266.09 KB |        2.36 |
// | DelayTree_Hybrid       | 1000000    | 1000  | 1,351.77 ms | 36.509 ms | 107.647 ms | 1,277.23 ms |  0.64 |    0.06 |         1000000.0000 |           1.0000 | 1399.927 ms |  63397.41 KB |        0.37 |

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
