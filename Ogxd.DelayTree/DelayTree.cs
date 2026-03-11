using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Ogxd.DelayTree.Completions;
using Ogxd.DelayTree.Timers;

namespace Ogxd.DelayTree;

public class DelayTree<T1, T2> : IDelayTree, IDisposable where T1 : class, ICompletion<T2>, new()
{
    private readonly int _bitDepth;
    private readonly DelayTreeNode _root = new();
    private readonly Stopwatch _stopwatch;
    private readonly IDelayTreeTimer _timer;
    private readonly T1 _completed;
    private readonly Stack<StackNode> _pooledStack = new();
    private readonly Stack<(DelayTreeNode Node, int Depth, uint Current)> _pooledPeekStack = new();
    private readonly Stack<T1> _pooledCompletions = new();
    private readonly ReaderWriterLockSlim _lock = new();
    // _maxDelay is the timestamp space size: 2^bitDepth (or uint.MaxValue for bitDepth=32)
    private readonly uint _maxDelay;
    // _bitMask has all bitDepth lower bits set, used as initial Current in trie traversal
    private readonly uint _bitMask;
    private long _disposed = 0;
    private uint _lastTimestamp = 0;
    private ulong _count;
    // Plain uint; accessed via Volatile.Read/Write or Interlocked for thread safety
    private uint _nextDelayTimestampMs = uint.MaxValue;

    public DelayTree()
        : this(32)
    {
    }

    public DelayTree(int bitDepth)
        : this(bitDepth, new DelayTreeHybridTimer())
    {
    }

    public DelayTree(int bitDepth, IDelayTreeTimer timer)
    {
        _bitDepth = bitDepth;
        _maxDelay = bitDepth < 32 ? (1u << bitDepth) : uint.MaxValue;
        _bitMask = bitDepth < 32 ? (1u << bitDepth) - 1u : uint.MaxValue;

        _completed = new T1();
        _completed.SetCompleted(false);
        _stopwatch = Stopwatch.StartNew();

        _timer = timer;
        _timer.SetDelayTree(this);
    }

    public ulong Count => Interlocked.Read(ref _count);
    public uint CurrentTimestampMs => _bitDepth == 32
        ? unchecked((uint)_stopwatch.ElapsedMilliseconds) // natural uint wrap at 2^32
        : (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
    public uint NextDelayTimestampMs => Volatile.Read(ref _nextDelayTimestampMs);

    public T2 Delay(uint delay)
    {
        if (delay >= _maxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay is too large for the bit depth.");
        }

        if (delay == 0)
        {
            return _completed.CompletionHandle;
        }

        uint timestamp = CurrentTimestampMs;
        uint timestampDelay = (timestamp + delay) % _maxDelay;

        bool isEarlierDeadline = false;
        T2 handle;

        _lock.EnterReadLock();
        try
        {
            DelayTreeNode node = _root;
            for (int i = _bitDepth - 1; i >= 0; i--)
            {
                if ((timestampDelay & (1u << i)) == 0)
                {
                    if (Volatile.Read(ref node._zero) == null)
                    {
                        Interlocked.CompareExchange(ref node._zero, new DelayTreeNode(), null);
                    }

                    node = node._zero!;
                }
                else
                {
                    if (Volatile.Read(ref node._one) == null)
                    {
                        Interlocked.CompareExchange(ref node._one, new DelayTreeNode(), null);
                    }

                    node = node._one!;
                }
            }

            handle = node.GetCompletionHandle(ref _count);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Update the minimum deadline with a CAS loop — lock-free and race-free.
        // Done outside the lock so we don't hold ReadLock while potentially notifying the timer
        // (which would cause its WriteLock attempt to block all subsequent ReadLock acquisitions).
        uint prev = Volatile.Read(ref _nextDelayTimestampMs);
        while (timestampDelay < prev)
        {
            uint observed = Interlocked.CompareExchange(ref _nextDelayTimestampMs, timestampDelay, prev);
            if (observed == prev)
            {
                isEarlierDeadline = true;
                break;
            }
            prev = observed;
        }

        if (isEarlierDeadline)
        {
            _timer.NotifyEarlierDelay();
        }

        return handle;
    }

    public void Collect(uint timestamp)
    {
        // Fast path: nothing in the tree
        if (Interlocked.Read(ref _count) == 0)
        {
            return;
        }

        // Fast path: next due delay is still in the future
        if (timestamp < Volatile.Read(ref _nextDelayTimestampMs))
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            // Find next deadline strictly after current timestamp.
            // If none found, items may have post-wrap timestamps (≤ current timestamp);
            // find the global minimum so the timer wakes up correctly after the wrap.
            if (!TryPeekNextDelay(timestamp, out uint nextDelayTimestamp) && !TryPeekMinDelay(out nextDelayTimestamp))
            {
                nextDelayTimestamp = uint.MaxValue;
            }

            Volatile.Write(ref _nextDelayTimestampMs, nextDelayTimestamp);

            if (timestamp < _lastTimestamp)
            {
                // Timestamp wrapped around: collect [_lastTimestamp, _bitMask] then [0, timestamp]
                CollectIterative(_bitMask, uint.MinValue, _lastTimestamp, _bitMask);
                CollectIterative(_bitMask, uint.MinValue, uint.MinValue, timestamp);
            }
            else if (timestamp > _lastTimestamp)
            {
                CollectIterative(_bitMask, uint.MinValue, _lastTimestamp, timestamp);
            }

            _lastTimestamp = timestamp;
        }
        finally
        {
            _lock.ExitWriteLock();
            // Fire completions outside the lock: a completion may call back into Delay
            while (_pooledCompletions.TryPop(out T1? tcs))
            {
                tcs.SetCompleted(true);
            }
        }
    }

    // ClearParent/ClearIsOne: the parent node and which child pointer to null out when this leaf is collected.
    // When a node has two branches, each branch records the node itself as its own ClearParent.
    // When a node has only one branch, the branch inherits the ancestor's ClearParent to enable chain pruning.
    private record struct StackNode(DelayTreeNode Node, int Depth, uint Current, uint CurrentMin, uint CurrentMax, DelayTreeNode? ClearParent, bool ClearIsOne);

    private void CollectIterative(uint currentMin, uint currentMax, uint min, uint max)
    {
        _pooledStack.Push(new StackNode(_root, _bitDepth, 0, currentMin, currentMax, null, false));

        try
        {
            while (_pooledStack.TryPop(out StackNode stackNode))
            {
                if (stackNode.Depth == 0)
                {
                    _pooledCompletions.Push(stackNode.Node.TaskCompletionSource!);
                    if (stackNode.ClearParent != null)
                    {
                        if (stackNode.ClearIsOne)
                        {
                            stackNode.ClearParent._one = null;
                        }
                        else
                        {
                            stackNode.ClearParent._zero = null;
                        }
                    }
                    Interlocked.Decrement(ref _count);
                    continue;
                }

                bool hasTwoBranches = stackNode.Node == _root || stackNode.Node is { _zero: not null, _one: not null };
                uint depthBit = 1u << (stackNode.Depth - 1);
                uint newCurrentMin = stackNode.CurrentMin & ~depthBit;
                uint newCurrentMax = stackNode.CurrentMax | depthBit;

                if (stackNode.Node._zero != null && newCurrentMin >= min)
                {
                    _pooledStack.Push(new StackNode(
                        stackNode.Node._zero,
                        stackNode.Depth - 1,
                        stackNode.Current & ~depthBit,
                        newCurrentMin,
                        stackNode.CurrentMax,
                        hasTwoBranches ? stackNode.Node : stackNode.ClearParent,
                        !hasTwoBranches && stackNode.ClearIsOne));
                }

                if (stackNode.Node._one != null && newCurrentMax <= max)
                {
                    _pooledStack.Push(new StackNode(
                        stackNode.Node._one,
                        stackNode.Depth - 1,
                        stackNode.Current | depthBit,
                        stackNode.CurrentMin,
                        newCurrentMax,
                        hasTwoBranches ? stackNode.Node : stackNode.ClearParent,
                        hasTwoBranches || stackNode.ClearIsOne));
                }
            }
        }
        finally
        {
            _pooledStack.Clear(); // Exception safety: leave stack clean for next call
        }
    }

    private bool TryPeekMinDelay(out uint nextDelayTimestamp)
    {
        // DFS with zero-first: finds the absolute minimum timestamp in the trie (no lower bound)
        _pooledPeekStack.Push((_root, _bitDepth, _bitMask));

        try
        {
            while (_pooledPeekStack.TryPop(out var stackNode))
            {
                if (stackNode.Depth == 0)
                {
                    nextDelayTimestamp = stackNode.Current;
                    return true;
                }

                uint depthBit = 1u << (stackNode.Depth - 1);
                uint currentZero = stackNode.Current & ~depthBit;

                // Push one first so zero is popped first (smaller values explored first)
                if (stackNode.Node._one != null)
                {
                    _pooledPeekStack.Push((stackNode.Node._one, stackNode.Depth - 1, stackNode.Current));
                }

                if (stackNode.Node._zero != null)
                {
                    _pooledPeekStack.Push((stackNode.Node._zero, stackNode.Depth - 1, currentZero));
                }
            }
        }
        finally
        {
            _pooledPeekStack.Clear();
        }

        nextDelayTimestamp = uint.MaxValue;
        return false;
    }

    private bool TryPeekNextDelay(uint min, out uint nextDelayTimestamp)
    {
        // DFS with LIFO: zero branch pushed last → popped first → explores smaller values first
        _pooledPeekStack.Push((_root, _bitDepth, _bitMask));

        try
        {
            while (_pooledPeekStack.TryPop(out var stackNode))
            {
                if (stackNode.Depth == 0)
                {
                    if (stackNode.Current > min)
                    {
                        nextDelayTimestamp = stackNode.Current;
                        return true;
                    }
                    continue;
                }

                uint depthBit = 1u << (stackNode.Depth - 1);
                uint currentZero = stackNode.Current & ~depthBit;

                // Push one first so zero is popped first (smaller values explored first)
                if (stackNode.Node._one != null)
                {
                    _pooledPeekStack.Push((stackNode.Node._one, stackNode.Depth - 1, stackNode.Current));
                }

                if (stackNode.Node._zero != null && currentZero >= min)
                {
                    _pooledPeekStack.Push((stackNode.Node._zero, stackNode.Depth - 1, currentZero));
                }
            }
        }
        finally
        {
            _pooledPeekStack.Clear(); // Exception safety + early-return cleanup
        }

        nextDelayTimestamp = 0;
        return false;
    }

    public class DelayTreeNode
    {
        public DelayTreeNode? _zero;
        public DelayTreeNode? _one;
        private T1? _taskCompletionSource;
        public T1? TaskCompletionSource => _taskCompletionSource;

        public T2 GetCompletionHandle(ref ulong count)
        {
            if (_taskCompletionSource == null)
            {
                if (Interlocked.CompareExchange(ref _taskCompletionSource, new T1(), null) == null)
                {
                    Interlocked.Increment(ref count);
                }
            }
            return _taskCompletionSource!.CompletionHandle;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            _lock.Dispose();
        }
    }

    public void PrintTree()
    {
        void PrintNode(DelayTreeNode? node, int depth, uint current)
        {
            if (depth == 0)
            {
                Console.WriteLine($"- {current:B16} ({current} ms)");
                return;
            }

            if (node!._zero != null)
            {
                PrintNode(node._zero, depth - 1, current);
            }

            if (node._one != null)
            {
                PrintNode(node._one, depth - 1, current | 1u << (depth - 1));
            }
        }

        PrintNode(_root, _bitDepth, 0);
    }
}
