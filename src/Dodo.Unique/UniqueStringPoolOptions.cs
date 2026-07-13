namespace Dodo.Unique;

/// <summary>
/// Configuration for <see cref="UniqueStringPool"/>. Use this overload when constructing a
/// pool whose call site would otherwise become a long positional argument list, or to keep
/// configuration readable when future knobs are added.
/// </summary>
public sealed class UniqueStringPoolOptions
{
    /// <inheritdoc cref="UniqueStringPool(TimeSpan, int, bool)" path="/param[@name='minRetention']"/>
    public required TimeSpan MinRetention { get; init; }

    /// <inheritdoc cref="UniqueStringPool(TimeSpan, int, bool)" path="/param[@name='maxLength']"/>
    public int MaxLength { get; init; } = 256;

    /// <inheritdoc cref="UniqueStringPool(TimeSpan, int, bool)" path="/param[@name='useFrozenGeneration']"/>
    public bool UseFrozenGeneration { get; init; } = true;
}
