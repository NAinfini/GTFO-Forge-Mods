using System;
using System.Collections.Generic;
using ForgeVisual;
using Gear;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceArsenal;

/// <summary>The lent grenade launcher: the game's own weapon fires (ammunition, recoil, animation and sound stay the game's),
/// its bullet is withheld on the shooter's side, and a grenade flies instead. Every peer flies it the same way from the
/// shooter's start and velocity; the host's flight decides where it bursts, and the burst is the game's explosion.</summary>
internal static class Grenades
{
    private const float Gravity = 9.81f;
    private const float Lifetime = 6f;
    private const float Spawn = 0.7f;
    private static bool _suppress;
    private static uint _nextShot;
    private static FxHandle? _explosion;

    private sealed class Flight
    {
        internal ulong Shooter;
        internal Guid Loan;
        internal uint Sequence;
        internal Vector3 Position, Velocity;
        internal float Damage, Radius, Ends;
        internal GameObject? Look;
    }

    private static readonly List<Flight> Flights = new();
    private static readonly List<(GrenadeShot Shot, float At)> Pending = new();
    private static readonly Dictionary<(ulong, Guid, uint), float> Settled = new();
    private static readonly List<(ulong, Guid, uint)> Expired = new();

    /// <summary>Set around the game's <c>Fire</c> of the local lent launcher; the bullet hit is skipped meanwhile.</summary>
    internal static bool Suppressing => _suppress;

    internal static void BeforeFire(BulletWeapon weapon)
    {
        var launcher = HeavyWeapons.ActiveLauncher;
        if (launcher == null || weapon.Owner == null || !weapon.Owner.IsLocallyOwned || weapon.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial) return;
        _suppress = true;
    }

    internal static void AfterFire(BulletWeapon weapon)
    {
        if (!_suppress) return;
        _suppress = false;
        var launcher = HeavyWeapons.ActiveLauncher;
        var agent = weapon.Owner;
        if (launcher == null || agent == null) return;
        var eye = agent.FPSCamera.transform;
        var shot = new GrenadeShot
        {
            Shooter = SNet.LocalPlayer.Lookup, Loan = HeavyWeapons.ActiveLauncherIdentity, Sequence = ++_nextShot,
            OX = eye.position.x, OY = eye.position.y, OZ = eye.position.z,
            VX = eye.forward.x, VY = eye.forward.y, VZ = eye.forward.z,
        };
        if (SNet.IsMaster) OnShot(SNet.LocalPlayer.Lookup, shot);
        else
        {
            // Client prediction is visual only. Host acceptance uses its independent loan and definition.
            var predicted = shot;
            var speed = launcher.Spec.Grenade!.Speed;
            predicted.VX *= speed;
            predicted.VY = predicted.VY * speed + 1.5f;
            predicted.VZ *= speed;
            Launch(predicted);
            ArsenalNetwork.ToHost(GrenadeShot.Name, shot);
        }
    }

    /// <summary>The host admits local and remote shots through the same loan budget; peers fly only host broadcasts.</summary>
    internal static void OnShot(ulong sender, GrenadeShot shot)
    {
        if (SNet.IsMaster)
        {
            if (!HeavyWeapons.AuthorizeGrenade(sender, ref shot, out var delay, out var reason))
            {
                return;
            }
            if (delay > 0f)
            {
                Pending.Add((shot, Time.time + delay));
                return;
            }
            LaunchHost(shot);
            return;
        }
        else if (SNet.Master == null || sender != SNet.Master.Lookup) return;
        // Replace the shooter's predicted trajectory too: everyone follows the admitted host origin and velocity.
        RemoveFlight(shot.Shooter, shot.Loan, shot.Sequence);
        if (Settled.ContainsKey((shot.Shooter, shot.Loan, shot.Sequence))) return;
        Launch(shot);
    }

    private static void LaunchHost(GrenadeShot shot)
    {
        ArsenalNetwork.Broadcast(GrenadeShot.Name, shot);
        Launch(shot);
    }

    private static void Launch(GrenadeShot s)
    {
        var flight = new Flight
        {
            Shooter = s.Shooter, Loan = s.Loan, Sequence = s.Sequence,
            Position = new Vector3(s.OX, s.OY, s.OZ), Velocity = new Vector3(s.VX, s.VY, s.VZ),
            Damage = s.Damage, Radius = s.Radius, Ends = Time.time + Lifetime,
        };
        if (!float.IsFinite(flight.Position.x + flight.Position.y + flight.Position.z + flight.Velocity.x + flight.Velocity.y + flight.Velocity.z)) return;
        // Sweep from the eye before applying the spawn offset; a nearby wall or door must stop the grenade.
        var forward = (flight.Velocity - Vector3.up * 1.5f).normalized;
        if (SNet.IsMaster && Physics.Raycast(flight.Position, forward, out var hit, Spawn,
                LayerManager.MASK_WORLD | LayerManager.MASK_ENEMY_DAMAGABLE, QueryTriggerInteraction.Ignore))
        {
            Burst(flight, hit.point + hit.normal * 0.1f);
            return;
        }
        flight.Position += forward * Spawn;
        var model = ArsenalArt.Bomblet;
        if (model != null) flight.Look = model.Spawn(flight.Position, Quaternion.LookRotation(flight.Velocity) * ArsenalArt.FrontToForward);
        Flights.Add(flight);
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.GrenadeFire, flight.Position);
    }

    internal static void Tick(float now)
    {
        for (var i = 0; i < Pending.Count;)
        {
            var pending = Pending[i];
            if (now < pending.At) { i++; continue; }
            Pending.RemoveAt(i);
            if (SNet.IsMaster) LaunchHost(pending.Shot);
        }
        Expired.Clear();
        foreach (var pair in Settled) if (now >= pair.Value) Expired.Add(pair.Key);
        foreach (var key in Expired) Settled.Remove(key);
        if (Flights.Count == 0) return;
        var dt = Time.deltaTime;
        var mask = LayerManager.MASK_WORLD | LayerManager.MASK_ENEMY_DAMAGABLE;
        for (var i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            try
            {
                var next = f.Position + f.Velocity * dt;
                f.Velocity += Vector3.down * (Gravity * dt);
                var step = next - f.Position;
                if (!SNet.IsMaster && now >= f.Ends + 2f)
                {
                    Flights.RemoveAt(i);
                    if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
                    Plugin.Error("Grenade settlement timed out for " + f.Shooter + "/" + f.Sequence + "; projectile removed without a predicted explosion.");
                    continue;
                }
                var burst = SNet.IsMaster && now >= f.Ends;
                var at = next;
                if (SNet.IsMaster && !burst && Physics.Raycast(f.Position, step.normalized, out var hit, step.magnitude, mask, QueryTriggerInteraction.Ignore))
                {
                    burst = true;
                    at = hit.point + hit.normal * 0.1f;
                }
                f.Position = at;
                if (f.Look != null && !f.Look.WasCollected) { f.Look.transform.position = at; if (f.Velocity.sqrMagnitude > 0.01f) f.Look.transform.rotation = Quaternion.LookRotation(f.Velocity) * ArsenalArt.FrontToForward; }
                if (!burst) continue;
                Burst(f, at);
                Flights.RemoveAt(i);
            }
            catch (Exception error)
            {
                Flights.RemoveAt(i);
                if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
                Plugin.Error("A grenade failed: " + error);
            }
        }
    }

    private static void Burst(Flight f, Vector3 at)
    {
        if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
        ForgeCore.Combat.Explode(at, f.Radius, f.Damage, 600f);
        Settled[(f.Shooter, f.Loan, f.Sequence)] = Time.time + Lifetime + 2f;
        ArsenalNetwork.Broadcast(GrenadeBurst.Name, new GrenadeBurst
        {
            Shooter = f.Shooter, Loan = f.Loan, Sequence = f.Sequence, X = at.x, Y = at.y, Z = at.z,
        });
        ShowBurst(at);
    }

    internal static void OnBurst(ulong sender, GrenadeBurst burst)
    {
        if (SNet.IsMaster || SNet.Master == null || sender != SNet.Master.Lookup
            || burst.Shooter == 0 || burst.Loan == Guid.Empty || burst.Sequence == 0
            || !float.IsFinite(burst.X) || !float.IsFinite(burst.Y) || !float.IsFinite(burst.Z)) return;
        var key = (burst.Shooter, burst.Loan, burst.Sequence);
        if (!Settled.TryAdd(key, Time.time + Lifetime + 2f)) return;
        RemoveFlight(burst.Shooter, burst.Loan, burst.Sequence);
        ShowBurst(new Vector3(burst.X, burst.Y, burst.Z));
    }

    private static void RemoveFlight(ulong shooter, Guid loan, uint sequence)
    {
        for (var i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            if (f.Shooter != shooter || f.Loan != loan || f.Sequence != sequence) continue;
            if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
            Flights.RemoveAt(i);
        }
    }

    private static void ShowBurst(Vector3 at)
    {
        // The game's own explosion effect (the trip mine's), drawn on this peer only.
        _explosion ??= Fx.Find("Game:FX_Tripmine");
        if (_explosion != null) Fx.Play(_explosion, at, Vector3.up);
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.GrenadeBurst, at);
    }

    internal static void Clear()
    {
        foreach (var f in Flights) if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
        Flights.Clear();
        Pending.Clear();
        Settled.Clear();
        Expired.Clear();
        _suppress = false;
    }
}

/// <summary>Adds the selected mechanical pump sound when the local borrowed grenade launcher starts a real reload.</summary>
internal static class LauncherReload
{
    internal static void Subscribe() => ForgeCore.GameHooks.ReloadTriggered.Around(
        item => item.IsReloading,
        (item, wasReloading, ran) => Postfix(item, ran, wasReloading),
        null);

    private static void Postfix(ItemEquippable __instance, bool __runOriginal, bool __state)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!__runOriginal || __state || !__instance.IsReloading || __instance.Owner == null || !__instance.Owner.IsLocallyOwned
            || __instance.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial
            || HeavyWeapons.ActiveLauncher == null
            || backpack == null || !backpack.TryGetBackpackItem(InventorySlot.GearSpecial, out var item)
            || item?.Instance != __instance) return;
        ArsenalAudio.PlayAttached(ArsenalAudio.Cue.GrenadeReload, __instance.gameObject,
            (int)__instance.Owner.DimensionIndex, true);
    }
}

/// <summary>The launcher's firing, seen around ForgeCore's one patch of <c>BulletWeapon.Fire</c>.</summary>
internal static class LauncherFire
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Before(weapon =>
        {
            try { Grenades.BeforeFire(weapon); }
            catch (Exception error) { Plugin.Error("Grenade launcher fire hook failed: " + error); }
        });
        ForgeCore.GameHooks.Fire.BulletWeapon.After((weapon, _) =>
        {
            try { Grenades.AfterFire(weapon); }
            catch (Exception error) { Plugin.Error("Grenade launcher fire hook failed: " + error); }
        });
    }
}

internal static class LauncherBullet
{
    /// <summary>The launcher's own bullet does not exist: no damage, no impact effect.</summary>
    internal static void Subscribe() => ForgeCore.GameHooks.BulletHit.Before(call =>
    {
        if (Grenades.Suppressing) call.Skip(false);
    });
}
