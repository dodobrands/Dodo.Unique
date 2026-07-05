using System.Diagnostics;

namespace Dodo.Unique.Tests;

/// <summary>
/// Fence-cost A/B: retention far above run length means no rotation ever fires, so the
/// modes differ only by the conditional barrier after a winning GetOrAdd.
/// </summary>
[Category("RotationStress")]
[Category("PerfProof")]
public sealed class UniqueStringPoolInsertFenceTests
{
    private const int KeyCount = 1_000_000;
    private const int Trials = 5;
    private static readonly TimeSpan NoRotation = TimeSpan.FromHours(1);

    [Test]
    [NotInParallel]
    public async Task Insert_fence_cost_single_thread()
    {
        var keys = MakeKeys();
        Warmup(keys);

        var fenced = new List<double>(Trials);
        var unfenced = new List<double>(Trials);
        for (var t = 0; t < Trials; t++)
        {
            fenced.Add(MeasureSingle(keys, useFrozenGeneration: true));
            unfenced.Add(MeasureSingle(keys, useFrozenGeneration: false));
        }

        Report("1T", fenced, unfenced);
        await Assert.That(Median(unfenced)).IsGreaterThan(0);
    }

    [Test]
    [NotInParallel]
    public async Task Insert_fence_cost_eight_threads()
    {
        var keys = MakeKeys();
        Warmup(keys);

        var fenced = new List<double>(Trials);
        var unfenced = new List<double>(Trials);
        for (var t = 0; t < Trials; t++)
        {
            fenced.Add(MeasureParallel(keys, useFrozenGeneration: true));
            unfenced.Add(MeasureParallel(keys, useFrozenGeneration: false));
        }

        Report("8T", fenced, unfenced);
        await Assert.That(Median(unfenced)).IsGreaterThan(0);
    }

    private static string[] MakeKeys()
    {
        var keys = new string[KeyCount];
        for (var i = 0; i < KeyCount; i++)
            keys[i] = $"fence-{i:D7}";
        return keys;
    }

    private static void Warmup(string[] keys)
    {
        foreach (var frozen in new[] { true, false })
        {
            var pool = new UniqueStringPool(NoRotation, maxLength: 256, useFrozenGeneration: frozen);
            for (var i = 0; i < 100_000; i++)
                pool.Make(keys[i].AsSpan());
        }
    }

    private static double MeasureSingle(string[] keys, bool useFrozenGeneration)
    {
        var pool = new UniqueStringPool(NoRotation, maxLength: 256, useFrozenGeneration: useFrozenGeneration);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < KeyCount; i++)
            pool.Make(keys[i].AsSpan());
        sw.Stop();
        return sw.Elapsed.TotalNanoseconds / KeyCount;
    }

    private static double MeasureParallel(string[] keys, bool useFrozenGeneration)
    {
        const int threadCount = 8;
        const int slice = KeyCount / threadCount;
        var pool = new UniqueStringPool(NoRotation, maxLength: 256, useFrozenGeneration: useFrozenGeneration);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var gate = new Barrier(threadCount + 1);
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var from = t * slice;
            threads[t] = new Thread(() =>
            {
                gate.SignalAndWait();
                for (var i = from; i < from + slice; i++)
                    pool.Make(keys[i].AsSpan());
            }) { IsBackground = true };
            threads[t].Start();
        }

        gate.SignalAndWait();
        var sw = Stopwatch.StartNew();
        foreach (var thread in threads)
            thread.Join();
        sw.Stop();
        return sw.Elapsed.TotalNanoseconds / (slice * threadCount);
    }

    private static void Report(string label, List<double> fenced, List<double> unfenced)
    {
        var f = Median(fenced);
        var u = Median(unfenced);
        Console.WriteLine(
            $"[fence-bench:{label}] fenced (frozen) = {f:F1} ns/insert, unfenced (sealed) = {u:F1} ns/insert, " +
            $"delta = {f - u:F1} ns ({(f - u) / f * 100:F1}%)");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
