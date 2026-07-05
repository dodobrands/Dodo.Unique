using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Dodo.Unique;

/// <summary>
/// Returns a single canonical <see cref="string"/> instance per equal value, so repeated
/// reads of the same text share memory and become reference-equal.
///
/// <para>
/// Inspired by Go's <c>unique</c> package, but the retention strategy differs: instead of
/// dropping entries when no live references remain (Go uses weak refs + GC), this pool keeps
/// each entry for at least <c>minRetention</c> after its last access, then evicts via a
/// two-tier hot/cold rotation. Trade-off: predictable cost and no GC coupling, at the price
/// of entries possibly lingering past their last use for up to one rotation cycle.
/// </para>
/// </summary>
public sealed class UniqueStringPool
{
    private readonly long _steadyIntervalMs;
    private readonly bool _useFrozenGeneration;
    private int _rotationInProgress;
    private State _state;

    /// <inheritdoc cref="UniqueStringPool(TimeSpan, int, bool)"/>
    public UniqueStringPool(TimeSpan minRetention, int maxLength = 256)
        : this(minRetention, maxLength, useFrozenGeneration: true)
    {
    }

    /// <summary>
    /// Creates a new pool.
    /// </summary>
    /// <param name="minRetention">
    /// Retention floor after last access. Idle entries evict at delay d where
    /// <paramref name="minRetention"/> ≤ d &lt; 2·<paramref name="minRetention"/>,
    /// via a two-tier hot/cold rotation. Live entries ≤ unique inserts during
    /// 2·<paramref name="minRetention"/>. Rotation is driven by misses; under hit-only or
    /// fully idle traffic, idle entries can outlive the stated bound (memory still does not
    /// grow).
    /// </param>
    /// <param name="maxLength">
    /// Values longer than this bypass canonicalization: <c>Make(string)</c> returns the input
    /// unchanged, <c>Make(ReadOnlySpan&lt;char&gt;)</c> allocates a fresh <see cref="string"/>.
    /// Caps per-entry memory cost.
    /// </param>
    /// <param name="useFrozenGeneration">
    /// <c>true</c> (default) — each rotation snapshots the retiring hot tier into an immutable
    /// <see cref="FrozenDictionary{TKey,TValue}"/> on the thread pool; fastest cold-tier
    /// lookups, one background copy per rotation. <c>false</c> — rotation republishes the
    /// sealed hot map as the cold tier; no copying, no background work, marginally slower
    /// cold-tier lookups.
    /// </param>
    public UniqueStringPool(TimeSpan minRetention, int maxLength, bool useFrozenGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        MaxLength = maxLength;
        _useFrozenGeneration = useFrozenGeneration;
        _steadyIntervalMs = Math.Max(1, (long)minRetention.TotalMilliseconds);
        _state = new State(new Generation(fenceOnAdd: useFrozenGeneration), FrozenGeneration.Empty, NextRotateAt());
    }

    /// <summary>
    /// Creates a new pool from an options object. Equivalent to the positional ctor — pick
    /// this overload for readability or when more knobs are added.
    /// </summary>
    public UniqueStringPool(UniqueStringPoolOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).MinRetention, options.MaxLength,
            options.UseFrozenGeneration)
    {
    }

    /// <summary>
    /// The configured upper bound on cached string length. Exposed so callers (e.g.
    /// <see cref="UniqueStringConverter"/>) can size their decode buffers consistently.
    /// </summary>
    public int MaxLength { get; }

    internal bool RotationIdle => Volatile.Read(ref _rotationInProgress) == 0;

    public string Make(ReadOnlySpan<char> chars)
    {
        if (chars.Length == 0)
            return string.Empty;
        if (chars.Length > MaxLength)
            return new string(chars);

        var state = Volatile.Read(ref _state);
        if (state.Hot.TryGet(chars, out var hit))
            return hit;
        if (state.Cold.TryGet(chars, out hit))
            return state.Hot.AddOrGet(hit);

        MaybeRotate();

        var latest = Volatile.Read(ref _state);
        if (!ReferenceEquals(latest, state))
        {
            if (latest.Cold.TryGet(chars, out hit))
                return latest.Hot.AddOrGet(hit);
        }
        // Unchanged state does not mean no rotation: an in-flight seal may already
        // be routing inserts past this thread's view of the hot tier. The sealed
        // map stays live and fresher than any snapshot — re-probe it rather than
        // mint a second instance for a value a racing writer just added.
        else if (!RotationIdle && latest.Hot.TryGet(chars, out hit))
            return latest.Hot.AddOrGet(hit);

        var value = new string(chars);
        return latest.Hot.AddOrGet(value);
    }

    public string Make(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return string.Empty;
        if (value.Length > MaxLength)
            return value;

        var state = Volatile.Read(ref _state);
        if (state.Hot.TryGet(value, out var hit))
            return hit;
        if (state.Cold.TryGet(value, out hit))
            return state.Hot.AddOrGet(hit);

        MaybeRotate();

        var latest = Volatile.Read(ref _state);
        if (!ReferenceEquals(latest, state))
        {
            if (latest.Cold.TryGet(value, out hit))
                return latest.Hot.AddOrGet(hit);
        }
        // See Make(ReadOnlySpan<char>) — sealed-map re-probe during in-flight rotation.
        else if (!RotationIdle && latest.Hot.TryGet(value, out hit))
            return latest.Hot.AddOrGet(hit);

        return latest.Hot.AddOrGet(value);
    }

    private void MaybeRotate()
    {
        var nowMs = Environment.TickCount64;
        if (nowMs < Volatile.Read(ref _state).RotateAtMs)
            return;

        if (Volatile.Read(ref _rotationInProgress) != 0 ||
            Interlocked.CompareExchange(ref _rotationInProgress, 1, 0) != 0)
            return;

        var handedOff = false;
        try
        {
            var current = Volatile.Read(ref _state);
            if (nowMs < current.RotateAtMs)
                return;

            var currentCount = current.Hot.Map.Count;
            var seed = currentCount + (currentCount >> 2); // x1.25
            var newHot = current.Hot.SealTo(new Generation(seed, _useFrozenGeneration));

            if (_useFrozenGeneration)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static s => s.pool.CompleteFrozenRotation(s.sealedHot, s.newHot, s.seed),
                    (pool: this, sealedHot: current.Hot, newHot, seed),
                    preferLocal: false);
                handedOff = true;
            }
            else
            {
                // Deadline anchors to publish time, not entry time: a stall between the
                // two (GC on the generation alloc, preemption under oversubscription)
                // would publish an already-expired deadline, and the µs-long generation
                // the next miss then rotates in evicts everything it never saw.
                Volatile.Write(ref _state, new State(newHot, current.Hot, NextRotateAt()));
            }
        }
        finally
        {
            if (!handedOff)
                Volatile.Write(ref _rotationInProgress, 0);
        }
    }

    private void CompleteFrozenRotation(Generation sealedHot, Generation newHot, int seed)
    {
        try
        {
            var newCold = new FrozenGeneration(Freeze(sealedHot.Map, seed));
            // Publish-time deadline for the same reason as the sealed path — here the
            // freeze itself is the stall.
            Volatile.Write(ref _state, new State(newHot, newCold, NextRotateAt()));
        }
        finally
        {
            Volatile.Write(ref _rotationInProgress, 0);
        }
    }

    private long NextRotateAt() => Environment.TickCount64 + _steadyIntervalMs;

    private static FrozenDictionary<string, string> Freeze(ConcurrentDictionary<string, string> source, int capacity)
    {
        var snapshot = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        foreach (var kvp in source)
            snapshot[kvp.Key] = kvp.Value;
        return snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private interface IColdGeneration
    {
        bool TryGet(ReadOnlySpan<char> key, [NotNullWhen(true)] out string? value);
    }

    private sealed class State
    {
        internal readonly Generation Hot;
        internal readonly IColdGeneration Cold;
        internal readonly long RotateAtMs;

        internal State(Generation hot, IColdGeneration cold, long rotateAtMs)
        {
            Hot = hot;
            Cold = cold;
            RotateAtMs = rotateAtMs;
        }
    }

    private sealed class Generation: IColdGeneration
    {
        internal readonly ConcurrentDictionary<string, string> Map;
        private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly bool _fenceOnAdd;
        private Generation? _next;

        internal Generation(int capacity = 0, bool fenceOnAdd = true)
        {
            _fenceOnAdd = fenceOnAdd;
            Map = new ConcurrentDictionary<string, string>(
                concurrencyLevel: Environment.ProcessorCount,
                capacity: capacity,
                comparer: StringComparer.Ordinal);
            _lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public bool TryGet(ReadOnlySpan<char> key, [NotNullWhen(true)] out string? value) =>
            _lookup.TryGetValue(key, out value);

        // Dekker fence pairing with the writer's after GetOrAdd. Without it the
        // rotator's snapshot may miss a racing writer's add while the writer's
        // _next re-read misses this seal — transient duplicate strings at next
        // rotation. The CAS is itself a full fence, subsuming the explicit barrier
        // it replaces. Idempotent: a rotation retried after a failed freeze reuses
        // the orphaned seal target instead of overwriting _next and losing its
        // entries.
        internal Generation SealTo(Generation next) =>
            Interlocked.CompareExchange(ref _next, next, null) ?? next;

        internal string AddOrGet(string candidate)
        {
            var target = this;
            Generation? sealedTo;
            while ((sealedTo = Volatile.Read(ref target._next)) != null)
                target = sealedTo;

            var stored = target.Map.GetOrAdd(candidate, candidate);

            // Dekker fence pairing with SealTo — needed only where a snapshot can race
            // this add (frozen mode): either the freeze sees the entry or this re-read
            // sees the seal. Sealed mode has no snapshot to lose to — the map itself
            // becomes the cold tier, so a racing add stays reachable — and a writer
            // stalled across a whole era re-reads a seal old enough that the plain
            // volatile load below cannot miss it.
            if (target._fenceOnAdd)
                Interlocked.MemoryBarrier();
            sealedTo = Volatile.Read(ref target._next);
            return sealedTo != null ? sealedTo.AddOrGet(stored) : stored;
        }
    }

    private sealed class FrozenGeneration: IColdGeneration
    {
        internal static readonly FrozenGeneration Empty = new(FrozenDictionary<string, string>.Empty);

        private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        internal FrozenGeneration(FrozenDictionary<string, string> map)
        {
            _lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public bool TryGet(ReadOnlySpan<char> key, [NotNullWhen(true)] out string? value) =>
            _lookup.TryGetValue(key, out value);
    }
}
