// using System;
// using System.Collections.Generic;
// using System.Diagnostics;
// using System.Threading;
//
// namespace Ogxd.DelayTree;
//
// public class DelayTree4<T>
// {
//     private readonly int _bitDepth;
//     private readonly NodeArena _arena;
//     private readonly Stopwatch _stopwatch;
//     private readonly Stack<StackNode> _pooledStack = new();
//     private readonly ReaderWriterLockSlim _lock = new();
//     private readonly uint _maxDelay;
//     private uint _lastTimestamp = 0;
//     private ulong _count;
//     private uint _nextDelayTimestampMs = uint.MaxValue;
//     private NodeHandle _root;
//
//     public DelayTree4(int bitDepth, int initialArenaCapacity = 1024)
//     {
//         _bitDepth = bitDepth;
//         _maxDelay = uint.MaxValue >> (32 - bitDepth);
//         _arena = new NodeArena(initialArenaCapacity);
//         _root = _arena.AllocateNode();
//         _stopwatch = Stopwatch.StartNew();
//     }
//
//     public ulong Count => Interlocked.Read(ref _count);
//     public uint CurrentTimestampMs => (uint)(_stopwatch.ElapsedMilliseconds % _maxDelay);
//     public uint NextDelayTimestampMs => _nextDelayTimestampMs;
//
//     public void Add(T item, uint delay)
//     {
//         if (delay >= _maxDelay)
//         {
//             throw new ArgumentOutOfRangeException(nameof(delay), "Delay is too large for the bit depth.");
//         }
//
//         Interlocked.Increment(ref _count);
//
//         uint timestamp = CurrentTimestampMs;
//         uint timestampDelay = timestamp + delay;
//         timestampDelay %= _maxDelay;
//
//         _lock.EnterReadLock();
//         try
//         {
//             if (timestampDelay < _nextDelayTimestampMs)
//             {
//                 Interlocked.Exchange(ref _nextDelayTimestampMs, timestampDelay);
//             }
//
//             NodeHandle currentNode = _root;
//             for (int i = _bitDepth - 1; i >= 0; i--)
//             {
//                 long bit = timestampDelay & (1 << i);
//                 if (bit == 0)
//                 {
//                     ref var node = ref _arena.GetNode(currentNode);
//                     if (!node.ZeroChild.IsValid)
//                     {
//                         var newNode = _arena.AllocateNode();
//                         // Use compare-exchange for thread safety
//                         var original = Interlocked.CompareExchange(ref node.ZeroChild.Index, newNode.Index, NodeHandle.InvalidIndex);
//                         if (original == NodeHandle.InvalidIndex)
//                         {
//                             node.ZeroChild = newNode;
//                         }
//                         else
//                         {
//                             // Another thread allocated, use theirs and free ours
//                             _arena.FreeNode(newNode);
//                             node.ZeroChild = new NodeHandle(original);
//                         }
//                     }
//                     currentNode = node.ZeroChild;
//                 }
//                 else
//                 {
//                     ref var node = ref _arena.GetNode(currentNode);
//                     if (!node.OneChild.IsValid)
//                     {
//                         var newNode = _arena.AllocateNode();
//                         var original = Interlocked.CompareExchange(ref node.OneChild.Index, newNode.Index, NodeHandle.InvalidIndex);
//                         if (original == NodeHandle.InvalidIndex)
//                         {
//                             node.OneChild = newNode;
//                         }
//                         else
//                         {
//                             _arena.FreeNode(newNode);
//                             node.OneChild = new NodeHandle(original);
//                         }
//                     }
//                     currentNode = node.OneChild;
//                 }
//             }
//
//             ref var leafNode = ref _arena.GetNode(currentNode);
//             leafNode.Items ??= new List<T>();
//             leafNode.Items.Add(item);
//         }
//         finally
//         {
//             _lock.ExitReadLock();
//         }
//     }
//
//     public IEnumerable<T> Collect()
//     {
//         if (Interlocked.Read(ref _count) == 0)
//         {
//             return Array.Empty<T>();
//         }
//
//         uint timestamp = CurrentTimestampMs;
//         Stack<T> completions = new();
//
//         _lock.EnterWriteLock();
//         try
//         {
//             if (TryPeekNextDelay(timestamp, out uint nextDelayTimestamp))
//             {
//                 Interlocked.Exchange(ref _nextDelayTimestampMs, nextDelayTimestamp);
//             }
//             else
//             {
//                 Interlocked.Exchange(ref _nextDelayTimestampMs, 0);
//             }
//
//             if (timestamp < _lastTimestamp)
//             {
//                 CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, _maxDelay);
//                 CollectIterative(ref completions, _maxDelay, uint.MinValue, uint.MinValue, timestamp);
//             }
//             else if (timestamp > _lastTimestamp)
//             {
//                 CollectIterative(ref completions, _maxDelay, uint.MinValue, _lastTimestamp, timestamp);
//             }
//
//             _lastTimestamp = timestamp;
//         }
//         finally
//         {
//             _lock.ExitWriteLock();
//         }
//
//         return completions;
//     }
//
//     private record struct StackNode(NodeHandle NodeHandle, int Depth, uint Current, uint CurrentMin, uint CurrentMax, Action? ClearRef);
//
//     private void CollectIterative(ref Stack<T> completions, uint currentMin, uint currentMax, uint min, uint max)
//     {
//         _pooledStack.Push(new StackNode(_root, _bitDepth, 0, currentMin, currentMax, null));
//
//         while (_pooledStack.TryPop(out StackNode stackNode))
//         {
//             ref var node = ref _arena.GetNode(stackNode.NodeHandle);
//             
//             if (stackNode.Depth == 0)
//             {
//                 if (node.Items != null)
//                 {
//                     foreach (var item in node.Items)
//                     {
//                         completions.Push(item);
//                         Interlocked.Decrement(ref _count);
//                     }
//                 }
//                 stackNode.ClearRef?.Invoke();
//                 continue;
//             }
//
//             bool hasTwoBranches = stackNode.NodeHandle.Equals(_root) || 
//                                 (node.ZeroChild.IsValid && node.OneChild.IsValid);
//             uint depthBit = 1u << (stackNode.Depth - 1);
//             uint newCurrentMin = stackNode.CurrentMin & ~depthBit;
//             uint newCurrentMax = stackNode.CurrentMax | depthBit;
//
//             if (node.ZeroChild.IsValid && newCurrentMin >= min)
//             {
//                 _pooledStack.Push(new StackNode(
//                     node.ZeroChild, 
//                     stackNode.Depth - 1, 
//                     stackNode.Current & ~depthBit, 
//                     newCurrentMin, 
//                     stackNode.CurrentMax, 
//                     hasTwoBranches ? CreateClearZeroAction(stackNode.NodeHandle) : stackNode.ClearRef));
//             }
//
//             if (node.OneChild.IsValid && newCurrentMax <= max)
//             {
//                 _pooledStack.Push(new StackNode(
//                     node.OneChild, 
//                     stackNode.Depth - 1, 
//                     stackNode.Current | depthBit, 
//                     stackNode.CurrentMin, 
//                     newCurrentMax, 
//                     hasTwoBranches ? CreateClearOneAction(stackNode.NodeHandle) : stackNode.ClearRef));
//             }
//         }
//     }
//
//     private Action CreateClearZeroAction(NodeHandle handle)
//     {
//         return () => {
//             ref var node = ref _arena.GetNode(handle);
//             if (node.ZeroChild.IsValid)
//             {
//                 _arena.FreeNode(node.ZeroChild);
//                 node.ZeroChild = NodeHandle.Invalid;
//             }
//         };
//     }
//
//     private Action CreateClearOneAction(NodeHandle handle)
//     {
//         return () => {
//             ref var node = ref _arena.GetNode(handle);
//             if (node.OneChild.IsValid)
//             {
//                 _arena.FreeNode(node.OneChild);
//                 node.OneChild = NodeHandle.Invalid;
//             }
//         };
//     }
//
//     private bool TryPeekNextDelay(uint min, out uint nextDelayTimestamp)
//     {
//         var stack = new Stack<(NodeHandle, int, uint)>();
//         stack.Push((_root, _bitDepth, _maxDelay));
//
//         while (stack.TryPop(out (NodeHandle NodeHandle, int Depth, uint Current) stackNode))
//         {
//             if (stackNode.Depth == 0 && stackNode.Current > min)
//             {
//                 nextDelayTimestamp = stackNode.Current;
//                 return true;
//             }
//
//             ref var node = ref _arena.GetNode(stackNode.NodeHandle);
//             uint depthBit = 1u << (stackNode.Depth - 1);
//             uint currentZero = stackNode.Current & ~depthBit;
//
//             if (node.OneChild.IsValid)
//             {
//                 stack.Push((node.OneChild, stackNode.Depth - 1, stackNode.Current));
//             }
//
//             if (node.ZeroChild.IsValid && currentZero >= min)
//             {
//                 stack.Push((node.ZeroChild, stackNode.Depth - 1, currentZero));
//             }
//         }
//
//         nextDelayTimestamp = 0;
//         return false;
//     }
// }
//
// // Handle to a node in the arena
// public struct NodeHandle : IEquatable<NodeHandle>
// {
//     public const int InvalidIndex = -1;
//     public static readonly NodeHandle Invalid = new(InvalidIndex);
//     
//     public readonly int Index;
//
//     public NodeHandle(int index)
//     {
//         Index = index;
//     }
//
//     public bool IsValid => Index != InvalidIndex;
//
//     public bool Equals(NodeHandle other) => Index == other.Index;
//     public override bool Equals(object? obj) => obj is NodeHandle other && Equals(other);
//     public override int GetHashCode() => Index;
// }
//
// // Node structure stored in the arena
// public struct DelayTreeNodeStruct<T>
// {
//     public NodeHandle ZeroChild;
//     public NodeHandle OneChild;
//     public List<T>? Items;
//
//     public DelayTreeNodeStruct()
//     {
//         ZeroChild = NodeHandle.Invalid;
//         OneChild = NodeHandle.Invalid;
//         Items = null;
//     }
// }
//
// // Arena allocator for nodes
// public class NodeArena
// {
//     private DelayTreeNodeStruct<T>[] _nodes;
//     private readonly Stack<int> _freeList = new();
//     private int _nextIndex = 0;
//     private readonly object _lock = new();
//
//     public NodeArena(int initialCapacity)
//     {
//         _nodes = new DelayTreeNodeStruct<T>[initialCapacity];
//     }
//
//     public NodeHandle AllocateNode()
//     {
//         lock (_lock)
//         {
//             int index;
//             if (_freeList.TryPop(out index))
//             {
//                 _nodes[index] = new DelayTreeNodeStruct<T>();
//                 return new NodeHandle(index);
//             }
//
//             if (_nextIndex >= _nodes.Length)
//             {
//                 // Grow the arena
//                 Array.Resize(ref _nodes, _nodes.Length * 2);
//             }
//
//             index = _nextIndex++;
//             _nodes[index] = new DelayTreeNodeStruct<T>();
//             return new NodeHandle(index);
//         }
//     }
//
//     public void FreeNode(NodeHandle handle)
//     {
//         if (!handle.IsValid) return;
//         
//         lock (_lock)
//         {
//             _nodes[handle.Index] = new DelayTreeNodeStruct<T>();
//             _freeList.Push(handle.Index);
//         }
//     }
//
//     public ref DelayTreeNodeStruct<T> GetNode(NodeHandle handle)
//     {
//         if (!handle.IsValid)
//             throw new ArgumentException("Invalid node handle");
//         return ref _nodes[handle.Index];
//     }
// }