using System;

namespace Ogxd.DelayTree;

public interface IDelayTreeTimer : IDisposable
{
    void SetDelayTree(IDelayTree delayTree);
}