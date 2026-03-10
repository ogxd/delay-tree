using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ogxd.DelayTree;

public class DelayTree2<T>
{
    private readonly int _bitDepth;
    private readonly DelayTreeNode _root = new();
    private readonly Stopwatch _stopwatch;
    private readonly Stack<StackNode> _pooledStack = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly uint _maxDelay;
    private uint _lastTimestamp = 0;
    private ulong _count;

    private uint _nextDelayTimestampMs = uint.MaxValue;

    public DelayTree2()
        : this(32)
    {
    }
    
    public DelayTree2(int bitDepth)
    {
        _bitDepth = bitDepth;
        _maxDelay = uint.MaxValue >> (32 - bitDepth);
        
        // A reusable completion that is already completed
        _stopwatch = Stopwatch.StartNew();
    }
    
    public ulong Count => Interlocked.Read(ref _count);
    public uint CurrentTimestampMs => (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
    public uint NextDelayTimestampMs => _nextDelayTimestampMs;
    
    public void Add(T item, uint delay)
    {
        if (delay >= _maxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay is too large for the bit depth.");
        }
        
        Interlocked.Increment(ref _count);
        
        uint timestamp = CurrentTimestampMs;
        uint timestampDelay = timestamp + delay;
        timestampDelay %= _maxDelay;

        _lock.EnterReadLock(); // Can happen concurrently thanks to Interlocked semantics
        try
        {
            // Here or after modulo?
            if (timestampDelay < _nextDelayTimestampMs)
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, timestampDelay);
            }

            DelayTreeNode? node = _root;
            for (int i = _bitDepth - 1; i >= 0; i--)
            {
                long bit = timestampDelay & (1 << i);
                if (bit == 0)
                {
                    if (node._zero == null)
                    {
                        Interlocked.CompareExchange(ref node._zero, new DelayTreeNode(), null);
                    }

                    node = node._zero;
                }
                else
                {
                    if (node._one == null)
                    {
                        Interlocked.CompareExchange(ref node._one, new DelayTreeNode(), null);
                    }

                    node = node._one;
                }
            }

            node._items ??= new List<T>();
            node._items.Add(item);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IEnumerable<T> Collect()
    {
        // Fast path - no delays to collect because the tree is empty
        if (Interlocked.Read(ref _count) == 0)
        {
            return Array.Empty<T>();
        }

        uint timestamp = CurrentTimestampMs;

        Stack<T> completions = new();
        _lock.EnterWriteLock();
        try
        {
            if (TryPeekNextDelay(timestamp, out uint nextDelayTimestamp))
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, nextDelayTimestamp);
            }
            else
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, 0);
            }

            if (timestamp < _lastTimestamp)
            {
                CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, _maxDelay);
                CollectIterative(ref completions, _maxDelay, uint.MinValue, uint.MinValue, timestamp);
            }
            else if (timestamp > _lastTimestamp)
            {
                CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, timestamp);
            }

            _lastTimestamp = timestamp;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return completions;
    }

    private record struct StackNode(DelayTreeNode Node, int Depth, uint Current, uint CurrentMin, uint CurrentMax, Action? ClearRef);

    private void CollectIterative(ref Stack<T> completions, uint currentMin, uint currentMax, uint min, uint max)
    {
        // Push the initial state to the stack
        _pooledStack.Push(new StackNode(_root, _bitDepth, 0, currentMin, currentMax, null));

        while (_pooledStack.TryPop(out StackNode stackNode))
        {
            // Terminal case - reached a leaf node
            if (stackNode.Depth == 0)
            {
                foreach (var item in stackNode.Node._items!)
                {
                    completions.Push(item);
                    Interlocked.Decrement(ref _count);
                }
                stackNode.ClearRef?.Invoke();
                continue;
            }

            bool hasTwoBranches = stackNode.Node == _root || stackNode.Node is { _zero: not null, _one: not null };
            uint depthBit = 1u << (stackNode.Depth - 1);
            uint newCurrentMin = stackNode.CurrentMin & ~depthBit;
            uint newCurrentMax = stackNode.CurrentMax | depthBit;

            // Check zero branch
            if (stackNode.Node._zero != null && newCurrentMin >= min)
            {
                _pooledStack.Push(new StackNode(stackNode.Node._zero, stackNode.Depth - 1, stackNode.Current & ~depthBit, newCurrentMin, stackNode.CurrentMax, hasTwoBranches ? stackNode.Node.ClearZero : stackNode.ClearRef));
            }

            // Check one branch
            if (stackNode.Node._one != null && newCurrentMax <= max)
            {
                _pooledStack.Push(new StackNode(stackNode.Node._one, stackNode.Depth - 1, stackNode.Current | depthBit, stackNode.CurrentMin, newCurrentMax, hasTwoBranches ? stackNode.Node.ClearOne : stackNode.ClearRef));
            }
        }
    }

    private bool TryPeekNextDelay(uint min, out uint nextDelayTimestamp)
    {
        // LIFO needed for DFS, so we stop on the minimum
        var stack = new Stack<(DelayTreeNode, int, uint)>();
        stack.Push((_root, _bitDepth, _maxDelay));

        while (stack.TryPop(out (DelayTreeNode Node, int Depth, uint Current) stackNode))
        {
            // Terminal case - reached a leaf node
            if (stackNode.Depth == 0 && stackNode.Current > min)
            {
                nextDelayTimestamp = stackNode.Current;
                return true; // stop
            }

            uint depthBit = 1u << (stackNode.Depth - 1);
            uint currentZero = stackNode.Current & ~depthBit;

            // Check one branch first, because we want to check zero last for the DFS min
            if (stackNode.Node._one != null)
            {
                stack.Push((stackNode.Node._one, stackNode.Depth - 1, stackNode.Current));
            }

            // Check zero branch
            if (stackNode.Node._zero != null && currentZero >= min)
            {
                stack.Push((stackNode.Node._zero, stackNode.Depth - 1, currentZero));
            }
        }

        nextDelayTimestamp = 0;
        return false;
    }

    public class DelayTreeNode
    {
        public DelayTreeNode? _zero;
        public DelayTreeNode? _one;
        
        public List<T>? _items;

        public void ClearZero()
        {
            _zero = null;
        }

        public void ClearOne()
        {
            _one = null;
        }
    }
}
