using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using HWT;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

// | Method                 | Recursions | Delay | Mean        | Error     | StdDev     | Median      | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |------------:|----------:|-----------:|------------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |    14.36 ms |  0.286 ms |   0.515 ms |    14.38 ms |  1.00 |    0.05 |           10000.0313 |          14.9375 |    7.390 ms |    1640.8 KB |       1.000 |                                                                                                       
// | HashedWheelTimer_Delay | 10000      | 10    |    21.48 ms |  0.423 ms |   0.910 ms |    21.10 ms |  1.50 |    0.08 |           10443.3750 |                - |    7.326 ms |   3984.55 KB |       2.428 |
// | DelayTree              | 10000      | 10    |    11.82 ms |  0.056 ms |   0.047 ms |    11.82 ms |  0.82 |    0.03 |               2.4375 |           0.1563 |    0.380 ms |      1.25 KB |       0.001 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 10000      | 1000  | 1,199.54 ms |  0.535 ms |   0.500 ms | 1,199.56 ms |  1.00 |    0.00 |           10000.0000 |         214.0000 |   62.248 ms |   1644.69 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 1000  | 1,213.03 ms |  1.676 ms |   1.568 ms | 1,213.59 ms |  1.01 |    0.00 |           10000.0000 |                - |   16.785 ms |   3988.16 KB |        2.42 |
// | DelayTree              | 10000      | 1000  | 1,200.23 ms |  0.635 ms |   0.594 ms | 1,200.33 ms |  1.00 |    0.00 |               6.0000 |                - |    4.715 ms |     75.78 KB |        0.05 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    |   853.55 ms | 52.826 ms | 155.758 ms |   875.20 ms |  1.04 |    0.29 |         1000000.0000 |        5998.0000 | 1749.742 ms | 164062.67 KB |       1.000 |
// | HashedWheelTimer_Delay | 1000000    | 10    |   710.18 ms | 12.949 ms |  12.113 ms |   711.41 ms |  0.86 |    0.18 |         1000633.0000 |                - |  974.608 ms | 398445.48 KB |       2.429 |
// | DelayTree              | 1000000    | 10    |    71.59 ms |  1.352 ms |   2.024 ms |    70.62 ms |  0.09 |    0.02 |               8.4286 |                - |  123.333 ms |     12.22 KB |       0.000 |
// |                        |            |       |             |           |            |             |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 1000  | 1,932.87 ms | 38.396 ms | 104.460 ms | 1,944.77 ms |  1.00 |    0.08 |         1000014.0000 |         800.0000 | 2050.442 ms | 164070.72 KB |       1.000 |
// | HashedWheelTimer_Delay | 1000000    | 1000  | 1,993.49 ms | 36.582 ms |  58.023 ms | 1,976.97 ms |  1.03 |    0.06 |         1000007.0000 |                - | 1158.313 ms | 398446.34 KB |       2.429 |
// | DelayTree              | 1000000    | 1000  | 1,276.58 ms |  4.470 ms |   4.182 ms | 1,275.23 ms |  0.66 |    0.04 |              54.0000 |                - |   47.668 ms |     91.73 KB |       0.001 |

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
    // Pre-allocated once per Setup; reused across all benchmark iterations to avoid per-call Task[] allocation.
    // Passed as ReadOnlySpan<Task> to Task.WhenAll (the .NET 9 overload that skips the internal defensive copy).
    private Task[] _tasks = null!;
    private ValueTask[] _valueTasks = null!;

    [Params(10_000, 1_000_000)]
    public int Recursions { get; set; }

    [Params(10, 1000)]
    public int Delay { get; set; }

    private int GetRandomDelay(int _) => (int)(Delay * (0.8 + Random.Shared.NextDouble() * 0.4));

    [GlobalSetup]
    public void Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _tasks = new Task[Recursions];
        _valueTasks = new ValueTask[Recursions];
    }

    #region Task.Delay

    [Benchmark(Baseline = true, OperationsPerInvoke = 1)]
    public async Task Task_Delay()
    {
        for (int i = 0; i < _delays.Length; i++)
        {
            _tasks[i] = Task.Delay(_delays[i]);
        }

        foreach (Task task in _tasks)
        {
            await task;
        }
    }

    #endregion

    #region HashedWheelTimer

    private HashedWheelTimer? _wheelTimer;

    [GlobalSetup(Target = nameof(HashedWheelTimer_Delay))]
    public void HashedWheelTimer_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _tasks = new Task[Recursions];
        _valueTasks = new ValueTask[Recursions];
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
        for (int i = 0; i < _delays.Length; i++)
        {
            _tasks[i] = WrapWheelTimer((uint)_delays[i]);
        }

        foreach (Task task in _tasks)
        {
            await task;
        }
    }

    private async Task WrapWheelTimer(uint delay)
    {
        await _wheelTimer!.Delay(delay);
    }

    #endregion

    #region DelayTree

    private DelayTree<TaskCompletion, Task>? _delayTree;

    [GlobalSetup(Target = nameof(DelayTree))]
    public void DelayTree_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _tasks = new Task[Recursions];
        _valueTasks = new ValueTask[Recursions];
        _delayTree = new DelayTree<TaskCompletion, Task>(16, new DelayTreeHybridTimer());
    }

    [GlobalCleanup(Target = nameof(DelayTree))]
    public void DelayTree_Cleanup()
    {
        _delayTree!.Dispose();
        _delayTree = null;
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public async Task DelayTree()
    {
        for (int i = 0; i < _delays.Length; i++)
        {
            _tasks[i] = _delayTree!.Delay((uint)_delays[i]);
        }

        foreach (Task task in _tasks)
        {
            await task;
        }
    }

    #endregion
}
