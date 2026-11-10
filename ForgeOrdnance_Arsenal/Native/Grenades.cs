using System;
using System.Collections.Generic;
using Enemies;
using ForgeVisual;
using Gear;
using Player;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceArsenal;

/// <summary>The lent grenade launcher: the game's own weapon fires (ammunition, recoil, animation and sound stay the game's),
/// its bullet is withheld on the shooter's side, and a grenade flies instead. Every peer flies it the same way from the
/// shooter's start and velocity; the host's flight decides where it bursts, and the burst is the slot's effect.</summary>
internal static class Grenades
{
    private const float Gravity = 9.81f;
    private const float Lifetime = 6f;
    private const float Spawn = 0.7f;
    private const float MuzzleReach = 2f;
    /// <summary>How fat the round is against an enemy (m): a thin ray passed between a running enemy's limbs.</summary>
    private const float RoundRadius = 0.15f;
    /// <summary>How long a shooter's predicted round flies without the host's answer (s). The host refuses in silence, and an admitted round waits at most one shot
    /// interval on the host's timeline, whose gun fired no sooner here; past this the round was refused or lost.</summary>
    private const float PredictionWindow = 1.5f;
    private static uint _nextShot;

    private sealed class Flight
    {
        /// <summary>The ribbon and smoke the round leaves along its flight, in the world where it has been; null before it first moves.</summary>
        internal Trail? Trail;
        internal ulong Shooter;
        internal Guid Loan;
        internal uint Sequence;
        internal Vector3 Position, Velocity;
        internal float Damage, Radius, Ends;
        internal float TrueEdge = -1f;
        /// <summary>Above 0: the shooter's own prediction, gone at this time unless the host's broadcast replaced it.</summary>
        internal float PredictedUntil;
        internal GameObject? Look;
        /// <summary>The enemy the round struck, whatever the blast's line to it says.</summary>
        internal EnemyAgent? Struck;
    }

    private static readonly List<Flight> Flights = new();
    private static readonly List<(GrenadeShot Shot, float At)> Pending = new();
    private static readonly Dictionary<(ulong, Guid, uint), float> Settled = new();
    private static readonly List<(ulong, Guid, uint)> Expired = new();

    internal static void BeforeFire(BulletWeapon weapon)
    {
        var launcher = HeavyWeapons.ActiveLauncher;
        if (launcher == null || weapon.Owner == null || weapon.Owner != ForgeCore.Players.Local || weapon.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial) return;
        WithheldBullet.Launcher = true;
    }

    internal static void AfterFire(BulletWeapon weapon)
    {
        if (!WithheldBullet.Launcher) return;
        WithheldBullet.Launcher = false;
        var launcher = HeavyWeapons.ActiveLauncher;
        var agent = weapon.Owner;
        if (launcher == null || agent == null) return;
        var eye = agent.FPSCamera.transform;
        var origin = LaunchPoint(weapon, eye);
        var shot = new GrenadeShot
        {
            Shooter = SNet.LocalPlayer.Lookup,
            Loan = HeavyWeapons.ActiveLauncherIdentity,
            Sequence = ++_nextShot,
            OX = origin.x,
            OY = origin.y,
            OZ = origin.z,
            VX = eye.forward.x,
            VY = eye.forward.y,
            VZ = eye.forward.z,
        };
        if (ForgeCore.Session.IsHost) OnShot(SNet.LocalPlayer.Lookup, shot);
        else
        {
            // Client prediction is visual only. Host acceptance uses its independent loan and definition.
            var predicted = shot;
            var grenade = launcher.Spec.Grenade!;
            var speed = grenade.Speed;
            predicted.VX *= speed;
            predicted.VY = predicted.VY * speed + 1.5f;
            predicted.VZ *= speed;
            predicted.TrueEdge = grenade.TrueDamage ? grenade.EdgeShare : -1f;
            Launch(predicted, Time.time + PredictionWindow);
            ArsenalNetwork.ToHost(GrenadeShot.Name, shot);
        }
    }

    /// <summary>Where the round leaves the gun: the bore point ForgeVisual's gun mount bound to the weapon (the aim direction stays the
    /// camera's), or, with no bore point or one outside the host's reach proof, the eye plus <see cref="Spawn"/> along the aim.</summary>
    private static Vector3 LaunchPoint(BulletWeapon weapon, Transform eye)
    {
        var muzzle = weapon.MuzzleAlign;
        if (muzzle != null && !muzzle.WasCollected)
        {
            var at = ForgeCore.Combat.ShownMuzzle(weapon, eye.position);
            if (float.IsFinite(at.x + at.y + at.z) && (at - eye.position).sqrMagnitude <= MuzzleReach * MuzzleReach) return at;
        }
        return eye.position + eye.forward * Spawn;
    }

    /// <summary>The host admits local and remote shots through the same loan budget; peers fly only host broadcasts.</summary>
    internal static void OnShot(ulong sender, GrenadeShot shot)
    {
        if (ForgeCore.Session.IsHost)
        {
            if (!HeavyWeapons.AuthorizeGrenade(sender, ref shot, out var delay, out var reason))
            {
                Plugin.Logger.ErrorOnce("arsenal-launcher-refused", "a launcher shot was refused: " + reason + ".");
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
        else if (!ArsenalNetwork.Channel.FromHost(sender)) return;
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

    private static void Launch(GrenadeShot s, float predictedUntil = 0f)
    {
        var flight = new Flight
        {
            Shooter = s.Shooter,
            Loan = s.Loan,
            Sequence = s.Sequence,
            Position = new Vector3(s.OX, s.OY, s.OZ),
            Velocity = new Vector3(s.VX, s.VY, s.VZ),
            Damage = s.Damage,
            Radius = s.Radius,
            TrueEdge = s.TrueEdge,
            Ends = Time.time + Lifetime,
            PredictedUntil = predictedUntil,
        };
        if (!float.IsFinite(flight.Position.x + flight.Position.y + flight.Position.z + flight.Velocity.x + flight.Velocity.y + flight.Velocity.z)) return;
        // The origin is already where the round leaves the gun; a wall or door right in front of it still stops the grenade.
        var forward = (flight.Velocity - Vector3.up * 1.5f).normalized;
        // The flight comes first: nothing that only shows may stop the round.
        Flights.Add(flight);
        try { ShowLaunch(flight, forward, s.Shooter != SNet.LocalPlayer.Lookup || ForgeCore.Session.IsHost || predictedUntil > 0f); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-launcher-look-failed", "a launcher shot's look failed: " + error); }
        if (!ForgeCore.Session.IsHost) return;
        // Fired into an enemy at arm's length: a cast does not hit a collider it starts inside.
        var close = ForgeCore.Enemies.Near(flight.Position, RoundRadius);
        if (close.Length != 0) flight.Struck = close[0];
        var strike = close.Length != 0;
        var strikeAt = flight.Position;
        if (!strike && Sweep(flight.Position, forward, Spawn, out var hit, out flight.Struck))
        {
            strike = true;
            strikeAt = hit.point + hit.normal * 0.1f;
        }
        if (!strike) return;
        Flights.Remove(flight);
        Burst(flight, strikeAt);
    }

    /// <summary>The launch is seen on every peer from the host's accepted shot: the recoilless rifle's round (true-damage blast) is a rocket, with a
    /// muzzle flame and a back-blast; any other launcher round is a 40 mm grenade. The shooter's prediction already showed the flash and sound (<paramref name="flash"/> false for the host's copy of it).</summary>
    private static void ShowLaunch(Flight flight, Vector3 forward, bool flash)
    {
        var model = flight.TrueEdge >= 0f ? ArsenalArt.Rocket : ArsenalArt.Grenade;
        if (model != null) flight.Look = model.Spawn(flight.Position, Quaternion.LookRotation(flight.Velocity) * ArsenalArt.FrontToForward);
        if (!flash) return;
        if (flight.TrueEdge >= 0f)
        {
            ArsenalFxSlots.Play(ArsenalFxSlots.RocketMuzzle, flight.Position, forward);
            ArsenalFxSlots.Play(ArsenalFxSlots.RocketBackblast, flight.Position - forward * HeavyWeapons.HeldLength(flight.Shooter), -forward); // out of the tube's back, not back over the shooter's face from the muzzle
            ArsenalAudio.PlayAt(ArsenalAudio.Cue.RecoillessFire, flight.Position);
            ArsenalAudio.PlayAt(ArsenalAudio.Cue.RecoillessBackblast, flight.Position);
        }
        else
        {
            ArsenalFxSlots.Play(ArsenalFxSlots.GrenadeMuzzle, flight.Position, forward);
            ArsenalAudio.PlayAt(ArsenalAudio.Cue.GrenadeFire, flight.Position);
        }
    }

    internal static void Tick(float now)
    {
        for (var i = 0; i < Pending.Count;)
        {
            var pending = Pending[i];
            if (now < pending.At) { i++; continue; }
            Pending.RemoveAt(i);
            if (ForgeCore.Session.IsHost) LaunchHost(pending.Shot);
        }
        Expired.Clear();
        foreach (var pair in Settled) if (now >= pair.Value) Expired.Add(pair.Key);
        foreach (var key in Expired) Settled.Remove(key);
        if (Flights.Count == 0) return;
        var dt = Time.deltaTime;
        for (var i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            try
            {
                var next = f.Position + f.Velocity * dt;
                f.Velocity += Vector3.down * (Gravity * dt);
                var step = next - f.Position;
                if (!ForgeCore.Session.IsHost && f.PredictedUntil > 0f && now >= f.PredictedUntil)
                {
                    Flights.RemoveAt(i);
                    Retire(f);
                    Plugin.Logger.ErrorOnce("arsenal-launcher-host-silent", "the host did not answer launcher shot " + f.Sequence + " in time; the predicted round is removed.");
                    continue;
                }
                if (!ForgeCore.Session.IsHost && now >= f.Ends + 2f)
                {
                    Flights.RemoveAt(i);
                    Retire(f);
                    Plugin.Logger.ErrorOnce("Grenade settlement timed out for " + f.Shooter + "/" + f.Sequence + "; projectile removed without a predicted explosion.");
                    continue;
                }
                var burst = ForgeCore.Session.IsHost && now >= f.Ends;
                var at = next;
                if (ForgeCore.Session.IsHost && !burst && Sweep(f.Position, step.normalized, step.magnitude, out var hit, out f.Struck))
                {
                    burst = true;
                    at = hit.point + hit.normal * 0.1f;
                }
                f.Position = at;
                if (burst)
                {
                    Flights.RemoveAt(i);
                    Burst(f, at);
                    continue;
                }
                try
                {
                    f.Trail ??= ShellTrails.Rent(at, f.TrueEdge >= 0f);
                    f.Trail?.Move(at, 0f);
                    if (f.Look != null && !f.Look.WasCollected) { f.Look.transform.position = at; if (f.Velocity.sqrMagnitude > 0.01f) f.Look.transform.rotation = Quaternion.LookRotation(f.Velocity) * ArsenalArt.FrontToForward; }
                }
                catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-launcher-round-look-failed", "a launcher round's look failed: " + error); }
            }
            catch (Exception error)
            {
                Flights.Remove(f);
                Retire(f);
                Plugin.Logger.ErrorOnce("A grenade failed: " + error);
            }
        }
    }

    /// <summary>The round is over (burst, replaced or lost): its trail ends where it is, the ribbon gathers into that point and the smoke left behind thins out by itself, and its model goes.</summary>
    private static void Retire(Flight f)
    {
        f.Trail?.End();
        f.Trail = null;
        if (f.Look != null && !f.Look.WasCollected) Object.Destroy(f.Look);
    }

    /// <summary>Whether anything solid (the world or an enemy) lies within <paramref name="distance"/> of <paramref name="from"/> along <paramref name="direction"/>.</summary>
    internal static bool Blocked(Vector3 from, Vector3 direction, float distance) => Sweep(from, direction, distance, out _, out _);

    private static readonly ForgeCore.SweepQuery WorldSweep = new(4), EnemySweep = new(8);

    /// <summary>The nearest level or enemy hit. An enemy's damage colliders are triggers, so they are cast with triggers on (as the game's bullet
    /// ray and ForgeCore's enemy queries do); the level keeps trigger volumes ignored. Casting both with triggers off flew the round through enemies.</summary>
    private static bool Sweep(Vector3 from, Vector3 direction, float distance, out RaycastHit hit, out EnemyAgent? struck)
    {
        struck = null;
        var world = WorldSweep.Cast(from, 0f, direction, distance, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore) > 0;
        var enemy = EnemySweep.Cast(from, RoundRadius, direction, distance, LayerManager.MASK_ENEMY_DAMAGABLE, QueryTriggerInteraction.Collide) > 0;
        var enemyFirst = enemy && (!world || EnemySweep[0].distance < WorldSweep[0].distance);
        hit = enemyFirst ? EnemySweep[0] : world ? WorldSweep[0] : default;
        if (enemyFirst) struck = hit.collider.GetComponentInParent<EnemyAgent>();
        return world || enemy;
    }

    /// <summary>The damage first and each step on its own: an effect, a sound or a message that fails takes nothing from the blast, and a failed blast still shows.</summary>
    private static void Burst(Flight f, Vector3 at)
    {
        try
        {
            if (f.TrueEdge >= 0f) ForgeCore.Combat.AreaDamage(at, f.Radius, f.Damage, f.TrueEdge, 600f, ForgeCore.BlastLine.FromBurst, struck: f.Struck);
            else ForgeCore.Combat.Explode(at, f.Radius, f.Damage, 600f);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-launcher-blast-failed", "a launcher round's blast failed: " + error); }
        Settled[(f.Shooter, f.Loan, f.Sequence)] = Time.time + Lifetime + 2f;
        try
        {
            ArsenalNetwork.Broadcast(GrenadeBurst.Name, new GrenadeBurst
            {
                Shooter = f.Shooter,
                Loan = f.Loan,
                Sequence = f.Sequence,
                X = at.x,
                Y = at.y,
                Z = at.z,
                Rocket = (byte)(f.TrueEdge >= 0f ? 1 : 0),
            });
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-launcher-burst-message-failed", "a launcher round's burst message failed: " + error); }
        try
        {
            f.Trail?.Move(at, 0f); // the ribbon runs on to where it burst
            Retire(f);
            ShowBurst(at, f.TrueEdge >= 0f);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("arsenal-launcher-burst-look-failed", "a launcher round's burst look failed: " + error); }
    }

    internal static void OnBurst(ulong sender, GrenadeBurst burst)
    {
        if (!ArsenalNetwork.Channel.FromHost(sender)
            || burst.Shooter == 0 || burst.Loan == Guid.Empty || burst.Sequence == 0 || burst.Rocket > 1
            || !float.IsFinite(burst.X) || !float.IsFinite(burst.Y) || !float.IsFinite(burst.Z)) return;
        var key = (burst.Shooter, burst.Loan, burst.Sequence);
        if (!Settled.TryAdd(key, Time.time + Lifetime + 2f)) return;
        RemoveFlight(burst.Shooter, burst.Loan, burst.Sequence);
        ShowBurst(new Vector3(burst.X, burst.Y, burst.Z), burst.Rocket == 1);
    }

    private static void RemoveFlight(ulong shooter, Guid loan, uint sequence)
    {
        for (var i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            if (f.Shooter != shooter || f.Loan != loan || f.Sequence != sequence) continue;
            Retire(f);
            Flights.RemoveAt(i);
        }
    }

    internal static void ShowBurst(Vector3 at, bool rocket = false)
    {
        var boom = rocket ? ArsenalAudio.Cue.RecoillessBurst : ArsenalAudio.Cue.GrenadeBurst;
        if (rocket) ArsenalFxSlots.Play(ArsenalFxSlots.RocketBlast, at, Vector3.up);
        else HeavyBlast.Play(at);
        ArsenalAudio.PlayAt(boom, at);
    }

    internal static void Clear()
    {
        foreach (var f in Flights) Retire(f);
        Flights.Clear();
        Pending.Clear();
        Settled.Clear();
        Expired.Clear();
        WithheldBullet.Launcher = false;
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
        if (!__runOriginal || __state || !__instance.IsReloading || __instance.Owner == null || __instance.Owner != ForgeCore.Players.Local
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
        // Around, not Before and After: a Fire body that throws never reaches an After, and the faulted end takes the withheld-bullet flag back down.
        ForgeCore.GameHooks.Fire.BulletWeapon.Around(
            weapon =>
            {
                try { Grenades.BeforeFire(weapon); }
                catch (Exception error) { Plugin.Logger.ErrorOnce("Grenade launcher fire hook failed: " + error); }
                return true;
            },
            (weapon, _, _) =>
            {
                try { Grenades.AfterFire(weapon); HeavyFx.AfterFire(weapon); }
                catch (Exception error) { Plugin.Logger.ErrorOnce("Grenade launcher fire hook failed: " + error); }
            },
            (_, _, _) => WithheldBullet.Launcher = false);
        // Another player's gun: the game's synced fire bodies (a remote player's shot reaches this peer through them).
        ForgeCore.GameHooks.Fire.BulletWeaponSynced.After((weapon, _) =>
        {
            try { HeavyFx.AfterSyncedFire(weapon); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Heavy weapon remote effect failed: " + error); }
        });
    }
}

/// <summary>The bullet of a lent weapon that fires something else does not exist: no damage, no impact effect. The flag is set around the
/// game's <c>Fire</c> of the local lent launcher (<see cref="Launcher"/>) or of a tracking rifle shot that has a locked enemy
/// (<see cref="Homing"/>); the bullet hit is skipped meanwhile.</summary>
internal static class WithheldBullet
{
    internal static bool Launcher, Homing = false;

    internal static void Subscribe() => ForgeCore.GameHooks.BulletHit.Before(call =>
    {
        if (Launcher || Homing) call.Skip(false);
    });
}
