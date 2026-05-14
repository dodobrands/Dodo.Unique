using System.Text.Json;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Dodo.Unique.Tests;

public sealed class UniqueStringConverterTests
{
	private static JsonSerializerOptions OptionsWithPool(UniqueStringPool pool)
	{
		var options = new JsonSerializerOptions();
		options.Converters.Add(new UniqueStringConverter(pool));
		return options;
	}

	[Test]
	public async Task Deserialize_same_value_twice_returns_reference_equal_string()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		var options = OptionsWithPool(pool);

		var a = JsonSerializer.Deserialize<string>("\"vegan\"", options);
		var b = JsonSerializer.Deserialize<string>("\"vegan\"", options);

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Deserialize_null_returns_null()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

		await Assert.That(JsonSerializer.Deserialize<string?>("null", options)).IsNull();
	}

	[Test]
	public async Task Deserialize_empty_returns_empty_string()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

		await Assert.That(JsonSerializer.Deserialize<string>("\"\"", options)).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task Deserialize_long_value_skips_pool()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		var options = OptionsWithPool(pool);
		var longValue = new string('z', 300);
		var json = $"\"{longValue}\"";

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo(longValue);
		await Assert.That(ReferenceEquals(a, b)).IsFalse();
	}

	[Test]
	public async Task Deserialize_dictionary_keys_dedupes_via_ReadAsPropertyName()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));
		var options = OptionsWithPool(pool);

		var a = JsonSerializer.Deserialize<Dictionary<string, int>>("{\"key\":1}", options)!;
		var b = JsonSerializer.Deserialize<Dictionary<string, int>>("{\"key\":2}", options)!;

		await Assert.That(ReferenceEquals(a.Keys.First(), b.Keys.First())).IsTrue();
	}

	[Test]
	public async Task Convenience_constructor_creates_private_pool()
	{
		var converter = new UniqueStringConverter(new UniqueStringPoolOptions
		{
			MinRetention = TimeSpan.FromHours(1),
		});
		var options = new JsonSerializerOptions();
		options.Converters.Add(converter);

		var a = JsonSerializer.Deserialize<string>("\"alpha\"", options);
		var b = JsonSerializer.Deserialize<string>("\"alpha\"", options);

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Convenience_constructor_rejects_null_options()
	{
		await Assert.That(() => new UniqueStringConverter((UniqueStringPoolOptions)null!))
			.Throws<ArgumentNullException>();
	}

	[Test]
	public async Task Constructor_null_pool_throws()
	{
		await Assert.That(() => new UniqueStringConverter((UniqueStringPool)null!)).Throws<ArgumentNullException>();
	}

	[Test]
	public async Task Write_passes_value_through_unchanged()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

		var json = JsonSerializer.Serialize("hello", options);

		await Assert.That(json).IsEqualTo("\"hello\"");
	}

	[Test]
	public async Task Read_returns_same_instance_for_repeated_values_within_array()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));
		const string json = "[\"vegan\",\"vegan\",\"spicy\",\"vegan\"]";

		var result = JsonSerializer.Deserialize<string[]>(json, options)!;

		await Assert.That(ReferenceEquals(result[0], result[1])).IsTrue();
		await Assert.That(ReferenceEquals(result[0], result[3])).IsTrue();
		await Assert.That(ReferenceEquals(result[0], result[2])).IsFalse();
	}

	[Test]
	public async Task Read_handles_non_ascii_utf8()
	{
		// Multi-byte UTF-8 chars: byteLen > charLen. CopyString must transcode correctly
		// and the buffer slice must reflect *written chars*, not bytes.
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));
		const string value = "пицца";
		var json = JsonSerializer.Serialize(value);

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo(value);
		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Read_interns_escaped_cyrillic_within_byte_limit()
	{
		// 40 chars × 6 bytes per "я" = 240 bytes escaped — fits the 256-byte gate.
		// Guards against the gate erroneously firing on byte length when chars would fit.
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));
		var value = new string('я', 40);
		var json = JsonSerializer.Serialize(value);

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo(value);
		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Read_handles_escaped_sequences()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));
		const string json = "\"line\\nbreak\"";

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo("line\nbreak");
		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Custom_stack_buffer_length_extends_intern_coverage()
	{
		// Default 256-byte gate would bypass this string; bumping the buffer to match
		// pool.MaxLength keeps it on the interning fast path.
		var pool = new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 512);
		var options = new JsonSerializerOptions();
		options.Converters.Add(new UniqueStringConverter(pool, stackBufferLength: pool.MaxLength));
		var value = new string('q', 400);
		var json = $"\"{value}\"";

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo(value);
		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}

	[Test]
	public async Task Custom_stack_buffer_length_gates_at_configured_size()
	{
		// Strings whose UTF-8 byte length exceeds the buffer fall back to GetString — not interned.
		var pool = new UniqueStringPool(TimeSpan.FromHours(1), maxLength: 512);
		var options = new JsonSerializerOptions();
		options.Converters.Add(new UniqueStringConverter(pool, stackBufferLength: 64));
		var value = new string('q', 80);
		var json = $"\"{value}\"";

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(a).IsEqualTo(value);
		await Assert.That(ReferenceEquals(a, b)).IsFalse();
	}

	[Test]
	public async Task Constructor_rejects_zero_or_negative_stack_buffer()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		await Assert.That(() => new UniqueStringConverter(pool, stackBufferLength: 0))
			.Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => new UniqueStringConverter(pool, stackBufferLength: -1))
			.Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Constructor_rejects_stack_buffer_above_safe_ceiling()
	{
		var pool = new UniqueStringPool(TimeSpan.FromHours(1));

		await Assert.That(() => new UniqueStringConverter(pool, stackBufferLength: 1025))
			.Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Read_throws_JsonException_for_non_string_token()
	{
		var options = OptionsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

		await Assert.That(() => JsonSerializer.Deserialize<string>("42", options)).Throws<JsonException>();
		await Assert.That(() => JsonSerializer.Deserialize<string>("true", options)).Throws<JsonException>();
	}

	[Test]
	public async Task Convenience_constructor_uses_pool_options_independently_of_stack_buffer()
	{
		// Pool caches up to MaxLength=600; buffer caps at 512. Strings up to ~512 bytes
		// hit the interning fast path; anything above bypasses via GetString (heap).
		var converter = new UniqueStringConverter(
			new UniqueStringPoolOptions { MinRetention = TimeSpan.FromHours(1), MaxLength = 600 },
			stackBufferLength: 512);
		var options = new JsonSerializerOptions();
		options.Converters.Add(converter);
		var value = new string('m', 400);
		var json = $"\"{value}\"";

		var a = JsonSerializer.Deserialize<string>(json, options);
		var b = JsonSerializer.Deserialize<string>(json, options);

		await Assert.That(ReferenceEquals(a, b)).IsTrue();
	}
}
