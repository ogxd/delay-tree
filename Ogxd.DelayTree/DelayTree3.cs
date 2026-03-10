using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ogxd.DelayTree;

public class DelayTree3<T>
{
    // The node is now a struct, holding indices into the _nodes list instead of references.
    // -1 represents a null link.
    private struct DelayTreeNode
    {
        public int Zero;
        public int One;
    }

    private readonly int _bitDepth;
    private readonly Stopwatch _stopwatch;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly uint _maxDelay;

    // --- Arena Allocation Fields ---
    private const int InitialCapacity = 1024; // Initial size of the node arena
    private const int RootIndex = 0;
    private List<DelayTreeNode> _nodes; // The arena for all nodes
    private readonly ConcurrentQueue<int> _freeNodeIndices; // Pool of recycled node indices
    private readonly ConcurrentDictionary<int, List<T>> _items; // Maps node index to its item list
    // -----------------------------

    private uint _lastTimestamp = 0;
    private long _count;
    private uint _nextDelayTimestampMs = uint.MaxValue;

    public DelayTree3(int bitDepth)
    {
        _bitDepth = bitDepth;
        _maxDelay = uint.MaxValue >> (32 - bitDepth);
        _stopwatch = Stopwatch.StartNew();

        // Initialize the arena allocator
        _nodes = new List<DelayTreeNode>(InitialCapacity);
        _freeNodeIndices = new ConcurrentQueue<int>();
        _items = new ConcurrentDictionary<int, List<T>>();
        
        // Pre-allocate the root node at index 0
        _nodes.Add(new DelayTreeNode { Zero = -1, One = -1 });
    }
    
    public long Count => Interlocked.Read(ref _count);
    public uint CurrentTimestampMs => (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
    public uint NextDelayTimestampMs => _nextDelayTimestampMs;

    /// <summary>
    /// Gets a new or recycled node index from the arena.
    /// This is the core of the allocator.
    /// </summary>
    private int AllocateNode()
    {
        // First, try to reuse a node from the pool
        if (_freeNodeIndices.TryDequeue(out int index))
        {
            return index;
        }

        // If no nodes are available for recycling, create a new one
        index = _nodes.Count;
        _nodes.Add(new DelayTreeNode { Zero = -1, One = -1 });
        return index;
    }

    public void Add(T item, uint delay)
    {
        if (delay >= _maxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay is too large for the bit depth.");
        }
        
        Interlocked.Increment(ref _count);
        
        uint timestamp = CurrentTimestampMs;
        uint timestampDelay = (timestamp + delay) % _maxDelay;

        // A WriteLock is now used to ensure thread-safety for node allocation.
        // This is a trade-off for eliminating GC pressure from node creation.
        _lock.EnterWriteLock();
        try
        {
            if (timestampDelay < _nextDelayTimestampMs)
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, timestampDelay);
            }

            int currentNodeIndex = RootIndex;
            for (int i = _bitDepth - 1; i >= 0; i--)
            {
                long bit = timestampDelay & (1 << i);
                ref DelayTreeNode node = ref CollectionsMarshal.AsSpan(_nodes)[currentNodeIndex]; // Use ref for performance

                if (bit == 0)
                {
                    if (node.Zero == -1)
                    {
                        node.Zero = AllocateNode();
                    }
                    currentNodeIndex = node.Zero;
                }
                else
                {
                    if (node.One == -1)
                    {
                        node.One = AllocateNode();
                    }
                    currentNodeIndex = node.One;
                }
            }
            
            // Get or create the list of items for the target leaf node.
            var itemList = _items.GetOrAdd(currentNodeIndex, _ => new List<T>());
            // Lock the specific list while adding to it for fine-grained thread safety.
            lock (itemList)
            {
                itemList.Add(item);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public IEnumerable<T> Collect()
    {
        if (Interlocked.Read(ref _count) == 0)
        {
            return Enumerable.Empty<T>();
        }

        uint timestamp = CurrentTimestampMs;
        var collectedItems = new List<T>();
        var nodesToClear = new List<int>();

        _lock.EnterWriteLock();
        try
        {
            // This part of the logic remains largely the same
            if (TryPeekNextDelay(timestamp, out uint nextDelayTimestamp))
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, nextDelayTimestamp);
            }
            else
            {
                Interlocked.Exchange(ref _nextDelayTimestampMs, uint.MaxValue);
            }

            if (timestamp < _lastTimestamp)
            {
                CollectRange(collectedItems, nodesToClear, _lastTimestamp + 1, _maxDelay);
                CollectRange(collectedItems, nodesToClear, 0, timestamp);
            }
            else if (timestamp > _lastTimestamp)
            {
                CollectRange(collectedItems, nodesToClear, _lastTimestamp + 1, timestamp);
            }

            _lastTimestamp = timestamp;

            // After collecting, recycle the cleared nodes
            foreach (int nodeIndex in nodesToClear)
            {
                // Reset the node's children and recycle its index
                _nodes[nodeIndex] = new DelayTreeNode { Zero = -1, One = -1 }; 
                _freeNodeIndices.Enqueue(nodeIndex);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return collectedItems;
    }
    
    // The collection logic is now in a separate method to be called for each range.
    private void CollectRange(List<T> collectedItems, List<int> nodesToClear, uint min, uint max)
    {
        if (min > max) return;
        
        var stack = new Stack<(int NodeIndex, int Depth, uint PathValue)>();
        stack.Push((RootIndex, _bitDepth, 0));

        while (stack.TryPop(out var current))
        {
            // If the current path is a leaf node
            if (current.Depth == 0)
            {
                // Check if this leaf falls within the collection range
                if (current.PathValue >= min && current.PathValue <= max)
                {
                    if (_items.TryRemove(current.NodeIndex, out var items))
                    {
                        collectedItems.AddRange(items);
                        Interlocked.Add(ref _count, -items.Count);
                        nodesToClear.Add(current.NodeIndex);
                    }
                }
                continue;
            }

            int nextDepth = current.Depth - 1;
            uint bitValue = 1u << nextDepth;
            
            ref var node = ref CollectionsMarshal.AsSpan(_nodes)[current.NodeIndex];

            // Explore the 'zero' branch if it exists
            if (node.Zero != -1)
            {
                // The maximum value reachable from this branch
                uint maxPathValue = current.PathValue + bitValue - 1;
                if (max <= maxPathValue) // The range is fully contained in this branch
                {
                     stack.Push((node.Zero, nextDepth, current.PathValue));
                }
            }

            // Explore the 'one' branch if it exists
            if (node.One != -1)
            {
                uint nextPathValue = current.PathValue | bitValue;
                // The minimum value reachable from this branch
                if (min >= nextPathValue) // The range is fully contained in this branch
                {
                    stack.Push((node.One, nextDepth, nextPathValue));
                }
            }
        }
    }
    
    // Peek logic updated to use indices
    private bool TryPeekNextDelay(uint min, out uint nextDelayTimestamp)
    {
        var stack = new Stack<(int NodeIndex, int Depth, uint PathValue)>();
        stack.Push((RootIndex, _bitDepth, 0));

        uint closestTimestamp = uint.MaxValue;

        while (stack.TryPop(out var current))
        {
            // If it's a leaf node with items, it's a potential candidate
            if (current.Depth == 0)
            {
                if (current.PathValue > min && current.PathValue < closestTimestamp && _items.ContainsKey(current.NodeIndex))
                {
                    closestTimestamp = current.PathValue;
                }
                continue;
            }

            int nextDepth = current.Depth - 1;
            uint bitValue = 1u << nextDepth;
            ref var node = ref CollectionsMarshal.AsSpan(_nodes)[current.NodeIndex];

            // DFS: Push 'one' then 'zero' so 'zero' (lower values) is processed first.
            // This ensures we find the minimum value first.
            if (node.One != -1)
            {
                stack.Push((node.One, nextDepth, current.PathValue | bitValue));
            }
            if (node.Zero != -1)
            {
                stack.Push((node.Zero, nextDepth, current.PathValue));
            }
        }

        if (closestTimestamp != uint.MaxValue)
        {
            nextDelayTimestamp = closestTimestamp;
            return true;
        }

        nextDelayTimestamp = 0;
        return false;
    }
}