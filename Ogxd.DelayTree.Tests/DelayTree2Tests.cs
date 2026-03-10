using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Ogxd.DelayTree.Tests;

public class DelayTree2Tests
{
    [Test]
    [Timeout(5000)]
    public async Task Sequential_Ascending()
    {
        DelayTree3<string> delayTree = new(24);
        
        delayTree.Add("world", 2000);
        delayTree.Add("hello", 1000);
        delayTree.Add("cruel", 1500);

        delayTree.Collect().Should().BeEmpty();

        await Task.Delay(3000);
        
        delayTree.Collect().OrderBy(s => s).Should().BeEquivalentTo(new[] { "hello", "cruel", "world" });
    }
    
    [Test]
    public async Task Sequential_Ascending2()
    {
        DelayTree2<uint> delayTree = new(24);

        Stopwatch sw = Stopwatch.StartNew();
        Parallel.ForEach(Enumerable.Range(1, 100_000), new ParallelOptions { MaxDegreeOfParallelism = 1 }, i =>
        {
            uint delay = (uint)Random.Shared.Next(10, 5_000) + (uint)sw.ElapsedMilliseconds;
            delayTree.Add(delay, delay);
        });

        await Task.Delay(10_000);
        
        var collected = delayTree.Collect().ToArray();
        //collected.Should().HaveCount(100_000);
        collected.Should().BeInAscendingOrder();
    }
}
