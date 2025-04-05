# Delay Tree

The delay tree is an innovative binary tree-based datastructure to efficiently handle delays and timeouts.

## Motivation

Delays (via `Task.Delay`) and timeouts (via `CancellationTokenSource`) are very common in dotnet. Both rely on the same underlying mechanism: callbacks from a timer running on the thread pool. While theses are very pratical to use from the surface, it comes with some hidden complexity:

https://github.com/dotnet/runtime/issues/9114

[more to come, this is a work in progress]