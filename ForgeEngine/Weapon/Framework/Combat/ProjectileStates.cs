using System;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The state of every flight `forge.action.combat.bullet_projectile` keeps in the air on this host, one entry per
/// flight slot, and the `projectile` handle each launch hands its plan.
///
/// <b>Who writes it.</b> The native flight stepping: <see cref="Open"/> when a launch takes a slot, <see cref="Move"/>
/// after every step it places the body at, <see cref="End"/> when the flight ends. <see cref="Move"/> writes six
/// numbers into a preallocated entry addressed by the slot index — no allocation, no string and no dictionary on the
/// per-frame path.
///
/// <b>How long a handle answers.</b> A handle stays this table's for as long as its slot does: after the flight ends
/// it answers `alive = false` with no position, and it is given back to the kernel only when a later launch reuses the
/// slot (the slot that ended longest ago is reused first) or the world ends. From then on the kernel no longer holds
/// it, and the read refuses it as `stale-handle`; a handle of an earlier world is refused as `stale-world`. A live
/// handle of this provider that is not a flight's is refused as `handle-kind`. None of these is ever answered with a
/// position.
///
/// <b>Where it answers.</b> Only the machine that stepped the flight: <see cref="Installed"/> is attached by the native
/// half on the host's own thread, and a read from any other thread, or while none is attached, is refused by name.
/// </summary>
internal sealed class ProjectileStates
{
    public const string SourceUnavailableCode = "projectile-state-source-unavailable";
    public const string MissingFieldCode = "projectile-state-input-missing";
    public const string StaleWorldCode = "stale-world";

    private struct Entry
    {
        internal bool Held;
        internal bool Alive;
        internal long EndedOrder;
        internal RuntimeHandle Handle;
        internal double X, Y, Z, Dx, Dy, Dz;
    }

    /// <summary>The port shape a handle is given back through: the kind and lifetime both rows declare.</summary>
    private static readonly HandlePort HandlePort = new(ProjectileStateContract.HandleKind, ProjectileStateContract.HandleLifetime);

    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _owner;
    private readonly Entry[] _entries;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _endings;

    public ProjectileStates(IForgeEngine kernel, RuntimeModuleHandle owner, int capacity)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = new Entry[capacity];
    }

    /// <summary>The table of the host this package is loaded into, or null while none is attached.</summary>
    public static ProjectileStates? Installed { get; private set; }

    public static ProjectileStates Attach(ProjectileStates states)
        => Installed = states ?? throw new ArgumentNullException(nameof(states));

    /// <summary>Detaches exactly the table one attach installed, giving every handle it still holds back.</summary>
    public static void Detach(ProjectileStates states)
    {
        if (!ReferenceEquals(Installed, states)) return;
        Installed = null;
        states.ReleaseAll();
    }

    public int Capacity => _entries.Length;

    /// <summary>How long ago the flight in <paramref name="slot"/> ended, as an ordering: 0 for a slot that holds no
    /// handle (reuse it first), otherwise the ending's sequence number (the smallest ended longest ago).</summary>
    public long EndedOrder(int slot) => _entries[slot].Held ? _entries[slot].EndedOrder : 0;

    /// <summary>
    /// A launch takes <paramref name="slot"/>: the handle the slot still held is given back, and a new `projectile`
    /// handle is minted through the kernel's one allocation path (so the `handle-budget` ceiling applies). The flight
    /// is alive at the given point and heading until <see cref="End"/>.
    /// </summary>
    public RuntimeHandle Open(int slot, double x, double y, double z, double dx, double dy, double dz)
    {
        Release(slot);
        var handle = _owner.CastProjectileHandle(ProjectileStateContract.HandleLifetime);
        ref var entry = ref _entries[slot];
        entry.Held = true;
        entry.Alive = true;
        entry.EndedOrder = 0;
        entry.Handle = handle;
        entry.X = x; entry.Y = y; entry.Z = z;
        entry.Dx = dx; entry.Dy = dy; entry.Dz = dz;
        return handle;
    }

    /// <summary>One step of a live flight: where its body now is and its unit heading. A slot whose flight has
    /// ended is left as it is.</summary>
    public void Move(int slot, float x, float y, float z, float dx, float dy, float dz)
    {
        ref var entry = ref _entries[slot];
        if (!entry.Alive) return;
        entry.X = x; entry.Y = y; entry.Z = z;
        entry.Dx = dx; entry.Dy = dy; entry.Dz = dz;
    }

    /// <summary>The flight in <paramref name="slot"/> ended. Its handle keeps answering, with `alive = false` and no
    /// point, until the slot is reused or the world ends; the last point is cleared so nothing can read it.</summary>
    public void End(int slot)
    {
        ref var entry = ref _entries[slot];
        if (!entry.Held || !entry.Alive) return;
        entry.Alive = false;
        entry.EndedOrder = ++_endings;
        entry.X = entry.Y = entry.Z = entry.Dx = entry.Dy = entry.Dz = 0d;
    }

    /// <summary>Gives the handle <paramref name="slot"/> still holds back to the kernel. A handle the kernel no longer
    /// holds (its world ended) has nothing left to give back.</summary>
    public void Release(int slot)
    {
        ref var entry = ref _entries[slot];
        if (!entry.Held) return;
        var handle = entry.Handle;
        entry = default;
        try { _owner.ReleaseEndedHandle(handle, HandlePort, "projectile-slot-released"); }
        // The handle is already gone (its world ended, or the module left with the session): nothing is left to release.
        catch (RuntimeContractException error) when (error.Code is RuntimeAbiCodes.StaleHandle or "stale-world" or "module-unregistered") { }
    }

    /// <summary>Every flight ended and every handle given back: the world or the session ended.</summary>
    public void ReleaseAll()
    {
        for (var slot = 0; slot < _entries.Length; slot++) Release(slot);
    }

    /// <summary>`forge.query.projectile.state`: the flight the node's `projectile` handle names.</summary>
    internal static void StateRow(EvaluationContext context, EvaluationOutputs outputs)
    {
        var states = Installed ?? throw new RuntimeContractException(SourceUnavailableCode, "No projectile table is attached on this machine.");
        if (Environment.CurrentManagedThreadId != states._threadId)
            throw new RuntimeContractException(SourceUnavailableCode, "The projectile table is read off its own thread.");
        if (!context.TryRuntimeHandle(ProjectileStateContract.ProjectileSlot, out var handle))
            throw new RuntimeContractException(MissingFieldCode, "projectile");
        states.Answer(handle, outputs);
    }

    /// <summary>The answer for one handle value, or the refusal it gets. Checked in order: the value is a handle of
    /// this world, the kernel still holds it for this provider, and it is one of this table's flight handles.</summary>
    public void Answer(RuntimeHandle handle, EvaluationOutputs outputs)
    {
        if (handle.WorldEpoch != _kernel.WorldEpoch)
            throw new RuntimeContractException(StaleWorldCode, "The projectile handle belongs to another world.");
        if (!_kernel.IsHandleLive(_owner, handle))
            throw new RuntimeContractException(RuntimeAbiCodes.StaleHandle, "The projectile handle is no longer held.");
        for (var slot = 0; slot < _entries.Length; slot++)
        {
            ref var entry = ref _entries[slot];
            if (!entry.Held || entry.Handle != handle) continue;
            if (!entry.Alive)
            {
                outputs.Null(0);
                outputs.Null(1);
                outputs.Boolean(2, false);
                return;
            }
            outputs.Vector(0, entry.X, entry.Y, entry.Z);
            outputs.Vector(1, entry.Dx, entry.Dy, entry.Dz);
            outputs.Boolean(2, true);
            return;
        }
        throw new RuntimeContractException(RuntimeAbiCodes.HandleKind, "The handle is not a projectile handle.");
    }
}
