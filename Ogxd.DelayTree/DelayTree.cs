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
    private volatile uint _nextDelayTimestampMs = uint.MaxValue;

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
    public uint CurrentTimestampMs => (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
    public uint NextDelayTimestampMs => _nextDelayTimestampMs;

    public T2 Delay(uint delay)
    {
        if (delay >= _maxDelay)
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay is too large for the bit depth.");

        if (delay == 0)
            return _completed.CompletionHandle;

        uint timestamp = CurrentTimestampMs;
        uint timestampDelay = (timestamp + delay) % _maxDelay;

        bool isEarlierDeadline = false;
        T2 handle;

        _lock.EnterReadLock();
        try
        {
            if (timestampDelay < _nextDelayTimestampMs)
            {
                _nextDelayTimestampMs = timestampDelay;
                isEarlierDeadline = true;
            }

            DelayTreeNode node = _root;
            for (int i = _bitDepth - 1; i >= 0; i--)
            {
                if ((timestampDelay & (1u << i)) == 0)
                {
                    if (node._zero == null)
                        Interlocked.CompareExchange(ref node._zero, new DelayTreeNode(), null);
                    node = node._zero!;
                }
                else
                {
                    if (node._one == null)
                        Interlocked.CompareExchange(ref node._one, new DelayTreeNode(), null);
                    node = node._one!;
                }
            }

            handle = node.GetCompletionHandle(ref _count);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Notify outside the lock: waking the timer while holding ReadLock would cause
        // the timer's WriteLock attempt to block all subsequent ReadLock acquisitions.
        if (isEarlierDeadline)
            _timer.NotifyEarlierDelay();

        return handle;
    }

    public void Collect(uint timestamp)
    {
        // Fast path: nothing in the tree
        if (Interlocked.Read(ref _count) == 0)
            return;

        // Fast path: next due delay is still in the future
        if (timestamp < _nextDelayTimestampMs)
            return;

        _lock.EnterWriteLock();
        try
        {
            _nextDelayTimestampMs = TryPeekNextDelay(timestamp, out uint nextDelayTimestamp)
                ? nextDelayTimestamp
                : uint.MaxValue;

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
                tcs.SetCompleted(true);
        }
    }

    private record struct StackNode(DelayTreeNode Node, int Depth, uint Current, uint CurrentMin, uint CurrentMax, Action? ClearRef);

    private void CollectIterative(uint currentMin, uint currentMax, uint min, uint max)
    {
        _pooledStack.Push(new StackNode(_root, _bitDepth, 0, currentMin, currentMax, null));

        try
        {
            while (_pooledStack.TryPop(out StackNode stackNode))
            {
                if (stackNode.Depth == 0)
                {
                    _pooledCompletions.Push(stackNode.Node.TaskCompletionSource!);
                    stackNode.ClearRef?.Invoke();
                    Interlocked.Decrement(ref _count);
                    continue;
                }

                bool hasTwoBranches = stackNode.Node == _root || stackNode.Node is { _zero: not null, _one: not null };
                uint depthBit = 1u << (stackNode.Depth - 1);
                uint newCurrentMin = stackNode.CurrentMin & ~depthBit;
                uint newCurrentMax = stackNode.CurrentMax | depthBit;

                if (stackNode.Node._zero != null && newCurrentMin >= min)
                    _pooledStack.Push(new StackNode(
                        stackNode.Node._zero,
                        stackNode.Depth - 1,
                        stackNode.Current & ~depthBit,
                        newCurrentMin,
                        stackNode.CurrentMax,
                        hasTwoBranches ? stackNode.Node._clearZeroAction : stackNode.ClearRef));

                if (stackNode.Node._one != null && newCurrentMax <= max)
                    _pooledStack.Push(new StackNode(
                        stackNode.Node._one,
                        stackNode.Depth - 1,
                        stackNode.Current | depthBit,
                        stackNode.CurrentMin,
                        newCurrentMax,
                        hasTwoBranches ? stackNode.Node._clearOneAction : stackNode.ClearRef));
            }
        }
        finally
        {
            _pooledStack.Clear(); // Exception safety: leave stack clean for next call
        }
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
                    _pooledPeekStack.Push((stackNode.Node._one, stackNode.Depth - 1, stackNode.Current));

                if (stackNode.Node._zero != null && currentZero >= min)
                    _pooledPeekStack.Push((stackNode.Node._zero, stackNode.Depth - 1, currentZero));
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

        // Cached to avoid per-traversal delegate allocation
        internal readonly Action _clearZeroAction;
        internal readonly Action _clearOneAction;

        public DelayTreeNode()
        {
            _clearZeroAction = ClearZero;
            _clearOneAction = ClearOne;
        }

        public T2 GetCompletionHandle(ref ulong count)
        {
            if (_taskCompletionSource == null)
            {
                if (Interlocked.CompareExchange(ref _taskCompletionSource, new T1(), null) == null)
                    Interlocked.Increment(ref count);
            }
            return _taskCompletionSource!.CompletionHandle;
        }

        public void ClearZero() => _zero = null;
        public void ClearOne() => _one = null;
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
                PrintNode(node._zero, depth - 1, current);
            if (node._one != null)
                PrintNode(node._one, depth - 1, current | 1u << (depth - 1));
        }

        PrintNode(_root, _bitDepth, 0);
    }
}
