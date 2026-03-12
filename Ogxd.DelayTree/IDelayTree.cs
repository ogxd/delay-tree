namespace Ogxd.DelayTree;

public interface IDelayTree
{
    internal ulong Count { get; }
    
    uint CurrentTimestampMs { get; }

    uint NextDelayTimestampMs { get; }
    
    void Collect(uint timestamp);
}