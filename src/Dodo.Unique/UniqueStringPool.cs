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
	private int _rotationInProgress;
	private State _state;

	/// <summary>
	/// Creates a new pool.
	/// </summary>
	/// <param name="minRetention">
	/// Minimum time a canonical instance is guaranteed to be retained after its last access.
	/// Idle entries are evicted between <paramref name="minRetention"/> and
	/// <c>2 * minRetention</c> later via a two-tier hot/cold rotation. Worst-case live entry
	/// count is bounded by the number of unique inserts during <c>2 * minRetention</c>.
	/// </param>
	/// <param name="maxLength">
	/// Values longer than this bypass canonicalization: <c>Make(string)</c> returns the input
	/// unchanged, <c>Make(ReadOnlySpan&lt;char&gt;)</c> allocates a fresh <see cref="string"/>.
	/// Caps per-entry memory cost.
	/// </param>
	public UniqueStringPool(TimeSpan minRetention, int maxLength = 256)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minRetention, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
		MaxLength = maxLength;
		_steadyIntervalMs = Math.Max(1, (long)minRetention.TotalMilliseconds);
		_state = new State(new Generation(), FrozenGeneration.Empty,
			Environment.TickCount64 + _steadyIntervalMs);
	}

	/// <summary>
	/// Creates a new pool from an options object. Equivalent to the positional ctor — pick
	/// this overload for readability or when more knobs are added.
	/// </summary>
	public UniqueStringPool(UniqueStringPoolOptions options)
		: this(
			(options ?? throw new ArgumentNullException(nameof(options))).MinRetention,
			options.MaxLength)
	{
	}

	/// <summary>
	/// The configured upper bound on cached string length. Exposed so callers (e.g.
	/// <see cref="UniqueStringConverter"/>) can size their decode buffers consistently.
	/// </summary>
	public int MaxLength { get; }

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
			return state.Hot.AddOrGet(hit, hit);

		MaybeRotate();
		// Re-check cold against the latest state: rotation may have folded another
		// writer's commit from oldHot into newCold while we were between our initial
		// cold check and here. Without this, we'd mint a second canonical instance
		// into newHot for a value already present in newCold.
		var latest = Volatile.Read(ref _state);
		return latest.Cold.TryGet(chars, out hit)
			? latest.Hot.AddOrGet(hit, hit)
			: latest.Hot.AddOrGet(chars, new string(chars));
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
			return state.Hot.AddOrGet(hit, hit);

		MaybeRotate();
		var latest = Volatile.Read(ref _state);
		return latest.Cold.TryGet(value, out hit)
			? latest.Hot.AddOrGet(hit, hit)
			: latest.Hot.AddOrGet(value, value);
	}

	private void MaybeRotate()
	{
		var nowMs = Environment.TickCount64;
		if (nowMs < Volatile.Read(ref _state).RotateAtMs)
			return;

		// CAS-claim instead of lock: losers bail and let their Make() continue against the
		// still-valid old state instead of parking for the rotator's snapshot build (which
		// is hundreds of µs on a 10k-entry pool, dominated by ToFrozenDictionary's hash
		// analysis). The claim is mutually exclusive — only one rotation runs at a time —
		// and the defensive re-check below covers being preempted between the outer
		// timestamp check and the claim itself.
		if (Interlocked.CompareExchange(ref _rotationInProgress, 1, 0) != 0)
			return;

		try
		{
			var current = Volatile.Read(ref _state);
			if (nowMs < current.RotateAtMs)
				return;

			var newHot = new Generation();
			current.Hot.SealTo(newHot);
			// Dekker's-style drain: by the time SealTo's barrier and the writers' own
			// Interlocked fences synchronise, any writer that incremented _writers will
			// be observed here; any writer that hadn't incremented yet observes the seal
			// and forwards to newHot. Spinning until _writers == 0 means no writer is
			// inside oldHot's TryAdd critical section — the snapshot below is sound.
			current.Hot.WaitForWritersToDrain();
			var newCold = new FrozenGeneration(
				current.Hot.Map.ToFrozenDictionary(StringComparer.Ordinal));

			Volatile.Write(ref _state,
				new State(newHot, newCold, nowMs + _steadyIntervalMs));
		}
		finally
		{
			Volatile.Write(ref _rotationInProgress, 0);
		}
	}

	private sealed class State
	{
		internal readonly Generation Hot;
		internal readonly FrozenGeneration Cold;
		internal readonly long RotateAtMs;

		internal State(Generation hot, FrozenGeneration cold, long rotateAtMs)
		{
			Hot = hot;
			Cold = cold;
			RotateAtMs = rotateAtMs;
		}
	}

	private sealed class Generation
	{
		internal readonly ConcurrentDictionary<string, string> Map;
		private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
		private Generation? _next;
		private int _writers;

		internal Generation()
		{
			Map = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
			_lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
		}

		internal bool TryGet(ReadOnlySpan<char> key, out string value) =>
			_lookup.TryGetValue(key, out value!);

		internal void SealTo(Generation next)
		{
			Volatile.Write(ref _next, next);
			// Pairs with the writer's Interlocked.Increment(_writers) on the other side
			// to form a Dekker's-style synchronisation: after this fence, our read of
			// _writers below sees any writer that already incremented, and that writer's
			// read of _next sees our seal. Plain Volatile.Write/Read alone is not
			// sequentially consistent under ECMA-335, so this fence is load-bearing.
			//
			// "Dekker's-style" here refers to the store-load fence pattern derived from
			// Dekker's mutex (store own flag → full fence → load other's flag), not the
			// full 3-variable mutex with a turn tie-breaker. We use the pattern only to
			// mutually exclude "writer-in-TryAdd on oldHot" from "rotator-snapshotting
			// oldHot"; writers do not contend with each other, so no turn is needed.
			Interlocked.MemoryBarrier();
		}

		internal void WaitForWritersToDrain()
		{
			var spin = new SpinWait();
			while (Volatile.Read(ref _writers) > 0)
				spin.SpinOnce();
		}

		internal string AddOrGet(ReadOnlySpan<char> key, string candidate)
		{
			// Fast path: already sealed — forward without touching the counter.
			var sealedTo = Volatile.Read(ref _next);
			if (sealedTo != null)
				return sealedTo.AddOrGet(key, candidate);

			// Register as in-flight writer. Interlocked.Increment is a full fence;
			// it pairs with the rotator's MemoryBarrier in SealTo (see comment there).
			Interlocked.Increment(ref _writers);
			try
			{
				sealedTo = Volatile.Read(ref _next);
				if (sealedTo != null)
					return sealedTo.AddOrGet(key, candidate);

				while (true)
				{
					if (_lookup.TryGetValue(key, out var existing))
						return existing;
					if (Map.TryAdd(candidate, candidate))
						return candidate;
				}
			}
			finally
			{
				Interlocked.Decrement(ref _writers);
			}
		}
	}

	private sealed class FrozenGeneration
	{
		internal static readonly FrozenGeneration Empty =
			new(FrozenDictionary<string, string>.Empty);

		private readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

		internal FrozenGeneration(FrozenDictionary<string, string> map)
		{
			_lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
		}

		internal bool TryGet(ReadOnlySpan<char> key, out string value) =>
			_lookup.TryGetValue(key, out value!);
	}
}
