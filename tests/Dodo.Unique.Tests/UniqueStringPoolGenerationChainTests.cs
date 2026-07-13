namespace Dodo.Unique.Tests;

/// <summary>
/// Deterministic reproductions of the sealed-chain identity races — no scheduling luck needed.
/// A caller holding a stale generation reference must converge on the instance already stored
/// in the sealed map instead of minting a duplicate past it.
/// </summary>
public sealed class UniqueStringPoolGenerationChainTests
{
    [Test]
    public async Task AddOrGet_on_sealed_generation_returns_existing_instance()
    {
        var g1 = new UniqueStringPool.Generation();
        var g2 = new UniqueStringPool.Generation();
        var a = g1.AddOrGet("value");
        g1.SealTo(g2);

        var b = g1.AddOrGet(new string("value".AsSpan()));

        await Assert.That(ReferenceEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task AddOrGet_on_sealed_generation_forwards_new_entry_into_seal_target()
    {
        var g1 = new UniqueStringPool.Generation();
        var g2 = new UniqueStringPool.Generation();
        g1.SealTo(g2);

        var stored = g1.AddOrGet("fresh");
        var viaTarget = g2.AddOrGet(new string("fresh".AsSpan()));

        await Assert.That(ReferenceEquals(stored, viaTarget)).IsTrue();
    }

    [Test]
    public async Task Canonical_forwards_sealed_map_hit_to_chain_tail_winner()
    {
        var g1 = new UniqueStringPool.Generation();
        var g2 = new UniqueStringPool.Generation();
        var a = g1.AddOrGet("value");
        var winner = g2.AddOrGet(new string("value".AsSpan()));
        g1.SealTo(g2);

        var resolved = g1.Canonical(a);

        await Assert.That(ReferenceEquals(resolved, winner)).IsTrue();
    }

    [Test]
    public async Task Canonical_returns_hit_unchanged_on_live_generation()
    {
        var g = new UniqueStringPool.Generation();
        var a = g.AddOrGet("value");

        await Assert.That(ReferenceEquals(g.Canonical(a), a)).IsTrue();
    }
}
