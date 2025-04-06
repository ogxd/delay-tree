/*
using System;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using HarmonyLib;
using NUnit.Framework;

namespace Ogxd.DelayTree.Tests;

public class PatchesTests
{
    [Test]
    public void HarmonyCanPatchDelay()
    {
        Patches.ApplyPatches();
    
        Assert.AreEqual(3, Harmony.GetAllPatchedMethods().Count(), "Patching did not work correctly.");
    }

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
*/