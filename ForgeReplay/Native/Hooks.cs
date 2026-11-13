using System;
using Agents;
using ChainedPuzzles;
using Enemies;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The game methods the recorder listens to. A method no other package patches is a Harmony class below, put on separately; a
/// method other packages patch too is patched once by ForgeCore (<c>ForgeCore.GameHooks</c>) and the recorder subscribes to it
/// (<see cref="InstallBrackets"/>, <see cref="InstallMoreBrackets"/>). Each feature is put on alone, so a method the game no longer has
/// costs that feature only (one error in the log) and never the others. Every hook returns at once when no recording runs and never lets
/// an exception into the game. Players and enemies are not hooked here: they are read by polling (PlayerRecorder, EnemyRecorder).</summary>
internal static partial class Hooks
{
    private static readonly Type[] Features =
    {
        typeof(ShotHeavy), typeof(Melee),
        typeof(SecurityDoor), typeof(Scan), typeof(WaveStart), typeof(ChatMessage),
        typeof(Interact), typeof(Throw), typeof(Sentry), typeof(Mine), typeof(Tongue), typeof(Projectiles), typeof(Scout), typeof(Noise), typeof(Ping),
        typeof(Environment), typeof(Presence), typeof(OwnDamage), typeof(ReviveBy), typeof(Supply), typeof(Disinfection),
        typeof(Say), typeof(TerminalOutput), typeof(TerminalUse), typeof(LockHook), typeof(BulkheadHook), typeof(HsuHook), typeof(PortalHook), typeof(ClusterHook), typeof(TurbineHook), typeof(PuzzleClusterHook), typeof(LaidHook), typeof(ScanMovedHook), typeof(HeldHook), typeof(RemoteChargeHook), typeof(HeartbeatHook), typeof(MeleeHitHook),
    };

    // Read-only watchers of methods another package owns (ForgeEnemy patches the same sleep and wake-up methods): put on as observers
    // through ForgeCore, which lets them sit beside the owner.
    private static readonly Type[] Watchers = { typeof(Wake), typeof(WakeAnim), typeof(SleepSense) };

    internal static void Install()
    {
        var harmony = new Harmony(Plugin.Id + ".recorder");
        foreach (var feature in Features)
            try { harmony.CreateClassProcessor(feature).Patch(); }
            catch (Exception error) { Plugin.Logger.Error("The " + feature.Name + " recording hook could not be put on, so that part is not recorded: " + error.Message); }
        foreach (var watcher in Watchers)
            try { ForgeCore.Hooks.ObserveClass(Plugin.Id, watcher); }
            catch (Exception error) { Plugin.Logger.Error("The " + watcher.Name + " recording hook could not be put on, so that part is not recorded: " + error.Message); }
        InstallBrackets();
        InstallMoreBrackets();
        InstallWorldBrackets();
        ForgeCore.GameEvents.CheckpointReached += () => { if (Rec.On) { Rec.Begin().U(0); Rec.Emit(EventKind.Checkpoint); } };
        ForgeCore.GameEvents.MasterChanged += HostChanged;
        ForgeCore.Items.Spawned += (data, replicator) =>
        {
            if (!Rec.On) return;
            try
            {
                var p = data.position;
                Rec.Begin().U(data.itemData.itemID_gearCRC).Pos(p.x, p.y, p.z).U((ulong)data.mode);
                Rec.Emit(EventKind.ItemSpawn);
                NoteSpawned(data, replicator);
            }
            catch (Exception e) { Rec.Fail("item-spawn", e); }
        };
        // A reload puts the whole field back: the first block once the game has settled is a keyframe (PlayerRecorder.Tick).
        ForgeCore.GameEvents.CheckpointReloaded += () => { if (Rec.On) { Rec.Begin().U(1); Rec.Emit(EventKind.Checkpoint); PlayerRecorder.Unsettled(); } };
    }

    // One subscription to a ForgeCore bracket, made on its own like a Harmony class, so one that cannot be hung costs that feature only.
    private static void Subscribe(string feature, Action subscribe)
    {
        try { subscribe(); }
        catch (Exception error) { Plugin.Logger.Error("The " + feature + " recording hook could not be put on, so that part is not recorded: " + error.Message); }
    }

    private static void InstallBrackets()
    {
        Subscribe("Pack", () => ForgeCore.GameHooks.ResourcePackApplied.Before(PackUsed));
        Subscribe("EnemyDeath", () => ForgeCore.GameHooks.EnemyDead.Before(EnemyRecorder.NoteDead));
        Subscribe("PlayerHurt.Kind", () => ForgeCore.GameHooks.PlayerReceived.Before(PlayerReceived));
        Subscribe("PlayerHurt", () => ForgeCore.PlayerHits.Watch(Plugin.Id, PlayerHit));
        Subscribe("EnemyHurt.Settled", () => ForgeCore.GameHooks.EnemyDamageReceived.Around(SettleEnter, SettleLeave));
        Subscribe("HealthSync", () => ForgeCore.GameHooks.HealthSetReceived.Around(receiver => Rec.On ? receiver.Health : 0f, HealthSynced));
        Subscribe("EnemyLimb", () => ForgeCore.GameHooks.EnemyCustomLimbDestroyed.After(EnemyLimbDestroyed));
        Subscribe("EnemyHurt.Bullet", () => ForgeCore.GameHooks.EnemyBulletReceived.Before(call => Pending(call.Receiver, 0, Attacker(call.Data.source), call.Data.allowDirectionalBonus)));
        Subscribe("EnemyHurt.Melee", () => ForgeCore.GameHooks.EnemyMeleeReceived.Before(call => Pending(call.Receiver, 1, Attacker(call.Data.source), true)));
        Subscribe("EnemyHurt.Explosion", () => ForgeCore.GameHooks.EnemyExplosionReceived.Before(call =>
        {
            if (!Rec.On) return;
            // The packet names no source: the owner of the mine that just went off, the only thing players blow up.
            Pending(call.Receiver, 2, Mine.LastExploder(), false);
        }));
        Subscribe("Shots", () =>
        {
            WatchShooter(ForgeCore.GameHooks.Fire.BulletWeapon, weapon => weapon);
            WatchShooter(ForgeCore.GameHooks.Fire.Shotgun, weapon => weapon);
            WatchShooter(ForgeCore.GameHooks.Fire.BulletWeaponSynced, weapon => weapon);
            WatchShooter(ForgeCore.GameHooks.Fire.ShotgunSynced, weapon => weapon);
        });
        Subscribe("ShotLight", () => ForgeCore.GameHooks.AlignedRay.Around<bool>(RayEnter, RayExit));
        Subscribe("ShotPush", () => ForgeCore.GameHooks.PushEntered.After((push, _) => Melee.Swing(push.m_weapon.Owner, 2)));
        Subscribe("Revived", () => ForgeCore.GameHooks.PlayerRevived.Before(downed =>
        {
            if (!Rec.On) return;
            try { Rec.Begin().U((ulong)downed.m_owner.PlayerSlotIndex); Rec.Emit(EventKind.Revive); } catch (Exception e) { Rec.Fail("revive", e); }
        }));
        Subscribe("WeakDoor", () => ForgeCore.GameHooks.WeakDoorState.After((call, _) =>
        {
            try { DoorState(call.Door, Rec.Key(call.Door), call.State, call.Door.m_healthMax); } catch (Exception e) { Rec.Fail("door", e); }
        }));
        Subscribe("Puzzle", () => ForgeCore.GameHooks.PuzzleState.After((call, _) =>
        {
            try { World(EventKind.Puzzle, call.Puzzle, Rec.Key(call.Puzzle), (ulong)(int)call.New.status); } catch (Exception e) { Rec.Fail("puzzle", e); }
        }));
        Subscribe("Wake", () => ForgeCore.GameHooks.HibernateWakeActivated.After(Wake.Activated));
        Subscribe("WaveStop", () => ForgeCore.GameHooks.WaveStopped.After((wave, _) =>
        {
            if (!Rec.On) return;
            try { Rec.Begin().U(0).U(WaveId(wave)); Rec.Emit(EventKind.Wave); } catch (Exception e) { Rec.Fail("wave", e); }
        }));
        Subscribe("TerminalCommand", () => ForgeCore.GameHooks.TerminalCommand.After((call, _) =>
        {
            if (!Rec.On) return;
            try
            {
                var terminal = call.Interpreter.m_terminal;
                if (terminal == null) return;
                var p = terminal.transform.position;
                var key = Rec.Key(terminal);
                Rec.Begin().U(key).Pos(p.x, p.y, p.z).Text(call.Line).U((ulong)(int)call.Command).U(TerminalUserOf(key));
                Rec.Emit(EventKind.Terminal);
            }
            catch (Exception e) { Rec.Fail("terminal", e); }
        }));
        Subscribe("GeneratorState", () => ForgeCore.GameHooks.GeneratorState.After((call, _) =>
        {
            try { World(EventKind.Generator, call.Generator, Rec.Key(call.Generator), (ulong)(int)call.New.status, PlayerOf(call.New.playerThatChangedTheState)); } catch (Exception e) { Rec.Fail("generator", e); }
        }));
        Subscribe("ItemPicked", () => ForgeCore.GameHooks.PickupItemState.After((call, _) =>
        {
            try { ItemState(call.Item, Rec.Key(call.Item), call.New); } catch (Exception e) { Rec.Fail("item-state", e); }
        }));
    }

    // A wave's id: the wrapper object differs at every call, the game object does not.
    private static ulong WaveId(SurvivalWave wave) => (ulong)wave.Pointer.ToInt64();

    // Every hit on a player, whatever its kind, comes through the game's one accept path (ForgeCore.PlayerHits) with the damage the game
    // applies, so PlayerHurt is written there and only there. What kind it is, is known from the receive method running around it:
    // 0 bullet, 1 melee, 2 explosion, 3 tongue, 4 projectile, 5 fall, 6 anything else (fire, grabs, traps, parasites, no air).
    private const ulong OtherHurt = 6;
    private static ulong _hurtKind = OtherHurt;
    private static int _hurtKindFrame = -1;

    private static void HurtKind(ulong kind) { _hurtKind = kind; _hurtKindFrame = Time.frameCount; }

    private static void PlayerReceived(ForgeCore.PlayerReceive call)
    {
        if (!Rec.On) return;
        HurtKind(call.Kind switch
        {
            ForgeCore.PlayerReceiveKind.Bullet => 0UL,
            ForgeCore.PlayerReceiveKind.Melee => 1UL,
            ForgeCore.PlayerReceiveKind.Explosion => 2UL,
            ForgeCore.PlayerReceiveKind.TentacleAttack => 3UL,
            ForgeCore.PlayerReceiveKind.ShooterProjectile => 4UL,
            _ => 5UL,
        });
    }

    private static void PlayerHit(Dam_PlayerDamageBase receiver, float damage, Agent? source)
    {
        if (!Rec.On || damage <= 0f) return;
        try
        {
            var kind = _hurtKindFrame == Time.frameCount ? _hurtKind : OtherHurt;
            _hurtKindFrame = -1;
            var fromPlayer = source?.TryCast<PlayerAgent>();
            var attacker = fromPlayer != null || source == null ? 0UL : EnemyAttacker(source.TryCast<EnemyAgent>());
            PlayerDamage(receiver, kind, damage, attacker, fromPlayer == null ? 0UL : (ulong)fromPlayer.PlayerSlotIndex + 1);
            // The hit this machine settled on a player (the host's own player and the bots on the host): a client has no PlayerHurt for those.
            if (HostSync.Sending && receiver.Owner is { } owner && !owner.WasCollected)
                HostSync.Hurt(owner.PlayerSlotIndex, (int)kind, damage, fromPlayer?.PlayerSlotIndex ?? -1, source?.TryCast<EnemyAgent>()?.GlobalID ?? 0);
        }
        catch (Exception e) { Rec.Fail("player-hurt", e); }
    }

    // One event per ray the weapon casts (a shotgun casts one per pellet). The shooter is the player whose Fire is running, the weapon key its
    // archetype: the item id is the same for every gun of a class (GEAR_Bulletweapon_Standard, _Special), so it says nothing of this gun.
    private static PlayerAgent? Shooter;
    private static ulong ShooterGun;

    private static uint GunKey(BulletWeapon gun) => gun.m_archeType?.m_archetypeData?.persistentID ?? gun.ItemDataBlock?.persistentID ?? 0;

    private static void WatchShooter<TWeapon>(ForgeCore.Bracket<TWeapon> fire, Func<TWeapon, BulletWeapon> asWeapon)
        => fire.Around<bool>(weapon =>
        {
            var gun = asWeapon(weapon);
            Shooter = gun.Owner;
            if (!Rec.On) return true;
            try { var key = GunKey(gun); ShooterGun = key; NoteWeaponSound(key, gun); } catch (Exception e) { Rec.Fail("weapon-sound", e); }
            return true;
        }, (_, _, _) => Shooter = null, (_, _, _) => Shooter = null);

    // What the ray hit: 1 level geometry, 2 an enemy limb (blood), 3 a player.
    private static ulong HitKind(Weapon.WeaponHitData data)
    {
        var collider = data.rayHit.collider;
        if (collider == null) return 1UL;
        if (collider.GetComponent<Dam_EnemyDamageLimb>() != null) return 2UL;
        return collider.GetComponentInParent<PlayerAgent>() != null ? 3UL : 1UL;
    }

    private static bool RayEnter(Transform muzzle, ref Weapon.WeaponHitData data, Vector3 origin) => true;

    private static void RayExit(bool state, Transform muzzle, ref Weapon.WeaponHitData data, Vector3 originPos, ref bool result, bool ran)
    {
        var shooter = Shooter;
        if (shooter == null || !Rec.On) return;
        try
        {
            var end = data.rayHit.point;
            var what = result ? HitKind(data) : 0UL;
            var slot = shooter.PlayerSlotIndex;
            Rec.Begin().U((ulong)slot).U(ShooterGun).Pos(originPos.x, originPos.y, originPos.z)
                .Pos(end.x, end.y, end.z).U(result ? 1UL : 0UL).U(what);
            Rec.Emit(EventKind.Shot);
            // A shot fired on the host's machine (its own player and the bots): a client has no Shot event for it.
            if (HostSync.Sending && shooter.IsLocallyOwned) HostSync.Ray(slot, what == 2);
        }
        catch (Exception e) { Rec.Fail("shot", e); }
    }

    // The attacker of a hit on a player: the enemy's entity number plus one, 0 when it is not an enemy.
    private static ulong EnemyAttacker(EnemyAgent? enemy)
    {
        var entity = EnemyRecorder.Entity(enemy);
        return entity < 0 ? 0UL : (ulong)entity + 1;
    }

    internal static ulong SlotOf(Agent? source)
    {
        var player = source?.TryCast<PlayerAgent>();
        return player == null || player.WasCollected || player.Owner == null ? 255UL : (ulong)player.PlayerSlotIndex;
    }

    // Who dealt it: the player slot, or 255.
    private static ulong Attacker(pAgent source) => source.TryGet(out var agent) ? SlotOf(agent) : 255UL;

    // A hit the host settles: the receive method of its kind names the kind and who dealt it (the explosion packet names nobody: the
    // mine that just went off), and the funnel every hit passes (ProcessReceivedDamage) runs inside it on the same frame, where the health
    // before and after says what the hit took off. A hit through the funnel no receive named (fire) is not written.
    private static IntPtr _pendingOn;
    private static ulong _pendingKind, _pendingAttacker;
    private static bool _pendingDirectional;
    private static int _pendingFrame = -1;

    private static void Pending(Dam_EnemyDamageBase receiver, ulong kind, ulong attacker, bool directional)
    {
        if (!Rec.On) return;
        _pendingOn = receiver.Pointer; _pendingKind = kind; _pendingAttacker = attacker; _pendingDirectional = directional; _pendingFrame = Time.frameCount;
    }

    private static float SettleEnter(ForgeCore.EnemyDamageCall call) => Rec.On ? call.Receiver.Health : 0f;

    private static void SettleLeave(ForgeCore.EnemyDamageCall call, float before, bool ran)
    {
        if (!Rec.On || _pendingFrame != Time.frameCount || _pendingOn != call.Receiver.Pointer) return;
        _pendingFrame = -1;
        try
        {
            var after = call.Receiver.Health;
            var taken = Mathf.Max(0f, before - Mathf.Max(0f, after));
            var lethal = before > 0f && after <= 0f;
            var source = call.Source?.TryCast<PlayerAgent>();
            var behind = _pendingKind != 2 && (_pendingKind == 1 || _pendingDirectional) && Behind(call.Receiver, call.LimbId, call.Position, call.Direction);
            EnemyDamage(call.Receiver, _pendingKind, call.Damage, _pendingAttacker, call.LimbId, true, taken, lethal, lethal ? Mathf.Max(0f, call.Damage - before) : 0f,
                behind, call.GearCategoryId, source == null ? 0f : Vector3.Distance(source.transform.position, call.Position));
        }
        catch (Exception e) { Rec.Fail("enemy-hurt", e); }
    }

    // The game's own from-behind bonus of the limb for this hit, on this machine's picture of the enemy: above 1 is a hit in the back.
    private static bool Behind(Dam_EnemyDamageBase receiver, int limbId, Vector3 position, Vector3 direction)
    {
        var limb = Limb(receiver, limbId);
        return limb != null && limb.ApplyDamageFromBehindBonus(1f, position, direction, 1f) > 1.001f;
    }

    private static Dam_EnemyDamageLimb? Limb(Dam_EnemyDamageBase receiver, int limbId)
    {
        var limbs = receiver.DamageLimbs;
        return limbs == null || limbId < 0 || limbId >= limbs.Length ? null : limbs[limbId];
    }

    // EnemyHurt: entity, kind, the damage the hit came with, attacker, limb, then whether the game settled it on this machine, the health it
    // took off, lethal, the damage past the last health, the limb's type (0 normal, 1 weak spot, 2 armour) and its multiplier (x100), in the
    // back, the gear category and the distance from the attacker (cm). A hit read before the host settles it (a client's own) has no taken.
    private static void EnemyDamage(Dam_EnemyDamageBase damage, ulong kind, float amount, ulong attacker, int limbId, bool settled, float taken, bool lethal,
        float overkill, bool behind, uint gear, float distance)
    {
        var enemy = damage.Owner;
        var entity = EnemyRecorder.Entity(enemy);
        if (entity < 0) return;
        EnemyRecorder.Hot(entity); // a hit may move or wake a sleeper: its place is read at once
        var limb = Limb(damage, limbId);
        var type = limb == null ? 0 : (int)limb.m_type;
        var multiplier = limb == null ? 1f : type == 1 ? limb.m_weakspotDamageMulti : type == 2 ? limb.m_armorDamageMulti : 1f;
        Rec.Begin().U((ulong)entity).U(kind).U(Pack(amount, 10f)).U(attacker).U((ulong)Mathf.Max(0, limbId))
            .U(settled ? 1UL : 0UL).U(Pack(taken, 10f)).U(lethal ? 1UL : 0UL).U(Pack(overkill, 10f))
            .U((ulong)Mathf.Clamp(type, 0, 2)).U(Pack(multiplier, 100f)).U(behind ? 1UL : 0UL).U(gear)
            .U(Pack(distance, 100f));
        Rec.Emit(EventKind.EnemyHurt);
        // The host tells the clients that run the same mod what it settled (HostSync); attacker 255 is nobody's hit.
        if (settled && attacker < 4 && HostSync.Sending && enemy is { } owner)
            HostSync.Hit(owner, (int)kind, (int)attacker, Mathf.Clamp(type, 0, 2), lethal, behind, taken, gear, distance);
    }

    // A set-health receive (the host's value arriving on a client, and on the host its own after every hit), with the health before and
    // after it: the only exact health a client has of an enemy, and of another player.
    private static void HealthSynced(Dam_SyncedDamageBase receiver, float before, bool ran)
    {
        if (!Rec.On) return;
        try
        {
            if (receiver.TryCast<Dam_EnemyDamageBase>() is { } enemy)
            {
                var entity = EnemyRecorder.Entity(enemy.Owner);
                if (entity < 0) return;
                Rec.Begin().U((ulong)entity).U(Pack(before, 10f)).U(Pack(enemy.Health, 10f));
                Rec.Emit(EventKind.EnemyHealth);
            }
            else if (receiver.TryCast<Dam_PlayerDamageBase>() is { } player)
            {
                var slot = player.Owner?.PlayerSlotIndex ?? -1;
                if (slot < 0) return;
                Rec.Begin().U((ulong)slot).U(Pack(before, 10f)).U(Pack(player.Health, 10f));
                Rec.Emit(EventKind.PlayerHealth);
            }
        }
        catch (Exception e) { Rec.Fail("health-sync", e); }
    }

    private static void PlayerDamage(Dam_PlayerDamageBase damage, ulong kind, float amount, ulong attacker, ulong fromPlayer)
    {
        var slot = damage.Owner?.PlayerSlotIndex ?? -1;
        if (slot < 0) return;
        Rec.Begin().U((ulong)slot).U(kind).U(Pack(amount, 10f)).U(attacker).U(fromPlayer);
        Rec.Emit(EventKind.PlayerHurt);
    }

    // A custom limb was destroyed (its override of DestroyLimb; ForgeCore patches it once for every package).
    private static void EnemyLimbDestroyed(Dam_EnemyDamageLimb_Custom limb, bool ran)
    {
        if (!Rec.On) return;
        try
        {
            var entity = EnemyRecorder.Entity(limb.m_base?.Owner);
            if (entity < 0) return;
            Rec.Begin().U((ulong)entity).U((ulong)limb.m_limbID);
            Rec.Emit(EventKind.EnemyLimb);
        }
        catch (Exception e) { Rec.Fail("enemy-limb", e); }
    }

    // A client's own hits: the host settles every hit (the funnel never runs here), so on a client the local player's hit is read where the
    // limb takes it, with the limb's weak spot and armour, the bonus from behind and a swing's back and sleeper multipliers. The host may still refuse it; the health the
    // host then syncs (EnemyHealth) says what it took off.
    [HarmonyPatch]
    private static class OwnDamage
    {
        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.BulletDamage))]
        [HarmonyPrefix]
        private static void Bullet(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, bool allowDirectionalBonus, float precisionMulti, uint gearCategoryId)
            => Own(__instance, 0, dam, sourceAgent, precisionMulti, position, direction, allowDirectionalBonus, gearCategoryId, 1f, 1f);

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.MeleeDamage))]
        [HarmonyPrefix]
        private static void Melee(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, float precisionMulti, float backstabberMulti, float sleeperMulti, uint gearCategoryId)
            => Own(__instance, 1, dam, sourceAgent, precisionMulti, position, direction, true, gearCategoryId, backstabberMulti, sleeperMulti);

        private static void Own(Dam_EnemyDamageLimb limb, ulong kind, float amount, Agent? source, float precision, Vector3 position, Vector3 direction, bool directional, uint gear, float backstabber, float sleeper)
        {
            if (!Rec.On || ForgeCore.Session.IsHost) return;
            try
            {
                var player = source?.TryCast<PlayerAgent>();
                var damage = limb.m_base;
                if (player == null || !player.IsLocallyOwned || damage == null) return;
                var behind = directional && limb.ApplyDamageFromBehindBonus(1f, position, direction, 1f) > 1.001f;
                // The game's order: the limb, the bonus from behind with the swing's back multiplier, a sleeper's multiplier.
                var reading = limb.TestDamageModifiers(amount, precision);
                if (directional) reading = limb.ApplyDamageFromBehindBonus(reading, position, direction, backstabber);
                if (sleeper != 1f && damage.Owner?.Locomotion?.CurrentStateEnum == Enemies.ES_StateEnum.Hibernate) reading *= sleeper;
                EnemyDamage(damage, kind, reading, (ulong)player.PlayerSlotIndex, limb.m_limbID, false, 0f, false, 0f,
                    behind, gear, Vector3.Distance(player.transform.position, position));
            }
            catch (Exception e) { Rec.Fail("enemy-hurt", e); }
        }
    }

    // 0 light swing, 1 heavy swing, 2 push
    [HarmonyPatch]
    private static class ShotHeavy
    {
        [HarmonyPatch(typeof(MWS_AttackHeavy), nameof(MWS_AttackHeavy.Enter))]
        [HarmonyPostfix]
        private static void Heavy(MWS_AttackHeavy __instance) => Melee.Swing(__instance.m_weapon.Owner, 1);
    }

    [HarmonyPatch]
    internal static class Melee
    {
        // The game's light attack state is the press: held on, the same press becomes a charge. The swing is written once the game has decided,
        // so playback never has to wait to tell a tap from a charge: a light swing when the button is up first (or the state moves on to
        // anything but the charge), nothing when the charge starts (its charge and heavy release are written).
        [HarmonyPatch(typeof(MWS_AttackLight), nameof(MWS_AttackLight.Enter))]
        [HarmonyPostfix]
        private static void Light(MWS_AttackLight __instance) { if (Rec.On) _pressed = __instance.m_weapon.Owner; }

        private static PlayerAgent? _pressed;

        /// <summary>Every frame while recording: settles a press of the local player's melee weapon.</summary>
        internal static void Settle()
        {
            var owner = _pressed;
            if (owner == null) return;
            try
            {
                var state = owner.WasCollected ? null : owner.Inventory?.WieldedItem?.TryCast<MeleeWeaponFirstPerson>()?.CurrentState;
                if (state == null) { _pressed = null; return; }
                if (state.TryCast<MWS_ChargeUp>() != null) { _pressed = null; NoteCharge(owner, true); return; }
                if (state.TryCast<MWS_AttackHeavy>() != null) { _pressed = null; return; }
                if (state.TryCast<MWS_AttackLight>() != null && ForgeCore.InputLease.Held(InputAction.Fire)) return;
                _pressed = null;
                Swing(owner, 0);
            }
            catch (Exception e) { _pressed = null; Rec.Fail("melee", e); }
        }

        internal static void Reset() => _pressed = null;

        // U slot, U kind, then U 1: written once the game decided the swing.
        internal static void Swing(PlayerAgent? owner, ulong kind)
        {
            if (owner == null || !Rec.On) return;
            try { Rec.Begin().U((ulong)owner.PlayerSlotIndex).U(kind).U(1); Rec.Emit(EventKind.Melee); } catch (Exception e) { Rec.Fail("melee", e); }
        }
    }

    private static void PackUsed(ResourcePackFirstPerson pack)
    {
        if (!Rec.On) return;
        try
        {
            var owner = pack.Owner;
            if (owner == null) return;
            Rec.Begin().U((ulong)owner.PlayerSlotIndex).U(pack.ItemDataBlock?.persistentID ?? 0);
            Rec.Emit(EventKind.Pack);
            // A pack applied on the host's machine (its own player and the bots): a client has no Pack event for those.
            if (HostSync.Sending && owner.IsLocallyOwned) HostSync.Pack(owner.PlayerSlotIndex);
        }
        catch (Exception e) { Rec.Fail("pack", e); }
    }

    private static void World(ushort kind, Component? thing, ulong key, ulong state, ulong extra = 0)
    {
        if (thing == null || !Rec.On) return;
        var p = thing.transform.position;
        Rec.Begin().U(key).U(state).U(extra).Pos(p.x, p.y, p.z);
        Rec.EmitState(kind, key);
    }

    // A door's state, the common layout, then U slot + 1 of who changed it, U damage taken x 10, U full health x 10 (0 a security door), U glue percent.
    private static void DoorState(Component door, ulong key, pDoorState state, float health)
    {
        if (door == null || !Rec.On) return;
        var p = door.transform.position;
        Rec.Begin().U(key).U((ulong)(int)state.status).U(0).Pos(p.x, p.y, p.z).U(PlayerOf(state.instigator)).U(Pack(state.damageTaken, 10f)).U(Pack(health, 10f)).U(Pack(Mathf.Clamp01(state.glueRel), 100f));
        Rec.EmitState(EventKind.Door, key);
    }

    // A pickup's state, the common layout, then U slot + 1 of who holds it, U ammunition or uses x 100, U on the floor, U in a machine, Pos of the place the level had it
    // (where the recording first saw it: the first state of a level item comes before anything moves it), so a replay that jumps finds the level's own object and puts it back.
    private static readonly System.Collections.Generic.Dictionary<ulong, Vector3> ItemHome = new();

    private static void ItemState(Component item, ulong key, pPickupItemState state)
    {
        if (item == null || !Rec.On) return;
        var p = item.transform.position;
        if (!ItemHome.TryGetValue(key, out var home)) ItemHome[key] = home = p;
        Rec.Begin().U(key).U((ulong)(int)state.status).U(0).Pos(p.x, p.y, p.z).U(PlayerOf(state.pPlayer)).U(Pack(state.custom.ammo, 100f)).U(state.placement.droppedOnFloor ? 1UL : 0UL).U(state.placement.linkedToMachine ? 1UL : 0UL).Pos(home.x, home.y, home.z);
        Rec.EmitState(EventKind.ItemState, key);
    }

    [HarmonyPatch]
    private static class SecurityDoor
    {
        [HarmonyPatch(typeof(LG_SecurityDoor), nameof(LG_SecurityDoor.OnSyncDoorStatusChange))]
        [HarmonyPostfix]
        private static void Changed(LG_SecurityDoor __instance, pDoorState state)
        {
            try { DoorState(__instance, Rec.Key(__instance), state, 0f); } catch (Exception e) { Rec.Fail("door", e); }
        }
    }

    [HarmonyPatch]
    private static class Scan
    {
        [HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.OnSyncStateChange))]
        [HarmonyPostfix]
        private static void Changed(CP_Bioscan_Core __instance, eBioscanStatus status, float progress, Il2CppSystem.Collections.Generic.List<PlayerAgent> playersInScan, int playersMax)
        {
            // The extra: progress in percent, bits 8 to 11 the slots of the players inside, bits 12 to 15 how many players the scan needs (the game's scan sound counts who is inside).
            try
            {
                var inside = 0UL;
                if (playersInScan != null)
                    foreach (var player in playersInScan) if (player != null) inside |= 1UL << (player.PlayerSlotIndex & 3);
                var extra = (ulong)(uint)Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f) | inside << 8 | (ulong)(uint)Math.Clamp(playersMax, 0, 15) << 12;
                World(EventKind.Scan, __instance, Rec.Key(__instance), (ulong)(int)status, extra);
            }
            catch (Exception e) { Rec.Fail("scan", e); }
        }
    }

    [HarmonyPatch]
    private static class WaveStart
    {
        [HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.StartWave))]
        [HarmonyPostfix]
        private static void Started(SurvivalWave __instance)
        {
            if (!Rec.On) return;
            try { Rec.Begin().U(1).U(WaveId(__instance)).U(__instance.m_settings == null ? 0UL : __instance.m_settings.persistentID).U(__instance.m_populationDataID).U((ulong)(int)__instance.m_spawnType); Rec.Emit(EventKind.Wave); } catch (Exception e) { Rec.Fail("wave", e); }
        }
    }

    [HarmonyPatch]
    private static class ChatMessage
    {
        [HarmonyPatch(typeof(PlayerChatManager), nameof(PlayerChatManager.PostChatMessageLocaly))]
        [HarmonyPostfix]
        private static void Posted(SNet_Player fromPlayer, string message)
        {
            if (!Rec.On) return;
            try { Rec.Begin().U(fromPlayer == null ? 255UL : (ulong)fromPlayer.PlayerSlotIndex()).Text(message); Rec.Emit(EventKind.Chat); } catch (Exception e) { Rec.Fail("chat", e); }
        }
    }
}
