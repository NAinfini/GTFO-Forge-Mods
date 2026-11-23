using System;
using ForgeEngine.Framework;
using Agents;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>Host-side weapon combat facts. One shot is counted per `Fire` body, and the native hits that shot
/// causes are correlated to it by the open shot this observer keeps; nothing here applies damage or reads a
/// target's health, and every published reference is checked through Runtime before it leaves.
///
/// The observer is also where one shot's own tally lives, because the shot is the unit both rows are about: a
/// `hit_candidate` is billed to the shot that is open, and the `shot_resolved` outcome is the same tally read
/// once the firing body has returned. A shot's own context — where it ended, how far it travelled, which part it
/// reached and whether it came through geometry — is kept beside the tally so the resolution and the read that
/// follows it describe the same impact.
///
/// Every hit runs through here, but native impact reads are skipped while no row consumes them. The shot, tally
/// and impact records are structs, the rows' gates are taken once when the observer is built, an impact
/// record is only measured while a plan reads the hit-context row, and a published payload is written into one
/// reused buffer.</summary>
internal sealed class WeaponCombatObserver : IHitContextSource
{
    /// <summary>How many impact records the hit-context table holds. While a loaded plan reads the hit-context row, a
    /// record is written for every hit on a body this machine can name, and a plan reads the one its candidate named
    /// by hit number, so the table only has to span the hits of the last few moments; the record 256 hits older than
    /// a new one is dropped, and reported, when the new one takes its slot.
    /// </summary>
    internal const int HitContextCapacity = 256;

    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<IForgeEngine?> _kernel;
    private readonly WeaponLog _log;
    // WHY: P5 — the kernel's own refreshed gates, taken once, so a hit or a shot reads one field instead of a lookup.
    private readonly RuntimeSubscriptionGate _shotGate, _hitGate, _shotResolvedGate;
    /// <summary>Open while a loaded plan reads `forge.query.combat.hit_context` through a query step: the impact
    /// record is that read's answer and nothing else, so no record is measured or kept while no plan can ask.</summary>
    private readonly RuntimeSubscriptionGate _hitContextGate;
    private Shot? _open;
    private Shot? _firing;
    private Weapon? _firingInstance;
    private Tally _tally;
    private long _sequence;
    /// <summary>The number of the last hit this machine observed: every hit takes the next one, and it is both the
    /// hit candidate's `hit` port and the slot its impact record lives in (number modulo the capacity).</summary>
    private long _hits;
    /// <summary>The impact context of the hits this machine observed. A record is the answer
    /// `forge.query.combat.hit_context` reads; it is not a fact and is published nowhere. Every hit has its own record
    /// under its own number — the pellets of one shotgun shot are as many records — so a plan reads exactly the hit
    /// that triggered it, also after a wait. The table is a fixed ring dropped when the world changes, because every
    /// body in it belongs to the world that produced it.
    /// </summary>
    private readonly HitRecord[] _ring = new HitRecord[HitContextCapacity];
    private long _evicted;
    private long _world = -1;
    private long _unrecordedReported = -1, _unownedReported = -1;
    /// <summary>The search distance the native hit body was entered with, written by the prefix beside the postfix
    /// that reads it. One call, one pair: the prefix stores it and the postfix that follows the same invocation
    /// consumes it, so a hit can never be described with another hit's value.</summary>
    private float _searchDistance;
    /// <summary>Set once the hit body has been entered, so a postfix that runs without its own prefix — a patch
    /// order this build does not produce, but which no code here may assume — describes the impact it read rather
    /// than the previous hit's search distance.</summary>
    private bool _searchKnown;

    /// <summary>The shot a native hit belongs to: the firing equipment life and the 1-based number of the shot
    /// inside that life. A shot is opened by its `Fire` and stays open until the next `Fire`, because a shotgun
    /// fires its several pellets from one body and the game resolves those hits after that body has returned.</summary>
    internal readonly record struct Shot(EntityReference Equipment, long Index);

    // Attribution lives only inside the native damage-producing call. A previous shot must never claim a
    // later melee hit, explosion or scripted damage. Nested calls restore the exact enclosing attribution.
    internal readonly record struct Attribution(EntityReference? Source, EntityReference? Equipment);

    internal static Attribution? EnterAttribution(EntityReference? source, EntityReference? equipment)
    {
        var previousSource = CombatShotAttribution.CurrentSource;
        if (source == null && previousSource == null) return null;
        var previous = CombatShotAttribution.EquipmentOf(previousSource);
        if (previousSource != null) CombatShotAttribution.Forget(previousSource);
        if (source != null && equipment != null) CombatShotAttribution.RecordShot(source, equipment);
        return new Attribution(previousSource, previous);
    }

    internal static void LeaveAttribution(Attribution? scope)
    {
        if (scope is not { } value) return;
        if (CombatShotAttribution.CurrentSource is { } current) CombatShotAttribution.Forget(current);
        if (value.Source != null && value.Equipment != null) CombatShotAttribution.RecordShot(value.Source, value.Equipment);
    }

    internal Attribution? Attribute(Item? weapon)
        => EnterAttribution(_adapter.OwnerOf(weapon), _adapter.CurrentEntityOf(weapon));

    /// <summary>What one shot reached: how many impacts reached a damageable target, how many only reached world
    /// geometry, and the first impact's own reading. The first impact is the one a shot's outcome describes,
    /// because that is the impact the game itself worked the damage out from.</summary>
    internal readonly record struct Tally(int Targets, int Worlds, EntityReference? Target,
        double PositionX, double PositionY, double PositionZ, double NormalX, double NormalY, double NormalZ);

    /// <summary>No impact at all yet: the reading a shot starts from.</summary>
    private static readonly Tally Empty = default;

    /// <summary>One impact as the hit-context row keeps it: its hit number, the references the read is checked against,
    /// the measured readings, and the offset the hit point had from the target at the moment of impact, which is what
    /// the anchored position is recomputed from.</summary>
    private struct HitRecord
    {
        internal bool Live;
        internal long Hit;
        internal EntityReference Source, Target;
        internal double Backstab, Angle, Distance, Falloff;
        internal bool ThroughWall;
        internal double X, Y, Z;
        internal bool Anchored;
        internal double AnchorX, AnchorY, AnchorZ;
    }

    internal WeaponCombatObserver(EquipmentNativeAdapter adapter, Func<IForgeEngine?> kernel, WeaponLog log)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _shotGate = adapter.GateOf(ModuleDefinition.ShotCommittedBinding);
        _hitGate = adapter.GateOf(ModuleDefinition.HitCandidateBinding);
        _hitContextGate = adapter.GateOf(HitContextContract.BindingId);
        _shotResolvedGate = adapter.GateOf(CombatPrimitiveContract.ShotResolvedBinding);
    }

    /// <summary>Set while a caller is submitting hits that must not feed the hit triggers — the
    /// `run_hit_triggers = false` choice of `forge.action.combat.hitscan_shot`. The hit is still tallied to the
    /// open shot, because the shot's own outcome is a fact about the shot; only the candidate publication is
    /// skipped, which is exactly what "this hit feeds no trigger" means. The session is single-threaded and the
    /// flag is set and cleared around one native call, so no hit of another caller can observe it.</summary>
    internal static bool SuppressTriggers;

    /// <summary>Greater than zero while a deployed sentry's firing update runs (entered by the sentry fire hook around
    /// <c>UpdateFireMaster</c>): the bullet routine is shared with the player weapons, and nothing reached inside this
    /// window belongs to the open shot. Single-threaded, entered and left in pairs by the hook's prefix and finalizer.</summary>
    internal static int DeployableFiringDepth;

    /// <summary>A hit with no open shot is treated as unobservable rather than as a shot of its own: a deployable's
    /// own firing reaches the same bullet routine without any weapon `Fire`, so it must not be billed to a player.</summary>
    internal void Hit(Weapon.WeaponHitData? data)
    {
        // A sentry's own firing body is the caller here, and the shot left open by the last player `Fire` is not its
        // shot: billing the sentry's bullets to it would publish them under that player's weapon, possibly one the
        // world no longer holds.
        if (DeployableFiringDepth > 0) return;
        var kernel = Ready();
        if (kernel == null || data == null) return;
        if (_open is not { } shot)
        {
            _log.Reason(RuntimeLogLevel.Trace, WeaponLogCodes.FactSkipped, "no-open-shot",
                binding: ModuleDefinition.HitCandidateBinding);
            return;
        }
        // These are the Runtime's live gates, read on every hit. Shot resolution is the tally's consumer even
        // when neither hit row is observed; without any consumer, do not read rayHit or climb its collider chain.
        var recording = !Closed(_hitContextGate);
        var publishing = !SuppressTriggers && !Closed(_hitGate);
        if (!recording && !publishing && Closed(_shotResolvedGate)) return;
        _adapter.RefreshEquipment(shot.Equipment);
        var weapon = _adapter.WeaponOf(shot.Equipment);
        if (weapon == null || _adapter.CurrentEntityOf(weapon) != shot.Equipment) return;
        // The ray hit is a by-value struct on the data, so a miss or a non-collider hit reads as a zero point
        // rather than throwing. The target is resolved from the hit object's own native type through its owning
        // domain; when that fails — world geometry, a deployable, a kind no installed package resolves — the shot
        // is tallied as a world hit and the candidate leaves the target port out rather than publishing a null.
        // The equipment comes from the open shot itself and never from the hit object: this shot is what the
        // candidate belongs to, so a `gear-block` mount can claim it, and it is always present because a candidate
        // is only ever published inside a firing window. The limb stays null on the candidate: it exists before
        // any damage is worked out, so no limb index has been decided yet.
        var point = data.rayHit.point;
        double x = point.x, y = point.y, z = point.z;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
        var shooter = data.owner;
        // A suppressed candidate still contributes to an observed shot's outcome. With only shot resolution
        // listening, the target is enough: shooter identity and body geometry belong to the other two rows.
        if (!recording && !publishing)
        {
            Trace(shot, data, x, y, z, _adapter.HitTarget(data));
            return;
        }
        var owner = _adapter.OwnerOf(shooter);
        var impact = _adapter.Impact(data);
        var target = impact.Target;
        Trace(shot, data, x, y, z, target);
        var hit = checked(++_hits);
        // The impact record answers the hit-context read and nothing else, so it is measured and kept only while a
        // loaded plan reads that row (contract §4.5 P5).
        if (recording) Record(hit, owner, impact, data, x, y, z);
        if (!publishing) return;
        // A rejected candidate is a defect in this provider's own payload; the kernel writes it as `event.rejected`
        // and a queued one as `trigger.fired`, so neither is recorded twice here.
        PublishHit(kernel, hit, owner, shot.Equipment, shot.Index, target, x, y, z);
    }

    /// <summary>One `hit_candidate`: <c>{ source, equipment, [target,] limb = null, position, hit }</c>. The target port
    /// is left out, not written null, when the hit named no target.</summary>
    private void PublishHit(IForgeEngine kernel, long hit, EntityReference? source, EntityReference equipment, long shot,
        EntityReference? target, double x, double y, double z)
    {
        var id = "gtfo.weapon.hit:" + Number(kernel.WorldEpoch) + ":" + Number(hit);
        var fact = _adapter.Begin(_hitGate, kernel.WorldEpoch);
        HitCandidateOutputs(fact.Values, source, equipment, target, x, y, z, hit);
        fact.Raise(id, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick), "gtfo.weapon.shot:" + equipment.Id + ":" + Number(shot));
    }

    /// <summary>The candidate row's ports, shared with the projectile flights that raise the same row. A hit whose
    /// context this machine does not record (a projectile flight) carries a null hit number.</summary>
    internal static void HitCandidateOutputs(EvaluationOutputs values, EntityReference? source, EntityReference equipment,
        EntityReference? target, double x, double y, double z, long? hit)
    {
        values.Entity(Blocks.TriggerCombatHitCandidate.OutSource, source);
        values.Entity(Blocks.TriggerCombatHitCandidate.OutEquipment, equipment);
        if (target != null) values.Entity(Blocks.TriggerCombatHitCandidate.OutTarget, target);
        values.Null(Blocks.TriggerCombatHitCandidate.OutLimb);
        values.Vector(Blocks.TriggerCombatHitCandidate.OutPosition, x, y, z);
        if (hit is { } number) values.Integer(Blocks.TriggerCombatHitCandidate.OutHit, number);
        else values.Null(Blocks.TriggerCombatHitCandidate.OutHit);
    }

    /// <summary>The head of one firing body, taken before it runs: the native hit routine has no context of its
    /// own, so the shot's tally starts here and the postfix reads it once the body is done. A body with no open
    /// shot (an item this machine does not answer for) clears the tally instead of carrying the previous shot's,
    /// which is what keeps an unobservable shot from being billed to the one before it.</summary>
    internal void BeginFire(Weapon? weapon)
    {
        // The native bullet routine runs inside the firing body, so the shot has to be open before the body runs:
        // opening it after the body would bill this shot's hits to the previous shot (or to none) and then clear them.
        _firing = weapon == null ? null : Open(weapon);
        _firingInstance = _firing == null ? null : weapon;
    }

    /// <summary>The tail of a firing body whose original did not run: the shot opened for it never happened.</summary>
    internal void AbandonFire()
    {
        _open = null; _firing = null; _firingInstance = null; _tally = Empty;
    }

    /// <summary>The tail of one firing body: the shot it produced, or null when this body's shot is not one this
    /// machine answers for. The shot stays open afterwards — a later hit belongs to the later shot, and the shot
    /// that just resolved keeps its own tally for the resolution.</summary>
    internal Shot? ResolveFire(Weapon? weapon)
    {
        var shot = _firing;
        var invocation = _firingInstance;
        _firing = null;
        _firingInstance = null;
        if (shot is not { } fired || weapon is null) return null;
        if (_open is { } open && open != fired) return null;
        // A collected wrapper can only complete its own captured invocation. A live wrapper must still
        // resolve to that exact life; another weapon callback cannot claim this shot.
        if (weapon.WasCollected ? !ReferenceEquals(weapon, invocation)
            : _adapter.CurrentEntityOf(weapon) != fired.Equipment) return null;
        // The callback wrapper can have expired. Refresh the captured life through its current native slot;
        // a replacement or a new owner life must never inherit this shot.
        _adapter.RefreshEquipment(fired.Equipment);
        if (_adapter.WeaponOf(fired.Equipment) == null) return null;
        return fired;
    }

    /// <summary>What one resolved shot reached, for the shot-resolution fact.</summary>
    internal Tally HitsOf(Shot shot) => _open == shot ? _tally : Empty;

    // -----------------------------------------------------------------------------------------------------------
    // Hit context: the impact record `forge.query.combat.hit_context` is answered from
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>The refusal code for a read this machine may not make at all: no authority over the world, or a
    /// reference no shooter can be named for.</summary>
    internal const string UnavailableCode = "hit-context-unavailable";

    /// <summary>
    /// The search distance the native hit body was entered with, taken by the prefix that runs before the body and
    /// consumed by the postfix that describes the impact it produced. A non-zero value is the one native signal
    /// this build gives that the body was asked to look past the first collider it reached.
    /// </summary>
    internal void HitSearched(float additionalDis)
    {
        _searchDistance = additionalDis;
        _searchKnown = true;
    }

    /// <summary>
    /// One impact's own context, recorded under its hit number for the read that follows it. A hit this machine
    /// cannot name a shooter or a target for, whose target body carries no facing to measure a from-behind relation
    /// against, or whose falloff range is unusable is recorded nowhere — the read then refuses by name instead of
    /// answering a relation the game never had. When the hit's slot still holds an older record, that record is
    /// dropped and the drop is reported.
    /// </summary>
    private void Record(long hit, EntityReference? source, in EquipmentNativeAdapter.RayImpact impact,
        Weapon.WeaponHitData data, double x, double y, double z)
    {
        if (source == null || impact.Target == null) return;
        if (!impact.HasFacing || !Direction(data, x, y, z, out var dx, out var dy, out var dz)) return;
        var distance = Distance(data, x, y, z);
        if (Falloff(data.damageFalloff.x, data.damageFalloff.y, distance) is not { } falloff) return;
        Normal(data, out var nx, out var ny, out var nz);
        Store(hit, source, impact, dx, dy, dz, nx, ny, nz, distance, falloff, ThroughWall(), x, y, z);
    }

    /// <summary>A client's own impact, admitted by <see cref="ClientWeaponReport"/>: the same two rows as <see cref="Hit"/>, measured from
    /// the shooter's reported eye and point against the host's reading of the target's body. The shot number is the shooter's own count.
    /// False when nothing listens.</summary>
    internal bool HitReported(EntityReference source, EntityReference equipment, int shot,
        in EquipmentNativeAdapter.RayImpact impact, UnityEngine.Vector3 point, UnityEngine.Vector3 normal, UnityEngine.Vector3 eye, bool through, float falloffStart, float falloffEnd)
    {
        var kernel = Ready();
        if (kernel == null) return false;
        var recording = !Closed(_hitContextGate);
        var publishing = !Closed(_hitGate);
        if (!recording && !publishing) return false;
        var hit = checked(++_hits);
        double x = point.x, y = point.y, z = point.z;
        double dx = x - eye.x, dy = y - eye.y, dz = z - eye.z;
        var distance = Math.Sqrt(Square(dx) + Square(dy) + Square(dz));
        if (recording && impact.Target != null && impact.HasFacing && Unit(ref dx, ref dy, ref dz)
            && Falloff(falloffStart, falloffEnd, distance) is { } falloff)
        {
            double nx = normal.x, ny = normal.y, nz = normal.z;
            if (!Unit(ref nx, ref ny, ref nz)) { nx = 0d; ny = 0d; nz = 1d; }
            Store(hit, source, impact, dx, dy, dz, nx, ny, nz, distance, falloff, through, x, y, z);
        }
        if (!publishing) return true;
        PublishHit(kernel, hit, source, equipment, shot, impact.Target, x, y, z);
        return true;
    }

    private void Store(long hit, EntityReference source, in EquipmentNativeAdapter.RayImpact impact,
        double dx, double dy, double dz, double nx, double ny, double nz, double distance, double falloff, bool through, double x, double y, double z)
    {
        var target = impact.Target!;
        ref var slot = ref _ring[Slot(hit)];
        if (slot.Live) Evict(ref slot);
        // The anchor is the offset the hit point had from the target's body at the moment of impact. A body this
        // machine cannot place answers no offset, and the anchored port then reports a missing value rather than a
        // point measured against a position nobody read.
        slot = new HitRecord
        {
            Live = true,
            Hit = hit,
            Source = source,
            Target = target,
            Backstab = Backstab(impact.FacingX, impact.FacingY, impact.FacingZ, dx, dy, dz),
            Angle = Angle(dx, dy, dz, nx, ny, nz),
            Distance = distance,
            ThroughWall = through,
            Falloff = falloff,
            X = x,
            Y = y,
            Z = z,
            Anchored = impact.HasPosition,
            AnchorX = x - impact.X,
            AnchorY = y - impact.Y,
            AnchorZ = z - impact.Z
        };
    }

    /// <summary>The ring slot one hit number lives in.</summary>
    private static int Slot(long hit) => (int)(hit % HitContextCapacity);

    /// <summary>An older record gives up its slot to a new one: the drop is counted and reported, so a later read of it
    /// refuses by name and the trace says why.</summary>
    private void Evict(ref HitRecord slot)
    {
        slot.Live = false;
        _evicted++;
        if (!_log.Logs(RuntimeLogLevel.Trace)) return;
        _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.HitContextEvicted, new RuntimeLogFacts
        {
            Entity = slot.Target.Id,
            Values = new[] { RuntimeLogValue.Of("capacity", (long)HitContextCapacity), RuntimeLogValue.Of("evicted", _evicted) }
        });
    }

    /// <summary>Every record belongs to the world that produced it.</summary>
    private void ForgetImpacts()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _evicted = 0;
        _hitDiagnostics = 0;
    }

    /// <summary>
    /// The damage falloff this hit's distance earned, derived from the two native facts the game states and one
    /// interpolation. `WeaponHitData.damageFalloff` is the weapon's own falloff range — `x` the distance the
    /// falloff starts at, `y` the distance it reaches its floor at, which is the meaning the site's weapon
    /// calculator and the workshop copy already record for the archetype's `DamageFalloff` — and
    /// `BulletWeapon.s_falloffMin` is the floor itself. Between the two ends the coefficient falls linearly from
    /// one to that floor, and at or before the start it is one. A range that is not a finite pair with
    /// `start <= end`, or a floor the game does not state as a positive number, answers null so the hit is
    /// recorded nowhere rather than answered with an invented coefficient. The linear shape is this project's
    /// established reading of the two values (the site's own `damageFalloff`), not a decoded instruction: what the
    /// game's own body computes between the same two ends is a game-verification point.
    /// </summary>
    private static double? Falloff(double start, double end, double distance)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0d || end < start) return null;
        double floor;
        try { floor = BulletWeapon.s_falloffMin; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponCombatObserver.Falloff", failure); return null; }
        if (!double.IsFinite(floor) || floor <= 0d) return null;
        if (distance <= start) return 1d;
        if (end == start) return floor;
        return Math.Max(floor, 1d - (distance - start) / (end - start));
    }

    /// <summary>
    /// The world position the target's body occupies right now, read from the native object its own domain
    /// registered for the reference — the same door every step reaches another module's entity through — and never
    /// derived from the shot. A reference this machine holds no live object for, a body with no transform, or a
    /// non-finite coordinate answers null, which is what keeps an anchored position from being invented.
    /// </summary>
    private double[]? TargetPosition(EntityReference target)
    {
        var kernel = _kernel();
        if (kernel == null) return null;
        object? instance;
        try { instance = kernel.EntityInstance(target); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponCombatObserver.TargetPosition", failure); return null; }
        if (instance is not Agent agent) return null;
        var transform = agent.transform;
        if (transform == null) return null;
        var position = transform.position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    /// <summary>
    /// The world direction the shot was travelling in when it reached what it hit: the ray's own fired direction
    /// where the game set one, and the direction from the ray's origin to the point it reached otherwise. Both are
    /// fields of the hit data the game itself produced, so the relation is measured against the shot that was
    /// really fired rather than against where the shooter happens to stand when the read runs.
    /// </summary>
    private static bool Direction(Weapon.WeaponHitData data, double px, double py, double pz,
        out double x, out double y, out double z)
    {
        if (data.hasFireDir)
        {
            x = data.fireDir.x; y = data.fireDir.y; z = data.fireDir.z;
        }
        else
        {
            var origin = data.fireAtPos;
            x = px - origin.x; y = py - origin.y; z = pz - origin.z;
        }
        return Unit(ref x, ref y, ref z);
    }

    /// <summary>The surface normal the shot struck, as the game recorded it on the ray hit.</summary>
    private static void Normal(Weapon.WeaponHitData data, out double x, out double y, out double z)
    {
        var normal = data.rayHit.normal;
        x = normal.x; y = normal.y; z = normal.z;
        if (!Unit(ref x, ref y, ref z)) { x = 0d; y = 0d; z = 1d; }
    }

    /// <summary>How far the ray travelled to the point it reached, in metres, as the game's own reading of that
    /// ray. A distance the game did not report as a finite number is the ray's two endpoints measured instead, so
    /// the port always carries the shot that was really fired.</summary>
    private static double Distance(Weapon.WeaponHitData data, double x, double y, double z)
    {
        var travelled = data.rayHit.distance;
        if (float.IsFinite(travelled) && travelled >= 0f) return travelled;
        var origin = data.fireAtPos;
        return Math.Sqrt(Square(x - origin.x) + Square(y - origin.y) + Square(z - origin.z));
    }

    /// <summary>Whether the native hit body was asked to search past the first collider it reached. That search
    /// distance is the one signal this build gives that a shot arrived with geometry between it and its target, and
    /// it is read per invocation: a postfix whose own prefix did not run answers with the previous hit's value only
    /// if it was never cleared, which is why the flag is consumed here.</summary>
    internal bool ThroughWall()
    {
        var value = _searchKnown && _searchDistance > 0f;
        _searchKnown = false;
        _searchDistance = 0f;
        return value;
    }

    /// <summary>The angle between the shot's direction and the surface it struck, in degrees, from the two native
    /// vectors the impact produced. A direction or normal that cannot be normalized answers a right angle rather
    /// than a NaN, because a port of unit `deg` is a number a plan compares.</summary>
    private static double Angle(double dx, double dy, double dz, double nx, double ny, double nz)
        => Math.Acos(Math.Clamp(Dot(dx, dy, dz, nx, ny, nz), -1d, 1d)) * 180d / Math.PI;

    /// <summary>
    /// How much of the hit came from behind the target: `(1 + cos) / 2` between the direction the target's body
    /// faces and the direction the shot travelled, so a shot that entered the target's back — travelling the way
    /// the target faces — is 1, and one that met it head-on is 0.
    /// </summary>
    private static double Backstab(double fx, double fy, double fz, double dx, double dy, double dz)
    {
        var cos = Math.Clamp(Dot(fx, fy, fz, dx, dy, dz), -1d, 1d);
        return (1d + cos) / 2d;
    }

    /// <summary>The vector made unit length in place, or false for a non-finite or zero-length one.</summary>
    private static bool Unit(ref double x, ref double y, ref double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return false;
        var length = Math.Sqrt(Square(x) + Square(y) + Square(z));
        if (length <= double.Epsilon) return false;
        x /= length; y /= length; z /= length;
        return true;
    }

    private static double Square(double value) => value * value;
    private static double Dot(double lx, double ly, double lz, double rx, double ry, double rz) => lx * rx + ly * ry + lz * rz;

    /// <summary>`forge.query.combat.hit_context`'s native half: the record of the hit the number names, or the code
    /// this machine refuses the read by. The read is one slot of the fixed table plus one live read of the target's
    /// position, and nothing else — the geometry was measured when the impact happened, which is the only moment all
    /// of it existed at once, and the anchored position is that measured offset applied to where the target stands
    /// now. A target this machine can no longer name answers a missing anchored position rather than a point
    /// measured against a body nobody read; a hit this machine holds no record of, or whose record names another
    /// shooter or target than the read, is refused by name and never answered with another hit.</summary>
    public bool TryHitContext(EntityReference source, EntityReference target, long hit, out HitContextReading reading, out string code)
    {
        reading = null!;
        if (source == null || target == null || !_adapter.Authoritative()) { code = UnavailableCode; return false; }
        if (hit <= 0) { code = HitContextReads.UnknownHitCode; return false; }
        ref readonly var found = ref _ring[Slot(hit)];
        if (!found.Live || found.Hit != hit || !string.Equals(found.Source.Id, source.Id, StringComparison.Ordinal)
            || !string.Equals(found.Target.Id, target.Id, StringComparison.Ordinal))
        {
            code = HitContextReads.UnknownHitCode;
            return false;
        }
        double[]? anchored = null;
        var current = TargetPosition(target);
        if (current != null && found.Anchored)
            anchored = new[] { current[0] + found.AnchorX, current[1] + found.AnchorY, current[2] + found.AnchorZ };
        reading = new HitContextReading(found.Backstab, found.Angle, found.Distance, found.ThroughWall, found.Falloff,
            new[] { found.X, found.Y, found.Z }, anchored);
        code = "";
        return true;
    }

    private const int HitDiagnosticsPerWorld = 48;
    private int _hitDiagnostics;

    /// <summary>One impact on a map object or on world geometry, with the collider the bullet reached and the layer it
    /// sits on. This is the only record that shows a bullet reached a collider the map-object resolver did not claim.</summary>
    private void DiagnoseHit(Shot shot, UnityEngine.RaycastHit ray, EntityReference? target, double x, double y, double z)
    {
        if (_hitDiagnostics >= HitDiagnosticsPerWorld || !_log.Logs(RuntimeLogLevel.Info)) return;
        _hitDiagnostics++;
        var collider = ray.collider;
        var body = collider == null ? null : collider.gameObject;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.HitObserved, new RuntimeLogFacts
        {
            Entity = target?.Id,
            Values = new[]
            {
                RuntimeLogValue.Of("shot", shot.Index),
                RuntimeLogValue.Of("collider", body == null ? "none" : body.name),
                RuntimeLogValue.Of("layer", (long)(body == null ? -1 : body.layer)),
                RuntimeLogValue.Of("root", body == null ? "none" : body.transform.root.name),
                RuntimeLogValue.Of("target", target?.Id ?? "world"),
                RuntimeLogValue.Of("x", x), RuntimeLogValue.Of("y", y), RuntimeLogValue.Of("z", z)
            }
        });
    }

    /// <summary>The shooter of one resolved shot, re-derived from the shot's own equipment life.</summary>
    internal EntityReference? Owner(Shot shot)
    {
        var weapon = _adapter.WeaponOf(shot.Equipment);
        return weapon == null ? null : _adapter.OwnerOf(weapon);
    }

    /// <summary>
    /// One impact, added to the open shot's tally. The first impact of a shot is the one whose own reading is
    /// kept: it is the impact the game worked the damage out from, and a later pellet of the same shotgun blast
    /// does not describe a different shot.
    /// </summary>
    private void Trace(Shot shot, Weapon.WeaponHitData data, double x, double y, double z, EntityReference? target)
    {
        if (_open != shot) return;
        var ray = data.rayHit;
        double nx = ray.normal.x, ny = ray.normal.y, nz = ray.normal.z;
        if (!double.IsFinite(nx) || !double.IsFinite(ny) || !double.IsFinite(nz)) { nx = 0d; ny = 0d; nz = 0d; }
        var first = _tally.Targets == 0 && _tally.Worlds == 0;
        _tally = target == null
            ? _tally with { Worlds = _tally.Worlds + 1 }
            : _tally with { Targets = _tally.Targets + 1 };
        if (target == null || target.Id.StartsWith(EntityKinds.MapObject, StringComparison.Ordinal)) DiagnoseHit(shot, ray, target, x, y, z);
        if (!first) return;
        _tally = _tally with
        {
            Target = target,
            PositionX = x,
            PositionY = y,
            PositionZ = z,
            NormalX = nx,
            NormalY = ny,
            NormalZ = nz
        };
    }

    /// <summary>Counts one shot of a weapon and opens it. Null when the weapon is not a recorded equipment life,
    /// the item is authority-less or no owner reference is current — a shot without its actor is not a fact. The
    /// shot this replaces is no longer open, so a hit arriving after a later shot was fired belongs to that later
    /// shot; at most one shot is ever open, which is what keeps one hit from being billed twice.</summary>
    internal Shot? Open(Weapon? weapon)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return null;
        var recorded = _adapter.RecordShot(weapon);
        if (recorded == null)
        {
            // The first shot of a world that is not a recorded equipment life is told once at Info: every later shot of
            // the same weapon is unobserved for the same reason, and the cause is otherwise invisible in a player log.
            if (_unrecordedReported != kernel.WorldEpoch)
            {
                _unrecordedReported = kernel.WorldEpoch;
                _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.FactSkipped, "shot-weapon-not-recorded",
                    binding: ModuleDefinition.ShotCommittedBinding);
            }
            return null;
        }
        var owner = _adapter.OwnerOf(weapon);
        if (owner == null)
        {
            if (_unownedReported != kernel.WorldEpoch)
            {
                _unownedReported = kernel.WorldEpoch;
                _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.FactSkipped, "shot-owner-unresolved",
                    entity: recorded.Value.Equipment.Id, binding: ModuleDefinition.ShotCommittedBinding);
            }
            _log.Reason(RuntimeLogLevel.Trace, WeaponLogCodes.FactSkipped, "owner-unresolved",
                entity: recorded.Value.Equipment.Id, binding: ModuleDefinition.ShotCommittedBinding);
            return null;
        }
        var shot = new Shot(recorded.Value.Equipment, recorded.Value.Index);
        _open = shot;
        _tally = Empty;
        // Nothing is listening on the shot row: the event value is never built, and the shot stays open because
        // the window is this observer's own state rather than the fact's.
        if (Closed(_shotGate)) return shot;
        var id = "gtfo.weapon.shot:" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));
        var fact = _adapter.Begin(_shotGate, kernel.WorldEpoch);
        fact.Values.Entity(Blocks.TriggerCombatShotCommitted.OutSource, owner);
        fact.Values.Entity(Blocks.TriggerCombatShotCommitted.OutEquipment, recorded.Value.Equipment);
        fact.Values.Integer(Blocks.TriggerCombatShotCommitted.OutIndex, recorded.Value.Index);
        fact.Raise(id, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick), ShotScope.Of(recorded.Value.Equipment));
        return shot;
    }

    private static readonly EntityScope ShotScope = new("gtfo.weapon.equipment:");

    private IForgeEngine? Ready()
    {
        if (!_adapter.Authoritative()) return null;
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return null;
        // Every record of the impact table is an entity of one world, so the table is dropped with the world that
        // produced it rather than answering a later read about a life the reference no longer names.
        // The open shot names an equipment life of its world too: carried into the next one it would be the subject
        // of every candidate there and the kernel would refuse each as `stale-world`.
        if (kernel.WorldEpoch != _world)
        { _world = kernel.WorldEpoch; ForgetImpacts(); _open = null; _firing = null; _firingInstance = null; _tally = Empty; }
        return kernel;
    }

    /// <summary>A declared row nobody listens on.</summary>
    private static bool Closed(RuntimeSubscriptionGate gate) => !gate.HasSubscribers;

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
