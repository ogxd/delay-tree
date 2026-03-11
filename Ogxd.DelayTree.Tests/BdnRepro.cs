using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree.Tests;

/// <summary>
/// Reproduces the BDN InProcess scenario: many sequential benchmark iterations sharing one DelayTree instance.
/// BDN runs: JIT(~16 ops) + Pilot(16+32+64 ops) + Warmup(3×64 ops) + Actual(5×64 ops)
/// Total ≈ 350 calls, each creating 10k tasks with ~10ms delays.
/// With bitDepth=12 (maxDelay=4096ms), timestamp wraps around ~4s into the run.
/// </summary>
[TestFixture]
public class BdnRepro
{
    /// <summary>
    /// Simulates BDN's InProcess blocking: benchmark thread calls GetAwaiter().GetResult()
    /// which BLOCKS a dedicated thread (not a thread pool thread), just like BDN does.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public void SimulateBdnInProcessRun_BitDepth12_Blocking()
    {
        using var delayTree = new DelayTree<TaskCompletion, Task>(12, new DelayTreeHybridTimer());

        int recursions = 10_000;
        int[] delays = Enumerable.Range(0, recursions)
            .Select(_ => (int)(10 * (0.8 + Random.Shared.NextDouble() * 0.4)))
            .ToArray();

        int[] opsPerIteration = [1, 1, 16, 32, 64, 64, 64, 64, 64, 64, 64, 64];

        int totalCalls = 0;
        string? failure = null;
        var sw = Stopwatch.StartNew();

        // BDN creates a dedicated thread (not thread pool) and calls GetResult() on it
        var benchmarkThread = new Thread(() =>
        {
            foreach (int ops in opsPerIteration)
            {
                for (int op = 0; op < ops; op++)
                {
                    totalCalls++;
                    var elapsed = sw.ElapsedMilliseconds;
                    var ts = (uint)(elapsed % 4096);

                    var tasks = delays.Select(d => delayTree.Delay((uint)d)).ToArray();
                    var whenAll = Task.WhenAll(tasks);

                    // This is how BDN calls async benchmarks: blocking GetResult()
                    if (!whenAll.Wait(500))
                    {
                        failure = $"Deadlock at call #{totalCalls}, elapsed={elapsed}ms, ts={ts}, count={delayTree.Count}";
                        return;
                    }
                }
            }
        });
        benchmarkThread.IsBackground = true;
        benchmarkThread.Start();
        benchmarkThread.Join();

        Console.WriteLine($"Completed {totalCalls} calls in {sw.ElapsedMilliseconds}ms");
        if (failure != null) Assert.Fail(failure);
    }

    [Test]
    [Timeout(120_000)]
    public async Task SimulateBdnInProcessRun_BitDepth12()
    {
        using var delayTree = new DelayTree<TaskCompletion, Task>(12, new DelayTreeHybridTimer());

        int recursions = 10_000;
        int[] delays = Enumerable.Range(0, recursions)
            .Select(_ => (int)(10 * (0.8 + Random.Shared.NextDouble() * 0.4)))
            .ToArray();

        // Simulate BDN phases: Jitting(2) + Pilot(3) + OverheadWarmup(10) + OverheadActual(20) + WorkloadWarmup(3) + WorkloadActual(5)
        // Each workload call runs 64 ops (unroll factor from BDN pilot)
        int[] opsPerIteration = [1, 1, 1, 1, 1, 1, 1, 1, 16, 32, 64, 64, 64, 64, 64, 64, 64, 64];
        //                        jit  jit pilot pilot pilot wup  wup  wup act  act  act  act  act

        int totalCalls = 0;
        var sw = Stopwatch.StartNew();

        foreach (int ops in opsPerIteration)
        {
            for (int op = 0; op < ops; op++)
            {
                totalCalls++;
                var elapsed = sw.ElapsedMilliseconds;
                var ts = (uint)(elapsed % 4096);

                var tasks = delays.Select(d => delayTree.Delay((uint)d)).ToArray();

                var cts = new CancellationTokenSource(500);
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"Deadlock at call #{totalCalls}, elapsed={elapsed}ms, ts={ts}, count={delayTree.Count}");
                }
            }
        }

        Console.WriteLine($"Completed {totalCalls} calls in {sw.ElapsedMilliseconds}ms");
    }
}
