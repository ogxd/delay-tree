using System;

namespace Ogxd.DelayTree;

public interface IDelayTreeTimer : IDisposable
{
    void SetDelayTree(IDelayTree delayTree);

    /// <summary>
    /// Called when a new delay was registered with a deadline earlier than the current next deadline.
    /// Implementations that sleep (e.g. hybrid timer) should use this to wake up and re-evaluate.
    /// </summary>
    void NotifyEarlierDelay() { }
}