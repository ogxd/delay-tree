# Delay Tree

A binary trie data structure for efficiently scheduling large numbers of concurrent delays and timeouts in .NET: far faster, lighter, and less CPU-hungry than `Task.Delay` or a hashed wheel timer at scale.

## Why

`Task.Delay` and `CancellationTokenSource` are convenient, but each call allocates a dedicated timer callback on the thread pool. At high concurrency this causes:

- **Thread pool flooding**: 1 million concurrent delays → 1 million work items queued
- **Heavy allocations**: each delay allocates its own `Task`, callback, and timer state
- **Lock contention**: the runtime's internal timer queue becomes a bottleneck

See the underlying runtime issue: https://github.com/dotnet/runtime/issues/9114

DelayTree solves this by sharing a single background timer across all pending delays and using a binary trie to track expiry times efficiently. Multiple delays expiring at the same millisecond share a single trie node for zero additional cost per extra waiter.

## Performance

Benchmark: schedule N concurrent delays (random ±20% jitter around target), wait for all to complete.
Machine: Apple M1 Pro, .NET 9. Measured with BenchmarkDotNet (memory + CPU + threading diagnostics).

### 10 000 concurrent delays, 10 ms target

| Method             | Wall time   | CPU time  | Allocated | Work items | Lock contentions |
|--------------------|-------------|-----------|-----------|------------|------------------|
| `Task.Delay`       | 14.36 ms    | 7.39 ms   | 1 640 KB  | 10 000     | 15               |
| HashedWheelTimer   | 21.48 ms    | 7.33 ms   | 3 985 KB  | 10 443     | —                |
| **DelayTree**      | **11.82 ms**| **0.38 ms**| **1.25 KB**| **2**    | **0.2**          |

→ **19× less CPU**, **1 300× less memory**, wall time slightly faster.

### 1 000 000 concurrent delays, 10 ms target

| Method             | Wall time    | CPU time    | Allocated   | Work items | Lock contentions |
|--------------------|--------------|-------------|-------------|------------|------------------|
| `Task.Delay`       | 853 ms       | 1 749 ms    | 164 063 KB  | 1 000 000  | 5 998            |
| HashedWheelTimer   | 710 ms       | 975 ms      | 398 445 KB  | 1 000 633  | —                |
| **DelayTree**      | **71 ms**    | **123 ms**  | **12 KB**   | **8**      | **0**            |

→ **12× faster wall time**, **14× less CPU**, **13 000× less memory** than `Task.Delay`.

### 1 000 000 concurrent delays, 1 000 ms target

| Method             | Wall time     | CPU time    | Allocated   | Work items | Lock contentions |
|--------------------|---------------|-------------|-------------|------------|------------------|
| `Task.Delay`       | 1 932 ms      | 2 050 ms    | 164 071 KB  | 1 000 014  | 800              |
| HashedWheelTimer   | 1 993 ms      | 1 158 ms    | 398 446 KB  | 1 000 007  | —                |
| **DelayTree**      | **1 276 ms**  | **48 ms**   | **92 KB**   | **54**     | **0**            |

→ **34% faster wall time**, **43× less CPU**, **1 800× less memory** than `Task.Delay`.

The advantage grows with concurrency. At low concurrency or for sequential one-shot delays, `Task.Delay` may still be faster.

## Usage

```csharp
// Create once, keep alive for the lifetime of your workload
var delayTree = new DelayTree<TaskCompletion, Task>(bitDepth: 16);

// Await a delay (ms)
await delayTree.Delay(500);

// Clean up when done
delayTree.Dispose();
```

`bitDepth: 16` supports delays up to 65 535 ms. Use `bitDepth: 32` for up to ~49 days.

### Pluggable completions

The generic parameters let you choose what `Delay()` returns:

| `T1`                    | `T2`                | Returns                           |
|-------------------------|---------------------|-----------------------------------|
| `TaskCompletion`        | `Task`              | Awaitable `Task`                  |
| `CancellationCompletion`| `CancellationToken` | A token cancelled at the deadline |

### Drop-in `Task.Delay` / `CancellationTokenSource` replacement

`BclPatch` uses Harmony to intercept `Task.Delay` and `CancellationTokenSource` constructors globally so existing code benefits without any changes:

```csharp
BclPatch.Apply(new DelayTree<TaskCompletion, Task>(16));
```

## How it works

### The binary trie

Each pending delay is stored in a binary trie (radix tree) keyed by its **expiry timestamp** (current time + requested delay, in milliseconds, modulo the timestamp space).

The timestamp is a `uint` of `bitDepth` bits. Each bit selects a branch at each trie level: `0` goes left, `1` goes right, so the path from root to a leaf spells out the binary representation of the expiry time:

```
Expiry = 0b1011 (= 11 ms)

root
 └─1─ node
       └─0─ node
             └─1─ node
                   └─1─ leaf  ← completion fires at t=11
```

**Key property:** the left subtree of any node always contains smaller timestamps than the right subtree. The trie is an implicit sorted structure, so no sorting pass is needed at collection time.

**Key insight:** multiple callers requesting a delay that resolves to the same millisecond share the exact same leaf node. One `TaskCompletionSource` satisfies all of them simultaneously, which is why allocated memory stays near-zero even at 1 million concurrent delays.

### Insertion (`Delay`)

Walk the trie bit by bit from MSB to LSB, creating child nodes on demand with `Interlocked.CompareExchange` (lock-free). Leaf nodes hold a `TaskCompletionSource` (or equivalent completion). Insertion takes a **read lock** so many callers can insert concurrently.

A lock-free CAS loop also tracks the earliest known deadline (`_nextDelayTimestampMs`) and wakes the timer early if the new deadline is sooner.

### Collection (`Collect`)

The background timer calls `Collect(now)` on each tick. Collection takes a **write lock** (exclusive), then traverses the trie depth-first, collecting every leaf whose timestamp ≤ `now`. During traversal, each subtree's possible range `[min, max]` is tracked; a subtree is skipped entirely if its entire range is still in the future. Collected leaf nodes are pruned from the trie.

Completions are fired **after** releasing the write lock, so a completion handler can safely call `Delay()` again without deadlocking.

```
now = 12, collect range [prev=9, now=12]

root
 ├─0─ subtree max=7  → skip (7 < 9, already collected)
 └─1─ subtree min=8
       ├─0─ subtree max=11  → collect all  ✓
       └─1─ subtree min=12  → collect t=12 ✓, skip t=13..
```

### The timer

`DelayTreeHybridTimer` (the default) runs a single dedicated background thread that sleeps until 1 ms before the next deadline, then busy-spins for the final millisecond. This balances precision against CPU usage and avoids the per-delay thread-pool scheduling that `Task.Delay` requires.

### Concurrency model

| Operation       | Lock           | Notes                                              |
|-----------------|----------------|----------------------------------------------------|
| `Delay` (insert)| Read lock      | Concurrent inserts safe; node creation via CAS     |
| `Collect` (fire)| Write lock     | Exclusive; completions fired after lock release    |
| `_nextDelayTimestampMs` update | None | CAS loop, fully lock-free            |

## Building and testing

```bash
# Build
dotnet build

# Run all tests (must run sequentially, chaos tests saturate the CPU)
dotnet test Ogxd.DelayTree.Tests

# Run a single test
dotnet test Ogxd.DelayTree.Tests --filter "FullyQualifiedName~Sequential_Ascending"

# Run benchmarks (Release build required)
dotnet run --project Ogxd.DelayTree.Benchmarks -c Release
```
