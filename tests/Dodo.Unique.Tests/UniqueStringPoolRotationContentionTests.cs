using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dodo.Unique.Tests;

[Category("RotationStress")]
public sealed class UniqueStringPoolRotationContentionTests
{
    private const int CheckedSetSize = 64;
    private static readonly TimeSpan StormDuration = TimeSpan.FromSeconds(1);
    // Small so rotations stay frequent -- rotation count is the lever for how many seals the race
    // can hit; raising it only thins those seal events.
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(5);
    // 2x cores: oversubscription keeps inserts in flight at each seal (what exposes the race). On too
    // few cores a checked value can go two rotations untouched and be legitimately evicted -- a false
    // positive, not a fence bug -- so this is gated to a multi-core runner via [Category].
    private static readonly int WorkerCount = Math.Max(8, Environment.ProcessorCount * 2);

    [Test]
    [NotInParallel]
    public async Task Make_under_rotation_contention_does_not_duplicate_strings()
    {
        var result = RunRotationStorm();

        await Assert.That(result.ErrorSummary).IsEqualTo(string.Empty);
        await Assert.That(result.Rotated).IsTrue();
        await Assert.That(result.DuplicationSummary).IsEqualTo(string.Empty);
    }

    [Test]
    [NotInParallel]
    public async Task Make_under_rotation_contention_does_not_lose_strings()
    {
        var result = RunRotationStorm();

        await Assert.That(result.ErrorSummary).IsEqualTo(string.Empty);
        await Assert.That(result.Rotated).IsTrue();
        await Assert.That(result.LossSummary).IsEqualTo(string.Empty);
    }

    private static StormResult RunRotationStorm()
    {
        var pool = new UniqueStringPool(Retention);

        var checkedValues = new string[CheckedSetSize];
        for (var i = 0; i < CheckedSetSize; i++)
            checkedValues[i] = $"v{i:D4}";

        var anchors = new string[CheckedSetSize];
        for (var i = 0; i < CheckedSetSize; i++)
            anchors[i] = pool.Make(checkedValues[i].AsSpan());

        // Canary: interned once, never touched during the storm. The span overload (not a string
        // literal) avoids CLR interning masking the reference change. Two rotations evict it, so a
        // post-storm re-make returning a different instance proves the storm actually rotated --
        // without this guard a run that never rotated would pass vacuously.
        var canaryValue = new string('c', 24);
        var canaryBefore = pool.Make(canaryValue.AsSpan());

        var seen = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var duplications = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();

        using var startGate = new Barrier(WorkerCount);
        var threads = new Thread[WorkerCount];
        for (var t = 0; t < WorkerCount; t++)
        {
            var workerId = t;
            threads[t] =
                new Thread(() => RunWorker(pool, checkedValues, seen, duplications, errors, startGate, workerId))
                {
                    IsBackground = true, Name = $"rotation-storm-{workerId}",
                };
        }

        foreach (var thread in threads)
            thread.Start();

        // Join with a timeout so a deadlock in Make fails the test instead of hanging the host.
        var joinDeadline = StormDuration + TimeSpan.FromSeconds(30);
        var stuck = 0;
        foreach (var thread in threads)
            if (!thread.Join(joinDeadline))
                stuck++;
        if (stuck > 0)
            errors.Enqueue(new TimeoutException($"{stuck} worker(s) did not terminate within {joinDeadline}."));

        var losses = new List<string>();
        for (var i = 0; i < CheckedSetSize; i++)
            if (!ReferenceEquals(pool.Make(checkedValues[i].AsSpan()), anchors[i]))
                losses.Add(checkedValues[i]);

        var rotated = !ReferenceEquals(canaryBefore, pool.Make(canaryValue.AsSpan()));

        return new StormResult(
            DuplicationSummary: Summarize("duplicated (handed out a second live reference)", duplications),
            LossSummary: Summarize("lost (anchor reference was replaced)", losses),
            ErrorSummary: ErrorSummaryOf(errors),
            Rotated: rotated);
    }

    private static void RunWorker(
        UniqueStringPool pool,
        string[] checkedValues,
        ConcurrentDictionary<string, string> seen,
        ConcurrentQueue<string> duplications,
        ConcurrentQueue<Exception> errors,
        Barrier startGate,
        int workerId)
    {
        try
        {
            startGate.SignalAndWait();
            var sw = Stopwatch.StartNew();
            var n = 0L;
            while (sw.Elapsed < StormDuration)
            {
                var start = (int)((workerId + n) % CheckedSetSize);
                for (var j = 0; j < CheckedSetSize; j++)
                {
                    var value = checkedValues[(start + j) % CheckedSetSize];
                    var made = pool.Make(value.AsSpan());
                    if (!ReferenceEquals(seen.GetOrAdd(value, made), made))
                        duplications.Enqueue(value);
                }

                // One fresh miss per pass: Make reaches MaybeRotate() only on a miss, so this both
                // drives rotation and is the continuous insert stream that races SealTo (the teeth).
                _ = pool.Make($"d-{workerId}-{n++}".AsSpan());
            }
        }
#pragma warning disable CA1031 // a worker is a thread root; any escape would crash the host, so record everything
        catch (Exception ex)
#pragma warning restore CA1031
        {
            errors.Enqueue(ex);
        }
    }

    private static string ErrorSummaryOf(ConcurrentQueue<Exception> errors)
    {
        if (errors.IsEmpty)
            return string.Empty;
        errors.TryPeek(out var first);
        return $"{errors.Count} worker exception(s); first: {first}";
    }

    private static string Summarize(string what, IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0
            ? string.Empty
            : $"{distinct.Count} value(s) {what}: {string.Join(", ", distinct.Take(10))}";
    }

    private sealed record StormResult(string DuplicationSummary, string LossSummary, string ErrorSummary, bool Rotated);
}
