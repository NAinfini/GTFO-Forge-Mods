using System;
using Enemies;
using ForgeVisual;
using Gear;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>The lent tracking rifle. When the local shooter fires, the enemy nearest the crosshair inside the weapon's cone and reach
/// (with nothing but that enemy in the way) is locked: the game's own bullet is withheld, as the grenade launcher's is, and the hit is
/// resolved from the muzzle to that enemy through ForgeCore's <c>Combat.Shot</c> (the game's own bullet resolution: limb, weak spot,
/// armour, replication, the hit marker). The shooter's peer decides the hit, exactly as for a stock bullet; the host settles the damage
/// as it does for any bullet. Every peer draws a glowing tracer (<c>ArcTrails</c>) that bends out and then onto the target from the shooter's one small
/// <see cref="HomingShot"/> message; the tracer decides nothing. Choosing the target is <see cref="ArsenalCombat"/>, the curve <see cref="ArcTrails"/>;
/// what stays here is this weapon's content and its one message. A shot with no enemy in the cone is the game's bullet, untouched.</summary>
internal static class HomingFire
{
    private const float TracerSpeed = 70f, TracerMin = 0.22f, TracerMax = 0.6f;

    private static readonly ForgeCore.Combat.Shot Shot = new();
    private static EnemyAgent? _locked;
    private static uint _seed;
    private static TrailStyle? _style;
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Before(weapon =>
        {
            try { Lock(weapon); }
            catch (Exception error) { WithheldBullet.Homing = false; _locked = null; Plugin.Logger.ErrorOnce("Tracking rifle lock failed: " + error); }
        });
        ForgeCore.GameHooks.Fire.BulletWeapon.After((weapon, _) =>
        {
            try { Resolve(weapon); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Tracking rifle hit failed: " + error); }
            finally { WithheldBullet.Homing = false; _locked = null; }
        });
    }

    /// <summary>Before the game's fire: lock the enemy (<c>ArsenalCombat.PickAimed</c>) and withhold the game's bullet.</summary>
    private static void Lock(BulletWeapon weapon)
    {
        WithheldBullet.Homing = false;
        _locked = null;
        var homing = HeavyWeapons.ActiveHoming;
        var owner = weapon.Owner;
        if (homing == null || owner == null || owner != ForgeCore.Players.Local || weapon.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial) return;
        var camera = owner.FPSCamera.transform;
        _locked = ArsenalCombat.PickAimed(owner, camera.position, camera.forward, homing.Angle, homing.Range);
        WithheldBullet.Homing = _locked != null;
    }
    /// <summary>After the game's fire: the locked enemy is hit from the muzzle through the game''s bullet resolution, and the tracer goes out.</summary>
    private static void Resolve(BulletWeapon weapon)
    {
        var enemy = _locked;
        var homing = HeavyWeapons.ActiveHoming;
        WithheldBullet.Homing = false;
        if (enemy == null || enemy.WasCollected || !enemy.Alive || homing == null) return;
        var owner = weapon.Owner;
        var data = weapon.ArchetypeData;
        var camera = owner.FPSCamera.transform;
        var toward = (ForgeCore.Enemies.AimPoint(enemy) - camera.position).normalized;
        // The damage the game gives this shooter's bullet, with the boosters on (the same call the game's own fire makes), not the block's bare number.
        var damage = data.GetDamageWithBoosterEffect(owner, weapon.ItemDataBlock.inventorySlot);
        Shot.Trace(weapon, new ForgeCore.Combat.BulletProfile(damage, data.StaggerDamageMulti, data.PrecisionDamageMulti, homing.Range), camera.position, toward);
        if (!Shot.HasHit || Shot.Receiver == null || Shot.Receiver.GetBaseAgent()?.Pointer != enemy.Pointer)
        {
            // The game's own bullet is already withheld and its ammunition spent: a lock the muzzle cannot reach is told, not lost without a word.
            Plugin.Logger.ErrorOnce("arsenal-homing-trace-missed", "the tracking rifle's locked enemy could not be hit from the muzzle (cover between the muzzle and it); that shot did nothing.");
            return;
        }
        Shot.Apply();
        var message = new HomingShot
        {
            Shooter = SNet.LocalPlayer.Lookup,
            Seed = ++_seed,
            OX = Shot.Start.x,
            OY = Shot.Start.y,
            OZ = Shot.Start.z,
            TX = Shot.End.x,
            TY = Shot.End.y,
            TZ = Shot.End.z,
        };
        Show(message);
        if (ForgeCore.Session.IsHost) ArsenalNetwork.Broadcast(HomingShot.Name, message);
        else if (SNet.Master != null) ArsenalNetwork.ToHost(HomingShot.Name, message);
    }

    /// <summary>The host passes a player's tracer on after checking its sender and size; a client shows only the host's, and not its own again.</summary>
    internal static void OnShot(ulong sender, HomingShot shot)
    {
        var from = new Vector3(shot.OX, shot.OY, shot.OZ);
        var to = new Vector3(shot.TX, shot.TY, shot.TZ);
        if (!float.IsFinite(from.x + from.y + from.z + to.x + to.y + to.z) || (to - from).sqrMagnitude > HomingShot.MaxReach * HomingShot.MaxReach) return;
        if (ForgeCore.Session.IsHost)
        {
            if (sender != shot.Shooter || !ArsenalNetwork.Channel.IsPlayer(sender)) return;
            ArsenalNetwork.Broadcast(HomingShot.Name, shot);
        }
        else if (!ArsenalNetwork.Channel.FromHost(sender)) return;
        if (shot.Shooter == SNet.LocalPlayer?.Lookup) return;
        Show(shot);
    }

    private static void Show(in HomingShot shot)
    {
        ArsenalAudio.PlayShot(ArsenalAudio.Cue.HomingFire, new Vector3(shot.OX, shot.OY, shot.OZ), shot.Shooter == SNet.LocalPlayer?.Lookup);
        var sheets = ArsenalArt.Sheets;
        _style ??= new TrailStyle
        {
            RibbonPoints = 22,
            RibbonStep = 0.012f,
            RibbonWidth = 0.13f,
            RibbonHead = new Color(0.55f, 0.95f, 1f, 1f),
            RibbonTail = new Color(0.2f, 0.6f, 1f, 0f),
            RibbonBlend = FxBlend.Additive,
            Glow = sheets?.Glow,
            GlowSize = 0.55f,
            GlowColour = new Color(0.5f, 0.9f, 1f, 0.9f),
            HeadBack = 0f,
            Flicker = 0.2f,
        };
        ArcTrails.Start(new Vector3(shot.OX, shot.OY, shot.OZ), new Vector3(shot.TX, shot.TY, shot.TZ), shot.Seed, _style, TracerSpeed, TracerMin, TracerMax);
    }
    internal static void Clear()
    {
        WithheldBullet.Homing = false;
        _locked = null;
        ArcTrails.Clear();
    }
}
