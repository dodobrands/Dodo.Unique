using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dodo.Unique.Tests;

/// <summary>
/// Oversubscribed stress tests for the hot/cold rotation race. Workers (2x the cores) hammer a
/// small fixed "checked" set -- asserting one stable canonical reference per value -- while a
/// continuous stream of fresh "driver" misses forces <c>MaybeRotate()</c> to fire repeatedly. The
/// continuous inserts racing each <c>SealTo</c> are the teeth: they target the Dekker fence pair
/// (<c>SealTo</c> / <c>AddOrGet</c> in <see cref="UniqueStringPool"/>), so a write landing in the
/// old hot generation as it is sealed and snapshotted must be neither lost (dropped from both new
/// hot and new cold, then re-added as a fresh instance) nor duplicated (two live canonical
/// instances for one value). Removing either barrier makes these tests fail.
///
/// <para>
/// A fresh-miss stream is mandatory: <c>Make</c> returns on a hot/cold hit before reaching
/// <c>MaybeRotate()</c>, so re-making an already-interned set never rotates. Driver values are
/// unique and excluded from the checked set.
/// </para>
/// <para>
/// <b>Why <c>[Category("RotationStress")]</c> + a dedicated runner.</b> The teeth need
/// oversubscription and frequent rotations; with too few (or heavily contended) cores a checked
/// value can be starved of access for two rotations and <i>legitimately evicted</i>, which the
/// reference-stability assertion mis-reads as loss/duplication -- a false positive unrelated to the
/// fences (verified: with the barriers removed the 2-core failure rate was identical). On a
/// dedicated many-core machine that starvation does not occur (verified green over many runs on a
/// native 10-core box, where barrier removal is still caught). So these tests are category-gated:
/// excluded from the default build and local run, and executed only by CI jobs pinned to runners
/// with enough cores. The race is a StoreLoad reordering, exposed far more on weak-memory (ARM)
/// hardware than on x86 (TSO) -- the ARM job is the real regression catcher.
/// </para>
/// </summary>
[Category("RotationStress")]
public sealed class UniqueStringPoolRotationContentionTests
{
    private const int CheckedSetSize = 64;
    private static readonly TimeSpan StormDuration = TimeSpan.FromSeconds(1);
    // Retention floor stays small so rotations are frequent (hundreds per storm) -- rotation count
    // is the teeth lever; raising it only thins the seal events the race needs.
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(5);
    // 2x the cores: oversubscription is what keeps inserts in flight at each seal (the teeth).
    // Requires a runner sized for it -- see the class remarks on the category gate.
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
                // Hits keep every checked value hot AND verify a single canonical reference; the
                // per-pass, per-worker start offset spreads the cores across the set.
                var start = (int)((workerId + n) % CheckedSetSize);
                for (var j = 0; j < CheckedSetSize; j++)
                {
                    var value = checkedValues[(start + j) % CheckedSetSize];
                    var made = pool.Make(value.AsSpan());
                    if (!ReferenceEquals(seen.GetOrAdd(value, made), made))
                        duplications.Enqueue(value);
                }

                // One fresh miss per pass: a continuous stream of inserts into the just-emptied hot
                // generation is what races SealTo -- these are the teeth.
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
