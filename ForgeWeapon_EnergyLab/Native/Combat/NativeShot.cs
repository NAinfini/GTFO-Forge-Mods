using System;
using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// This is the GTFO adapter. Presentation receives only positions and never applies damage.
internal sealed class NativeShot
{
    private Weapon.WeaponHitData _hit = new();
    private readonly Il2CppStructArray<RaycastHit> _rays = new(64);
    internal Vector3 Start { get; private set; }
    internal Vector3 End { get; private set; }
    internal bool HasHit { get; private set; }
    internal IDamageable? Receiver => HasHit && _hit.rayHit.collider != null
        ? _hit.rayHit.collider.GetComponentInParent<IDamageable>() : null;

    internal void Trace(BulletWeapon weapon, in ShotTuning tuning)
    {
        var camera = weapon.Owner.FPItemHolder.m_LookCamera;
        if (camera == null) throw new InvalidOperationException("The held gun has no camera.");
        Trace(weapon, tuning, camera.transform.position, camera.transform.forward);
    }

    internal void Trace(BulletWeapon weapon, in ShotTuning tuning, Vector3 origin, Vector3 direction)
    {
        var holder = weapon.Owner.FPItemHolder;
        var camera = holder.m_LookCamera;
        var muzzle = weapon.MuzzleAlign;
        if (camera == null || muzzle == null) throw new InvalidOperationException("The held gun has no camera or muzzle.");
        direction.Normalize();
        Prepare(weapon.Owner, tuning, origin, direction, tuning.Range);
        var aimed = Cast(origin, direction, tuning.Range, weapon.Owner);
        var target = aimed ? _hit.rayHit.point : origin + direction * tuning.Range;

        // First-person models use a separate FOV. Put their muzzle at the same screen point in world space.
        var itemCamera = holder.m_ItemLayerCamera;
        var start = itemCamera == null ? muzzle.position
            : camera.ViewportToWorldPoint(itemCamera.WorldToViewportPoint(muzzle.position));
        var toMuzzle = start - origin;
        if (toMuzzle.sqrMagnitude > 0.0001f && Physics.Raycast(origin, toMuzzle.normalized, out var obstruction,
                toMuzzle.magnitude, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            start = obstruction.point - toMuzzle.normalized * 0.025f;

        // Trace again from the visible muzzle: cover must block both the effect and the damage.
        var toTarget = target - start;
        Start = start;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            End = start;
            HasHit = false;
            return;
        }
        Prepare(weapon.Owner, tuning, start, toTarget.normalized, MathF.Min(tuning.Range, toTarget.magnitude + 0.025f));
        HasHit = Cast(start, _hit.fireDir, _hit.maxRayDist, weapon.Owner);
        End = HasHit ? _hit.rayHit.point : start + _hit.fireDir * _hit.maxRayDist;
    }

    internal void TraceRemote(PlayerAgent owner, in ShotTuning tuning, Vector3 start, Vector3 direction)
    {
        direction.Normalize();
        Prepare(owner, tuning, start, direction, tuning.Range);
        HasHit = Cast(start, direction, tuning.Range, owner);
        Start = start;
        End = HasHit ? _hit.rayHit.point : start + direction * tuning.Range;
    }

    // The nearest hit only: a long ray would cross more colliders than any fixed buffer. The shooter's own
    // colliders are stepped over; the reported distance stays measured from the start.
    private bool Cast(Vector3 start, Vector3 direction, float range, PlayerAgent owner)
    {
        var travelled = 0f;
        for (var step = 0; step < 32; step++)
        {
            if (!Physics.Raycast(start + direction * travelled, direction, out var hit, range - travelled,
                    LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal)) return false;
            if (hit.collider != null && !VanillaDamage.IsShooter(hit.collider, owner))
            {
                hit.distance += travelled;
                _hit.rayHit = hit;
                return true;
            }
            travelled += hit.distance + 0.01f;
            if (travelled >= range) return false;
        }
        throw new InvalidOperationException("Energy direct ray crossed too many shooter colliders; no hit submitted.");
    }

    internal bool VisibleTarget(PlayerAgent owner, Vector3 start, Vector3 point, Collider target, out RaycastHit hit)
    {
        var offset = point - start;
        if (offset.sqrMagnitude < 0.000001f)
        {
            return RayTarget(start, point, target, out hit);
        }
        hit = default;
        if (!Cast(start, offset.normalized, offset.magnitude + 0.025f, owner)) return false;
        var actual = _hit.rayHit.collider.GetComponentInParent<IDamageable>();
        var wanted = target.GetComponentInParent<IDamageable>();
        if (actual == null || wanted == null || VanillaDamage.Identity(actual) != VanillaDamage.Identity(wanted)) return false;
        hit = _hit.rayHit;
        return true;
    }

    // Area queries select actors, not damage limbs. Resolve the first surface of that actor
    // along the radial path; each caller retains its existing world/receiver blocker policy.
    internal bool RayTarget(Vector3 origin, Vector3 point, Collider target, out RaycastHit hit)
    {
        var offset = point - origin;
        if (offset.sqrMagnitude < 0.000001f) offset = target.bounds.center - origin;
        var direction = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector3.up;
        if ((target.ClosestPoint(origin) - origin).sqrMagnitude >= 0.00000001f)
            return ReceiverRay(origin, direction, offset.magnitude + 0.025f, target, out hit);

        // Unity omits origin-containing colliders. A ray from outside finds their real exit
        // surface on the same radial path; damage still travels from the centre to that surface.
        var reach = ReceiverBounds(target, origin, out _).size.magnitude + 0.05f;
        var forward = ReceiverRay(origin, direction, reach, target, out hit);
        var exit = ReceiverRay(origin + direction * reach, -direction, reach, target, out var surface,
            exitsContainingColliders: true);
        if (exit && (!forward || surface.distance < hit.distance)) hit = surface;
        if (forward || exit) hit.distance = 0f;
        return forward || exit;
    }

    // A sphere sweep/overlap is only a contact candidate. Trace a parallel incoming ray
    // through its contact point so velocity, the first limb and its native modifiers agree.
    internal bool ContactTarget(Vector3 origin, Vector3 direction, RaycastHit contact, out RaycastHit hit)
    {
        var collider = contact.collider;
        if (collider == null) { hit = default; return false; }
        direction = direction.normalized;
        var point = contact.distance > 0f ? contact.point : collider.ClosestPoint(origin);
        var along = Vector3.Dot(point - origin, direction);
        var start = point - direction * along;
        var bounds = ReceiverBounds(collider, start, out var inside);
        var reach = inside ? bounds.size.magnitude + 0.05f : 0f;
        var found = ReceiverRay(start - direction * reach, direction, MathF.Max(0f, along) + reach + 0.025f, collider, out hit);
        // Keep the sweep's travel distance (or overlap's zero distance) for native falloff.
        if (found) hit.distance = contact.distance;
        return found;
    }

    private static Bounds ReceiverBounds(Collider collider, Vector3 point, out bool inside)
    {
        var bounds = collider.bounds;
        inside = (collider.ClosestPoint(point) - point).sqrMagnitude < 0.00000001f;
        var limbs = collider.GetComponentInParent<IDamageable>()?.GetBaseDamagable()?.TryCast<Dam_EnemyDamageBase>()?.DamageLimbs;
        if (limbs != null)
            for (var index = 0; index < limbs.Length; index++)
                if (limbs[index]?.GetComponent<Collider>() is { } limb && limb.enabled && limb.gameObject.activeInHierarchy)
                {
                    bounds.Encapsulate(limb.bounds);
                    inside |= (limb.ClosestPoint(point) - point).sqrMagnitude < 0.00000001f;
                }
        return bounds;
    }

    private bool ReceiverRay(Vector3 origin, Vector3 direction, float distance, Collider target, out RaycastHit hit,
        bool exitsContainingColliders = false)
    {
        hit = default;
        var receiver = target.GetComponentInParent<IDamageable>();
        if (receiver == null) return false;
        var identity = VanillaDamage.Identity(receiver);
        var count = Physics.RaycastNonAlloc(origin, direction, _rays, distance,
            LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (count == _rays.Length) throw new InvalidOperationException("Energy limb ray budget exhausted; truncated hit refused.");
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < count; index++)
        {
            var candidate = _rays[index];
            var collider = candidate.collider;
            var actual = collider?.GetComponentInParent<IDamageable>();
            if (collider == null || actual == null || VanillaDamage.Identity(actual) != identity) continue;
            if (exitsContainingColliders)
            {
                var centre = origin + direction * distance;
                if ((collider.ClosestPoint(centre) - centre).sqrMagnitude >= 0.00000001f) continue;
                candidate.distance = distance - candidate.distance;
            }
            if (candidate.distance >= nearest) continue;
            nearest = candidate.distance;
            hit = candidate;
        }
        return float.IsFinite(nearest);
    }

    private void Prepare(PlayerAgent owner, in ShotTuning tuning, Vector3 origin, Vector3 direction, float range)
    {
        _hit.owner = owner;
        _hit.fireAtPos = origin;
        _hit.fireDir = direction;
        _hit.hasFireDir = true;
        _hit.angOffsetX = 0f;
        _hit.angOffsetY = 0f;
        _hit.randomSpread = 0f;
        _hit.maxRayDist = range;
        _hit.damage = tuning.Damage;
        _hit.staggerMulti = tuning.Stagger;
        _hit.precisionMulti = tuning.Precision;
        _hit.damageFalloff = new Vector2(tuning.Range, tuning.Range);
        _hit.vfxBulletHit = null;
        _hit.gearCategoryId = 0;
    }

    internal void ResolveHit(PlayerAgent owner, in ShotTuning tuning, Vector3 start, Vector3 direction, RaycastHit hit)
    {
        if (!float.IsFinite(direction.sqrMagnitude) || direction.sqrMagnitude < 0.00000001f)
            throw new InvalidOperationException("Energy limb hit has no travel direction.");
        Prepare(owner, tuning, start, direction.normalized, tuning.Range);
        _hit.rayHit = hit;
        Start = start;
        End = hit.point;
        HasHit = true;
    }

    internal void Apply()
    {
        if (HasHit) VanillaDamage.Hit(_hit);
    }

    internal void Apply(float damage, float stagger)
    {
        _hit.damage = damage; _hit.staggerMulti = stagger;
        Apply();
    }

    internal static RaycastHit OverlapContact(Collider collider, Vector3 origin, Vector3 direction)
        // Query metadata only. ContactTarget replaces it with a physics ray before damage.
        => new() { m_Collider = collider.GetInstanceID(), point = collider.ClosestPoint(origin), normal = -direction, distance = 0f };
}
