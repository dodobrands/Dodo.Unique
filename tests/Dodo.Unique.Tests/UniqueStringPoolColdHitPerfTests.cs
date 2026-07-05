using System.Diagnostics;

namespace Dodo.Unique.Tests;

/// <summary>
/// First touch after a rotation (cold lookup + promote), per mode. The promote insert
/// dominates, so the cold-tier structure choice barely moves read latency.
/// </summary>
[Category("RotationStress")]
[Category("PerfProof")]
public sealed class UniqueStringPoolColdHitPerfTests
{
    private const int KeyCount = 200_000;
    private const int Trials = 3;
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(300);

    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Cold_hit_promote_sweep(bool useFrozenGeneration)
    {
        var keys = new string[KeyCount];
        for (var i = 0; i < KeyCount; i++)
            keys[i] = $"cold-{i:D6}";

        var results = new List<double>(Trials);
        for (var t = 0; t < Trials; t++)
        {
            var pool = new UniqueStringPool(Retention, maxLength: 256, useFrozenGeneration: useFrozenGeneration);
            foreach (var key in keys)
                pool.Make(key.AsSpan());

            Thread.Sleep(Retention + TimeSpan.FromMilliseconds(50));
            _ = pool.Make($"rotate-{t}".AsSpan());
            WaitForRotationIdle(pool);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sw = Stopwatch.StartNew();
            foreach (var key in keys)
                pool.Make(key.AsSpan());
            sw.Stop();
            results.Add(sw.Elapsed.TotalNanoseconds / KeyCount);
        }

        var mode = useFrozenGeneration ? "frozen" : "sealed";
        Console.WriteLine($"[coldhit-bench:{mode}] cold hit + promote = {Median(results):F1} ns/op over {KeyCount} entries");

        await Assert.That(Median(results)).IsGreaterThan(0);
    }

    private static void WaitForRotationIdle(UniqueStringPool pool)
    {
        var deadlineMs = Environment.TickCount64 + 5000;
        while (!pool.RotationIdle)
        {
            if (Environment.TickCount64 > deadlineMs)
                throw new TimeoutException("Rotation did not complete within 5s.");
            Thread.Sleep(1);
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
