using NUnit.Framework;

// Chaos tests (100k concurrent tasks) saturate CPU; run all tests sequentially to avoid interference
[assembly: NonParallelizable]
