using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using HarmonyLib;
using NUnit.Framework;

namespace Ogxd.DelayTree.Tests;

public class DelayTreeTests
{
    [Test]
    [Timeout(5000)]
    public async Task Sequential_Ascending()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(24);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await delayTree.Delay(500);
        await delayTree.Delay(1000);
        await delayTree.Delay(2000);
        stopwatch.Stop();

        Assert.AreEqual(3500, stopwatch.ElapsedMilliseconds, 100);
    }

    [Test]
    [Timeout(5000)]
    public async Task Sequential_Descending()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(24);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await delayTree.Delay(1000);
        Console.WriteLine("1000 at " + stopwatch.ElapsedMilliseconds);
        await delayTree.Delay(750);
        Console.WriteLine("750 at " + stopwatch.ElapsedMilliseconds);
        await delayTree.Delay(500);
        Console.WriteLine("500 at " + stopwatch.ElapsedMilliseconds);
        stopwatch.Stop();

        Assert.AreEqual(2250, stopwatch.ElapsedMilliseconds, 100);
    }

    [Test]
    [Timeout(5000)]
    public async Task Bitdepth_Overflow()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(8, 20); // 256ms max delay

        int awaited = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 3000)
        {
            await delayTree.Delay(50);
            awaited++;
            //Console.WriteLine("[received] Delay task received");
        }
        stopwatch.Stop();

        Assert.AreEqual(3000, stopwatch.ElapsedMilliseconds, 100);
        Assert.AreEqual(3000d / 50, awaited, 20);
    }

    [Test]
    [Timeout(5000)]
    public async Task Simultaneous()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(24);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Task t1 = delayTree.Delay(500);
        Task t2 = delayTree.Delay(1000);
        Task t3 = delayTree.Delay(2000);

        await Task.WhenAll(t1, t2, t3);
        stopwatch.Stop();

        Assert.AreEqual(2000, stopwatch.ElapsedMilliseconds, 100);
    }

    [Test]
    [Timeout(5000)]
    public async Task SkipCollectionWhenFarDelays()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(24);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await delayTree.Delay(1000);
        stopwatch.Stop();

        Assert.AreEqual(1000, stopwatch.ElapsedMilliseconds, 100);
    }

    [Test]
    [Timeout(5000)]
    public async Task Simultaneous_Random()
    {
        using DelayTree<TaskCompletion, Task> delayTree = new(24);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 100)
            .Select(async _ => await delayTree.Delay((uint)Random.Shared.Next(500, 2000))).ToList();
        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Assert.AreEqual(2000, stopwatch.ElapsedMilliseconds, 100);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Chaos(
        [Values(16, 32)] int depth,
        [Values(5, 20)] int accuracy,
        [Values(100, 100_000)] int parallelism)
    {
        const int duration = 10_000;
        const int minDelay = 10;
        const int maxDelay = 2000;

        using DelayTree<TaskCompletion, Task> delayTree = new(depth, accuracy); // 256ms max delay

        int awaited = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        Task[] tasks = Enumerable.Range(0, parallelism).Select(async _ =>
        {
            while (stopwatch.ElapsedMilliseconds < duration)
            {
                await delayTree.Delay((uint)Random.Shared.Next(minDelay, maxDelay));
                Interlocked.Increment(ref awaited);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        stopwatch.Stop();

        double expectedAwaitedTasks = 1d * parallelism * duration / ((maxDelay - minDelay) / 2d);
        Assert.AreEqual(expectedAwaitedTasks, awaited, 0.2d * expectedAwaitedTasks);
        Assert.AreEqual(duration + maxDelay, stopwatch.ElapsedMilliseconds, 1000d);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Chaos_CancellationToken(
        [Values(16, 32)] int depth,
        [Values(5, 20)] int accuracy,
        [Values(100, 100_000)] int parallelism)
    {
        const int duration = 10_000;
        const int minDelay = 10;
        const int maxDelay = 2000;

        using DelayTree<CancellationCompletion, CancellationToken> delayTree = new(depth, accuracy); // 256ms max delay

        int awaited = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        Task[] tasks = Enumerable.Range(0, parallelism).Select(async _ =>
        {
            while (stopwatch.ElapsedMilliseconds < duration)
            {
                CancellationToken token = delayTree.Delay((uint)Random.Shared.Next(minDelay, maxDelay));
                await token; // Possible thanks to custom awaiter
                Interlocked.Increment(ref awaited);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        stopwatch.Stop();

        double expectedAwaitedTasks = 1d * parallelism * duration / ((maxDelay - minDelay) / 2d);
        Assert.AreEqual(expectedAwaitedTasks, awaited, 0.2d * expectedAwaitedTasks);
        Assert.AreEqual(duration + maxDelay, stopwatch.ElapsedMilliseconds, 1000d);
    }

    // [Test]
    // public void HarmonyCanPatchDelay()
    // {
    //     Patches.ApplyPatches();
    //
    //     Assert.AreEqual(3, Harmony.GetAllPatchedMethods().Count(), "Patching did not work correctly.");
    // }

    [Test]
    public async Task CancellationTokenSourceWorksWhenPatched()
    {
        Patches.ApplyPatches();

        using CancellationTokenSource cts1 = new(1000);
        using CancellationTokenSource cts2 = new(500);
        using CancellationTokenSource ctsCombined = CancellationTokenSource.CreateLinkedTokenSource(cts1.Token, cts2.Token, CancellationToken.None);

        Assert.IsFalse(cts1.IsCancellationRequested);
        Assert.IsFalse(cts2.IsCancellationRequested);
        Assert.IsFalse(ctsCombined.IsCancellationRequested);

        await Task.Delay(600);

        Assert.IsFalse(cts1.IsCancellationRequested);
        Assert.IsTrue(cts2.IsCancellationRequested);
        Assert.IsTrue(ctsCombined.IsCancellationRequested);

        await Task.Delay(600);

        Assert.IsTrue(cts1.IsCancellationRequested);
        Assert.IsTrue(cts2.IsCancellationRequested);
        Assert.IsTrue(ctsCombined.IsCancellationRequested);
    }

    [Test]
    public async Task CancellationTokenFlurl()
    {
        Patches.ApplyPatches();

        Console.WriteLine($"[{DateTime.UtcNow}] CancellationTokenFlurl");

        using var timeoutTokenSource = new CancellationTokenSource(5_000);
        using var totalTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, CancellationToken.None);

        string result = await "https://google.fr"
            .GetAsync(cancellationToken: totalTokenSource.Token)
            .ReceiveString();

        Assert.IsNotEmpty(result);
    }
}
