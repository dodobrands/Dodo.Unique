using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Dodo.Unique.Tests;

public sealed class UniqueStringPoolTests
{
	[Test]
	public async Task Make_span_twice_returns_reference_equal_string()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		var a = pool.Make("vegan".AsSpan());
		var b = pool.Make("vegan".AsSpan());

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Make_string_twice_returns_reference_equal_string()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		var a = pool.Make(new string('v', 5));
		var b = pool.Make(new string('v', 5));

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Make_string_returns_input_when_first_added()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		var input = new string('a', 5);

		var a = pool.Make(input);

		await Assert.That(ReferenceEquals(a, input)).IsTrue();
	}

	[Test]
	public async Task Make_span_over_length_limit_returns_new_string_without_caching()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 8);
		var input = new string('x', 9);

		var a = pool.Make(input.AsSpan());
		var b = pool.Make(input.AsSpan());

		await Assert.That(a).IsEqualTo(input);
		await Assert.That(ReferenceEquals(a, b)).IsFalse();
	}

	[Test]
	public async Task Make_string_over_length_limit_returns_input_without_caching()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 8);
		var input1 = new string('x', 9);
		var input2 = new string('x', 9);

		var a = pool.Make(input1);
		var b = pool.Make(input2);

		await Assert.That(ReferenceEquals(a, input1)).IsTrue();
		await Assert.That(ReferenceEquals(b, input2)).IsTrue();
	}

	[Test]
	public async Task Make_empty_span_returns_empty_string()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		var a = pool.Make(ReadOnlySpan<char>.Empty);

		await Assert.That(a).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task Make_empty_string_returns_empty_string()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		await Assert.That(pool.Make(string.Empty)).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task Make_null_string_throws()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		await Assert.That(() => pool.Make((string)null!)).Throws<ArgumentNullException>();
	}

	[Test]
	public async Task Make_at_length_boundary_is_interned()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 8);
		var input = new string('y', 8);

		var a = pool.Make(input.AsSpan());
		var b = pool.Make(input.AsSpan());

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Make_after_rotation_promotes_from_cold_preserving_reference()
	{
		var pool = new UniqueStringPool(TimeSpan.FromMilliseconds(1));

		var a = pool.Make("hot".AsSpan());
		await Task.Delay(20);
		_ = pool.Make("other".AsSpan());
		var b = pool.Make("hot".AsSpan());

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Make_after_two_rotations_evicts_without_throwing()
	{
		var pool = new UniqueStringPool(TimeSpan.FromMilliseconds(1));

		var a = pool.Make("evicted".AsSpan());
		await Task.Delay(20);
		_ = pool.Make("r1".AsSpan());
		await Task.Delay(20);
		_ = pool.Make("r2".AsSpan());
		var b = pool.Make("evicted".AsSpan());

		await Assert.That(b).IsEqualTo(a);
	}

	[Test]
	public async Task Make_high_volume_returns_correct_values()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		const int count = 4096;

		for (var i = 0; i < count; i++)
		{
			var key = $"k{i}";
			var returned = pool.Make(key.AsSpan());
			await Assert.That(returned).IsEqualTo(key);
		}
	}

	[Test]
	public async Task Make_concurrent_dedupes()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		const int threadCount = 32;
		const int iterationsPerThread = 200;

		var results = new string[threadCount * iterationsPerThread];
		var tasks = new Task[threadCount];
		for (var t = 0; t < threadCount; t++)
		{
			var baseIdx = t * iterationsPerThread;
			tasks[t] = Task.Run(() =>
			{
				for (var i = 0; i < iterationsPerThread; i++)
					results[baseIdx + i] = pool.Make("thincrust".AsSpan());
			});
		}
		await Task.WhenAll(tasks);

		var first = results[0];
		foreach (var s in results)
			await Assert.That(ReferenceEquals(s, first)).IsTrue();
	}

	[Test]
	public async Task Constructor_rejects_zero_or_negative_retention()
	{
		await Assert.That(() => new UniqueStringPool(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => new UniqueStringPool(TimeSpan.FromMilliseconds(-1))).Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Constructor_rejects_zero_or_negative_max_length()
	{
		await Assert.That(() => new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 0)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => new UniqueStringPool(TimeSpan.FromHours(1), maxLength: -1)).Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Options_ctor_builds_equivalent_pool()
	{
		var pool = new UniqueStringPool(new UniqueStringPoolOptions
		{
			MinRetention = TimeSpan.FromHours(1),
			MaxLength = 8,
		});

		await Assert.That(pool.MaxLength).IsEqualTo(8);

		var input = new string('y', 8);
		var a = pool.Make(input.AsSpan());
		var b = pool.Make(input.AsSpan());
		await Assert.That(ReferenceEquals(a, b)).IsTrue();

		var tooLong = new string('y', 9);
		await Assert.That(ReferenceEquals(
			pool.Make(tooLong.AsSpan()),
			pool.Make(tooLong.AsSpan()))).IsFalse();
	}

	[Test]
	public async Task Options_ctor_rejects_null_options()
	{
		await Assert.That(() => new UniqueStringPool(null!)).Throws<ArgumentNullException>();
	}

	[Test]
	public async Task Options_ctor_validates_via_underlying_constructor()
	{
		await Assert.That(() => new UniqueStringPool(new UniqueStringPoolOptions
		{
			MinRetention = TimeSpan.Zero,
		})).Throws<ArgumentOutOfRangeException>();

		await Assert.That(() => new UniqueStringPool(new UniqueStringPoolOptions
		{
			MinRetention = TimeSpan.FromHours(1),
			MaxLength = 0,
		})).Throws<ArgumentOutOfRangeException>();
	}
}
