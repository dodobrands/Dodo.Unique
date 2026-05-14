using Newtonsoft.Json;

namespace Dodo.Unique.NewtonsoftJson;

/// <summary>
/// Newtonsoft.Json converter that routes deserialized strings through a
/// <see cref="UniqueStringPool"/>, so repeating field values share a canonical instance.
/// Writes pass through unchanged.
/// </summary>
public sealed class UniqueJsonStringConverter : JsonConverter<string?>
{
	private readonly UniqueStringPool _pool;

	public UniqueJsonStringConverter(UniqueStringPool pool)
	{
		ArgumentNullException.ThrowIfNull(pool);
		_pool = pool;
	}

	public override string? ReadJson(
		JsonReader reader,
		Type objectType,
		string? existingValue,
		bool hasExistingValue,
		JsonSerializer serializer)
	{
		return reader.TokenType switch
		{
			JsonToken.Null => null,
			JsonToken.String => _pool.Make((string)reader.Value!),
			_ => throw new JsonSerializationException(
				$"Unexpected token {reader.TokenType} when reading string at path '{reader.Path}'."),
		};
	}

	public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
		=> writer.WriteValue(value);
}
