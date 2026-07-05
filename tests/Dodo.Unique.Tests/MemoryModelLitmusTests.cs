using System.Diagnostics.CodeAnalysis;

namespace Dodo.Unique.Tests;

/// <summary>
/// Store-buffering litmus for the shape the pool's Dekker pair protects: volatile-only
/// permits the both-read-old outcome, a full fence must forbid it.
/// </summary>
[Category("RotationStress")]
[Category("MemoryModel")]
[SuppressMessage("Performance", "CA1802", Justification = "instance state is the point")]
public sealed class MemoryModelLitmusTests
{
    private const int Rounds = 300_000;
    // Process-wide barriers are syscalls; fewer rounds keep the asymmetric variant fast.
    private const int AsymmetricRounds = 10_000;

    private enum FenceMode
    {
        VolatileOnly,
        FullBothSides,
        ProcessWideOnOneSide,
    }

    private int _x;
#pragma warning disable CS0169 // padding keeps _x and _y on different cache lines
    private long _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169
    private int _y;

    [Test]
    [NotInParallel]
    public async Task Full_fence_forbids_store_load_reordering()
    {
        var violations = RunStoreBuffering(FenceMode.FullBothSides, Rounds);
        Console.WriteLine($"[litmus] fenced: {violations} violations in {Rounds} rounds");

        await Assert.That(violations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task Volatile_only_store_load_reordering_witness()
    {
        var violations = RunStoreBuffering(FenceMode.VolatileOnly, Rounds);
        Console.WriteLine($"[litmus] volatile-only: {violations} violations in {Rounds} rounds " +
                          "(>0 means this host reorders StoreLoad and the pool's full fences are load-bearing; " +
                          "0 means this host/JIT currently cannot exhibit the race the fences guard against)");

        // Host-dependent witness — zero is legitimate on strong hosts, so no upper assert.
        await Assert.That(violations).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task Process_wide_barrier_on_one_side_forbids_store_load_reordering()
    {
        var violations = RunStoreBuffering(FenceMode.ProcessWideOnOneSide, AsymmetricRounds);
        Console.WriteLine($"[litmus] asymmetric process-wide: {violations} violations in {AsymmetricRounds} rounds");

        // Asymmetric Dekker: an IPI-backed barrier on one side must close SB with no fence on the other.
        await Assert.That(violations).IsEqualTo(0);
    }

    private int RunStoreBuffering(FenceMode mode, int rounds)
    {
        var violations = 0;
        var r0 = 0;
        var r1 = 0;
        using var gate = new Barrier(3);

        var t0 = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                gate.SignalAndWait();
                Volatile.Write(ref _x, 1);
                if (mode == FenceMode.FullBothSides)
                    Interlocked.MemoryBarrier();
                r0 = Volatile.Read(ref _y);
                gate.SignalAndWait();
            }
        }) { IsBackground = true };

        var t1 = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                gate.SignalAndWait();
                Volatile.Write(ref _y, 1);
                if (mode == FenceMode.FullBothSides)
                    Interlocked.MemoryBarrier();
                else if (mode == FenceMode.ProcessWideOnOneSide)
                    Interlocked.MemoryBarrierProcessWide();
                r1 = Volatile.Read(ref _x);
                gate.SignalAndWait();
            }
        }) { IsBackground = true };

        t0.Start();
        t1.Start();
        for (var i = 0; i < rounds; i++)
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
