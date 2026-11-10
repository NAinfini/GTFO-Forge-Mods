using ForgeCore;
using System;
using Enemies;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeReplay;

// The recorder hooks of PLAN 4.2 that came after the first set (Hooks.cs): reload and tool use, throws, sentries and mines, tongues and
// projectiles, screams and scout detection, noise, pings, resource boxes, the reactor, light and fog, joins, host changes and the game log.
// Same rules as Hooks.cs: each feature is put on separately (a Harmony class, or a subscription to a ForgeCore bracket where other packages
// patch the same method), nothing ever throws into the game.
internal static partial class Hooks
{
    private static void InstallMoreBrackets()
    {
        Subscribe("Sentry.Placed", () => ForgeCore.GameHooks.SentryGearSpawned.After((sentry, _) => Sentry.Placed(sentry)));
        Subscribe("Sentry.Shot", () => ForgeCore.GameHooks.SentryFired.Before(Sentry.Fired));
        Subscribe("Sentry.Removed", () => ForgeCore.GameHooks.SentryDespawned.After((sentry, _) => Sentry.Removed(sentry)));
        // Picked up by a player, the sentry goes to the backpack without a despawn.
        Subscribe("Sentry.PickedUp", () => ForgeCore.GameHooks.SentryLifecycle.After((call, _) => { if (call.Step is ForgeCore.SentryStep.PickUp or ForgeCore.SentryStep.SyncedPickup) Sentry.Removed(call.Sentry); }));
        Subscribe("Mine.Placed", () => ForgeCore.GameHooks.MineSpawned.After((mine, _) => Mine.Placed(mine)));
        Subscribe("Mine.PickedUp", () => ForgeCore.GameHooks.MinePickedUp.After((call, _) => Mine.PickedUp(call.Mine)));
        Subscribe("Scream.Scouted", () => ForgeCore.GameHooks.ScoutScreamActivated.After((scream, _) => Scream.Scouted(scream)));
        Subscribe("Reload", () => ForgeCore.GameHooks.ReloadAnimationStarted.After((item, _) => Reloaded(item)));
        Subscribe("Scream", () => { ForgeCore.GameHooks.EnemyScreamStarted.After((scream, _) => Scream.Screamed(scream)); ForgeCore.GameHooks.EnemyScreamed.After((scream, _) => Scream.Screamed(scream)); });
        Subscribe("EnemyAttack", () => ForgeCore.GameHooks.EnemyAttackStarted.After((call, _) => EnemyAct.Attack(call.State, call.Target, call.AnimIndex)));
        Subscribe("EnemyHitreact", () => ForgeCore.GameHooks.EnemyHitreacted.After((state, _) => EnemyAct.Hitreact(state)));
        Subscribe("Scout.On", () => ForgeCore.GameHooks.ScoutDetectionEntered.After((scout, _) => Scout.On(scout)));
        Subscribe("Noise.Made", () => ForgeCore.GameHooks.NoiseMade.Before(Noise.Made));
        Subscribe("ResourceBox", () => ForgeCore.GameHooks.ResourceContainerState.After((call, _) =>
        {
            try { WorldAt(EventKind.ResourceBox, call.Container.gameObject, Rec.Key(call.Container), (ulong)(int)call.New.status); } catch (Exception e) { Rec.Fail("resource-box", e); }
        }));
        Subscribe("Reactor", () => ForgeCore.GameHooks.ReactorState.After((call, _) =>
        {
            try { ReactorState(call.Reactor, call.New); } catch (Exception e) { Rec.Fail("reactor", e); }
        }));
        // 89 ReactorText: U key, U shown, Text the message, Text before the timer, Text after it. The reactor's own text above its console, as the recorder's game wrote it.
        Subscribe("ReactorText", () => ForgeCore.GameHooks.ReactorMessage.After((call, _) =>
        {
            if (!Rec.On) return;
            try
            {
                var said = call.Visible + call.Message + call.TimerPrefix + call.TimerSuffix;
                if (said == _lastReactorText) return;
                _lastReactorText = said;
                var key = Rec.Key(call.Reactor);
                Rec.Begin().U(key).U(call.Visible ? 1UL : 0UL).Text(call.Message, 500).Text(call.TimerPrefix).Text(call.TimerSuffix);
                Rec.EmitState(EventKind.ReactorText, key);
            }
            catch (Exception e) { Rec.Fail("reactor-text", e); }
        }));
        Subscribe("GameLog", () => ForgeCore.GameHooks.GameLogAdded.After((call, _) =>
        {
            if (!Rec.On) return;
            try
            {
                // The game keeps three log panels and each one takes the line.
                var now = PlayerRecorder.Now;
                if (call.Log == _lastLog && now - _lastLogMs <= RepeatMs) return;
                _lastLog = call.Log; _lastLogMs = now;
                Rec.Begin().U((ulong)(int)call.Type).Text(call.Log);
                Rec.Emit(EventKind.GameLog);
            }
            catch (Exception e) { Rec.Fail("game-log", e); }
        }));
    }

    // The reactor's state, the common layout, then U stage, U verify failed, Text the code it wants (the override code of the stage).
    private static void ReactorState(LG_WardenObjective_Reactor reactor, pReactorState state)
    {
        if (!Rec.On) return;
        var key = Rec.Key(reactor);
        var p = reactor.transform.position;
        Rec.Begin().U(key).U((ulong)(int)state.status).U((ulong)Mathf.RoundToInt(Mathf.Clamp01(state.stateProgress) * 100f)).Pos(p.x, p.y, p.z)
            .U((ulong)Math.Max(0, state.stateCount)).U(state.verifyFailed ? 1UL : 0UL).Text(reactor.CurrentStateOverrideCode);
        Rec.EmitState(EventKind.Reactor, key);
    }

    private static void WorldAt(ushort kind, GameObject? thing, ulong key, ulong state, ulong extra = 0)
    {
        if (thing == null || !Rec.On) return;
        var p = thing.transform.position;
        Rec.Begin().U(key).U(state).U(extra).Pos(p.x, p.y, p.z);
        Rec.EmitState(kind, key);
    }

    // The same call seen twice: a bot's or the host's interaction goes through both the send and the receive method; the two screams hooks
    // are two steps of one scream; each log panel takes the line.
    private const uint RepeatMs = 100;
    private static string? _lastLog;
    private static uint _lastLogMs;
    private static readonly (ulong Type, uint At)[] LastInteract = new (ulong, uint)[4], LastThrow = new (ulong, uint)[4];
    private static readonly System.Collections.Generic.Dictionary<int, uint> LastScream = new();

    private static bool Repeated((ulong Type, uint At)[] last, ulong slot, ulong type)
    {
        if (slot > 3) return false;
        var now = PlayerRecorder.Now;
        var (was, at) = last[slot];
        last[slot] = (type, now);
        return was == type && at != 0 && now - at <= RepeatMs;
    }

    // Everything the hooks remember across calls, cleared when a recording starts.
    internal static void ResetRecordingState()
    {
        SoundNoted.Clear(); LastScream.Clear(); Array.Clear(LastInteract); Array.Clear(LastThrow); _lastLog = null;
        Array.Clear(LastObjective); Mine.Reset(); Sentry.Reset(); Melee.Reset(); Projectiles.Reset(); Wake.Reset(); PlayerRecorder.ForgetLoadouts(); _lastMusic = -1; ResetWorldState();
    }

    // The weapon's own fire sound, written once per weapon as a state (key = the weapon's archetype id, then the Wwise event name), so a replay
    // can play it once per recorded shot without owning the weapon: the one-shot, in-the-world (3D) event a teammate's gun of this fire mode plays
    // per shot (WeaponAudioDataBlock: semi fire, else the synced per-shot event of a burst or automatic weapon). The weapon's own arrays are not
    // it: the recording player's gun holds the first-person (2D) events, and an automatic gun's start event begins a loop that only its end
    // event stops, which a replay posting it per shot never sent (11-06: every shot started another loop).
    private static string ShotSound(BulletWeapon weapon)
    {
        var audio = weapon.AudioData;
        var mode = weapon.ArchetypeData?.FireMode ?? eWeaponFireMode.Semi;
        if (audio != null)
        {
            var perShot = mode is eWeaponFireMode.Auto or eWeaponFireMode.SentryGunAuto ? audio.eventOnSyncedAutoFirePerShot3D
                : mode is eWeaponFireMode.Burst or eWeaponFireMode.SemiBurst or eWeaponFireMode.SentryGunBurst ? audio.eventOnSyncedBurstFirePerShot3D
                : audio.eventOnSemiFire3D;
            if (First(perShot) is { } named) return named;
            if (First(audio.eventOnSemiFire3D) is { } semi) return semi;
        }
        var events = weapon.m_audioFireSemi;
        if (events == null || events.Length == 0) events = weapon.m_audioFireBurst;
        return events != null && events.Length > 0 ? events[0] : "";
    }

    private static string? First(Il2CppSystem.Collections.Generic.List<string>? events)
    {
        if (events == null) return null;
        for (var i = 0; i < events.Count; i++) if (!string.IsNullOrEmpty(events[i])) return events[i];
        return null;
    }
    private static readonly System.Collections.Generic.HashSet<uint> SoundNoted = new();

    // 0 rifle, 1 pistol, 2 shotgun, 3 submachine gun: which of the game's third-person muzzle flashes the weapon's shots show.
    private static ulong FlashClass(BulletWeapon weapon)
    {
        if (weapon.TryCast<Shotgun>() != null || weapon.TryCast<ShotgunSynced>() != null) return 2;
        return weapon.ItemDataBlock?.inventorySlot == InventorySlot.GearClass ? 1UL : 0UL;
    }

    private static ulong Pack(float value, float scale) => (ulong)Mathf.Max(0, Mathf.RoundToInt(value * scale));

    private static void NoteWeaponSound(uint itemId, BulletWeapon? weapon)
    {
        if (weapon == null || !SoundNoted.Add(itemId)) return;
        var name = ShotSound(weapon);
        var write = Rec.Begin().U(itemId).Text(name).U(FlashClass(weapon));
        // The weapon's archetype data block, so that a replay can work out what its shots did where the game's own settlement is not in the file.
        var archetype = weapon.m_archeType?.m_archetypeData;
        if (archetype != null) write.U(Pack(archetype.Damage, 100f)).U(Pack(archetype.DamageFalloff.x, 10f)).U(Pack(archetype.DamageFalloff.y, 10f)).U(Pack(BulletWeapon.s_falloffMin, 100f));
        Rec.EmitState(EventKind.WeaponSound, itemId);
    }

    // 52 Reload: U slot. The local player's reload animation starts (a remote player's reload arrives as an Interact event).
    private static void Reloaded(ItemEquippable item)
    {
        if (!Rec.On) return;
        try { Rec.Begin().U(Slot(item.Owner)); Rec.Emit(EventKind.Reload); } catch (Exception e) { Rec.Fail("reload", e); }
    }

    // 255 when the interactor is not a usable player: a bot or a collected agent whose game player is gone (during level teardown) cannot read its slot.
    private static ulong Slot(PlayerAgent? player) => player == null || player.WasCollected || player.Owner == null ? 255UL : (ulong)player.PlayerSlotIndex;

    // U slot, U type (pGenericInteractAnimation: place mine, pick up, point, give or use a resource, shove, reload). The local player's goes
    // through PlayerSync.SendGenericInteract, everyone else's arrives in PlayerInventorySynced.SyncGenericInteract.
    [HarmonyPatch]
    private static class Interact
    {
        [HarmonyPatch(typeof(PlayerSync), nameof(PlayerSync.SendGenericInteract))]
        [HarmonyPrefix]
        private static void Local(PlayerSync __instance, pGenericInteractAnimation.TypeEnum type)
        {
            if (!Rec.On) return;
            try { Write(Slot(__instance.m_agent), (ulong)(int)type); } catch (Exception e) { Rec.Fail("interact", e); }
        }

        [HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.SyncGenericInteract))]
        [HarmonyPrefix]
        private static void Remote(PlayerInventorySynced __instance, pGenericInteractAnimation.TypeEnum type)
        {
            if (!Rec.On) return;
            try { Write(Slot(__instance.Owner), (ulong)(int)type); } catch (Exception e) { Rec.Fail("interact", e); }
        }

        private static void Write(ulong slot, ulong type)
        {
            if (Repeated(LastInteract, slot, type)) return;
            Rec.Begin().U(slot).U(type);
            Rec.Emit(EventKind.Interact);
        }
    }

    // U slot, U status (1 charging, 2 thrown).
    [HarmonyPatch]
    private static class Throw
    {
        [HarmonyPatch(typeof(PlayerSync), nameof(PlayerSync.SendThrowStatus))]
        [HarmonyPrefix]
        private static void Local(PlayerSync __instance, pPlayerThrowStatus.StatusEnum status)
        {
            if (!Rec.On) return;
            try { Write(Slot(__instance.m_agent), (ulong)(int)status); } catch (Exception e) { Rec.Fail("throw", e); }
        }

        [HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.SyncThrowState))]
        [HarmonyPrefix]
        private static void Remote(PlayerInventorySynced __instance, pPlayerThrowStatus.StatusEnum status)
        {
            if (!Rec.On) return;
            try { Write(Slot(__instance.Owner), (ulong)(int)status); } catch (Exception e) { Rec.Fail("throw", e); }
        }

        private static void Write(ulong slot, ulong status)
        {
            if (Repeated(LastThrow, slot, status)) return;
            Rec.Begin().U(slot).U(status);
            Rec.Emit(EventKind.Throw);
        }
    }

    // The owner in a sentry's or mine's state: the player slot + 1, 0 when the game has not said (the extra of the state event).
    private static ulong OwnerExtra(Item item)
    {
        // During level cleanup the agent may have lost its player (its slot is then unreadable).
        var owner = item.Owner;
        return owner == null || owner.WasCollected || owner.Owner == null ? 0UL : (ulong)owner.PlayerSlotIndex + 1;
    }

    // A deployed sentry's or mine's state: the common state layout, then Pos its rotation (Euler degrees), U its item data block (0 for a sentry),
    // and for a sentry Text its own gear (the game's gear JSON on one line), so a replay builds that sentry whatever the file says of its owner's
    // loadout (a header written before a player's gear arrived has an empty loadout for that player).
    private static void Deployed(ushort kind, Item item, ulong key, ulong state, ulong itemId, GearIDRange? gear = null)
    {
        if (!Rec.On) return;
        var t = item.transform;
        var p = t.position;
        var e = t.eulerAngles;
        var write = Rec.Begin().U(key).U(state).U(OwnerExtra(item)).Pos(p.x, p.y, p.z).Pos(e.x, e.y, e.z).U(itemId);
        if (kind == EventKind.Sentry) write.Text(gear == null ? "" : gear.ToJSON().Replace("\n", " "), PayloadWriter.Capacity);
        Rec.EmitState(kind, key);
    }

    // Sentry state: 0 standing, 1 gone. Shots: U object key, Pos.
    [HarmonyPatch]
    private static class Sentry
    {
        internal static void Placed(SentryGunInstance __instance)
        {
            try { Deployed(EventKind.Sentry, __instance, Rec.Key(__instance), 0, 0, __instance.GearIDRange); Ammo(__instance); } catch (Exception e) { Rec.Fail("sentry", e); }
        }

        // 93 SentryAmmo (a state per sentry): U the sentry's state key, U the bullets its screen shows (ammo / cost of a bullet), written when that number changes.
        private static readonly System.Collections.Generic.Dictionary<ulong, ulong> Shown = new();

        private static void Ammo(SentryGunInstance sentry)
        {
            var cost = sentry.CostOfBullet;
            var bullets = cost > 0f ? (ulong)Mathf.Max(0, Mathf.RoundToInt(sentry.Ammo / cost)) : 0UL;
            var key = Rec.Key(sentry);
            if (Shown.TryGetValue(key, out var last) && last == bullets) return;
            Shown[key] = bullets;
            Rec.Begin().U(key).U(bullets);
            Rec.EmitState(EventKind.SentryAmmo, key);
        }

        internal static void Reset() => Shown.Clear();

        internal static void Removed(SentryGunInstance __instance)
        {
            try { Deployed(EventKind.Sentry, __instance, Rec.Key(__instance), 1, 0, __instance.GearIDRange); } catch (Exception e) { Rec.Fail("sentry", e); }
        }

        internal static void Fired(SentryGunInstance_Firing_Bullets __instance)
        {
            if (!Rec.On) return;
            try
            {
                var gun = __instance.gameObject;
                var p = gun.transform.position;
                // Then U the key of the sentry's state event, Pos the direction of the muzzle.
                var sentry = __instance.GetComponentInParent<SentryGunInstance>();
                var aim = __instance.MuzzleAlign != null ? __instance.MuzzleAlign.forward : gun.transform.forward;
                // Then U the weapon audio data block id of this sentry's gun (0 unknown) and U its fire mode (eWeaponFireMode), which the replay's shot sound is chosen from.
                var audio = __instance.m_audioData;
                Rec.Begin().U(Rec.Key(gun)).Pos(p.x, p.y, p.z).U(sentry == null ? 0UL : Rec.Key(sentry)).Pos(aim.x, aim.y, aim.z)
                    .U(audio == null ? 0UL : audio.persistentID).U((ulong)(int)__instance.m_fireMode);
                Rec.Emit(EventKind.SentryShot);
                if (sentry != null) Ammo(sentry);
            }
            catch (Exception e) { Rec.Fail("sentry-shot", e); }
        }
    }

    // Mine state: 0 placed, 1 triggered, 2 picked up. Explosion: U kind (0 explosive, 1 foam), Pos.
    [HarmonyPatch]
    private static class Mine
    {
        // Who set off the last explosion: enemies are hurt by the packet it sends, which names no source and may come a moment later.
        private const uint ExplosionHurtMs = 500;
        private static ulong _exploder = 255;
        private static uint _explodedAt;

        internal static void Reset() { _exploder = 255; _explodedAt = 0; }

        internal static ulong LastExploder() => PlayerRecorder.Now - _explodedAt <= ExplosionHurtMs ? _exploder : 255UL;

        internal static void Placed(MineDeployerInstance __instance)
        {
            try { Deployed(EventKind.Mine, __instance, Rec.Key(__instance), 0, __instance.ItemDataBlock == null ? 0UL : __instance.ItemDataBlock.persistentID); } catch (Exception e) { Rec.Fail("mine", e); }
        }

        [HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.SyncedTrigger))]
        [HarmonyPostfix]
        private static void Triggered(MineDeployerInstance __instance)
        {
            try { Deployed(EventKind.Mine, __instance, Rec.Key(__instance), 1, __instance.ItemDataBlock == null ? 0UL : __instance.ItemDataBlock.persistentID); } catch (Exception e) { Rec.Fail("mine", e); }
        }

        internal static void PickedUp(MineDeployerInstance __instance)
        {
            try { Deployed(EventKind.Mine, __instance, Rec.Key(__instance), 2, __instance.ItemDataBlock == null ? 0UL : __instance.ItemDataBlock.persistentID); } catch (Exception e) { Rec.Fail("mine", e); }
        }

        [HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.DoExplode))]
        [HarmonyPrefix]
        private static void Exploded(MineDeployerInstance_Detonate_Explosive __instance)
        {
            if (Rec.On)
            {
                try
                {
                    var mine = __instance.GetComponentInParent<MineDeployerInstance>();
                    var owner = mine == null ? 0UL : OwnerExtra(mine);
                    _exploder = owner == 0 ? 255UL : owner - 1;
                    _explodedAt = PlayerRecorder.Now;
                }
                catch (Exception e) { Rec.Fail("explosion", e); }
            }
            // Then what the statistics need of it, read here: U max damage x 10, U min x 10, U the distance they lerp from x 10, U to x 10.
            if (__instance == null || !Rec.On) return;
            try
            {
                var p = __instance.transform.position;
                Rec.Begin().U(0).Pos(p.x, p.y, p.z).U(Pack(__instance.m_damageMax, 10f)).U(Pack(__instance.m_damageMin, 10f)).U(Pack(__instance.m_distanceMin, 10f)).U(Pack(__instance.m_distanceMax, 10f));
                Rec.Emit(EventKind.Explosion);
            }
            catch (Exception e) { Rec.Fail("explosion", e); }
        }

        [HarmonyPatch(typeof(MineDeployerInstance_Detonate_Glue), nameof(MineDeployerInstance_Detonate_Glue.DoExplode))]
        [HarmonyPrefix]
        private static void Foamed(MineDeployerInstance_Detonate_Glue __instance) => Boom(__instance.gameObject, 1);

        private static void Boom(GameObject? thing, ulong kind)
        {
            if (thing == null || !Rec.On) return;
            try { var p = thing.transform.position; Rec.Begin().U(kind).Pos(p.x, p.y, p.z); Rec.Emit(EventKind.Explosion); } catch (Exception e) { Rec.Fail("explosion", e); }
        }
    }

    // U enemy entity, U phase (0 tongue is out, 1 comes back, 2 is launched), Pos where it reaches (world), U the player it goes for: slot + 1,
    // 0 a place; a launch then has U which of the enemy's tentacles it is (their order under the enemy), so a replay starts that one.
    [HarmonyPatch]
    private static class Tongue
    {
        [HarmonyPatch(typeof(MovingEnemyTentacleBase), nameof(MovingEnemyTentacleBase.Attack), new[] { typeof(Agents.Agent), typeof(Il2CppSystem.Action) })]
        [HarmonyPostfix]
        private static void AtAgent(MovingEnemyTentacleBase __instance) => Launched(__instance);

        [HarmonyPatch(typeof(MovingEnemyTentacleBase), nameof(MovingEnemyTentacleBase.Attack), new[] { typeof(Vector3), typeof(Il2CppSystem.Action) })]
        [HarmonyPostfix]
        private static void AtPlace(MovingEnemyTentacleBase __instance) => Launched(__instance);

        private static void Launched(MovingEnemyTentacleBase tentacle)
        {
            if (!Rec.On) return;
            try
            {
                var owner = tentacle.m_owner;
                var entity = EnemyRecorder.Entity(owner);
                if (entity < 0) return;
                var all = owner.GetComponentsInChildren<MovingEnemyTentacleBase>(true);
                var index = 0;
                for (var i = 0; i < all.Length; i++) if (all[i].Pointer == tentacle.Pointer) { index = i; break; }
                var target = tentacle.PlayerTarget;
                var p = tentacle.m_hasStaticTargetPos ? tentacle.m_staticTargetPos : target != null ? target.Position : tentacle.transform.position;
                Rec.Begin().U((ulong)entity).U(2).Pos(p.x, p.y, p.z).U(target == null ? 0UL : (ulong)target.PlayerSlotIndex + 1).U((ulong)index);
                Rec.Emit(EventKind.Tongue);
            }
            catch (Exception e) { Rec.Fail("tongue", e); }
        }

        [HarmonyPatch(typeof(MovingEnemyTentacleBase), nameof(MovingEnemyTentacleBase.OnAttackIsOut))]
        [HarmonyPostfix]
        private static void Out(MovingEnemyTentacleBase __instance) => Phase(__instance, 0);

        [HarmonyPatch(typeof(MovingEnemyTentacleBase), nameof(MovingEnemyTentacleBase.AttackIn))]
        [HarmonyPostfix]
        private static void In(MovingEnemyTentacleBase __instance) => Phase(__instance, 1);

        private static void Phase(MovingEnemyTentacleBase tentacle, ulong phase)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(tentacle.m_owner);
                if (entity < 0) return;
                // m_localSpaceTargetPos is relative to the target: the target's own place is the world position.
                var target = tentacle.PlayerTarget;
                var p = tentacle.m_hasStaticTargetPos ? tentacle.m_staticTargetPos : target != null ? target.Position : tentacle.transform.position;
                Rec.Begin().U((ulong)entity).U(phase).Pos(p.x, p.y, p.z).U(target == null ? 0UL : (ulong)target.PlayerSlotIndex + 1);
                Rec.Emit(EventKind.Tongue);
            }
            catch (Exception e) { Rec.Fail("tongue", e); }
        }
    }

    // U kind (0 spitter ball or other aimed projectile fired, 1 foam fired, 2 foam destroyed, 3 an aimed projectile in flight, 4 one whose
    // flight ended), U id (the sync id for foam, the projectile's instance id for 3 and 4), Pos. A flight sample then has U its ProjectileType + 1
    // (0 not known) and Pos the direction it flies.
    [HarmonyPatch]
    internal static class Projectiles
    {
        // The aimed projectiles alive at the last sample, by instance id: where each was and its type + 1. A projectile that hit stays in the
        // manager's list without moving for about five seconds (11-06 recordings: median 4950 ms after a one second flight); its flight
        // ends at the first sample it is inactive or has not moved, and it is not written again while it lingers.
        private static readonly System.Collections.Generic.Dictionary<int, (Vector3 At, ulong Type)> Flying = new();
        private static readonly System.Collections.Generic.HashSet<int> Stopped = new();
        private static readonly System.Collections.Generic.HashSet<int> Now = new();
        private static readonly System.Collections.Generic.List<int> Ended = new();
        private const float StillMetres = 0.01f;

        internal static void Reset() { Flying.Clear(); Stopped.Clear(); Now.Clear(); Ended.Clear(); }

        /// <summary>At every enemy sample: where each of the game's aimed projectiles (the manager's own list) flies, and those that ended since the last sample.</summary>
        internal static void Sample()
        {
            if (!Rec.On) return;
            try
            {
                var manager = ProjectileManager.Current;
                var list = manager == null ? null : manager.m_projectiles;
                Now.Clear();
                if (list != null)
                    for (var i = 0; i < list.Count; i++)
                    {
                        var projectile = list[i];
                        if (projectile == null) continue;
                        var id = projectile.GetInstanceID();
                        Now.Add(id);
                        if (Stopped.Contains(id)) continue;
                        var t = projectile.transform;
                        var p = t.position; var f = t.forward;
                        var known = Flying.TryGetValue(id, out var was);
                        if (known && (!projectile.m_active || Vec.SqrLength(p - was.At) < StillMetres * StillMetres))
                        {
                            Stopped.Add(id);
                            End(id, p);
                            continue;
                        }
                        if (!known) was = (p, TypeOf(manager!, projectile));
                        Flying[id] = (p, was.Type);
                        Rec.Begin().U(3).U(Rec.Key(id)).Pos(p.x, p.y, p.z).U(was.Type).Pos(f.x, f.y, f.z);
                        Rec.Emit(EventKind.Projectile);
                    }
                Ended.Clear();
                foreach (var id in Flying.Keys) if (!Now.Contains(id)) Ended.Add(id);
                foreach (var id in Ended) End(id, Flying[id].At);
                Stopped.RemoveWhere(static id => !Now.Contains(id));
            }
            catch (Exception e) { Rec.Fail("projectile-flight", e); }
        }

        private static void End(int id, Vector3 p)
        {
            Flying.Remove(id);
            Rec.Begin().U(4).U(Rec.Key(id)).Pos(p.x, p.y, p.z);
            Rec.Emit(EventKind.Projectile);
        }

        // The prefab a projectile was made from, by the name the game's instantiation gives it ("<prefab>(Clone)"): its ProjectileType + 1, 0 none.
        private static ulong TypeOf(ProjectileManager manager, ProjectileTargeting projectile)
        {
            var prefabs = manager.m_projectilePrefabs;
            var name = projectile.gameObject.name;
            if (prefabs == null) return 0;
            for (var i = 0; i < prefabs.Length; i++)
                if (prefabs[i] != null && name.StartsWith(prefabs[i].name + "(", StringComparison.Ordinal)) return (ulong)i + 1;
            return 0;
        }

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoFireTargeting))]
        [HarmonyPostfix]
        private static void Targeting(ProjectileManager.pFireTargeting data)
        {
            if (!Rec.On) return;
            // Then U burst size, U ProjectileType, Pos direction, U the player it aims at: slot + 1, 0 none.
            try
            {
                var p = data.position; var f = data.forward;
                var target = data.target.TryGet(out var agent) ? agent?.TryCast<PlayerAgent>() : null;
                Rec.Begin().U(0).U(0).Pos(p.x, p.y, p.z).U((ulong)Math.Max(0, data.burstSize)).U((ulong)(int)data.type).Pos(f.x, f.y, f.z).U(target == null ? 0UL : (ulong)target.PlayerSlotIndex + 1);
                Rec.Emit(EventKind.Projectile);
            }
            catch (Exception e) { Rec.Fail("projectile", e); }
        }

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoFireGlue))]
        [HarmonyPostfix]
        private static void Glue(ProjectileManager.pFireGlue data)
        {
            Emit(1, data.syncID, data.position);
            if (!Rec.On) return;
            try { Foam(data.syncID, 0, data.position, default, data.velocity, data.expandVolume, data.glueStrengthMultiplier, data.allowSplat, data.owner.TryGet(out var agent) && agent != null ? (ulong)agent.PlayerSlotIndex + 1 : 0UL); } catch (Exception e) { Rec.Fail("foam", e); }
        }

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoSpawnGlueStatic))]
        [HarmonyPostfix]
        private static void LandedStatic(ProjectileManager.pSpawnGlueOnStatic data) => Landed(data.syncID, data.volumeDesc);

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoSpawnGlueOnEnemyAgent))]
        [HarmonyPostfix]
        private static void LandedEnemy(ProjectileManager.pSpawnGlueOnEnemyAgent data) => Landed(data.syncID, data.volumeDesc);

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoSpawnGlueOnStaticEnemy))]
        [HarmonyPostfix]
        private static void LandedStaticEnemy(ProjectileManager.pSpawnGlueOnStaticEnemy data) => Landed(data.syncID, data.volumeDesc);

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoSpawnGlueOnDoor))]
        [HarmonyPostfix]
        private static void LandedDoor(ProjectileManager.pSpawnGlueOnDoor data) => Landed(data.syncID, data.volumeDesc);

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoMergeGlue))]
        [HarmonyPostfix]
        private static void Merged(ProjectileManager.pMergeGlue data)
        {
            if (data.mergeToNothing) return; // it shrinks away and the game destroys it (DoDestroyGlue)
            if (!Rec.On || !Live(data.mergeTargetSyncID, out var glue)) return;
            try { Foam(data.mergeTargetSyncID, 1, glue.transform.position, glue.m_volumeDesc, Vec.Zero, 0f, 0f, false, 0); } catch (Exception e) { Rec.Fail("foam", e); }
        }

        // Foam on an enemy or a door is recorded where it is in the world: a replay shows it there, stuck to nothing.
        private static void Landed(uint id, GlueVolumeDesc volume)
        {
            if (!Rec.On || !Live(id, out var glue)) return;
            try { Foam(id, 1, glue.transform.position, volume, Vec.Zero, 0f, 0f, false, 0); } catch (Exception e) { Rec.Fail("foam", e); }
        }

        private static bool Live(uint id, out GlueGunProjectile glue)
        {
            glue = null!;
            var all = ProjectileManager.Current?.m_glueGunProjectiles;
            // Not TryGetValue: on this interop a game dictionary's TryGetValue with a class value throws (MissingMethodException, System.__Canon).
            if (all == null || !all.ContainsKey(id)) return false;
            glue = all[id];
            return glue != null;
        }

        // Foam (61), a state event: U sync id, U phase (0 flying, 1 landed, 2 gone), U volume x 1000, Pos, Pos velocity, U expand volume x 1000,
        // U current scale x 1000, U strength x 1000, U allow splat, U owner slot + 1.
        private static void Foam(uint id, ulong phase, Vector3 p, GlueVolumeDesc volume, Vector3 velocity, float expand, float strength, bool splat, ulong owner)
        {
            var key = (ulong)id;
            var flying = phase == 0;
            Rec.Begin().U(key).U(phase).U(Pack(volume.volume, 1000f)).Pos(p.x, p.y, p.z).Pos(velocity.x, velocity.y, velocity.z)
                .U(Pack(flying ? Mathf.Max(0f, expand) : volume.expandVolume, 1000f)).U(Pack(volume.currentScale, 1000f)).U(Pack(strength, 1000f)).U(splat ? 1UL : 0UL).U(owner);
            Rec.EmitState(EventKind.Foam, key);
        }

        [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.DoDestroyGlue))]
        [HarmonyPostfix]
        private static void GlueGone(ProjectileManager.pDestroyGlue data)
        {
            Emit(2, data.syncID, Vec.Zero);
            if (!Rec.On) return;
            try { Foam(data.syncID, 2, Vec.Zero, default, Vec.Zero, 0f, 0f, false, 0); } catch (Exception e) { Rec.Fail("foam", e); }
        }

        // While a recording plays the foam is what the recording says: a blob in the air flies until its recorded landing, and foam settles nothing with anything it touches.
        [HarmonyPatch(typeof(GlueGunProjectile), nameof(GlueGunProjectile.CollisionCheck))]
        [HarmonyPrefix]
        private static bool NoLanding(ref bool __result)
        {
            if (!Replayer.Active) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(GlueGunProjectile), nameof(GlueGunProjectile.OnTriggerEnter))]
        [HarmonyPrefix]
        private static bool NoTouch() => !Replayer.Active;

        private static void Emit(ulong kind, ulong id, Vector3 p)
        {
            if (!Rec.On) return;
            try { Rec.Begin().U(kind).U(id).Pos(p.x, p.y, p.z); Rec.Emit(EventKind.Projectile); } catch (Exception e) { Rec.Fail("projectile", e); }
        }
    }

    // U enemy entity, U kind (0 scream, 1 scout scream), U animation index (the scream animation the enemy plays).
    private static class Scream
    {
        internal static void Screamed(ES_Scream state) => Emit(state.m_enemyAgent, 0, (ulong)Math.Max(0, state.m_lastAnimIndex));

        internal static void Scouted(ES_ScoutScream state) => Emit(state.m_enemyAgent, 1, 0);

        private static void Emit(EnemyAgent? enemy, ulong kind, ulong animation)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(enemy);
                if (entity < 0) return;
                var now = PlayerRecorder.Now;
                if (kind == 0 && LastScream.TryGetValue(entity, out var at) && now - at <= RepeatMs) return;
                if (kind == 0) LastScream[entity] = now;
                Rec.Begin().U((ulong)entity).U(kind).U(animation);
                Rec.Emit(EventKind.Scream);
            }
            catch (Exception e) { Rec.Fail("scream", e); }
        }
    }

    // 49 EnemyAttack: U entity, U attack state (ES_StateEnum), U animation index, Pos aim, U wind-up and attack time in ms.
    // 50 EnemyHit: U entity, U reaction type (ES_HitreactType), U impact side (ImpactDirection), U animation index.
    private static class EnemyAct
    {
        internal static void Attack(ES_EnemyAttackBase state, Vector3 aim, int animation)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(state.m_enemyAgent);
                if (entity < 0) return;
                var seconds = Mathf.Max(0f, state.m_attackWindupDuration) + Mathf.Max(0f, state.m_attackDoneTimer);
                Rec.Begin().U((ulong)entity).U((ulong)(int)state.m_stateEnum).U((ulong)Mathf.Max(0, animation)).Pos(aim.x, aim.y, aim.z).U((ulong)Mathf.RoundToInt(Mathf.Clamp(seconds, 0f, 20f) * 1000f));
                Rec.Emit(EventKind.EnemyAttack);
            }
            catch (Exception e) { Rec.Fail("enemy-attack", e); }
        }

        internal static void Hitreact(ES_Hitreact state)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(state.m_enemyAgent);
                if (entity < 0) return;
                var data = state.m_data;
                Rec.Begin().U((ulong)entity).U((ulong)(int)data.ReactionType).U((ulong)(int)data.ReactionDirection).U((ulong)data.AnimationIndex);
                Rec.Emit(EventKind.EnemyHit);
            }
            catch (Exception e) { Rec.Fail("enemy-hit", e); }
        }
    }

    // 53 Wake: U enemy entity, U propagated (1 when another enemy's wake-up passed it on, 0 when the enemy itself noticed someone), U distance x10
    // (metres from what woke it), U delay in ms before it gets up, U propagator (the enemy entity + 1 whose propagation handed it its target,
    // 0 none), then what the game itself names as the cause: U the detection type of the enemy's target (the game's AgentTargetDetectionType:
    // 2 sensed while asleep, 3 an ability, 4 damage, 5 passed on; 255 no target), U 1 when it woke on a noise it heard
    // (EB_Hibernating.OnNoiseDetected), U the player it woke for: slot + 1 (the target's agent, or the noise's maker; 0 not a player),
    // U what that player was doing as the target says (the game's Agent.NoiseType: 2 sneak, 3 walk, 4 run, 5 jump, 6 loud landing, 7 melee
    // hit, 8 shot, 9 silenced shot, 10 decoy; 0 none). Written from ES_HibernateWakeUp.ActivateState, which only the host runs: a client
    // has no Wake events and its wake-ups have no known cause.
    // patch-targets: observation (read-only, put on through ForgeCore Hooks.ObserveClass beside each method's owner)
    [HarmonyPatch]
    private static class Wake
    {
        // A sleeper woken by another enemy is handed that enemy's target in the propagation itself (EnemyAgent.PropagateTargetLimited / Full
        // calls EnemyAI.InjectPropagatedTarget on each sleeper it reaches) and gets up on a later frame, when its own detection reads the
        // target as passed on. The enemy whose propagation ran is the propagator, by pointer of the woken enemy.
        private static int _propagating = -1;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, int> HandedBy = new();
        // A noise being heard, with its maker's slot (-1 not a player), while EB_Hibernating.OnNoiseDetected runs.
        private static bool _hearing;
        private static int _noiseMaker = -1;

        internal static void Reset() { _propagating = -1; HandedBy.Clear(); _hearing = false; _noiseMaker = -1; }

        [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.PropagateTargetLimited))]
        [HarmonyPrefix]
        private static void PropagatingLimited(EnemyAgent __instance) { if (Rec.On) _propagating = EnemyRecorder.Entity(__instance); }

        [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.PropagateTargetLimited))]
        [HarmonyFinalizer]
        private static Exception? PropagatedLimited(Exception? __exception) { _propagating = -1; return __exception; }

        [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.PropagateTargetFull))]
        [HarmonyPrefix]
        private static void PropagatingFull(EnemyAgent __instance) { if (Rec.On) _propagating = EnemyRecorder.Entity(__instance); }

        [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.PropagateTargetFull))]
        [HarmonyFinalizer]
        private static Exception? PropagatedFull(Exception? __exception) { _propagating = -1; return __exception; }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.InjectPropagatedTarget))]
        [HarmonyPostfix]
        private static void Handed(EnemyAI __instance)
        {
            if (!Rec.On || _propagating < 0) return;
            try { if (__instance.m_enemyAgent is { } woken) HandedBy[woken.Pointer] = _propagating; }
            catch (Exception e) { Rec.Fail("wake-pass", e); }
        }

        [HarmonyPatch(typeof(EB_Hibernating), nameof(EB_Hibernating.OnNoiseDetected))]
        [HarmonyPrefix]
        private static void Hearing(ref NM_NoiseData noiseData)
        {
            if (!Rec.On) return;
            _hearing = true; _noiseMaker = -1;
            try { _noiseMaker = noiseData.noiseMaker?.TryCast<PlayerAgent>() is { } maker ? maker.PlayerSlotIndex : -1; }
            catch (Exception e) { Rec.Fail("wake-noise", e); }
        }

        [HarmonyPatch(typeof(EB_Hibernating), nameof(EB_Hibernating.OnNoiseDetected))]
        [HarmonyFinalizer]
        private static Exception? Heard(Exception? __exception) { _hearing = false; return __exception; }

        // ES_HibernateWakeUp.ActivateState is patched once, by ForgeCore (GameHooks.HibernateWakeActivated); this follows the call when it ran.
        internal static void Activated((ES_HibernateWakeUp State, float Distance, float Delay, bool Propagated) call, bool ran)
        {
            if (!ran || !Rec.On) return;
            var (__instance, distance, delay, isPropagatedWakeup) = call;
            try
            {
                var enemy = __instance.m_enemyAgent;
                var entity = EnemyRecorder.Entity(enemy);
                if (entity < 0) return;
                EnemyRecorder.Hot(entity);
                var from = HandedBy.Remove(enemy.Pointer, out var by) ? by : -1;
                var target = enemy.AI?.Target;
                var player = _hearing ? _noiseMaker : target?.m_agent?.TryCast<PlayerAgent>() is { } agent ? agent.PlayerSlotIndex : -1;
                Rec.Begin().U((ulong)entity).U(isPropagatedWakeup ? 1UL : 0UL).U((ulong)Mathf.Max(0, Mathf.RoundToInt(distance * 10f))).U((ulong)Mathf.Max(0, Mathf.RoundToInt(delay * 1000f)))
                    .U(isPropagatedWakeup && from >= 0 ? (ulong)from + 1 : 0UL)
                    .U(target == null ? 255UL : (ulong)target.m_detectionType).U(_hearing ? 1UL : 0UL).U(player < 0 ? 0UL : (ulong)player + 1)
                    .U(target == null ? 0UL : (ulong)target.noiseType);
                Rec.Emit(EventKind.Wake);
                if (HostSync.Sending) HostSync.Wake(enemy, isPropagatedWakeup, _hearing, target == null ? 7 : (int)target.m_detectionType, player < 0 ? 0 : player + 1, target == null ? 0 : (int)target.noiseType, isPropagatedWakeup ? from : -1);
            }
            catch (Exception e) { Rec.Fail("wake", e); }
        }
    }

    // patch-targets: observation (read-only, put on through ForgeCore Hooks.ObserveClass beside each method's owner)
    [HarmonyPatch]
    private static class WakeAnim
    {
        // 92 WakeUp: U enemy entity, U 1 for the turning set of wake-up animations, U the animation's index, U delay in ms before it plays,
        // U propagated. ES_HibernateWakeUp.DoWakeup runs on every machine (the host from the state packet it sends itself), so a client has it too.
        [HarmonyPatch(typeof(ES_HibernateWakeUp), nameof(ES_HibernateWakeUp.DoWakeup))]
        [HarmonyPostfix]
        private static void Getting(ES_HibernateWakeUp __instance, bool turn, int index, float delay, bool isPropagatedWakeup)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(__instance.m_enemyAgent);
                if (entity < 0) return;
                EnemyRecorder.Hot(entity);
                Rec.Begin().U((ulong)entity).U(turn ? 1UL : 0UL).U((ulong)Math.Max(0, index)).U((ulong)Mathf.Max(0, Mathf.RoundToInt(delay * 1000f))).U(isPropagatedWakeup ? 1UL : 0UL);
                Rec.Emit(EventKind.WakeUp);
            }
            catch (Exception e) { Rec.Fail("wake-up", e); }
        }
    }

    // patch-targets: observation (read-only, put on through ForgeCore Hooks.ObserveClass beside each method's owner)
    [HarmonyPatch]
    private static class SleepSense
    {
        // A sleeper starting or ending to sense someone: written at once (EnemyMind), not left to the round of reads.
        [HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.DoStartDetection))]
        [HarmonyPostfix]
        private static void Sensing(ES_Hibernate __instance) { if (Rec.On) EnemyRecorder.Mind(__instance.m_enemyAgent); }

        [HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.DoEndDetection))]
        [HarmonyPostfix]
        private static void Calmed(ES_Hibernate __instance) { if (Rec.On) EnemyRecorder.Mind(__instance.m_enemyAgent); }
    }

    // U enemy entity, U on (1 while the scout's feelers are out).
    [HarmonyPatch]
    private static class Scout
    {
        internal static void On(ES_ScoutDetection __instance) => Emit(__instance.m_owner, 1);

        [HarmonyPatch(typeof(ES_ScoutDetection), nameof(ES_ScoutDetection.CommonExit))]
        [HarmonyPostfix]
        private static void Off(ES_ScoutDetection __instance) => Emit(__instance.m_owner, 0);

        private static void Emit(EnemyAgent? enemy, ulong on)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(enemy);
                if (entity < 0) return;
                Rec.Begin().U((ulong)entity).U(on);
                Rec.Emit(EventKind.Scout);
            }
            catch (Exception e) { Rec.Fail("scout", e); }
        }
    }

    // U source player slot (255 none), U type (NM_NoiseType), Pos, U largest radius x10. A noise made here (MakeNoise) and one that arrived
    // from another machine (ReceiveNoise) are each written once.
    [HarmonyPatch]
    private static class Noise
    {
        internal static void Made(NM_NoiseData noiseData)
        {
            if (!Rec.On) return;
            try
            {
                var source = noiseData.noiseMaker?.TryCast<PlayerAgent>();
                Write(Slot(source), (ulong)(int)noiseData.type, noiseData.position, noiseData.radiusMax);
            }
            catch (Exception e) { Rec.Fail("noise", e); }
        }

        [HarmonyPatch(typeof(NoiseManager), nameof(NoiseManager.ReceiveNoise))]
        [HarmonyPrefix]
        private static void Received(pNM_NoiseData data)
        {
            if (!Rec.On) return;
            try
            {
                var source = data.noiseMaker.TryGet(out var agent) ? agent?.TryCast<PlayerAgent>() : null;
                Write(Slot(source), (ulong)(int)data.type, data.position, data.radiusMax);
            }
            catch (Exception e) { Rec.Fail("noise", e); }
        }

        private static void Write(ulong slot, ulong type, Vector3 p, float radius)
        {
            Rec.Begin().U(slot).U(type).Pos(p.x, p.y, p.z).U((ulong)Mathf.Max(0, Mathf.RoundToInt(radius * 10f)));
            Rec.Emit(EventKind.Noise);
        }
    }

    // U slot, Pos of what was pinged.
    [HarmonyPatch]
    private static class Ping
    {
        [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.TriggerMarkerPing))]
        [HarmonyPostfix]
        private static void Pinged(PlayerAgent __instance, Vector3 worldPos)
        {
            if (!Rec.On) return;
            try { Rec.Begin().U(Slot(__instance)).Pos(worldPos.x, worldPos.y, worldPos.z); Rec.Emit(EventKind.Ping); } catch (Exception e) { Rec.Fail("ping", e); }
        }
    }

    // Light and fog: one state object (key 0) = current fog data block id of the reality dimension, extra = light sequence section count, Pos = where the sequence began.
    [HarmonyPatch]
    private static class Environment
    {
        [HarmonyPatch(typeof(EnvironmentStateManager), nameof(EnvironmentStateManager.OnStateChange))]
        [HarmonyPostfix]
        private static void Changed(pEnvironmentState newState)
        {
            if (!Rec.On) return;
            try
            {
                var fog = EnvironmentStateManager.GetCurrentFogID(eDimensionIndex.Reality);
                var sequence = newState.LatestLightSequenceInteraction;
                Rec.Begin().U(0).U(fog).U((ulong)Mathf.Max(0, sequence.SectionCount)).Pos(sequence.Position.x, sequence.Position.y, sequence.Position.z);
                Rec.EmitState(EventKind.Environment, 0);
                ZoneLights(newState);
            }
            catch (Exception e) { Rec.Fail("environment", e); }
        }
    }

    // U slot, U 1 joined / 0 left, Text name, Text loadout (the header's gear JSON lines, so a player who joins later can be drawn with
    // their gear). Not per frame: reading the gear JSON allocates.
    [HarmonyPatch]
    private static class Presence
    {
        [HarmonyPatch(typeof(PlayerSync), nameof(PlayerSync.OnSpawn))]
        [HarmonyPostfix]
        private static void Joined(PlayerSync __instance) => Emit(__instance, 1);

        [HarmonyPatch(typeof(PlayerSync), nameof(PlayerSync.OnDespawn))]
        [HarmonyPrefix]
        private static void Left(PlayerSync __instance) => Emit(__instance, 0);

        private static void Emit(PlayerSync sync, ulong joined)
        {
            if (!Rec.On) return;
            try
            {
                var agent = sync.m_agent;
                var owner = agent == null ? null : agent.Owner;
                Write(Slot(agent), joined, owner == null ? "" : owner.NickName, owner == null ? "" : Session.Loadout(owner));
            }
            catch (Exception e) { Rec.Fail("presence", e); }
        }

        internal static void Write(ulong slot, ulong joined, string name, string loadout)
        {
            Rec.Begin().U(slot).U(joined).Text(name).Text(loadout, PayloadWriter.Capacity);
            Rec.Emit(EventKind.Presence);
            if (joined == 1 && slot <= 3) PlayerRecorder.NoteLoadout((int)slot, loadout);
        }
    }

    // A player's gear differs from what the file last said (a bot's gear was not there yet when the header was written, a loadout changed at a
    // checkpoint): the newest Presence line is what a replay reads, so one more is written.
    internal static void GearChanged(SNet_Player owner, int slot, string loadout)
    {
        try { Presence.Write((ulong)slot, 1, owner.NickName, loadout); } catch (Exception e) { Rec.Fail("presence", e); }
    }

    // 56 ReviveBy: U target slot, U reviver slot. The revive action runs on every machine and names both.
    [HarmonyPatch]
    private static class ReviveBy
    {
        [HarmonyPatch(typeof(Agents.AgentReplicatedActions), "DoPlayerRevive")]
        [HarmonyPrefix]
        private static void Revive(Agents.AgentReplicatedActions.pPlayerReviveAction data)
        {
            if (!Rec.On) return;
            try
            {
                if (!data.TargetPlayer.TryGet(out var target) || !data.SourcePlayer.TryGet(out var source)) return;
                Rec.Begin().U(Slot(target)).U(Slot(source));
                Rec.Emit(EventKind.ReviveBy);
            }
            catch (Exception e) { Rec.Fail("revive-by", e); }
        }
    }

    // 57 Supply: U giver slot, U receiver slot, U kind (0 health, 1 ammunition, 2 disinfection). Runs on the giver's machine: the recorder's
    // own gifts and, on the host, the bots'. A gift from another machine reaches here without its giver (ClientInference works those out).
    [HarmonyPatch]
    private static class Supply
    {
        [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.GiveHealth))]
        [HarmonyPrefix]
        private static void Health(PlayerAgent __instance, PlayerAgent giverAgent) => Write(giverAgent, __instance, 0);

        [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.GiveAmmoRel))]
        [HarmonyPrefix]
        private static void Ammo(PlayerAgent __instance, PlayerAgent giverAgent) => Write(giverAgent, __instance, 1);

        [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.GiveDisinfection))]
        [HarmonyPrefix]
        private static void Disinfect(PlayerAgent __instance, PlayerAgent giverAgent) => Write(giverAgent, __instance, 2);

        private static void Write(PlayerAgent? giver, PlayerAgent receiver, ulong kind)
        {
            if (giver == null || !Rec.On) return;
            try
            {
                var from = Slot(giver); var to = Slot(receiver);
                Rec.Begin().U(from).U(to).U(kind);
                Rec.Emit(EventKind.Supply);
                // A gift made on the host's machine (its own player and the bots): a client recording has no Supply event for it.
                if (HostSync.Sending && giver.IsLocallyOwned && from <= 3 && to <= 3) HostSync.Supply((int)from, (int)to, (int)kind);
            }
            catch (Exception e) { Rec.Fail("supply", e); }
        }
    }

    // 59 Disinfection: a state like the other level objects (value = eDisinfectionStationStatus). Hooks SyncStatusChanged, which
    // OnStateChange tail-calls whenever the status changes or a recall replays it. OnStateChange itself must not be detoured: it is
    // 19 bytes with a branch into its own first 14, so the detour's trampoline jumps into the overwritten bytes and the game dies
    // (c0000096) at every recall in a level with a station.
    [HarmonyPatch]
    private static class Disinfection
    {
        [HarmonyPatch(typeof(LG_DisinfectionStation), nameof(LG_DisinfectionStation.SyncStatusChanged))]
        [HarmonyPostfix]
        private static void Changed(LG_DisinfectionStation __instance, eDisinfectionStationStatus newStatus)
        {
            try { World(EventKind.Disinfection, __instance, Rec.Key(__instance), (ulong)(int)newStatus); } catch (Exception e) { Rec.Fail("disinfection", e); }
        }
    }

    // 58 Objective: one state per layer (key 0 main, 1 secondary, 2 overload): value = eWardenObjectiveStatus, extra = sub status x 256 + chain
    // index. Read by polling: another package patches the game's state change, and the state is one small struct.
    private static readonly (int Status, int Extra, bool Known, int Tail)[] LastObjective = new (int, int, bool, int)[3];

    // Music (60), a state event: U 0, U the MUS_State the local machine's music is in, U 0, Pos 0. The game's music follows its drama machine, which needs the
    // real alarm and enemies; a replay sets the recorded state. Polled, because the machine changes it from many places.
    private static int _lastMusic = -1;
    internal static void PollMusic()
    {
        if (!Rec.On || MusicManager.Machine == null) return;
        try
        {
            var now = (int)MusicManager.Machine.CurrentState;
            if (now == _lastMusic) return;
            _lastMusic = now;
            Rec.Begin().U(0).U((ulong)now).U(0).Pos(0f, 0f, 0f);
            Rec.EmitState(EventKind.Music, 0);
        }
        catch (Exception e) { Rec.Fail("music", e); }
    }

    internal static void PollObjective()
    {
        if (!Rec.On) return;
        try
        {
            var state = WardenObjectiveManager.CurrentState;
            Objective(0, (int)state.main_status, (int)state.main_subObj, state.main_chainIndex, state);
            Objective(1, (int)state.second_status, (int)state.second_subObj, state.second_chainIndex, state);
            Objective(2, (int)state.third_status, (int)state.third_subObj, state.third_chainIndex, state);
        }
        catch (Exception e) { Rec.Fail("objective", e); }
    }

    // Then the tail: U start of each of the three layers x 10, U extra time x 10, U custom sub objective header and text, U 1 win on death + 2 exit wave started.
    private static void Objective(int layer, int status, int sub, int chain, pWardenObjectiveState state)
    {
        var extra = sub * 256 + chain;
        var flags = (state.forceWinOnDeath ? 1 : 0) | (state.exitWaveTriggered ? 2 : 0);
        float main = state.main_startTime, second = state.second_startTime, third = state.third_startTime, extraTime = state.extraTime;
        var header = state.customSubObjectiveHeaderID; var text = state.customSubObjectiveTextID;
        var tail = HashCode.Combine(main, second, third, extraTime, header, text, flags);
        if (LastObjective[layer] == (status, extra, true, tail)) return;
        LastObjective[layer] = (status, extra, true, tail);
        Rec.Begin().U((ulong)layer).U((ulong)status).U((ulong)extra).Pos(0f, 0f, 0f)
            .U(Pack(main, 10f)).U(Pack(second, 10f)).U(Pack(third, 10f)).U(Pack(extraTime, 10f)).U(header).U(text).U((ulong)flags);
        Rec.EmitState(EventKind.Objective, (ulong)layer);
    }

    // U master id (the Steam ID of the new host). Not a game method: ForgeCore tells.
    internal static void HostChanged()
    {
        if (!Rec.On) return;
        try { Rec.Begin().U(ForgeCore.GameEvents.Master).U(ForgeCore.Session.IsHost ? 1UL : 0UL); Rec.Emit(EventKind.HostChanged); } catch (Exception e) { Rec.Fail("host-changed", e); }
    }
}
