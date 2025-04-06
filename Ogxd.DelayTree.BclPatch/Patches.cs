using System;
using System.Threading;
using System.Threading.Tasks;
using Ogxd.DelayTree;
using HarmonyLib;

namespace Ogxd.DelayTree;

[HarmonyPatch(typeof(Task), nameof(Task.Delay), [typeof(uint), typeof(TimeProvider), typeof(CancellationToken)])]
public class TaskDelayPatch
{
    private static readonly DelayTree<TaskCompletion, Task> _DelayTree = new(16);

    static bool Prefix(ref Task __result, uint millisecondsDelay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // If not cancellable and delay is less than 10 seconds, we can use the DelayTree, which is faster
        if (!cancellationToken.CanBeCanceled && millisecondsDelay < 65_000)
        {
            __result = _DelayTree.Delay(millisecondsDelay);
            return false; // Skip Task.Delay default implementation
        }
        return true;
    }
}

[HarmonyPatch(typeof(CancellationTokenSource), "InitializeWithTimer", [typeof(TimeSpan), typeof(TimeProvider)])]
public class CancellationTokenPatch
{
    private static readonly DelayTree<CancellationCompletion, CancellationToken> _DelayTree = new(16);

    static bool Prefix(CancellationTokenSource __instance, TimeSpan millisecondsDelay, TimeProvider timeProvider)
    {
        // If not cancellable and delay is less than 10 seconds, we can use the DelayTree, which is faster
        if (millisecondsDelay > TimeSpan.Zero && millisecondsDelay.TotalMilliseconds < 65_000)
        {
            CancellationToken token = _DelayTree.Delay((uint)millisecondsDelay.TotalMilliseconds);
            token.Register(_ =>
            {
                try
                {
                    __instance.Cancel();
                }
                catch
                {
                }
            }, null);
            return false; // Skip default ctor implementation
        }
        return true;
    }
}

public static class Patches
{
    private static long _Applied = 0;
    public static void ApplyPatches()
    {
        if (Interlocked.CompareExchange(ref _Applied, 1, 0) != 0)
        {
            return; // Already applied
        }

        var harmony = new Harmony("Ogxd.DelayTree");
        harmony.PatchAll(typeof(Patches).Assembly);
    }
}
