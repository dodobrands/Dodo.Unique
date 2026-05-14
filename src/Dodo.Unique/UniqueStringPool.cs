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
	private long _expiryMs;
	private ConcurrentDictionary<string, string> _hot = new(StringComparer.Ordinal);
	// Cold is FrozenDictionary in steady state and briefly ConcurrentDictionary during
	// rotation while a snapshot of oldHot is built. Stored via the shared read-only
	// interface; concrete-type dispatch at span lookup sites preserves the alternate-lookup
	// fast path on both types.
	private IReadOnlyDictionary<string, string> _cold = FrozenDictionary<string, string>.Empty;

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
		_expiryMs = Environment.TickCount64 + _steadyIntervalMs;
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

		var hot = Volatile.Read(ref _hot);
		if (hot.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(chars, out var hit))
			return hit;

		if (TryColdLookup(chars, out hit))
		{
			MaybeRotate();
			Volatile.Read(ref _hot).TryAdd(hit, hit);
			return hit;
		}

		MaybeRotate();
		return InsertIntoHot(chars);
	}

	public string Make(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (value.Length == 0)
			return string.Empty;
		if (value.Length > MaxLength)
			return value;

		var hot = Volatile.Read(ref _hot);
		if (hot.TryGetValue(value, out var hit))
			return hit;

		if (Volatile.Read(ref _cold).TryGetValue(value, out hit))
		{
			MaybeRotate();
			Volatile.Read(ref _hot).TryAdd(hit, hit);
			return hit;
		}

		MaybeRotate();
		return Volatile.Read(ref _hot).GetOrAdd(value, value);
	}

	private bool TryColdLookup(ReadOnlySpan<char> chars, out string hit)
	{
		var cold = Volatile.Read(ref _cold);
		if (cold is FrozenDictionary<string, string> frozen)
			return frozen.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(chars, out hit!);
		return ((ConcurrentDictionary<string, string>)cold)
			.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(chars, out hit!);
	}

	private string InsertIntoHot(ReadOnlySpan<char> span)
	{
		var hot = Volatile.Read(ref _hot);
		if (hot.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(span, out var existing))
			return existing;

		var str = new string(span);
		return hot.GetOrAdd(str, str);
	}

	private void MaybeRotate()
	{
		var nowMs = Environment.TickCount64;
		var exp = Volatile.Read(ref _expiryMs);
		if (nowMs < exp ||
			Interlocked.CompareExchange(ref _expiryMs, nowMs + _steadyIntervalMs, exp) != exp)
			return;

		var oldHot = Volatile.Read(ref _hot);
		// ConcurrentDictionary.Count acquires all internal locks — read it once.
		var count = oldHot.Count;
		var seed = count + (count >> 2);
		var newHot = new ConcurrentDictionary<string, string>(
			concurrencyLevel: Environment.ProcessorCount, capacity: seed, comparer: StringComparer.Ordinal);

		// Phase 1: publish oldHot as the live cold. Reads via _cold now find any value in
		// oldHot, including writes arriving from stragglers that still hold a stale _hot
		// reference. Hot is swapped so new writers route to newHot.
		Volatile.Write(ref _cold, oldHot);
		Volatile.Write(ref _hot, newHot);

		// Phase 2: freeze oldHot for steady-state read perf. Stragglers that captured
		// _hot=oldHot before the swap mostly land their writes during phase 1 (visible via
		// live cold); only writers paused longer than the snapshot duration could land
		// after this point — and their writes would be lost. In practice this means
		// threads paused for ms-scale during a single Make call: effectively never.
		var frozen = oldHot.ToFrozenDictionary(StringComparer.Ordinal);
		Volatile.Write(ref _cold, frozen);
	}
}
