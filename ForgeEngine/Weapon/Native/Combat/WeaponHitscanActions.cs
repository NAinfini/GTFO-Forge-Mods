using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Gear;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The `forge.action.combat.hitscan_shot` body: one native hitscan shot group, from an origin and a direction,
/// through the game's own two entry points. <c>Weapon.CastWeaponRay</c> fills the <c>Weapon.WeaponHitData</c> the
/// shot is made of, and <c>BulletWeapon.BulletHit</c> is the static body that turns that data into a hit — the
/// same pair every weapon `Fire` body in this build calls (evidence/w4-player-fire.json,
/// evidence/weapon-hit-context.json). This action therefore submits a real hit; it does not reimplement the
/// damage pipeline, so limb, armor, backstab and cooldown handling stay the game's own.
///
/// <b>What the ports map onto.</b> `origin`/`direction` become `fireAtPos`/`fireDir` (with `hasFireDir` set),
/// `range` becomes `maxRayDist`, `amount` becomes `damage`, `spread` becomes `randomSpread`, a ray's offset pair
/// becomes `angOffsetX`/`angOffsetY`, and `ignore_falloff` becomes the `damageFalloff` range. The source's own
/// player becomes the hit data's `owner`, which is how the game credits the shot. `include_self` and `ignore` are
/// post-filters on the target the ray resolved to: the ray's own layer mask is the game's default weapon mask
/// (`altRayCastMask = -1`), and a target the row refuses is simply not submitted, which is what "the ray reached
/// it but it may not be damaged" means. The row makes no relation judgement of its own: which side a target is on
/// is the canonical relation filter's answer, and a plan that must spare allies feeds that filter's result into
/// `ignore`, while friendly-fire scaling stays the game's own hit body. `directional_bonus` is handed straight to
/// the hit body's own `allowDirectionalBonus`: absent is the native default of `true`, and `false` turns the
/// from-behind bonus off for every hit this node submits. `wall_handling` re-casts through the same entry point
/// with a reflected heading; `run_hit_triggers` suppresses the hit candidate this provider publishes for the
/// hits the action causes, through the observer's own switch.
///
/// Only the authoritative host may submit these calls: the game's hit body replicates damage by itself, and a
/// replica that ran it would be applying damage locally.
/// </summary>
internal sealed class WeaponHitscanActions
{
    /// <summary>The most rays one request fires: the 32-bit integer the ray loop counts in, the only bound the count
    /// has. A count above it is refused (`hitscan-count-out-of-range`), never truncated.</summary>
    internal const int MaximumCount = int.MaxValue;
    internal const double MaximumSpread = float.MaxValue;
    internal const double MaximumRange = float.MaxValue;
    internal const double MaximumAmount = float.MaxValue;
    /// <summary>The default wall-handling reach in metres: the ledger's own Shrapnel default.</summary>
    internal const double DefaultWallHandlingDistance = 1d;
    /// <summary>The clearance a ricochet continues from, so the second cast does not start inside the wall it
    /// just struck.</summary>
    internal const float WallHitBuffer = 0.03f;

    private readonly IForgeEngine _kernel;
    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;

    internal WeaponHitscanActions(IForgeEngine kernel, EquipmentNativeAdapter adapter, Func<bool> canExecute,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>`forge.action.combat.hitscan_shot`. Refusals, in the order they are checked: a request with no
    /// source, a command this machine may not run, a structural parameter outside its vocabulary, a source this
    /// machine cannot resolve, a malformed or missing vector, and a count, amount, spread or range outside the
    /// bounds one shot group can honour. The result's own field is how many rays reached a damageable target the
    /// row allowed, which is what the plan can check its request against.</summary>
    internal CommandResult Launch(CommandContext context)
    {
        if (!context.TryEntity(HitscanShotContract.SourceSlot, out var source))
            return Outcome.Rejected(ResultCode.SourceMissing).WithDetail("source");
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!HitscanShotContract.TryRead(context, out var parameters, out var parameterCode))
            return Outcome.Rejected(parameterCode);
        if (!TrySource(source, out var item, out var owner, out var sourceCode))
            return Outcome.Rejected(sourceCode);
        if (owner == null || owner.Pointer == IntPtr.Zero || _adapter.OwnerOf(owner) is not { } shooter
            || !_kernel.IsEntityCurrent(shooter))
            return Outcome.Rejected(ResultCode.HitscanSourceUnresolved);
        var align = item != null ? item.transform : owner == null ? null : owner.transform;
        if (align == null) return Outcome.Rejected(ResultCode.HitscanSourceUnresolved);
        var origin = align.position;
        if (context.Present(HitscanShotContract.OriginSlot))
        {
            if (!ShotVectors.TryRead(context, HitscanShotContract.OriginSlot, out origin))
                return Outcome.Rejected(ResultCode.VectorInvalid);
        }
        var direction = align.forward;
        if (context.Present(HitscanShotContract.DirectionSlot))
        {
            if (!ShotVectors.TryRead(context, HitscanShotContract.DirectionSlot, out direction))
                return Outcome.Rejected(ResultCode.VectorInvalid);
        }
        if (!ShotVectors.Unit(direction, out direction)) return Outcome.Rejected(ResultCode.VectorInvalid);
        var declaredCount = context.Integer(HitscanShotContract.CountSlot);
        if (context.Present(HitscanShotContract.CountSlot) && !declaredCount.HasValue)
            return Outcome.Rejected(ResultCode.HitscanCountOutOfRange);
        var countValue = declaredCount ?? 1L;
        if (!CountWithinBudget(countValue, MaximumCount)) return Outcome.Rejected(ResultCode.HitscanCountOutOfRange);
        var count = (int)countValue;
        var amount = context.Number(HitscanShotContract.AmountSlot)
                     ?? (context.Present(HitscanShotContract.AmountSlot) ? double.NaN : 0d);
        if (!double.IsFinite(amount) || Math.Abs(amount) > MaximumAmount)
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        var spread = context.Number(HitscanShotContract.SpreadSlot)
                     ?? (context.Present(HitscanShotContract.SpreadSlot) ? double.NaN : 0d);
        if (!double.IsFinite(spread) || Math.Abs(spread) > MaximumSpread)
            return Outcome.Rejected(ResultCode.HitscanSpreadOutOfRange);
        var range = context.Present(HitscanShotContract.RangeSlot)
            ? context.Number(HitscanShotContract.RangeSlot) ?? double.NaN : Range(null, item);
        if (!double.IsFinite(range)) return Outcome.Rejected(ResultCode.HitscanRangeRequired);
        if (Math.Abs(range) > MaximumRange) return Outcome.Rejected(ResultCode.RangeOutOfRange);
        var aimScale = context.Number(HitscanShotContract.AimOffsetScaleSlot)
                       ?? (context.Present(HitscanShotContract.AimOffsetScaleSlot) ? double.NaN : 1d);
        if (!double.IsFinite(aimScale)) return Outcome.Rejected(ResultCode.HitscanShapingOutOfRange);
        var wallDistance = context.Number(HitscanShotContract.WallHandlingDistanceSlot)
                           ?? (context.Present(HitscanShotContract.WallHandlingDistanceSlot) ? double.NaN : DefaultWallHandlingDistance);
        if (!double.IsFinite(wallDistance))
            return Outcome.Rejected(ResultCode.HitscanShapingOutOfRange);
        var offsetScale = item != null && item.AimButtonHeld ? aimScale : 1d;
        var offsetCursor = 0;
        while (HitscanShotContract.TryReadOffset(parameters.Offsets.AsSpan(), ref offsetCursor, out var x, out var y))
            if (!float.IsFinite((float)(x * offsetScale)) || !float.IsFinite((float)(y * offsetScale)))
                return Outcome.Rejected(ResultCode.HitscanShapingOutOfRange);
        offsetCursor = 0;
        var ignoreCount = context.Count(HitscanShotContract.IgnoreSlot);
        if (ignoreCount < 0 && context.Present(HitscanShotContract.IgnoreSlot))
            return Outcome.Rejected(ResultCode.HitscanIgnoreUnresolved);
        if (ignoreCount < 0) ignoreCount = 0;
        var ignore = new EntityReference[ignoreCount];
        for (var index = 0; index < ignoreCount; index++)
            if (!context.TryEntity(HitscanShotContract.IgnoreSlot, index, out ignore[index]) || !_kernel.IsEntityCurrent(ignore[index]))
                return Outcome.Rejected(ResultCode.HitscanIgnoreUnresolved);
        // This explicit action is its own shot group. A rule on the surrounding native Fire must not suppress
        // or redirect its replacement rays; the native window and its trigger gate are restored on every exit.
        using var nativeWindow = WeaponShotRules.Current?.OpenWindow(IntPtr.Zero) ?? default;

        var hits = 0;
        var unresolved = 0;
        for (var index = 0; index < count; index++)
        {
            var hit = new Weapon.WeaponHitData();
            hit.owner = owner;
            hit.fireAtPos = origin;
            hit.fireDir = direction;
            hit.hasFireDir = true;
            hit.maxRayDist = (float)range;
            hit.damage = (float)amount;
            hit.damageFalloff = parameters.IgnoreFalloff
                ? new UnityEngine.Vector2((float)range, (float)range)
                : new UnityEngine.Vector2(0f, (float)range);
            hit.randomSpread = (float)spread;
            float offsetX = 0f, offsetY = 0f;
            if (parameters.OffsetPairs != 0)
            {
                if (index % parameters.OffsetPairs == 0) offsetCursor = 0;
                HitscanShotContract.TryReadOffset(parameters.Offsets.AsSpan(), ref offsetCursor, out offsetX, out offsetY);
            }
            hit.angOffsetX = (float)(offsetX * offsetScale);
            hit.angOffsetY = (float)(offsetY * offsetScale);
            var suppressed = !parameters.RunHitTriggers;
            var previousSuppression = WeaponCombatObserver.SuppressTriggers;
            try
            {
                if (Fire(ref hit, align, origin, direction, parameters, wallDistance, out var target)
                    && target != null && Allowed(shooter, target, parameters, ignore))
                {
                    if (suppressed) WeaponCombatObserver.SuppressTriggers = true;
                    // Only a hit the game itself resolved counts; a false return is not a confirmed hit.
                    bool resolved;
                    AuthoredHitScope.Active = true;
                    try { resolved = BulletWeapon.BulletHit(hit, true, 0f, 0u, parameters.DirectionalBonus); }
                    finally { AuthoredHitScope.Active = false; }
                    if (resolved) hits++;
                    else if (EquipmentNativeAdapter.ReachedDamageLimb(hit)) unresolved++;
                }
            }
            catch (Exception error)
            {
                _report("weapon.hitscan-hit-unconfirmed: " + RuntimeDiagnostics.Describe(error));
                return Unconfirmed(context, hits);

            }
            finally
            {
                WeaponCombatObserver.SuppressTriggers = previousSuppression;
            }
        }
        if (unresolved != 0) return Unconfirmed(context, hits);
        context.Rows.Begin(0).Integer(Blocks.ActionCombatHitscanShot.RowHits, hits).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return Outcome.Succeeded();
    }

    /// <summary>
    /// One ray through the game's own cast, with the wall policy applied to its first impact. A cast that reaches
    /// a damageable target is answered with it; a cast that reaches only world geometry is either submitted as it
    /// landed (`none`), re-cast once from the origin along the reflected heading (`avoid`, when the wall is within
    /// the requested reach), or continued from the impact point along the reflected heading (`ricochet`, one
    /// bounce). The target is resolved by the adapter's own collider climb, so it is the same reference the hit
    /// candidate and the hit-context read name.
    /// </summary>
    private bool Fire(ref Weapon.WeaponHitData hit, Transform align, Vector3 origin, Vector3 direction,
        in HitscanShotParameters parameters, double wallDistance, out EntityReference? target)
    {
        target = null;
        var originPos = origin;
        var heading = direction;
        var limit = hit.maxRayDist;
        var responded = false;
        for (var cast = 0; cast < 2; cast++)
        {
            hit.fireAtPos = originPos;
            hit.fireDir = heading;
            hit.hasFireDir = true;
            hit.maxRayDist = limit;
            if (!Weapon.CastWeaponRay(align, ref hit, originPos, -1)) return false;
            target = _adapter.HitTarget(hit);
            if (target != null) return true;
            if (responded || parameters.WallHandling == HitscanWallHandling.None) return false;
            responded = true;
            var ray = hit.rayHit;
            var normal = ray.normal;
            var reach = ForgeCore.Vec.Dot(heading, normal) * -ray.distance;
            if (parameters.WallHandling == HitscanWallHandling.Avoid)
            {
                if (reach > wallDistance) return false;
                heading = Reflect(heading, normal);
                continue;
            }
            var point = ray.point;
            originPos = new Vector3(point.x + normal.x * WallHitBuffer, point.y + normal.y * WallHitBuffer,
                point.z + normal.z * WallHitBuffer);
            heading = Reflect(heading, normal);
            limit -= ray.distance;
            if (limit <= 0f) return false;
        }
        return false;
    }

    /// <summary>Whether one resolved target may be damaged by this shot group. `ignore` is an unconditional
    /// refusal and `include_self` gates the shooter's own body; every other target the ray reached is submitted.
    /// A target the row did not ask for is left untouched rather than retargeted.</summary>
    private static bool Allowed(EntityReference shooter, EntityReference target,
        in HitscanShotParameters parameters, IReadOnlyList<EntityReference> ignore)
    {
        for (var index = 0; index < ignore.Count; index++)
            if (ignore[index] == target) return false;
        return parameters.IncludeSelf || shooter != target;
    }

    /// <summary>
    /// The native item and player one source reference names. An equipment life is resolved through the
    /// adapter's own slot table, which is the same current-check the override rows make; a player reference is
    /// resolved through the game's in-level agent list and confirmed against the owning domain's instance lookup,
    /// so the answer is the player that reference was minted for and never one derived from a slot or a name. The
    /// wielded item comes from that player's own inventory, which is where the muzzle and the aim state live.
    /// </summary>
    private bool TrySource(EntityReference reference, out ItemEquippable? item, out PlayerAgent? owner, out string code)
    {
        item = null;
        owner = null;
        code = "";
        if (!_kernel.IsEntityCurrent(reference)) { code = ResultCode.HitscanSourceUnresolved; return false; }
        if (_adapter.EquippableOf(reference) is { } equippable)
        {
            item = equippable.Item;
            owner = item.Owner;
            return true;
        }
        if (_adapter.PlayerOf(reference) is { HasPlayerAgent: true } player
            && player.PlayerAgent?.TryCast<PlayerAgent>() is { } agent)
        {
            owner = agent;
            item = agent.Inventory == null ? null : agent.Inventory.WieldedItem;
            return true;
        }
        code = ResultCode.HitscanSourceUnresolved;
        return false;
    }

    /// <summary>Whether a ray count is one a shot group may fire: 1 up to <paramref name="budget"/>.</summary>
    internal static bool CountWithinBudget(long count, int budget) => count >= 1 && count <= budget;

    /// <summary>The range one request fires over: the `range` port when the plan wrote one, otherwise the
    /// resolved weapon's own `MaxRayDist`. A source with no weapon and no range is refused by name rather than
    /// given an invented distance.</summary>
    private static double Range(double? declared, ItemEquippable? item)
    {
        if (declared.HasValue) return declared.Value;
        return item is Weapon weapon ? weapon.MaxRayDist : double.NaN;
    }

    /// <summary>A shot whose hits could not all be confirmed. The one result row's `target` is the source the
    /// request carried, and its own column counts the rays that reached an allowed damageable target.</summary>
    private static CommandResult Unconfirmed(CommandContext context, int hits)
    {
        context.Rows.Begin(0).Integer(Blocks.ActionCombatHitscanShot.RowHits, hits)
            .End(Status.Failed, Commit.Unknown, ResultCode.HitscanHitUnconfirmed);
        return Outcome.FailedUnknown(ResultCode.HitscanHitUnconfirmed);
    }

    /// <summary>The reflection of one heading about a surface normal, written out from the two vectors so no
    /// engine helper is reached for a value this small.</summary>
    private static Vector3 Reflect(Vector3 direction, Vector3 normal)
    {
        var dot = ForgeCore.Vec.Dot(direction, normal);
        return new Vector3(direction.x - 2f * dot * normal.x, direction.y - 2f * dot * normal.y,
            direction.z - 2f * dot * normal.z);
    }

}
