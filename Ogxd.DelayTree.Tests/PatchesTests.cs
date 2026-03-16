using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using NUnit.Framework;

namespace Ogxd.DelayTree.Tests;

/// <summary>
/// Tests for the Harmony-based BCL patches (BclPatch project).
///
/// NOTE: As of Harmony 2.3.5 on .NET 8/9/10, patching Task.Delay and
/// CancellationTokenSource constructors fails with NotImplementedException
/// from HarmonyLib.PatchFunctions.UpdateWrapper. These BCL methods use JIT
/// mechanisms that Harmony cannot currently wrap on modern .NET.
///
/// The patch-mechanism tests below will fail until that is resolved.
/// The behavioral tests are skipped (Ignored) when patching fails, so they
/// do not generate false positives from the underlying BCL implementations.
/// </summary>
[TestFixture]
public class PatchesTests
{
    // Captured once for the whole fixture so all tests share the same outcome.
    private static Exception? _patchException;

    [OneTimeSetUp]
    public void ApplyOnce()
    {
        try
        {
            Patches.ApplyPatches();
        }
        catch (Exception ex)
        {
            _patchException = ex;
        }
    }

    // -------------------------------------------------------------------------
    // Patch-mechanism tests — these directly verify Harmony did its job.
    // They fail if patching is broken (intended: they surface the real problem).
    // -------------------------------------------------------------------------

    [Test]
    public void ApplyPatches_DoesNotThrow()
    {
        Assert.IsNull(_patchException,
            $"Patches.ApplyPatches() threw an exception. " +
            $"Harmony may not support these BCL methods on the current runtime.\n" +
            $"{_patchException?.InnerException?.Message ?? _patchException?.Message}");
    }

    [Test]
    public void ApplyPatches_PatchesTwoMethods()
    {
        if (_patchException != null)
            Assert.Ignore("ApplyPatches() threw — see ApplyPatches_DoesNotThrow for the root cause.");

        var patched = Harmony.GetAllPatchedMethods().ToList();
        Assert.AreEqual(2, patched.Count,
            $"Expected exactly 2 patched methods (Task.Delay + CTS.InitializeWithTimer), " +
            $"got: {string.Join(", ", patched.Select(m => $"{m.DeclaringType?.Name}.{m.Name}"))}");
    }

    [Test]
    public void ApplyPatches_IsIdempotent()
    {
        if (_patchException != null)
            Assert.Ignore("ApplyPatches() threw — see ApplyPatches_DoesNotThrow for the root cause.");

        int countBefore = Harmony.GetAllPatchedMethods().Count();
        Patches.ApplyPatches(); // second call must be a no-op
        Assert.AreEqual(countBefore, Harmony.GetAllPatchedMethods().Count(),
            "Second call to ApplyPatches() changed the patched-method count.");
    }

    // -------------------------------------------------------------------------
    // Behavioral tests — skipped (Ignored) when patching failed so they don't
    // pass trivially on the original BCL implementations.
    // -------------------------------------------------------------------------

    private void RequirePatching()
    {
        if (_patchException != null)
            Assert.Ignore(
                $"Patches could not be applied ({_patchException.InnerException?.Message ?? _patchException.Message}). " +
                "Fix the Harmony incompatibility first.");
    }

    [Test]
    [Timeout(5000)]
    public async Task TaskDelay_Patched_CompletesWithinExpectedWindow()
    {
        RequirePatching();

        var sw = Stopwatch.StartNew();
        await Task.Delay(500);
        sw.Stop();

        Assert.AreEqual(500, sw.ElapsedMilliseconds, 200);
    }

    [Test]
    [Timeout(5000)]
    public async Task TaskDelay_Patched_WithCancellableToken_FallsThroughToOriginal()
    {
        RequirePatching();

        // Patch condition: !cancellationToken.CanBeCanceled — a real CTS token bypasses the patch.
        using var cts = new CancellationTokenSource(5000);
        var sw = Stopwatch.StartNew();
        await Task.Delay(500, cts.Token);
        sw.Stop();

        Assert.AreEqual(500, sw.ElapsedMilliseconds, 200);
    }

    [Test]
    [Timeout(5000)]
    public void TaskDelay_Patched_CancelsEarlyWhenTokenCancelled()
    {
        RequirePatching();

        using var cts = new CancellationTokenSource(200);
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await Task.Delay(5000, cts.Token));
        sw.Stop();

        Assert.Less(sw.ElapsedMilliseconds, 1000, "Cancellation should have fired well under 1s.");
    }

    [Test]
    [Timeout(5000)]
    public async Task CancellationTokenSource_Patched_Int_CancelsAfterDelay()
    {
        RequirePatching();

        using var cts = new CancellationTokenSource(500);
        Assert.IsFalse(cts.IsCancellationRequested);

        await Task.Delay(700);

        Assert.IsTrue(cts.IsCancellationRequested);
    }

    [Test]
    [Timeout(5000)]
    public async Task CancellationTokenSource_Patched_TimeSpan_CancelsAfterDelay()
    {
        RequirePatching();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(cts.IsCancellationRequested);

        await Task.Delay(700);

        Assert.IsTrue(cts.IsCancellationRequested);
    }

    [Test]
    [Timeout(5000)]
    public async Task CancellationTokenSource_Patched_DoesNotCancelBeforeDelay()
    {
        RequirePatching();

        using var cts = new CancellationTokenSource(1000);
        await Task.Delay(300);

        Assert.IsFalse(cts.IsCancellationRequested);
    }

    [Test]
    [Timeout(5000)]
    public async Task CancellationTokenSource_Patched_MultipleTokensCancelInOrder()
    {
        RequirePatching();

        using var cts1 = new CancellationTokenSource(1000);
        using var cts2 = new CancellationTokenSource(500);

        await Task.Delay(600);
        Assert.IsFalse(cts1.IsCancellationRequested, "cts1 (1000ms) should not have fired yet");
        Assert.IsTrue(cts2.IsCancellationRequested, "cts2 (500ms) should have fired");

        await Task.Delay(600);
        Assert.IsTrue(cts1.IsCancellationRequested, "cts1 (1000ms) should now have fired");
    }

    [Test]
    [Timeout(5000)]
    public async Task CancellationTokenSource_Patched_LinkedTokenCancelsWhenShortestExpires()
    {
        RequirePatching();

        using var cts1 = new CancellationTokenSource(1000);
        using var cts2 = new CancellationTokenSource(500);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts1.Token, cts2.Token);

        await Task.Delay(700);

        Assert.IsTrue(linked.IsCancellationRequested, "Linked CTS should be cancelled when cts2 fires");
        Assert.IsFalse(cts1.IsCancellationRequested, "cts1 (1000ms) should not yet have fired");
    }
}
