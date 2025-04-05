using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ogxd.DelayTree;

public class DelayTree<T1, T2> : IDisposable where T1 : class, ICompletion<T2>, new()
{
    private readonly int _bitDepth;
    private readonly DelayTreeNode _root = new();
    private readonly Stopwatch _stopwatch;
    private readonly Timer _timer;
    private readonly T1 _completed;
    private readonly Stack<StackNode> _pooledStack = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly uint _maxDelay;
    private long _disposed = 0;
    private uint _lastTimestamp = 0;
    private ulong _count;

    public DelayTree(int bitDepth = 32, int accuracy = 20)
    {
        _bitDepth = bitDepth;
        _maxDelay = uint.MaxValue >> (32 - bitDepth);

        // A reusable completion that is already completed
        _completed = new T1();
        _completed.SetCompleted(false);
        _stopwatch = Stopwatch.StartNew();
        _timer = new Timer(_ =>
        {
            Collect();
        }, null, -1, -1);
    }

    private uint GetTimestampMs()
    {
        return (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
    }

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

        _lock.EnterReadLock(); // Can happen concurrently thanks to Interlocked semantics
        try
        {
            uint timestamp = GetTimestampMs();

            uint timestampDelay = timestamp + delay;

            // Here or after modulo?
            if (timestampDelay < _timestampUntilGuaranteedNoDelay)
            {
                Interlocked.Exchange(ref _timestampUntilGuaranteedNoDelay, timestampDelay);
                _timer.Change(delay, -1);
                //Console.WriteLine($"Timestamp {timestamp} + delay {delay} = {timestampDelay} < {_timestampUntilGuaranteedNoDelay}, setting timer to {delay}");
            }

            // Timestamps are wrapped around the max delay the tree can handle
            timestampDelay %= _maxDelay;
            //Console.WriteLine("[add] Creating task expiring in: " + delay);

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

            return node.GetCompletionHandle(ref _count);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private uint _timestampUntilGuaranteedNoDelay = uint.MaxValue;

    private void Collect()
    {
        // Fast path - no delays to collect because the tree is empty
        if (Interlocked.Read(ref _count) == 0)
        {
            return;
        }
        
        // Fast path - no delays to collect because we know the next delay is in the future
        // if (timestamp < _timestampUntilGuaranteedNoDelay)
        // {
        //     // There are no delays to collect, early return
        //     Console.WriteLine($"Timestamp {timestamp} < {_timestampUntilGuaranteedNoDelay}, skipping collection");
        //     return;
        // }

        //Console.WriteLine($"Timestamp {timestamp} >= {_timestampUntilGuaranteedNoDelay}, collecting");

        Stack<T1> completions = new();
        _lock.EnterWriteLock();
        try
        {
            uint timestamp = GetTimestampMs();
            
            if (TryPeekNextDelay(timestamp, out uint nextDelayTimestamp))
            {
                //Console.WriteLine($"Next delay in {nextDelayTimestamp - timestamp} ms. Next delay timestamp: {_timestampUntilGuaranteedNoDelay} -> {nextDelayTimestamp}");
                Interlocked.Exchange(ref _timestampUntilGuaranteedNoDelay, nextDelayTimestamp);
                _timer.Change((int)(nextDelayTimestamp - timestamp), 10);
            }
            else
            {
                Interlocked.Exchange(ref _timestampUntilGuaranteedNoDelay, 0);
                //_timer.Change(-1, -1);
                //Console.WriteLine($"No next delay");
            }

            if (timestamp < _lastTimestamp)
            {
                //Console.WriteLine("[collect] Overflow");
                CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, _maxDelay);
                CollectIterative(ref completions, _maxDelay, uint.MinValue, uint.MinValue, timestamp);
            }
            else if (timestamp > _lastTimestamp)
            {
                CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, timestamp);
            }

            //Console.WriteLine($"Drill between {_lastTimestamp} and {timestamp} ({_lastTimestamp:B} and {timestamp:B})");
            _lastTimestamp = timestamp;
        }
        finally
        {
            _lock.ExitWriteLock();
            // Trigger completions out of the lock, as it might go back to the Delay method
            while (completions.TryPop(out T1? tcs))
            {
                tcs.SetCompleted(true);
            }
        }
    }

    private record struct StackNode(DelayTreeNode Node, int Depth, uint Current, uint CurrentMin, uint CurrentMax, Action? ClearRef);

    private void CollectIterative(ref Stack<T1> completions, uint currentMin, uint currentMax, uint min, uint max)
    {
        //Console.WriteLine("[collect] Collect from " + min + " to " + max);

        // Push the initial state to the stack
        _pooledStack.Push(new StackNode(_root, _bitDepth, 0, currentMin, currentMax, null));

        while (_pooledStack.TryPop(out StackNode stackNode))
        {
            // Terminal case - reached a leaf node
            if (stackNode.Depth == 0)
            {
                //Console.WriteLine("[collect] Trigger task for time: " + stackNode.Current);
                //Console.WriteLine($"[collect] Clearing reference: {stackNode.ClearRef != null}");
                completions.Push(stackNode.Node.TaskCompletionSource!);
                stackNode.ClearRef?.Invoke();
                Interlocked.Decrement(ref _count);
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

            return _taskCompletionSource.CompletionHandle;
        }

        public void ClearZero()
        {
            _zero = null;
        }

        public void ClearOne()
        {
            _one = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            //_lock.Dispose();
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

            if (node._zero != null)
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
