using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using HWT;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Benchmarks;

// | Method                 | Recursions | Delay | Mean        | Error     | StdDev     | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |------------:|----------:|-----------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |    16.47 ms |  0.405 ms |   1.174 ms |  1.01 |    0.10 |           10000.0000 |           9.6875 |   15.806 ms |   1640.86 KB |        1.00 |                                                                                                                     
// | HashedWheelTimer_Delay | 10000      | 10    |    23.88 ms |  0.477 ms |   1.368 ms |  1.46 |    0.13 |           10000.0000 |                - |    9.976 ms |   3984.73 KB |        2.43 |
// | DelayTree_Hybrid       | 10000      | 10    |    12.67 ms |  0.130 ms |   0.115 ms |  0.77 |    0.06 |           10000.0000 |           0.0313 |    7.290 ms |    569.28 KB |        0.35 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 10000      | 1000  | 1,200.12 ms |  0.670 ms |   0.627 ms |  1.00 |    0.00 |           10000.0000 |         632.0000 |  166.580 ms |   1644.81 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 1000  | 1,212.00 ms |  2.788 ms |   2.608 ms |  1.01 |    0.00 |           10000.0000 |                - |   26.186 ms |   3988.56 KB |        2.42 |
// | DelayTree_Hybrid       | 10000      | 1000  | 1,199.79 ms |  0.302 ms |   0.283 ms |  1.00 |    0.00 |           10000.0000 |                - |  168.884 ms |    642.13 KB |        0.39 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    |   843.54 ms | 52.122 ms | 153.682 ms |  1.04 |    0.28 |         1000000.0000 |        7029.0000 | 1442.444 ms | 164076.87 KB |       1.000 |
// | HashedWheelTimer_Delay | 1000000    | 10    |   877.93 ms | 28.428 ms |  82.021 ms |  1.08 |    0.23 |         1000000.0000 |                - | 1099.105 ms | 398447.09 KB |       2.428 |
// | DelayTree_Hybrid       | 1000000    | 10    |   101.46 ms |  0.872 ms |   0.728 ms |  0.12 |    0.02 |           13231.0000 |                - |   87.809 ms |    722.73 KB |       0.004 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 1000  | 2,102.15 ms | 41.956 ms | 117.650 ms |  1.00 |    0.08 |          999996.0000 |        4092.0000 | 2562.816 ms | 164071.02 KB |        1.00 |
// | HashedWheelTimer_Delay | 1000000    | 1000  | 1,974.93 ms | 37.779 ms |  41.991 ms |  0.94 |    0.06 |         1000000.0000 |                - | 1306.272 ms | 398445.62 KB |        2.43 |
// | DelayTree_Hybrid       | 1000000    | 1000  | 1,300.77 ms |  1.282 ms |   1.001 ms |  0.62 |    0.04 |         1000000.0000 |                - | 1045.291 ms |  56412.55 KB |        0.34 |

// | Method                 | Recursions | Delay | Mean        | Error     | StdDev     | Ratio | RatioSD | Completed Work Items | Lock Contentions | CPU Time    | Allocated    | Alloc Ratio |
// |----------------------- |----------- |------ |------------:|----------:|-----------:|------:|--------:|---------------------:|-----------------:|------------:|-------------:|------------:|
// | Task_Delay             | 10000      | 10    |    16.78 ms |  0.332 ms |   0.814 ms |  1.00 |    0.07 |           10000.0000 |          38.0938 |   16.617 ms |   1640.86 KB |       1.000 |                                                                                                                     
// | HashedWheelTimer_Delay | 10000      | 10    |    23.63 ms |  0.616 ms |   1.747 ms |  1.41 |    0.12 |           10000.0000 |           0.0313 |    8.523 ms |   3984.61 KB |       2.428 |
// | DelayTree_Hybrid       | 10000      | 10    |    11.44 ms |  0.102 ms |   0.091 ms |  0.68 |    0.03 |               1.7031 |           0.0938 |    0.645 ms |      1.22 KB |       0.001 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 10000      | 1000  | 1,200.05 ms |  0.627 ms |   0.523 ms |  1.00 |    0.00 |           10000.0000 |         266.0000 |   91.390 ms |   1644.75 KB |        1.00 |
// | HashedWheelTimer_Delay | 10000      | 1000  | 1,210.67 ms |  2.827 ms |   2.645 ms |  1.01 |    0.00 |           10000.0000 |                - |   14.936 ms |   3984.61 KB |        2.42 |
// | DelayTree_Hybrid       | 10000      | 1000  | 1,199.58 ms |  0.282 ms |   0.264 ms |  1.00 |    0.00 |              10.0000 |                - |  156.172 ms |     79.66 KB |        0.05 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 10    |   814.32 ms | 47.577 ms | 140.283 ms |  1.03 |    0.26 |         1000000.0000 |        1351.0000 | 1417.274 ms | 164062.67 KB |       1.000 |
// | HashedWheelTimer_Delay | 1000000    | 10    |   830.33 ms | 19.892 ms |  55.121 ms |  1.05 |    0.20 |         1000000.0000 |                - | 1058.042 ms | 398441.52 KB |       2.429 |
// | DelayTree_Hybrid       | 1000000    | 10    |    86.21 ms |  1.074 ms |   0.952 ms |  0.11 |    0.02 |               7.6667 |                - |   54.766 ms |     15.94 KB |       0.000 |
// |                        |            |       |             |           |            |       |         |                      |                  |             |              |             |
// | Task_Delay             | 1000000    | 1000  | 2,057.83 ms | 40.913 ms |  61.237 ms |  1.00 |    0.04 |         1000000.0000 |        2913.0000 | 2207.383 ms | 164072.86 KB |       1.000 |
// | HashedWheelTimer_Delay | 1000000    | 1000  | 1,997.51 ms | 36.757 ms |  34.382 ms |  0.97 |    0.03 |         1000000.0000 |                - | 1166.088 ms | 398445.56 KB |       2.428 |
// | DelayTree_Hybrid       | 1000000    | 1000  | 1,286.28 ms |  1.493 ms |   1.323 ms |  0.63 |    0.02 |              93.0000 |           1.0000 |  220.195 ms |     92.45 KB |       0.001 |

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
    
    #region DelayTree ValueTask

    private DelayTree<ValueTaskCompletion, ValueTask>? _delayTreeValueTask;

    [GlobalSetup(Target = nameof(DelayTree_ValueTask))]
    public void DelayTree_ValueTask_Setup()
    {
        _delays = Enumerable.Range(0, Recursions).Select(GetRandomDelay).ToArray();
        _tasks = new Task[Recursions];
        _valueTasks = new ValueTask[Recursions];
        _delayTreeValueTask = new DelayTree<ValueTaskCompletion, ValueTask>(16, new DelayTreeHybridTimer());
    }

    [GlobalCleanup(Target = nameof(DelayTree_ValueTask))]
    public void DelayTree_ValueTask_Cleanup()
    {
        _delayTreeValueTask!.Dispose();
        _delayTreeValueTask = null;
    }

    [Benchmark(OperationsPerInvoke = 1)]
    public async Task DelayTree_ValueTask()
    {
        for (int i = 0; i < _delays.Length; i++)
        {
            _valueTasks[i] = _delayTreeValueTask!.Delay((uint)_delays[i]);
        }

        foreach (ValueTask valueTask in _valueTasks)
        {
            await valueTask;
        }
    }

    #endregion
}
