using System.Diagnostics.CodeAnalysis;

namespace Dodo.Unique.Tests;

/// <summary>
/// Store-buffering (SB) litmus for the exact shape UniqueStringPool's Dekker pair protects:
/// writer stores into the map then loads _next; rotator stores _next then loads the map.
/// The forbidden outcome — both sides reading the pre-store value — is permitted with
/// volatile-only accesses (acquire/release does not order StoreLoad) and must be impossible
/// once both sides run a full fence. Millions of bare-metal trials per second make this the
/// primitive-level teeth the integration storms structurally cannot provide.
/// </summary>
[Category("RotationStress")]
[Category("MemoryModel")]
[SuppressMessage("Performance", "CA1802", Justification = "instance state is the point")]
public sealed class MemoryModelLitmusTests
{
    private const int Rounds = 300_000;

    private int _x;
#pragma warning disable CS0169 // padding keeps _x and _y on different cache lines
    private long _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169
    private int _y;

    [Test]
    [NotInParallel]
    public async Task Full_fence_forbids_store_load_reordering()
    {
        var violations = RunStoreBuffering(fullFence: true);
        Console.WriteLine($"[litmus] fenced: {violations} violations in {Rounds} rounds");

        await Assert.That(violations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task Volatile_only_store_load_reordering_witness()
    {
        var violations = RunStoreBuffering(fullFence: false);
        Console.WriteLine($"[litmus] volatile-only: {violations} violations in {Rounds} rounds " +
                          "(>0 means this host reorders StoreLoad and the pool's full fences are load-bearing; " +
                          "0 means this host/JIT currently cannot exhibit the race the fences guard against)");

        // Informational witness: the reorder count is hardware/JIT-dependent and may
        // legitimately be zero on strong or lightly-loaded machines, so no upper assert.
        await Assert.That(violations).IsGreaterThanOrEqualTo(0);
    }

    private int RunStoreBuffering(bool fullFence)
    {
        var violations = 0;
        var r0 = 0;
        var r1 = 0;
        using var gate = new Barrier(3);

        var t0 = new Thread(() =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                gate.SignalAndWait();
                Volatile.Write(ref _x, 1);
                if (fullFence)
                    Interlocked.MemoryBarrier();
                r0 = Volatile.Read(ref _y);
                gate.SignalAndWait();
            }
        }) { IsBackground = true };

        var t1 = new Thread(() =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                gate.SignalAndWait();
                Volatile.Write(ref _y, 1);
                if (fullFence)
                    Interlocked.MemoryBarrier();
                r1 = Volatile.Read(ref _x);
                gate.SignalAndWait();
            }
        }) { IsBackground = true };

        t0.Start();
        t1.Start();
        for (var i = 0; i < Rounds; i++)
        {
            gate.SignalAndWait();
            gate.SignalAndWait();
            if (r0 == 0 && r1 == 0)
                violations++;
            _x = 0;
            _y = 0;
        }
        t0.Join();
        t1.Join();
        return violations;
    }
}
