using System.Collections.Concurrent;
using System.Diagnostics;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Dodo.Unique.Tests;

/// <summary>
/// Stress tests for the hot/cold rotation race. Many cores hammer a small fixed "checked" set --
/// asserting one stable canonical reference per value -- while a continuous stream of fresh
/// "driver" misses forces <c>MaybeRotate()</c> to fire repeatedly. Together they target the Dekker
/// fence pair (<c>SealTo</c> / <c>AddOrGet</c> in <see cref="UniqueStringPool"/>): a write landing
/// in the old hot generation as it is sealed and snapshotted must be neither lost (dropped from
/// both new hot and new cold, then re-added as a fresh instance) nor duplicated (two live canonical
/// instances for one value).
///
/// <para>
/// A fresh-miss stream is mandatory: <c>Make</c> returns on a hot/cold hit before reaching
/// <c>MaybeRotate()</c>, so re-making an already-interned set never rotates. Driver values are
/// unique and excluded from the checked set.
/// </para>
/// <para>
/// A reference mismatch on the checked set is a real bug, not legitimate eviction. Two guards keep
/// that true under contention: (1) workers traverse the checked set from a per-pass rotating offset,
/// so cores never march in phase and uniformly leave the same value untouched across a rotation; and
/// (2) retention sits far above a full-pass time, so every checked value is re-promoted many times
/// per interval and is in hot at every seal, never reaching the two-rotation eviction threshold.
/// (An in-phase, 1 ms-retention version false-positived ~10% of runs by evicting low-index values
/// mid-pass; both guards close that window.)
/// </para>
/// </summary>
public sealed class UniqueStringPoolRotationContentionTests
{
    private const int CheckedSetSize = 64;
    private static readonly TimeSpan StormDuration = TimeSpan.FromSeconds(1);

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
        // 5 ms retention keeps rotations frequent (hundreds per storm -- ample seal events to race)
        // while staying far above a microsecond-scale full-pass time, so the checked set cannot be
        // evicted between rotations. Lower (e.g. 1 ms) lets a contended pass approach the interval
        // and reopens the eviction window that makes the test false-positive on correct code.
        var pool = new UniqueStringPool(TimeSpan.FromMilliseconds(5));

        var checkedValues = new string[CheckedSetSize];
        for (var i = 0; i < CheckedSetSize; i++)
            checkedValues[i] = $"v{i:D4}";

        // Anchor = the canonical instance interned before the storm; the no-loss baseline.
        var anchors = new string[CheckedSetSize];
        for (var i = 0; i < CheckedSetSize; i++)
            anchors[i] = pool.Make(checkedValues[i].AsSpan());

        // Canary interned once and never touched during the storm. The span overload (not a string
        // literal) avoids CLR interning masking the reference change. Two rotations evict it, so a
        // post-storm re-make returning a different instance proves the storm actually rotated --
        // guarding against a silently vacuous run that exercises no rotation at all.
        var canaryValue = new string('c', 24);
        var canaryBefore = pool.Make(canaryValue.AsSpan());

        var seen = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var duplications = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();

        var workerCount = Math.Max(8, Environment.ProcessorCount * 2);
        using var startGate = new Barrier(workerCount);
        var threads = new Thread[workerCount];
        for (var t = 0; t < workerCount; t++)
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

        // Generous backstop: a worker that never returns means a deadlock in Make -- fail rather
        // than hang the host. Workers are otherwise bounded by StormDuration.
        var joinDeadline = StormDuration + TimeSpan.FromSeconds(30);
        var stuck = 0;
        foreach (var thread in threads)
            if (!thread.Join(joinDeadline))
                stuck++;
        if (stuck > 0)
            errors.Enqueue(new TimeoutException($"{stuck} worker(s) did not terminate within {joinDeadline}."));

        // No-loss: the instance interned before the storm must still be canonical afterwards.
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
            startGate.SignalAndWait(); // standing start: release all cores onto the pool together
            var sw = Stopwatch.StartNew();
            var n = 0L;
            while (sw.Elapsed < StormDuration)
            {
                // Hits keep every checked value hot AND verify a single canonical reference. The
                // start offset rotates per pass and per worker so cores never traverse the set in
                // phase -- otherwise a rotation firing mid-pass could leave the same early values
                // untouched across a full cycle and evict them (a false positive, not a race).
                var start = (int)((workerId + n) % CheckedSetSize);
                for (var j = 0; j < CheckedSetSize; j++)
                {
                    var value = checkedValues[(start + j) % CheckedSetSize];
                    var made = pool.Make(value.AsSpan());
                    if (!ReferenceEquals(seen.GetOrAdd(value, made), made))
                        duplications.Enqueue(value);
                }

                // One fresh miss reaches MaybeRotate() and drives a rotation once the gate elapses.
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
