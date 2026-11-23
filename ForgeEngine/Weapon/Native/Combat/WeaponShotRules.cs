using System;
using Agents;
using AIGraph;
using Enemies;
using ForgeEngine.GameBindings;
using ForgeEngine.Framework;
using Gear;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>The native build's bodies for the shot-rule and weapon-projectile rows, handed to the one
/// registration the session builds. Only this project compiles this file, so where it is absent neither row is
/// declared.</summary>
internal sealed partial class WeaponNativeSession
{
    static partial void NativeShotBodies(IForgeEngine kernel, IPlayerDirectory players, EquipmentNativeAdapter adapter, Func<bool> canExecute,
        Func<bool> canExecuteOwner, WeaponLog log, ref CommandHandler? shotRule,
        ref Func<EntityReference, string?>? shotRuleHolders, ref CommandHandler? bulletProjectile)
    {
        var rules = WeaponShotRules.Attach(kernel, players, adapter, canExecuteOwner, log);
        shotRule = rules.Apply;
        // The rule runs on the machine that holds the weapon: the holder channel names the player holding the
        // equipment and ForgePlayer's directory names that player's session.
        shotRuleHolders = equipment => players.SessionOf(WeaponHolderChannel.HolderOf(equipment));
        bulletProjectile = WeaponBulletProjectiles.Attach(kernel, adapter, canExecute, log).Launch;
    }

    static partial void NativeShotAttach(RuntimeModuleHandle registration)
    {
        WeaponBulletProjectiles.Current?.Bind(registration);
    }

    static partial void NativeShotLifecycle(RuntimeLifecycleEvent value)
    {
        WeaponHolderChannel.ObserveFireLifecycle(value);
        if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped
            || value.Current.IsHost == false)
            if (WeaponDebugSwitchSource.Current is { } switches && !switches.RestoreAll(out var code))
                Current?._report("weapon.debug-switch-restore-failed code=" + code);
        WeaponShotRules.Current?.Observe(value);
        WeaponBulletProjectiles.Current?.Observe(value);
    }

    static partial void NativeShotLifeEnded(EntityReference reference)
    {
        WeaponHolderChannel.StopFireFor(reference);
        WeaponShotRules.Current?.EndFor(reference);
    }

    static partial void NativeShotDetach()
    {
        WeaponHolderChannel.StopFire(ResultCode.HolderSessionEnded);
        WeaponDebugSwitchSource.Current?.EndSession();
        WeaponShotRules.Detach();
        WeaponBulletProjectiles.Detach();
    }
}


/// <summary>
/// The `forge.action.weapon.shot_rule` body: a standing rule on one held weapon that rewrites every shot its own
/// firing body makes while the rule lives.
///
/// <b>Where the rule acts.</b> A player's shot is <c>BulletWeapon.Fire</c> or <c>Shotgun.Fire</c> on the holder's
/// own machine (dump.cs 617275 / 617626), and each ray those bodies fire goes through
/// <c>Weapon.CastWeaponRay</c> (dump.cs 572342 / 572345) with the shared <c>Weapon.WeaponHitData</c>; the hit
/// itself is <c>BulletWeapon.BulletHit</c> (dump.cs 617296). A firing window opens before the two Fire
/// bodies and closes after them (<see cref="ShotRuleHooks"/>, over ForgeCore's brackets), and only a cast inside a window whose weapon carries a rule is adapted, so
/// the hitscan row, the sentry and every unruled weapon keep the game's own ray.
///
/// <b>What the parameters do.</b> `aim_at` + `aim_part` turn the ray: the hit data's `fireDir` is pointed at the
/// chosen point and `hasFireDir` set, and the weapon's own spread and pellet offsets still apply around it.
/// `suppress` lets the game cast the ray (so the tracer and the fire facts are the game's) and answers the cast as a
/// miss; the original firing body's miss branch avoids the hit entry and the Forge observer does not publish, so no
/// damage is taken, no candidate is raised and no `shot_resolved` miss is reported for a bullet that never flew — a
/// projectile the plan launches in its place reports its own resolution. `pierce_reachable` / `pierce_open_doors` keep the game's ray when it
/// reaches a damageable body in the open; when it reaches world geometry the same line is re-cast against
/// <c>LayerManager.MASK_ENEMY_DAMAGABLE</c> (dump.cs 583234), and the first enemy whose course node the shooter's
/// course node reaches through the level's own portals (<see cref="ShotRuleContract.Reach"/> over
/// <c>AIG_CourseNode.m_portals</c>, <c>AIG_CoursePortal.IsTraversable</c>/<c>GetOppositeNode</c> and, for open
/// doors, <c>iLG_Door_Core.LastStatus</c>) is the hit. An enemy that is not reachable is passed and the line
/// continues; when nothing reachable is found the game's own wall hit stands.
///
/// <b>How the rule ends.</b> By its duration (the holder's own tick), by the equipment life ending, by a newer rule
/// on the same weapon, or by a world boundary or checkpoint reload. The row casts no handle: an owner-tier action's
/// outputs never reach the host plan that dispatched it, so a handle would be a cancel nobody can call.
/// </summary>
internal sealed class WeaponShotRules
{
    /// <summary>How many rules one machine holds at once. A player holds one weapon at a time, so this is room for
    /// every weapon a machine could plausibly rule, and a request beyond it is refused by name.</summary>
    internal const int Capacity = 32;
    /// <summary>How many enemy-mask casts one pierced ray may make before it gives up: an enemy is several limb
    /// colliders, so a line through two unreachable enemies needs a few casts per body.</summary>
    internal const int MaximumProbes = 16;
    /// <summary>How far past one collider the next probe starts, so it does not start inside what it just hit.</summary>
    internal const float ProbeStep = 0.05f;
    /// <summary>How many course nodes one reachability walk may queue.</summary>
    internal const int ReachQueue = 256;
    /// <summary>The highest course-node id the walk keeps a visit stamp for; a node beyond it is not walked, which
    /// answers unreachable rather than guessing.</summary>
    internal const int MaximumNodeId = 1 << 16;

    internal static WeaponShotRules? Current { get; private set; }


    private sealed class Rule
    {
        internal bool Live;
        internal int Generation;
        internal IntPtr Weapon;
        internal BulletWeapon? WeaponObject;
        internal EntityReference Equipment = null!;
        internal ShotRuleParameters Parameters;
        internal Agent? Aim;
        internal EnemyAgent? AimEnemy;
        internal long ExpiryTick;
        internal bool AimReported, PierceReported, BudgetReported;
    }

    private readonly IForgeEngine _kernel;
    private readonly IPlayerDirectory _players;
    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<bool> _canExecute;
    private readonly WeaponLog _log;
    private readonly Rule[] _rules = new Rule[Capacity];
    private readonly CourseGraph _graph = new();
    private readonly AIG_CourseNode?[] _queue = new AIG_CourseNode?[ReachQueue];
    private ShotLimbCandidate[] _limbs = new ShotLimbCandidate[32];

    // Fire can reenter. Each invocation saves only Forge's own window.
    private Rule? _firing;
    // A consumed replacement belongs to this shot, even if its standing rule ends or its slot is reused.
    private bool _suppressShot;
    // WHY: P4 — the tick observer and the fire subscribers return at once while no rule stands, without walking the table.
    private int _live;
    private bool _inside;
    private CastScope _pending;
    private bool _suppressionBaseline;
    internal readonly struct FireScope : IDisposable
    {
        private readonly Rule? _previous;
        private readonly bool _suppress;
        private readonly bool _inside;
        private readonly CastScope _pending;
        private readonly bool _baseline;
        private readonly int _generation;
        private readonly bool _previousSuppressed;
        internal readonly WeaponShotRules? Owner;
        internal FireScope(WeaponShotRules owner)
        {
            Owner = owner; _previous = owner._firing; _generation = _previous?.Generation ?? 0;
            _previousSuppressed = owner._suppressShot;
            _suppress = WeaponCombatObserver.SuppressTriggers; _inside = owner._inside;
            _pending = owner._pending; _baseline = owner._suppressionBaseline;
        }
        internal void Restore()
        {
            if (Owner == null) return;
            try { Owner.HitFinished(); }
            finally
            {
                Owner._pending = _pending;
                var previousLive = _previous is { Live: true } && _previous.Generation == _generation;
                Owner._firing = previousLive ? _previous : null;
                Owner._suppressShot = _previousSuppressed;
                Owner._inside = _inside;
                Owner._suppressionBaseline = _baseline;
                WeaponCombatObserver.SuppressTriggers = _suppress;
            }
        }
        public void Dispose() => Restore();
    }

    internal struct CastScope
    {
        internal WeaponShotRules? Owner;
        internal Weapon.WeaponHitData? Data;
        internal bool HasDirection, WrittenHasDirection;
        internal Vector3 Direction, WrittenDirection;
    }

    private WeaponShotRules(IForgeEngine kernel, IPlayerDirectory players, EquipmentNativeAdapter adapter, Func<bool> canExecute,
        WeaponLog log)
    {
        _kernel = kernel;
        _players = players;
        _adapter = adapter;
        _canExecute = canExecute;
        _log = log;
        for (var index = 0; index < _rules.Length; index++) _rules[index] = new Rule();
    }

    internal static WeaponShotRules Attach(IForgeEngine kernel, IPlayerDirectory players, EquipmentNativeAdapter adapter, Func<bool> canExecute,
        WeaponLog log)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(adapter); ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(log);
        Current = new WeaponShotRules(kernel, players, adapter, canExecute, log);
        return Current;
    }

    internal static void Detach()
    {
        var current = Current;
        Current = null;
        if (current == null) return;
        current.CloseWindow();
        current.ForgetAll("session-ended");
    }

    /// <summary>
    /// `forge.action.weapon.shot_rule`, on the holder's machine. Refusals, in the order they are checked: an
    /// equipment this machine has no current life for, a command not addressed to this machine's player, a machine
    /// that may not write, a structural parameter outside its vocabulary, a duration that is not a positive tick
    /// count, an equipment that is not a hitscan weapon, an `aim_at` this machine cannot resolve to a live agent (or a
    /// player asked for a limb), and no free rule slot.
    /// </summary>
    internal CommandResult Apply(CommandContext context)
    {
        if (!context.TryEntity(ShotRuleContract.EquipmentSlot, out var equipment)
            || !_kernel.IsEntityCurrent(equipment))
            return Outcome.Rejected(ResultCode.StaleEntity);
        if (!_players.IsLocal(_players.SessionOf(WeaponHolderChannel.HolderOf(equipment))))
            return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        if (!_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!ShotRuleContract.TryRead(context, out var parameters, out var parameterCode))
            return Outcome.Rejected(parameterCode);
        long duration = 0;
        if (context.Present(ShotRuleContract.DurationSlot))
        {
            if (context.Integer(ShotRuleContract.DurationSlot) is not { } declared || declared <= 0)
                return Outcome.Rejected(ResultCode.ShotRuleDurationInvalid);
            duration = declared;
        }
        var held = _adapter.EquippableOf(equipment)?.Item;
        var weapon = held == null ? null : held.TryCast<BulletWeapon>();
        if (weapon == null || weapon.Pointer == IntPtr.Zero) return Outcome.Rejected(ResultCode.ShotRuleWeaponNotHitscan);

        Agent? aim = null;
        EnemyAgent? aimEnemy = null;
        if (context.TryEntity(ShotRuleContract.AimAtSlot, out var aimAt))
        {
            if (!_kernel.IsEntityCurrent(aimAt)) return Outcome.Rejected(ResultCode.ShotRuleAimUnresolved).WithDetail(aimAt.Id);
            aimEnemy = context.EntityInstance(aimAt) is EnemyAgent enemy && enemy.Pointer != IntPtr.Zero ? enemy : null;
            if (aimEnemy != null) aim = aimEnemy;
            else if (_adapter.PlayerOf(aimAt) is { HasPlayerAgent: true } player
                     && player.PlayerAgent?.TryCast<PlayerAgent>() is { } agent) aim = agent;
            if (aim == null) return Outcome.Rejected(ResultCode.ShotRuleAimUnresolved).WithDetail(aimAt.Id);
            // A player has no enemy limbs; a limb part aimed at one names a point this target does not have.
            if (aimEnemy == null && parameters.AimPart is ShotRuleAimPart.Weakspot or ShotRuleAimPart.ClosestLimb)
                return Outcome.Rejected(ResultCode.ParameterInvalid).WithDetail("aim_part " + aimAt.Id);
        }

        long expiry = -1;
        if (duration > 0)
        {
            var now = Math.Max(0, context.SimulationTick);
            if (duration > long.MaxValue - now) return Outcome.Rejected(ResultCode.ShotRuleDurationInvalid);
            expiry = now + duration;
        }
        // A newer rule on the same weapon replaces the older one: two standing rules on one ray would have to agree
        // on one direction and one wall policy, and the plan that wrote last is the one that asked for this shot.
        for (var index = 0; index < _rules.Length; index++)
            if (_rules[index].Live && _rules[index].Weapon == weapon.Pointer) End(_rules[index], "replaced");
        Rule? slot = null;
        for (var index = 0; index < _rules.Length && slot == null; index++)
            if (!_rules[index].Live) slot = _rules[index];
        if (slot == null) return Outcome.Rejected(ResultCode.ShotRuleCapacity);

        slot.Live = true;
        _live++;
        ++slot.Generation;
        slot.Weapon = weapon.Pointer;
        slot.WeaponObject = weapon;
        slot.Equipment = equipment;
        slot.Parameters = parameters;
        slot.Aim = aim;
        slot.AimEnemy = aimEnemy;
        slot.ExpiryTick = expiry;
        slot.AimReported = slot.PierceReported = slot.BudgetReported = false;
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ShotRule, new RuntimeLogFacts
            {
                Entity = equipment.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "applied"), RuntimeLogValue.Of("walls", parameters.Walls.ToString()),
                    RuntimeLogValue.Of("aim", aim == null ? "none" : parameters.AimPart.ToString()),
                    RuntimeLogValue.Of("suppress", parameters.Suppress), RuntimeLogValue.Of("expiry", expiry) }
            });
        context.Rows.Row(0, Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return Outcome.Succeeded();
    }

    /// <summary>The lifecycle the rules end on. A tick advance ends the rules whose duration has passed and closes a
    /// firing window a thrown body left open (no firing body is running between ticks); a world boundary, a
    /// checkpoint reload or a runtime that stopped ends every rule, because the weapons they named went with it.</summary>
    internal void Observe(RuntimeLifecycleEvent value)
    {
        if (_live == 0 && !Firing) return;
        if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
        {
            if (Firing) CloseWindow();
            var tick = value.Current.SimulationTick;
            for (var index = 0; index < _rules.Length; index++)
            {
                var rule = _rules[index];
                if (rule.Live && rule.ExpiryTick >= 0 && rule.ExpiryTick <= tick) End(rule, "expired");
            }
        }
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged
            or RuntimeLifecycleKind.CheckpointRestored
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        {
            CloseWindow();
            ForgetAll("world-boundary");
        }
    }

    /// <summary>One equipment life ended: the rule standing on it ends with it.</summary>
    internal void EndFor(EntityReference equipment)
    {
        if (equipment == null) return;
        for (var index = 0; index < _rules.Length; index++)
        {
            var rule = _rules[index];
            if (rule.Live && string.Equals(rule.Equipment.Id, equipment.Id, StringComparison.Ordinal))
                End(rule, "equipment-life-ended");
        }
    }

    private void ForgetAll(string reason)
    {
        for (var index = 0; index < _rules.Length; index++)
            if (_rules[index].Live) End(_rules[index], reason);
    }

    private void End(Rule rule, string reason)
    {
        if (!rule.Live) return;
        rule.Live = false;
        _live--;
        if (ReferenceEquals(_firing, rule))
        {
            // Do not release the remaining rays of an already replaced shot. Only future shots use the new rule.
            if (_suppressShot) _firing = null;
            else CloseWindow();
        }
        var equipment = rule.Equipment;
        rule.WeaponObject = null;
        rule.Aim = null;
        rule.AimEnemy = null;
        rule.Weapon = IntPtr.Zero;
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ShotRule, new RuntimeLogFacts
            {
                Entity = equipment?.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "ended"), RuntimeLogValue.Of("reason", reason) }
            });
    }

    // ---- The firing window ------------------------------------------------------------------------------------

    /// <summary>One firing body is starting: the rule on its weapon, if any, is the rule every cast inside it reads.</summary>
    internal FireScope OpenWindow(IntPtr weapon)
    {
        var scope = new FireScope(this);
        if (_suppressShot) WeaponCombatObserver.SuppressTriggers = _suppressionBaseline;
        _suppressionBaseline = WeaponCombatObserver.SuppressTriggers;
        _firing = null;
        _suppressShot = false;
        _inside = false;
        _pending = default;
        if (weapon == IntPtr.Zero || _live == 0) return scope;
        for (var index = 0; index < _rules.Length; index++)
        {
            var rule = _rules[index];
            if (!rule.Live || rule.Weapon != weapon) continue;
            _firing = rule;
            _suppressShot = rule.Parameters.Suppress;
            if (_suppressShot)
            {
                WeaponCombatObserver.SuppressTriggers = true;
            }
            return scope;
        }
        return scope;
    }

    /// <summary>The firing body is done: the aim this window wrote on the shared hit data is put back, so a later
    /// unruled cast reads the game's own heading, and the hit observer's switch is returned.</summary>
    internal void CloseWindow()
    {
        if (_suppressShot && WeaponCombatObserver.SuppressTriggers)
            WeaponCombatObserver.SuppressTriggers = _suppressionBaseline;
        _firing = null;
        _suppressShot = false;
        _inside = false;
        HitFinished();
    }

    /// <summary>Whether a window with a rule is open; the cast hooks read nothing of the game before this answers.</summary>
    internal bool Firing => _firing != null || _suppressShot;

    /// <summary>Whether the firing body now running is a suppressed shot: its rays were answered as misses, so its
    /// bullet never travelled and the body has no resolution of its own to report.</summary>
    internal bool ShotReplaced => _suppressShot;

    /// <summary>
    /// Adapt this invocation's aim input, leaving both overloads' original bodies and caller mask intact.
    /// </summary>
    internal CastScope Cast(Weapon.WeaponHitData data, Vector3 origin)
    {
        var rule = _firing;
        if ((rule == null && !_suppressShot) || _inside || data == null) return default;
        HitFinished();
        var scope = new CastScope { Owner = this, Data = data, HasDirection = data.hasFireDir, Direction = data.fireDir };
        _inside = true; // The muzzle overload delegates to the aligned overload; adapt the outer call once.
        try
        {
            if (rule?.Aim != null) TurnToward(rule, data, origin);
            scope.WrittenHasDirection = data.hasFireDir;
            scope.WrittenDirection = data.fireDir;
            return scope;
        }
        catch { data.hasFireDir = scope.HasDirection; data.fireDir = scope.Direction; _inside = false; throw; }
    }

    /// <summary>A suppressed window answers every cast as a miss, after the game's own cast drew its ray.</summary>
    internal void CastEnded(CastScope scope, Transform align, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result)
    {
        if (scope.Owner != this) return;
        if (_suppressShot) { result = false; return; }
        if (_firing is not { Live: true } rule) return;
        if (result && rule.Parameters.Walls != ShotRuleWalls.Block) result = Pierce(rule, align, ref data, origin);
    }

    internal void CastFinished(CastScope scope)
    {
        if (scope.Owner != this) return;
        _pending = scope; // BulletHit must still see the actual aimed heading (impact force/effects).
        _inside = false;
    }

    internal void HitFinished()
    {
        var scope = _pending;
        _pending = default;
        // Restore our input only while it is still ours; subsequent native direction writes are retained.
        if (scope.Data is { } data && data.hasFireDir == scope.WrittenHasDirection
            && Same(data.fireDir, scope.WrittenDirection))
        { data.hasFireDir = scope.HasDirection; data.fireDir = scope.Direction; }
    }

    private static bool Same(Vector3 left, Vector3 right) => left.x == right.x && left.y == right.y && left.z == right.z;

    /// <summary>A hit entry returned: only the one that carried the hit data of the pending cast ends it, so a nested
    /// hit raised inside the window (another weapon, a hitscan action) leaves the aimed heading in place.</summary>
    internal void HitFinishedFor(Weapon.WeaponHitData data)
    {
        if (_pending.Data is { } pending && pending.Pointer == data.Pointer) HitFinished();
    }

    private void TurnToward(Rule rule, Weapon.WeaponHitData data, Vector3 origin)
    {
        var aim = rule.Aim;
        if (aim == null || aim.Pointer == IntPtr.Zero || !aim.Alive)
        {
            if (!rule.AimReported) AimUnavailable(rule, "target-gone");
            rule.AimReported = true;
            return;
        }
        Vector3 point;
        switch (rule.Parameters.AimPart)
        {
            case ShotRuleAimPart.AimTarget:
            case ShotRuleAimPart.Body:
                var target = rule.Parameters.AimPart == ShotRuleAimPart.AimTarget ? aim.AimTarget : aim.AimTargetBody;
                if (target == null)
                {
                    if (!rule.AimReported) AimUnavailable(rule, "no-aim-point");
                    rule.AimReported = true;
                    return;
                }
                point = target.position;
                break;
            default:
                if (!TryLimb(rule, data, origin, out point))
                {
                    if (!rule.AimReported) AimUnavailable(rule, "no-limb");
                    rule.AimReported = true;
                    return;
                }
                break;
        }
        var dx = point.x - origin.x;
        var dy = point.y - origin.y;
        var dz = point.z - origin.z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 1e-4d)) return;
        data.fireDir = new Vector3((float)(dx / length), (float)(dy / length), (float)(dz / length));
        data.hasFireDir = true;
    }

    /// <summary>The weak spot or limb the rule's part chooses, read from the enemy's own damage limbs: the point is
    /// the limb's own <c>DamageTargetPos</c>, a destroyed limb is skipped and a weak spot is a limb whose
    /// <c>m_type</c> is <c>eLimbDamageType.Weakspot</c> (dump.cs 544409-544590).</summary>
    private bool TryLimb(Rule rule, Weapon.WeaponHitData data, Vector3 origin, out Vector3 point)
    {
        point = default;
        var enemy = rule.AimEnemy;
        var limbs = enemy?.Damage?.DamageLimbs;
        if (limbs == null) return false;
        var count = limbs.Length;
        if (count == 0) return false;
        if (count > _limbs.Length) _limbs = new ShotLimbCandidate[Math.Max(count, _limbs.Length * 2)];
        for (var index = 0; index < count; index++)
        {
            var limb = limbs[index];
            if (limb == null)
            {
                _limbs[index] = new ShotLimbCandidate { Destroyed = true };
                continue;
            }
            var position = limb.DamageTargetPos;
            _limbs[index] = new ShotLimbCandidate
            {
                X = position.x,
                Y = position.y,
                Z = position.z,
                Weakspot = limb.m_type == eLimbDamageType.Weakspot,
                Destroyed = limb.IsDestroyed
            };
        }
        var heading = data.hasFireDir
            ? data.fireDir
            : new Vector3(data.fireAtPos.x - origin.x, data.fireAtPos.y - origin.y, data.fireAtPos.z - origin.z);
        var chosen = ShotRuleContract.PickLimb(new ReadOnlySpan<ShotLimbCandidate>(_limbs, 0, count),
            origin.x, origin.y, origin.z, heading.x, heading.y, heading.z,
            rule.Parameters.AimPart == ShotRuleAimPart.Weakspot);
        if (chosen < 0) return false;
        var picked = _limbs[chosen];
        point = new Vector3((float)picked.X, (float)picked.Y, (float)picked.Z);
        return true;
    }

    /// <summary>
    /// One pierced ray. The game's own cast runs first on the weapon's own mask; a hit on a damageable body in the
    /// open is the answer as it stands. A hit on anything else is where the wall policy acts: the same line (the
    /// direction the game's cast really took, read back from its own impact) is re-cast against the enemy-damageable
    /// mask with the spread and pellet offsets zeroed, and the first enemy the shooter's course node reaches is the
    /// hit. The hit's distance is the whole line from the muzzle, so the weapon's own falloff reads the real range.
    /// Every input field this changed is put back before it returns.
    /// </summary>
    private bool Pierce(Rule rule, Transform align, ref Weapon.WeaponHitData data, Vector3 origin)
    {
        var landed = data.rayHit;
        var collider = landed.collider;
        if (collider == null) return true;
        if (EquipmentNativeAdapter.Climb(collider).Kind != EquipmentNativeAdapter.LimbKind.None) return true;
        // Unity objects are tested with `==`, which sees a destroyed object; `?.` would read through it.
        var shooterWeapon = rule.WeaponObject;
        var shooter = shooterWeapon == null ? null : shooterWeapon.Owner;
        var shooterNode = shooter == null ? null : shooter.CourseNode;
        if (shooterNode == null) return true;

        var point = landed.point;
        var dx = point.x - origin.x;
        var dy = point.y - origin.y;
        var dz = point.z - origin.z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 1e-4d)) return true;
        var heading = new Vector3((float)(dx / length), (float)(dy / length), (float)(dz / length));

        var fireDir = data.fireDir;
        var hasFireDir = data.hasFireDir;
        var spread = data.randomSpread;
        var offsetX = data.angOffsetX;
        var offsetY = data.angOffsetY;
        var limit = data.maxRayDist;
        var start = origin;
        var travelled = 0f;
        var skipped = IntPtr.Zero;
        var answered = false;
        RaycastHit found = default;
        try
        {
            for (var probe = 0; probe < MaximumProbes; probe++)
            {
                var remaining = limit - travelled;
                if (remaining <= 0f) break;
                data.fireDir = heading;
                data.hasFireDir = true;
                data.randomSpread = 0f;
                data.angOffsetX = 0f;
                data.angOffsetY = 0f;
                data.maxRayDist = remaining;
                if (!Weapon.CastWeaponRay(align, ref data, start, LayerManager.MASK_ENEMY_DAMAGABLE)) break;
                var ray = data.rayHit;
                var enemy = EquipmentNativeAdapter.Climb(ray.collider).Enemy;
                if (enemy != null && enemy.Pointer != skipped && enemy.Alive)
                {
                    var reach = Reach(shooterNode, enemy.CourseNode, rule.Parameters.Walls);
                    if (reach == ShotReach.Reachable)
                    {
                        ray.distance = travelled + ray.distance;
                        found = ray;
                        answered = true;
                        break;
                    }
                    if (reach == ShotReach.Budget && !rule.BudgetReported)
                    {
                        rule.BudgetReported = true;
                        if (_log.Logs(RuntimeLogLevel.Error))
                            _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ShotRuleFailed, new RuntimeLogFacts
                            {
                                Entity = rule.Equipment.Id,
                                Values = new[] { RuntimeLogValue.Of("phase", "reach-budget"), RuntimeLogValue.Of("queue", ReachQueue) }
                            });
                    }
                    skipped = enemy.Pointer;
                }
                var hitPoint = ray.point;
                travelled += ray.distance + ProbeStep;
                start = new Vector3(hitPoint.x + heading.x * ProbeStep, hitPoint.y + heading.y * ProbeStep,
                    hitPoint.z + heading.z * ProbeStep);
            }
        }
        finally
        {
            data.fireDir = fireDir;
            data.hasFireDir = hasFireDir;
            data.randomSpread = spread;
            data.angOffsetX = offsetX;
            data.angOffsetY = offsetY;
            data.maxRayDist = limit;
            if (!answered)
            {
                data.rayHit = landed;
            }
        }
        if (!answered) return true;
        data.rayHit = found;
        if (!rule.PierceReported)
        {
            rule.PierceReported = true;
            if (_log.Logs(RuntimeLogLevel.Trace))
                _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.ShotRulePierced, new RuntimeLogFacts
                {
                    Entity = rule.Equipment.Id,
                    Values = new[] { RuntimeLogValue.Of("walls", rule.Parameters.Walls.ToString()), RuntimeLogValue.Of("distance", (double)found.distance) }
                });
        }
        return true;
    }

    private ShotReach Reach(AIG_CourseNode from, AIG_CourseNode? to, ShotRuleWalls walls)
    {
        if (to == null) return ShotReach.Unreachable;
        _graph.Begin();
        return ShotRuleContract.Reach<AIG_CourseNode, CourseGraph>(_graph, from, to, walls, _queue);
    }

    /// <summary>The level's own course graph as the reachability walk reads it. Visits are stamped in a table this
    /// class owns, keyed by the node's own <c>NodeID</c>, so the walk writes nothing the game reads.</summary>
    private sealed class CourseGraph : IShotRuleGraph<AIG_CourseNode>
    {
        private int[] _stamps = new int[256];
        private int _walk;

        internal void Begin()
        {
            if (++_walk != int.MaxValue) return;
            Array.Clear(_stamps);
            _walk = 1;
        }

        public bool Visit(AIG_CourseNode node)
        {
            var id = node.NodeID;
            if (id < 0 || id >= MaximumNodeId) return false;
            if (id >= _stamps.Length) Array.Resize(ref _stamps, Math.Min(MaximumNodeId, Math.Max(id + 1, _stamps.Length * 2)));
            if (_stamps[id] == _walk) return false;
            _stamps[id] = _walk;
            return true;
        }

        public bool Same(AIG_CourseNode left, AIG_CourseNode right) => left.Pointer == right.Pointer;

        public int PortalCount(AIG_CourseNode node) => node.m_portals?.Count ?? 0;

        public AIG_CourseNode? Portal(AIG_CourseNode node, int index, out bool traversable, out bool doorOpen)
        {
            traversable = false;
            doorOpen = false;
            var portal = node.m_portals[index];
            if (portal == null) return null;
            traversable = portal.IsTraversable;
            var door = portal.m_door;
            doorOpen = door == null || door.LastStatus is eDoorStatus.Open or eDoorStatus.Destroyed;
            return portal.GetOppositeNode(node);
        }
    }

    /// <summary>A rule could not aim; reported once per rule, at info, because a gone target is a game condition.</summary>
    private void AimUnavailable(Rule rule, string reason)
    {
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ShotRule, new RuntimeLogFacts
        {
            Entity = rule.Equipment.Id,
            Values = new[] { RuntimeLogValue.Of("phase", "aim-unavailable"), RuntimeLogValue.Of("reason", reason) }
        });
    }

    /// <summary>A hook body's exception is recorded and the game's own path runs, so a defect here never takes a
    /// shot away from the player. Only the failure's type is logged: its message is free text the log does not carry.</summary>
    internal static void Fault(Exception error)
    {
        var current = Current;
        if (current == null) return;
        current._inside = false;
        if (!current._log.Logs(RuntimeLogLevel.Error)) return;
        current._log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ShotRuleFailed, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("phase", "hook"), RuntimeLogValue.Of("reason", error.GetType().Name) }
        });
    }

}

// The rule's subscriptions to ForgeCore's brackets: the two firing bodies the holder's own weapon runs (dump.cs
// 617275 / 617626) and the two ray casts (dump.cs 572342 / 572345). The window is open for the whole original
// body: every ray and hit entry the body makes happens between the bracket's before and its after or faulted.
internal static class ShotRuleHooks
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Around(OpenWindow, CloseWindow, WindowFaulted);
        ForgeCore.GameHooks.Fire.Shotgun.Around(OpenWindow, CloseWindow, WindowFaulted);
        ForgeCore.GameHooks.MuzzleRay.Around<WeaponShotRules.CastScope>(MuzzleEnter, MuzzleExit, CastFaulted);
        ForgeCore.GameHooks.AlignedRay.Around<WeaponShotRules.CastScope>(AlignedEnter, AlignedExit, CastFaulted);
        ShotRuleBulletHit.Subscribe();
    }

    private static WeaponShotRules.FireScope OpenWindow(BulletWeapon weapon)
    {
        var rules = WeaponShotRules.Current;
        if (rules == null || WeaponNativeSession.Offer(WeaponFeature.Fire) is not { Faulted: false } || weapon == null) return default;
        try { return rules.OpenWindow(weapon.Pointer); }
        catch (Exception error) { WeaponShotRules.Fault(error); return default; }
    }

    private static void CloseWindow(BulletWeapon weapon, WeaponShotRules.FireScope scope, bool ran) => Restore(scope);

    private static void WindowFaulted(BulletWeapon weapon, WeaponShotRules.FireScope scope, Exception error) => Restore(scope);

    private static void Restore(WeaponShotRules.FireScope scope)
    {
        try { scope.Restore(); }
        catch (Exception error) { WeaponShotRules.Fault(error); }
    }

    // `Weapon.CastWeaponRay(Transform muzzle, ref WeaponHitData, int altRayCastMask)`: the origin is the muzzle's own
    // position, which the bracket does not read.
    private static WeaponShotRules.CastScope MuzzleEnter(Transform muzzle, ref Weapon.WeaponHitData data, Vector3 origin)
    {
        var rules = WeaponShotRules.Current;
        // WHY: P5 — `Transform.position` is a native read; outside a ruled window the cast stays the game's own.
        if (rules is not { Firing: true } || WeaponNativeSession.Offer(WeaponFeature.Fire) is not { Faulted: false } || muzzle == null) return default;
        try { return rules.Cast(data, muzzle.position); }
        catch (Exception error) { WeaponShotRules.Fault(error); return default; }
    }

    private static void MuzzleExit(WeaponShotRules.CastScope scope, Transform muzzle, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran)
    {
        if (ran && scope.Owner != null)
        {
            try { scope.Owner.CastEnded(scope, muzzle, ref data, muzzle.position, ref result); }
            catch (Exception error) { WeaponShotRules.Fault(error); }
        }
        scope.Owner?.CastFinished(scope);
    }

    // `Weapon.CastWeaponRay(Transform alignTransform, ref WeaponHitData, Vector3 originPos, int altRayCastMask)`: the
    // origin is the caller's own.
    private static WeaponShotRules.CastScope AlignedEnter(Transform align, ref Weapon.WeaponHitData data, Vector3 origin)
    {
        var rules = WeaponShotRules.Current;
        if (rules is not { Firing: true } || WeaponNativeSession.Offer(WeaponFeature.Fire) is not { Faulted: false } || align == null) return default;
        try { return rules.Cast(data, origin); }
        catch (Exception error) { WeaponShotRules.Fault(error); return default; }
    }

    private static void AlignedExit(WeaponShotRules.CastScope scope, Transform align, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran)
    {
        if (ran && scope.Owner != null)
        {
            try { scope.Owner.CastEnded(scope, align, ref data, origin, ref result); }
            catch (Exception error) { WeaponShotRules.Fault(error); }
        }
        scope.Owner?.CastFinished(scope);
    }

    private static void CastFaulted(WeaponShotRules.CastScope scope) => scope.Owner?.CastFinished(scope);
}

// The hit entry (dump.cs 617296) always runs when the original Fire reaches it. Suppression returns a miss to
// Fire, so the game's own miss branch avoids the hit. Cleanup keeps the aimed heading through the original hit.
// BulletHit is static: there is no weapon instance, so the hit is matched to its cast by the shared hit data it was
// called with. The call is pooled and valid only inside the bracket; the hit data is what outlives it. The cleanup runs
// whether the body returned or threw, and must never throw into the game's hit entry.
internal static class ShotRuleBulletHit
{
    internal static void Subscribe() => ForgeCore.GameHooks.BulletHit.Around<WeaponShotRules?>(Enter, Finished, Faulted);

    private static WeaponShotRules? Enter(ForgeCore.BulletHitCall call)
        => WeaponNativeSession.Offer(WeaponFeature.Hit) == null ? null : WeaponShotRules.Current;

    private static void Finished(ForgeCore.BulletHitCall call, WeaponShotRules? rules, bool ran) => Finish(rules, call.Data);

    private static void Faulted(ForgeCore.BulletHitCall call, WeaponShotRules? rules, Exception error) => Finish(rules, call.Data);

    private static void Finish(WeaponShotRules? rules, Weapon.WeaponHitData data)
    {
        if (rules == null || data == null) return;
        try { rules.HitFinishedFor(data); }
        catch (Exception error) { WeaponShotRules.Fault(error); }
    }
}
