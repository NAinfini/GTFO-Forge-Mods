using System;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The execute half of the door open/close row, `forge.action.map.door_open`, whose `operation` member
/// picks opening or closing. It acts on the doors a plan named in the one `gtfo.map_object` namespace, through the native entry points the door observation already reads — no
/// second door table, no plan object, and no handle.
///
/// What the native side can carry is narrow and every other request is refused by name instead of imitated:
///
/// - Opening asks the door's own interaction entry, or its own force entry when the plan chose `force`.
///   The interaction entry toggles, so the door's own status decides first: a door that already reads open is
///   left alone and reported as the state it is in, never toggled shut by a second open.
/// - Closing asks the same toggling entry only for a door that reads open. `bypass_policy` is not read: the
///   interaction entry is the one close entry the game has.
///
/// A door's alarm is **not** a row here: the rulings deleted the alarm action, because a door's alarm is the
/// chained puzzle instance its lock component holds and `forge.action.map.scan_state` already writes that same
/// instance kind. A plan reads the puzzle reference from `forge.query.map.door_state`'s `puzzle` output and
/// feeds it to the scan row, so one native write keeps one card.
///
/// Every refusal is a code, never a silent success: a reference that is not a door, a reference the kernel no
/// longer answers for, an instance the provider's own table no longer maps back to that reference, a door whose
/// address changed under it, a door the game's own status refuses, and a bypass policy no native entry carries
/// are all different rows.</summary>
internal sealed class DoorActionCommands
{
    private const string Prefix = EntityKinds.MapObject + ":";
    /// <summary>The one category of that kind these rows act on. A terminal is a map object too, and a plan that
    /// wired one into `doors` is refused rather than read through the door reader.</summary>
    private const string DoorCategory = "door";

    /// <summary>The native instance behind one recipient reference, or null when this world cannot resolve it.
    /// It is a delegate so a registration hands over the session's own lookup and a test can hand over a table;
    /// the caller still verifies the answer against the kernel before anything is written.</summary>
    internal delegate object? Resolver(EntityReference reference);

    /// <summary>One door's own native entry, given what the handler read from the request.</summary>
    internal delegate MapActionOutcome DoorEntry<TRequest>(ref TRequest request, object door, MapObjectReference address);

    /// <summary>What one door step of <see cref="Each"/> needs: this half, the request and its entry, handed by
    /// reference so no closure is made.</summary>
    private struct DoorWalk<TRequest>
    {
        internal DoorActionCommands Doors;
        internal TRequest Request;
        internal DoorEntry<TRequest> Entry;
    }

    private static class DoorWalkStep<TRequest>
    {
        internal static readonly TargetStep<DoorWalk<TRequest>> Step = Walk;
    }

    /// <summary>The door damage request, read once before the first door.</summary>
    private struct DoorHit
    {
        internal DoorActionCommands Doors;
        internal eDoorDamageType Type;
        internal Vector3 Origin;
        internal global::Agents.Agent Instigator;
    }

    private static readonly TargetStep<DoorHit> DamageStep = Damage;

    /// <summary>Opening or closing, read once before the first door.</summary>
    private readonly record struct DoorMove(bool Close, DoorBypassPolicy Policy);

    private static readonly DoorEntry<DoorMove> MoveEntry = Move;

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly Resolver _resolve;
    private readonly Action<string> _report;

    internal DoorActionCommands(IForgeEngine kernel, Func<bool> ready, Resolver resolve, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Doors the native entry was invoked for.</summary>
    internal int Submitted { get; private set; }
    /// <summary>Doors that already read as the requested state, so the row committed without a write.</summary>
    internal int Settled { get; private set; }
    internal int Refused { get; private set; }
    internal int Unknown { get; private set; }

    /// <summary>Whether a reference names a door of the one map-object namespace. Only the address's own first
    /// segment is read here; the full grammar is the category's own parser's job.</summary>
    internal static bool IsDoor(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var text = id.AsSpan(Prefix.Length);
        int slash = text.IndexOf('/');
        return slash > 0 && text[..slash].SequenceEqual(DoorCategory);
    }

    /// <summary>The address a door reference carries, read back with the door category's own grammar. A
    /// five-segment address of that category with its fixed key is this provider's; every other string — another
    /// category, another shape, a coordinate that is not a plain decimal, a key the category does not use — is
    /// not.</summary>
    internal static MapObjectReference? Address(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return MapObjectDoorAddress.TryParse(id[Prefix.Length..]);
    }

    /// <summary>The native instance behind one door reference, as a registration hands it to the handler: the
    /// address the reference carries, resolved through the one door reader this assembly already owns. It is the
    /// same lookup the session's own category resolver makes for a door, so the action layer adds no second way
    /// to find a door; a reference of another category, of another shape, or one this world no longer holds
    /// answers null, and the caller verifies the answer against the kernel before anything is written.</summary>
    internal static object? ResolveByAddress(EntityReference? reference)
        => Address(reference) is { } address ? DoorObservation.Resolve(address) : null;

    /// <summary>One door behind one recipient reference, checked the same way for every row. The kernel is the
    /// authority on whether the reference is still for this world; the address text is read with the door
    /// category's own grammar; the instance the reference resolves to has to be the instance the kernel itself
    /// resolves that reference back to — a lookup that answers with a different door is refused rather than
    /// written to — and the instance still has to read as the address the reference was built from, so a door
    /// whose entrance zone changed under the same reference is no longer the door the plan named.</summary>
    internal LG_SecurityDoor? Resolve(EntityReference? reference, out string refusal)
    {
        var instance = ResolveCore(reference, out refusal);
        if (instance is LG_SecurityDoor security) return security;
        if (instance != null) refusal = ResultCode.UnsupportedRecipient;
        return null;
    }

    /// <summary>One validation path for all concrete door cores; actions then require their native receiver. A
    /// reference that does not name a door is `unsupported-recipient`; one the kernel no longer answers for, one
    /// the resolver cannot find, or an instance that no longer reads as the address it was resolved through is
    /// `stale-entity`; an instance the kernel resolves back to another reference is `door-identity-mismatch`.</summary>
    internal object? ResolveCore(EntityReference? reference, out string refusal)
    {
        refusal = "";
        if (reference == null || !IsDoor(reference)) { refusal = ResultCode.UnsupportedRecipient; return null; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = ResultCode.StaleEntity; return null; }
        if (Address(reference) is not { } address) { refusal = ResultCode.UnsupportedRecipient; return null; }
        object? door;
        try { door = _resolve(reference); }
        catch (Exception error)
        {
            _report("map.door-resolve-exception: " + error.GetType().Name);
            refusal = ResultCode.StaleEntity;
            return null;
        }
        if (door == null) { refusal = ResultCode.StaleEntity; return null; }
        if (door is not (LG_SecurityDoor or LG_WeakDoor)) { refusal = ResultCode.UnsupportedRecipient; return null; }
        if (_kernel.ResolveEntityInstance(EntityKinds.MapObject, door) is not { } verified || verified != reference)
        { refusal = ResultCode.DoorIdentityMismatch; return null; }
        if (!DoorObservation.IsCurrentAddress(door, address)) { refusal = ResultCode.StaleEntity; return null; }
        return door;
    }

    /// <summary>The same check in the form the loops use: the instance and the address it was resolved through,
    /// or the one code that refused it.</summary>
    private bool TryResolve(EntityReference target, out object door, out MapObjectReference address,
        out string refusal)
    {
        door = null!;
        address = null!;
        if (ResolveCore(target, out refusal) is not { } resolved) return false;
        if (Address(target) is not { } parsed) { refusal = ResultCode.UnsupportedRecipient; return false; }
        door = resolved;
        address = parsed;
        return true;
    }

    /// <summary>Opening. `bypass_policy` is checked once for the whole command, because a command
    /// that cannot be carried out as asked must not half-apply: a policy that is neither of the catalog's two
    /// members is refused before any door is read.</summary>
    private Outcome Open(CommandContext context)
    {
        if (!Authorized()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        DoorBypassPolicy? requestedPolicy = context.Text(DoorActionContract.BypassPolicy) switch
        {
            null or "respect" => DoorBypassPolicy.Respect,
            "force" => DoorBypassPolicy.Force,
            _ => null
        };
        if (requestedPolicy is not { } policy) return Outcome.Rejected(ResultCode.DoorUnknownPolicy);
        return Each(context, new DoorMove(false, policy), MoveEntry, Blocks.ActionMapDoorOpen.RowTargetCount);
    }

    /// <summary>Closing. It reads no structural parameter: the door's own interaction entry is the one close
    /// entry the game has.</summary>
    private Outcome Close(CommandContext context)
    {
        if (!Authorized()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        return Each(context, new DoorMove(true, DoorBypassPolicy.Respect), MoveEntry, Blocks.ActionMapDoorOpen.RowTargetCount);
    }

    private static MapActionOutcome Move(ref DoorMove move, object door, MapObjectReference address) => door switch
    {
        LG_SecurityDoor security => move.Close ? DoorActions.Close(security, address) : DoorActions.Open(security, address, move.Policy),
        LG_WeakDoor weak => move.Close ? DoorActions.Close(weak, address) : DoorActions.Open(weak, address, move.Policy),
        _ => MapActionOutcome.Refused(ResultCode.UnsupportedRecipient)
    };

    /// <summary>This side is the host of a runtime that is ready for a native write. The action skeleton refuses a
    /// command that did not arrive on the host; this is the session's own readiness.</summary>
    internal bool Authorized() => _ready() && Session.IsHost && _kernel.Lifecycle.IsReadyHost;

    /// <summary>A door row whose native entry is one <see cref="MapActionOutcome"/> per door; the lock row of
    /// <see cref="DoorTerminalActions"/> goes through it as well. Each recipient is resolved and acted on in order,
    /// independently of the rows before it, through the one multi-target skeleton.</summary>
    internal Outcome Each<TRequest>(CommandContext context, TRequest request, DoorEntry<TRequest> entry, int countColumn)
    {
        var walk = new DoorWalk<TRequest> { Doors = this, Request = request, Entry = entry };
        return context.EachTarget(ref walk, DoorWalkStep<TRequest>.Step, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: countColumn);
    }

    /// <summary>One door of <see cref="Each"/>: resolved, its entry run, and the layer's decision written as its row.
    /// An entry that was invoked is a confirmed commit, a door that already reads as the requested state is a
    /// confirmed commit that wrote nothing, a refusal is a rejected row with the layer's code, and a call that threw
    /// after it was entered is an unknown commit — the write may or may not have landed, so the door is not retried.
    /// The doors after it are still attempted.</summary>
    private static void Walk<TRequest>(ref DoorWalk<TRequest> walk, EntityReference target, int index, RowWriter rows)
    {
        var doors = walk.Doors;
        if (!doors.TryResolve(target, out var door, out var address, out var refusal))
        { rows.End(Status.Rejected, Commit.None, refusal); return; }
        MapActionOutcome outcome;
        try { outcome = walk.Entry(ref walk.Request, door, address); }
        catch (Exception error)
        {
            doors.Unknown++;
            doors._report("map.door-commit-exception: " + error.GetType().Name);
            rows.End(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
            return;
        }
        // A native entry that threw inside its own try, or whose readback did not show the write, leaves the commit
        // unknown: the write may or may not have landed.
        if (outcome.Code is ResultCode.NativeCommitException or ResultCode.ReadbackUnconfirmed)
        {
            doors.Unknown++;
            rows.End(Status.Failed, Commit.Unknown, outcome.Code);
            return;
        }
        switch (outcome.Commit)
        {
            case MapActionCommit.Issued:
                doors.Submitted++;
                rows.End(Status.Succeeded, Commit.Confirmed, outcome.Code);
                return;
            case MapActionCommit.AlreadyInState:
                doors.Settled++;
                rows.End(Status.Succeeded, Commit.Confirmed, outcome.Code);
                return;
            default:
                doors.Refused++;
                rows.End(Status.Rejected, Commit.None, outcome.Code);
                return;
        }
    }

    /// <summary>The shared combat-damage row for authored interior doors. A weak door's native entry carries a
    /// damage enum rather than an arbitrary number: build 20403457 maps its six members to fixed damage values.
    /// Only an exact canonical kind/value pair is submitted, so the handler never rounds or silently changes the
    /// requested amount. Security doors are explicit non-receivers because their override is an empty body.</summary>
    internal CommandResult HandleDamage(CommandContext context)
    {
        if (!Authorized()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Present(DoorActionContract.DamageSource)
            && !context.TryEntity(DoorActionContract.DamageSource, out _))
            return Outcome.Rejected(ResultCode.SourceInvalid);
        if (context.Present(DoorActionContract.DamageLimb)) return Outcome.Rejected(ResultCode.LimbUnsupported);
        if (context.Present(DoorActionContract.DamagePrecision)) return Outcome.Rejected(ResultCode.PrecisionUnsupported);
        if (context.Present(DoorActionContract.DamageStagger)) return Outcome.Rejected(ResultCode.StaggerUnsupported);

        string? kind = context.Text(DoorActionContract.DamageKind);
        double amount = context.Number(DoorActionContract.DamageAmount) ?? double.NaN;
        if (kind is not ("direct" or "melee" or "explosion")) return Outcome.Rejected(ResultCode.DamageKindUnsupported);
        if (!double.IsFinite(amount) || NativeDamage(kind, amount) is not { } damageType)
            return Outcome.Rejected(ResultCode.DamageAmountNativeMismatch);
        if (!context.TryEntity(DoorActionContract.DamageInstigator, out var instigator))
            return Outcome.Rejected(ResultCode.InstigatorMissing);

        global::Agents.Agent nativeInstigator;
        try
        {
            if (context.EntityInstance(instigator) is not global::Agents.Agent agent || agent.WasCollected)
                return Outcome.Rejected(ResultCode.InstigatorMissing);
            nativeInstigator = agent;
        }
        catch (Exception containedError) { MapLog.Contained("door-action-commands.handle-damage", containedError); return Outcome.Rejected(ResultCode.InstigatorMissing); }

        Vector3 origin;
        if (context.Present(DoorActionContract.DamageHitOrigin))
        {
            if (NativeVector(context.Vector(DoorActionContract.DamageHitOrigin)) is not { } point)
                return Outcome.Rejected(ResultCode.HitOriginInvalid);
            origin = point;
        }
        else
        {
            try { origin = nativeInstigator.Position; }
            catch (Exception containedError) { MapLog.Contained("door-action-commands.handle-damage", containedError); return Outcome.Rejected(ResultCode.HitOriginInvalid); }
            if (!ForgeCore.Vec.Finite(origin)) return Outcome.Rejected(ResultCode.HitOriginInvalid);
        }

        var hit = new DoorHit { Doors = this, Type = damageType, Origin = origin, Instigator = nativeInstigator };
        return context.EachTarget(ref hit, DamageStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: Blocks.ActionCombatDamage.RowTargetCount);
    }

    private static void Damage(ref DoorHit hit, EntityReference target, int index, RowWriter rows)
    {
        var doors = hit.Doors;
        if (!doors.TryResolve(target, out var door, out var address, out var refusal))
        { rows.End(Status.Rejected, Commit.None, refusal); return; }
        switch (door)
        {
            case LG_WeakDoor weak: doors.DamageOne(weak, address, hit.Type, hit.Origin, hit.Instigator, rows); return;
            case LG_SecurityDoor: doors.RefusedRow(rows, ResultCode.DoorNotDamageable); return;
            default: doors.RefusedRow(rows, ResultCode.UnsupportedRecipient); return;
        }
    }

    private void DamageOne(LG_WeakDoor door, MapObjectReference address, eDoorDamageType damageType, Vector3 origin,
        global::Agents.Agent instigator, RowWriter rows)
    {
        double? before = DoorTerminalDerivations.WeakDoorHealth(door, max: false);
        double? maximum = DoorTerminalDerivations.WeakDoorHealth(door, max: true);
        if (before is not > 0 || maximum is not > 0 || before > maximum)
        { RefusedRow(rows, ResultCode.MissingHealthReceiver); return; }
        try
        {
            var outcome = DoorActions.Damage(door, address, damageType, origin, instigator);
            if (outcome.Commit != MapActionCommit.Issued) { RefusedRow(rows, outcome.Code); return; }
            Submitted++;
        }
        catch (Exception error)
        {
            _report("map.door-damage-commit-exception: " + error.GetType().Name);
            UnknownRow(rows, ResultCode.NativeCommitException);
            return;
        }

        try
        {
            // The native damage entry has been entered: a door that is no longer the one the address names is a
            // receiver that changed during the commit, not a hit whose effect was merely not seen.
            if (door.WasCollected || !DoorObservation.IsCurrentAddress(door, address))
            { UnknownRow(rows, ResultCode.ReceiverChangedDuringCommit); return; }
            double? after = DoorTerminalDerivations.WeakDoorHealth(door, max: false);
            if (door.LastStatus != eDoorStatus.Destroyed && (after == null || !(after < before)))
            { UnknownRow(rows, ResultCode.DamageNotObserved); return; }
        }
        catch (Exception containedError) { MapLog.Contained("door-action-commands.damage-one", containedError); UnknownRow(rows, ResultCode.DamageNotObserved); return; }
        rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.DoorDamageCommitted);
    }

    private void RefusedRow(RowWriter rows, string code)
    {
        Refused++;
        rows.End(Status.Rejected, Commit.None, code);
    }

    private void UnknownRow(RowWriter rows, string code)
    {
        Unknown++;
        rows.End(Status.Failed, Commit.Unknown, code);
    }

    /// <summary>The exact fixed-value mapping implemented by
    /// `LG_WeakDoor_Destruction.GetDamageValueFromType` in build 20403457.</summary>
    private static eDoorDamageType? NativeDamage(string kind, double amount) => (kind, amount) switch
    {
        ("direct", 1d) => eDoorDamageType.EnemyLight,
        ("direct", 2d) => eDoorDamageType.EnemyHeavy,
        ("melee", 0.5d) => eDoorDamageType.MeleeWeaponMinimal,
        ("melee", 1.5d) => eDoorDamageType.MeleeWeaponLight,
        ("melee", 4d) => eDoorDamageType.MeleeWeaponHeavy,
        ("explosion", 100d) => eDoorDamageType.Explosion,
        _ => null
    };

    private static Vector3? NativeVector((double X, double Y, double Z)? value)
    {
        if (value is not { } point || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !double.IsFinite(point.Z)) return null;
        var vector = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
        return ForgeCore.Vec.Finite(vector) ? vector : null;
    }


    /// <summary>An unwritten operation is its first member, opening.</summary>
    internal CommandResult HandleOpen(CommandContext context) => context.Text(DoorActionContract.Operation) switch
    {
        null or "open" => Open(context),
        "close" => Close(context),
        _ => Outcome.Rejected(ResultCode.DoorUnknownPolicy)
    };
}
