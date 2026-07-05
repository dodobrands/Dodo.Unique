using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dodo.Unique.Tests;

/// <summary>
/// Steady-state hit-path throughput: the cost every canonicalizing read pays. Raw
/// ConcurrentDictionary lookup over the same keys is printed alongside as the floor the
/// pool's state read + tier probe adds overhead to. Hit paths are mode-independent (hot
/// tier only), so one pool suffices.
/// </summary>
[Category("RotationStress")]
[Category("PerfProof")]
public sealed class UniqueStringPoolHitPerfTests
{
    private const int KeyCount = 100_000;
    private const int Lookups = 2_000_000;
    private const int Trials = 5;
    private static readonly TimeSpan NoRotation = TimeSpan.FromHours(1);

    [Test]
    [NotInParallel]
    public async Task Hot_hit_single_thread()
    {
        var keys = MakeKeys();
        var pool = new UniqueStringPool(NoRotation, maxLength: 256);
        foreach (var key in keys)
            pool.Make(key.AsSpan());
        var raw = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
            raw[key] = key;

        for (var i = 0; i < 200_000; i++)
            pool.Make(keys[i % KeyCount].AsSpan());

        var spanHit = new List<double>(Trials);
        var stringHit = new List<double>(Trials);
        var rawHit = new List<double>(Trials);
        for (var t = 0; t < Trials; t++)
        {
            spanHit.Add(Measure(() =>
            {
                for (var i = 0; i < Lookups; i++)
                    pool.Make(keys[i % KeyCount].AsSpan());
            }));
            stringHit.Add(Measure(() =>
            {
                for (var i = 0; i < Lookups; i++)
                    pool.Make(keys[i % KeyCount]);
            }));
            rawHit.Add(Measure(() =>
            {
                for (var i = 0; i < Lookups; i++)
                    raw.TryGetValue(keys[i % KeyCount], out _);
            }));
        }

        var span = Median(spanHit);
        var str = Median(stringHit);
        var baseline = Median(rawHit);
        Console.WriteLine(
            $"[hit-bench:1T] span hit = {span:F1} ns/op, string hit = {str:F1} ns/op, " +
            $"raw CD lookup = {baseline:F1} ns/op, pool overhead = {span - baseline:F1} ns");

        await Assert.That(span).IsGreaterThan(0);
    }

    [Test]
    [NotInParallel]
    public async Task Hot_hit_eight_threads()
    {
        var keys = MakeKeys();
        var pool = new UniqueStringPool(NoRotation, maxLength: 256);
        foreach (var key in keys)
            pool.Make(key.AsSpan());

        var results = new List<double>(Trials);
        for (var t = 0; t < Trials; t++)
        {
            const int threadCount = 8;
            const int perThread = Lookups / threadCount;
            using var gate = new Barrier(threadCount + 1);
            var threads = new Thread[threadCount];
            for (var w = 0; w < threadCount; w++)
            {
                var offset = w * 31;
                threads[w] = new Thread(() =>
                {
                    gate.SignalAndWait();
                    for (var i = 0; i < perThread; i++)
                        pool.Make(keys[(i + offset) % KeyCount].AsSpan());
                }) { IsBackground = true };
                threads[w].Start();
            }
            gate.SignalAndWait();
            var sw = Stopwatch.StartNew();
            foreach (var thread in threads)
                thread.Join();
            sw.Stop();
            results.Add(sw.Elapsed.TotalNanoseconds / Lookups);
        }

        Console.WriteLine($"[hit-bench:8T] span hit = {Median(results):F1} ns/op (aggregate)");

        await Assert.That(Median(results)).IsGreaterThan(0);
    }

    private static string[] MakeKeys()
    {
        var keys = new string[KeyCount];
        for (var i = 0; i < KeyCount; i++)
            keys[i] = $"hit-{i:D6}";
        return keys;
    }

    private static double Measure(Action body)
    {
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        return sw.Elapsed.TotalNanoseconds / Lookups;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
