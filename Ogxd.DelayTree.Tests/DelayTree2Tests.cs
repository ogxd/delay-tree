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
        using DelayTree2<string> delayTree = new(24);
        
        delayTree.Add("world", 2000);
        delayTree.Add("hello", 1000);
        delayTree.Add("cruel", 1500);

        delayTree.Collect().Should().BeEmpty();

        await Task.Delay(3000);
        
        delayTree.Collect().OrderBy(s => s).Should().BeEquivalentTo(new[] { "hello", "cruel", "world" });
    }
}
