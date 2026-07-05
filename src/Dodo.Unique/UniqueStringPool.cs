using System.Collections.Concurrent;
using System.Collections.Frozen;

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
        _state = new State(new Generation(), FrozenGeneration.Empty, Environment.TickCount64 + _steadyIntervalMs);
    }

    /// <summary>
    /// Creates a new pool from an options object. Equivalent to the positional ctor — pick
    /// this overload for readability or when more knobs are added.
    /// </summary>
    public UniqueStringPool(UniqueStringPoolOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).MinRetention, options.MaxLength, options.UseFrozenGeneration)
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
        if (!ReferenceEquals(latest, state) && latest.Cold.TryGet(chars, out hit))
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
        if (!ReferenceEquals(latest, state) && latest.Cold.TryGet(value, out hit))
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
            var rotateAtMs = nowMs + _steadyIntervalMs;
            var newHot = current.Hot.SealTo(new Generation(seed));

            if (_useFrozenGeneration)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static s => s.pool.CompleteFrozenRotation(s.sealedHot, s.newHot, s.seed, s.rotateAtMs),
                    (pool: this, sealedHot: current.Hot, newHot, seed, rotateAtMs),
                    preferLocal: false);
                handedOff = true;
            }
            else
            {
                Volatile.Write(ref _state, new State(newHot, current.Hot, rotateAtMs));
            }
        }
        finally
        {
            if (!handedOff)
                Volatile.Write(ref _rotationInProgress, 0);
        }
    }

    private void CompleteFrozenRotation(Generation sealedHot, Generation newHot, int seed, long rotateAtMs)
    {
        try
        {
            var newCold = new FrozenGeneration(Freeze(sealedHot.Map, seed));
            Volatile.Write(ref _state, new State(newHot, newCold, rotateAtMs));
        }
        finally
        {
            Volatile.Write(ref _rotationInProgress, 0);
        }
    }

    private static FrozenDictionary<string, string> Freeze(ConcurrentDictionary<string, string> source, int capacity)
    {
        var snapshot = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        foreach (var kvp in source)
            snapshot[kvp.Key] = kvp.Value;
        return snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private interface IColdGeneration
    {
        bool TryGet(ReadOnlySpan<char> key, out string value);
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

    private sealed class Generation : IColdGeneration
    {
        internal readonly ConcurrentDictionary<string, string> Map;
        private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private Generation? _next;

        internal Generation(int capacity = 0)
        {
            Map = new ConcurrentDictionary<string, string>(
                concurrencyLevel: Environment.ProcessorCount,
                capacity: capacity,
                comparer: StringComparer.Ordinal);
            _lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public bool TryGet(ReadOnlySpan<char> key, out string value) =>
            _lookup.TryGetValue(key, out value!);

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

            // Dekker fence pairing with SealTo.
            Interlocked.MemoryBarrier();
            sealedTo = Volatile.Read(ref target._next);
            return sealedTo != null ? sealedTo.AddOrGet(stored) : stored;
        }
    }

    private sealed class FrozenGeneration : IColdGeneration
    {
        internal static readonly FrozenGeneration Empty = new(FrozenDictionary<string, string>.Empty);

        private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        internal FrozenGeneration(FrozenDictionary<string, string> map)
        {
            _lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public bool TryGet(ReadOnlySpan<char> key, out string value) =>
            _lookup.TryGetValue(key, out value!);
    }
}
