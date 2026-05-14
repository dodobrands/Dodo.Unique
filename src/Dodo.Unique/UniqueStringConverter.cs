using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dodo.Unique;

/// <summary>
/// System.Text.Json converter that routes deserialized strings through a
/// <see cref="UniqueStringPool"/>, so repeating field values share a canonical instance.
/// Writes pass through unchanged.
/// </summary>
public sealed class UniqueStringConverter: JsonConverter<string>
{
    // Hard ceiling for the stackalloc buffer (2 KB at sizeof(char)=2). Larger values
    // risk StackOverflowException on threads with small stacks (ASP.NET workers ~1 MB).
    private const int MaxStackBufferLength = 1024;

    private const int DefaultStackBufferLength = 256;

    private readonly UniqueStringPool _pool;

    private int StackBufferLength { get; }

    /// <summary>
    /// Creates a converter backed by an existing pool.
    /// </summary>
    /// <param name="pool">Pool used to canonicalize deserialized strings.</param>
    /// <param name="stackBufferLength">
    /// Maximum char length the stack-allocated decode buffer can hold. Strings whose
    /// UTF-8 byte length exceeds this fall back to <see cref="Utf8JsonReader.GetString"/>
    /// (heap-allocated, not deduplicated). For full coverage, set this to
    /// <see cref="UniqueStringPool.MaxLength"/>. Must be in <c>[1, 1024]</c>.
    /// <para>
    /// The 1024-char (2 KB) ceiling is generous compared to the conventional 1 KB
    /// stackalloc guideline; values near the top end add risk in deeply-nested
    /// deserialization or small-stack threads. Keep at the default 256 unless you have
    /// measured a real benefit.
    /// </para>
    /// </param>
    public UniqueStringConverter(UniqueStringPool pool, int stackBufferLength = DefaultStackBufferLength)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackBufferLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stackBufferLength, MaxStackBufferLength);
        _pool = pool;
        StackBufferLength = stackBufferLength;
    }

    /// <summary>
    /// Creates a converter with a private pool built from the supplied options.
    /// </summary>
    public UniqueStringConverter(UniqueStringPoolOptions poolOptions, int stackBufferLength = DefaultStackBufferLength)
        : this(new UniqueStringPool(poolOptions), stackBufferLength)
    {
    }

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        var byteLen = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (byteLen == 0)
            return string.Empty;
        if (byteLen > StackBufferLength)
            return reader.GetString();

        Span<char> buffer = stackalloc char[StackBufferLength];
        var written = reader.CopyString(buffer);
        return _pool.Make(buffer[..written]);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);

    public override string ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options) ?? string.Empty;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WritePropertyName(value);
}