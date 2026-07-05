using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;

namespace Dodo.Unique.Tests;

[Category("RotationStress")]
[Category("PerfProof")]
public sealed class UniqueStringPoolRotationPerfTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RotatingCallLatency(bool useFrozenGeneration)
    {
        const int entryCount = 150_000;
        var minRetention = TimeSpan.FromMilliseconds(100);
        var pool = new UniqueStringPool(minRetention, maxLength: 256, useFrozenGeneration: useFrozenGeneration);

        for (var i = 0; i < entryCount; i++)
            pool.Make($"latency-{i}".AsSpan());
        Thread.Sleep(minRetention + TimeSpan.FromMilliseconds(20));

        var sw = Stopwatch.StartNew();
        _ = pool.Make("latency-rotation-trigger".AsSpan());
        sw.Stop();
        WaitForRotationIdle(pool);

        var source = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entryCount; i++)
        {
            var value = $"latency-{i}";
            source[value] = value;
        }

        var baselineSw = Stopwatch.StartNew();
        var snapshot = new Dictionary<string, string>(entryCount, StringComparer.Ordinal);
        foreach (var kvp in source)
            snapshot[kvp.Key] = kvp.Value;
        _ = snapshot.ToFrozenDictionary(StringComparer.Ordinal);
        baselineSw.Stop();

        var mode = useFrozenGeneration ? "frozen" : "sealed";
        Console.WriteLine($"[RotatingCallLatency:{mode}] rotating call = {sw.Elapsed.TotalMicroseconds:F1} us, baseline freeze = {baselineSw.Elapsed.TotalMilliseconds:F2} ms");

        await Assert.That(sw.Elapsed.TotalMilliseconds).IsLessThan(baselineSw.Elapsed.TotalMilliseconds / 3);
    }

    [Test]
    public async Task SealedRotationAllocation()
    {
        const int entryCount = 100_000;
        var minRetention = TimeSpan.FromMilliseconds(100);
        var pool = new UniqueStringPool(minRetention, maxLength: 256, useFrozenGeneration: false);

        for (var i = 0; i < entryCount; i++)
            pool.Make($"alloc-{i}".AsSpan());
        Thread.Sleep(minRetention + TimeSpan.FromMilliseconds(20));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = pool.Make("alloc-rotation-trigger".AsSpan());
        var after = GC.GetAllocatedBytesForCurrentThread();
        WaitForRotationIdle(pool);

        var deltaBytes = after - before;
        Console.WriteLine($"[SealedRotationAllocation] sealed rotating call allocated {deltaBytes:N0} bytes");

        await Assert.That(deltaBytes).IsLessThan(8L * 1024 * 1024);

        var frozenCycle = MeasureRotationCycleAllocation(useFrozenGeneration: true);
        var sealedCycle = MeasureRotationCycleAllocation(useFrozenGeneration: false);
        Console.WriteLine($"[SealedRotationAllocation:contrast] frozen-mode rotation cycle = {frozenCycle:N0} bytes, sealed-mode rotation cycle = {sealedCycle:N0} bytes");
    }

    private static long MeasureRotationCycleAllocation(bool useFrozenGeneration)
    {
        const int entryCount = 100_000;
        var minRetention = TimeSpan.FromMilliseconds(100);
        var pool = new UniqueStringPool(minRetention, maxLength: 256, useFrozenGeneration: useFrozenGeneration);

        for (var i = 0; i < entryCount; i++)
            pool.Make($"cycle-{useFrozenGeneration}-{i}".AsSpan());
        Thread.Sleep(minRetention + TimeSpan.FromMilliseconds(20));
        WaitForRotationIdle(pool);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        _ = pool.Make($"cycle-{useFrozenGeneration}-trigger".AsSpan());
        WaitForRotationIdle(pool);
        var after = GC.GetTotalAllocatedBytes(precise: true);

        return after - before;
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
}
