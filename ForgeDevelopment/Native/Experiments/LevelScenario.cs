using System;
using System.Linq;
using Enemies;
using ChainedPuzzles;
using ForgeCore;
using Gear;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>A fight and a round of level interactions without anyone at the keyboard, all through the game's own calls (never key presses),
/// for any mod that records or reacts to play (ForgeReplay's recordings among them). Some seconds after <see cref="Start"/> it spawns aggressive strikers, a tank, shooters and a sleeping group
/// around the local player, fires the player's gun, damages the nearest enemy now and then, walks, crouches, jumps, throws, sends a chat
/// line, makes noise, pings a marker, places the tool in the tool slot (sentry or mine), uses a resource pack, downs and revives the player,
/// drives the level objects it finds (door, supply box, reactor, generator, alarm, pickup, terminal, wave), stores and reloads a
/// checkpoint, and ends after a fixed time by leaving the level (the level's cleanup runs, so a recorder closes its file) unless it was started to
/// stay. Each step is logged as "scenario: did ..." or "scenario: FAILED ..." in BepInEx/LogOutput.log; a step whose object the level does not
/// have fails, so pick a level that has them: 37-B1 (R4B1) has a reactor and a generator at a locked door
/// (a level whose only generators belong to a generator cluster, such as 38-C2, has none in the scene until its objective places them).
/// Called by name: <c>drive_cli.py call static:ForgeDevelopment.Native.LevelScenario Start [stay]</c>, then <c>State</c>.</summary>
internal static class LevelScenario
{
    private const float EndSeconds = 120f;
    private static float _start = -1f, _nextFire, _nextHit;
    private static bool _spawned;
    private static bool _meleeWielded, _stay, _ticking, _fireFailed, _walkFailed;
    private static string _state = "not started";

    private static void Note(string line) => Plugin.PluginLog.LogInfo("scenario: " + line);

    /// <summary>Starts the scenario a few seconds from now. <paramref name="stay"/>: the level stays up when it ends, so the game process can be
    /// ended from outside (a recorder's file is then cut off).</summary>
    internal static string Start(bool stay = false)
    {
        if (Players.Local == null) return "no local player: enter a level first";
        _start = Time.realtimeSinceStartup + 8f; _spawned = false; _meleeWielded = false; _stay = stay; Did.Clear();
        if (!_ticking) { _ticking = true; ForgeCore.Frame.OnUpdate("NAinfini.ForgeDevelopment.scenario", Tick); }
        _state = "armed";
        return _state;
    }

    internal static string State() => _start < 0f ? _state : "running " + (Time.realtimeSinceStartup - _start).ToString("F0") + " s of " + EndSeconds;

    private static void Tick()
    {
        if (_start < 0f || Time.realtimeSinceStartup < _start) return;
        var t = Time.realtimeSinceStartup - _start;
        var player = Players.Local;
        if (player == null) return;
        if (!_spawned) { _spawned = true; Spawn(player); player.Sync.WantsToWieldSlot(InventorySlot.GearStandard, false); }
        Interactions(t, player);
        World(t, player);
        Walk(t, player);
        if (!_meleeWielded && t > 25f) { _meleeWielded = true; player.Sync.WantsToWieldSlot(InventorySlot.GearMelee, false); }
        if (t > EndSeconds)
        {
            _start = -1f;
            Note("finished");
            _state = "finished, " + Did.Count + " steps tried";
            // Kept up, the level stays as it is, so a test can end the game process from outside while a recorder still writes.
            if (!_stay)
            {
                try { SNetwork.SNet.SessionHub.LeaveHub(); _state += ", level left"; }
                catch (Exception error) { _state += ", leaving failed: " + error.Message; }
            }
            return;
        }
        if (Time.realtimeSinceStartup >= _nextFire)
        {
            _nextFire = Time.realtimeSinceStartup + (_meleeWielded ? 1.5f : 0.25f);
            try
            {
                var gun = player.Inventory.WieldedItem?.TryCast<BulletWeapon>();
                var melee = player.Inventory.WieldedItem?.TryCast<MeleeWeaponFirstPerson>();
                if (gun != null) gun.Fire(false);
                else if (melee != null && t > 26f) melee.DoTriggerAttack(false);
            }
            catch (Exception error) { if (!_fireFailed) { _fireFailed = true; Note("FAILED fire: " + error.Message); } }
        }
        if (Time.realtimeSinceStartup >= _nextHit)
        {
            _nextHit = Time.realtimeSinceStartup + 1.5f;
            var near = ForgeCore.Enemies.Nearest(player.Position, 40f, 1);
            if (near.Length > 0 && near[0] != null)
            {
                var enemy = near[0]!;
                var direction = (enemy.Position - player.Position).normalized;
                if (_meleeWielded) Combat.Melee(enemy.Damage, 6f, player, enemy.Position, direction, 1f);
                else Combat.BulletDamage(enemy.Damage, 8f, player, enemy.Position, direction, -direction);
            }
        }
    }

    // The player walks (1.6 m/s, 4 to 8 s) and then runs (4.5 m/s, to 12 s) along the way they face, moved by the game's exact-root warp every
    // frame, so the recording holds a walking and a running player.
    private static bool _walkLogged, _ran;
    private static Vector3 _walkFrom;
    private static void Walk(float t, PlayerAgent player)
    {
        if (t >= 12f && _walkLogged) { _walkLogged = false; Note("walk ended at " + player.Position + " (started at " + _walkFrom + ")"); }
        if (t < 4f || t >= 12f) return;
        var run = t >= 8f;
        if (!_walkLogged) { _walkLogged = true; _walkFrom = player.Position; player.Locomotion.ChangeState(PlayerLocomotion.PLOC_State.Stand); }
        if (run && !_ran) { _ran = true; player.Locomotion.ChangeState(PlayerLocomotion.PLOC_State.Run); }
        var forward = player.TargetLookDir; forward.y = 0f; forward.Normalize();
        var to = player.Position + forward * ((run ? 4.5f : 1.6f) * Time.unscaledDeltaTime);
        try
        {
            // The exact-root warp: the native TryWarpTo snaps the position to the centre of the navigation cell, which undoes a step this small.
            var location = player.Sync.GetPlayerLocationReconstruction();
            location.courseNode.Set(player.CourseNode); location.goodPosition = to; location.lookDir = forward;
            player.WarpTo(to, Quaternion.LookRotation(forward, Vector3.up), location);
            player.FPSCamera?.SetLookDirection(forward);
        }
        catch (Exception error) { if (!_walkFailed) { _walkFailed = true; Note("FAILED walk: " + error.Message); } }
    }

    // Level interactions driven through the game' own calls (never key presses), one each, to see their recorder hooks fire.
    private static readonly System.Collections.Generic.HashSet<int> Did = new();

    private static void Once(int id, float t, float at, string name, Action act)
    {
        if (t < at || !Did.Add(id)) return;
        try { act(); Note("did: " + name); }
        catch (Exception error) { Note("FAILED " + name + ": " + error.Message); }
    }

    // A step that needs the game to be ready first: tried every frame from `at` on, done as soon as `waiting` answers null, failed with its
    // answer when that has not happened `wait` seconds later.
    private static void OnceReady(int id, float t, float at, float wait, string name, Func<string?> waiting, Action act)
    {
        if (t < at || Did.Contains(id)) return;
        var why = waiting();
        if (why != null && t < at + wait) return;
        Did.Add(id);
        if (why != null) { Note("FAILED " + name + ": " + why); return; }
        try { act(); Note("did: " + name); }
        catch (Exception error) { Note("FAILED " + name + ": " + error.Message); }
    }

    // The reload as the game plays it: the wielded weapon's own animation (the recorder's Reload event), and the interact message the others
    // receive. The gun is put in the hands first and the reload waits until it is there: after the scenario's first twenty seconds of firing,
    // walking and jumping the hands were seen empty at the second reload.
    private static string? NoGun(PlayerAgent player) => player.Inventory.WieldedItem?.TryCast<BulletWeapon>() != null ? null : "no gun in the hands (wielded: " + (player.Inventory.WieldedItem?.name ?? "nothing") + ")";

    private static void Interactions(float t, PlayerAgent player)
    {
        var at = player.Position;
        OnceReady(1, t, 3f, 3f, "reload animation", () => NoGun(player), () => PlayerActions.Reload());
        Once(2, t, 4f, "throw charge", () => PlayerActions.Throw(false));
        Once(3, t, 4.5f, "throw", () => PlayerActions.Throw(true));
        Once(9, t, 12f, "crouch", () => PlayerActions.Posture("Crouch"));
        Once(10, t, 15f, "stand", () => PlayerActions.Posture("Stand"));
        Once(11, t, 17f, "jump", () => PlayerActions.Posture("Jump"));
        Once(13, t, 19f, "gun back in the hands", () => player.Sync.WantsToWieldSlot(InventorySlot.GearStandard, false));
        OnceReady(12, t, 20f, 4f, "reload animation again", () => NoGun(player), () => PlayerActions.Reload());
        // The noise needs the course node it is made in, or the game's entry makes nothing.
        Once(4, t, 5f, "noise", () => { if (!Combat.Noise(at, 15f, player.CourseNode)) throw new InvalidOperationException("the game made no noise"); });
        // A line into the player's own chat, the entry the game's chat box ends in (the one the recorder hooks); sending one to other peers has no local echo.
        Once(5, t, 6f, "chat", () => PlayerChatManager.PostChatMessageLocaly(player.Owner, "replay scenario hello"));
        Once(6, t, 8f, "bullet damage to the player", () => Combat.BulletDamage(player.Damage, 2f, null, at, Vector3.forward, Vector3.back));
        Once(7, t, 9f, "explosion damage to the player", () => Combat.ExplosionDamage(player.Damage, 2f, at, Vector3.zero));
        Once(15, t, 15f, "supply box open", () => WorldStates.Container(Nearest<LG_ResourceContainer_Sync>(at, "supply box"), eResourceContainerStatus.Open, false));
        Once(16, t, 16f, "reactor start-up", () => WorldStates.Reactor(Nearest<LG_WardenObjective_Reactor>(at, "reactor"), eReactorStatus.Startup_intro, 0.5f, false));
        Once(17, t, 18f, "shooters", () => Spawn(player, "Shooter", 3, 0));
        // A tank (its tentacle attack is the one recorded by the AttackIn / OnAttackIsOut hooks besides the striker's tongue).
        Once(40, t, 21f, "tank", () => Spawn(player, "Tank", 1, 0));
        Once(8, t, 10f, "open the nearest security door", () =>
        {
            LG_SecurityDoor? best = null;
            foreach (var door in UnityEngine.Object.FindObjectsOfType<LG_SecurityDoor>())
                if (door != null && (best == null || (door.transform.position - at).sqrMagnitude < (best.transform.position - at).sqrMagnitude)) best = door;
            if (best == null) throw new InvalidOperationException("no security door in the level");
            best.m_sync.AttemptDoorInteraction(eDoorInteractionType.Open, 0f, 0f, at, player);
        });
    }

    // The level objects nearest the player, driven through ForgeCore.WorldStates (the game's own state setters), one of each kind.
    private static T? Nearest<T>(Vector3 at) where T : Component
    {
        T? best = null;
        foreach (var thing in UnityEngine.Object.FindObjectsOfType<T>())
            if (thing != null && (best == null || (thing.transform.position - at).sqrMagnitude < (best.transform.position - at).sqrMagnitude)) best = thing;
        return best;
    }

    private static void World(float t, PlayerAgent player)
    {
        var at = player.Position;
        Once(20, t, 30f, "melee kill the nearest custom limb", () =>
        {
            Dam_EnemyDamageLimb_Custom? limb = null;
            foreach (var enemy in ForgeCore.Enemies.Nearest(at, 60f, 8))
                if (enemy != null && limb == null) limb = enemy.GetComponentInChildren<Dam_EnemyDamageLimb_Custom>();
            if (limb == null) throw new InvalidOperationException("no enemy with a custom limb near the player");
            limb.DestroyLimb(default);
        });
        Once(21, t, 40f, "down the player", () => PlayerActions.Down());
        Once(22, t, 45f, "revive the player", () => PlayerActions.Revive());
        Once(23, t, 50f, "chained puzzle (scan, alarm)", () => WorldStates.Puzzle(Nearest<ChainedPuzzles.ChainedPuzzleInstance>(at) ?? throw new InvalidOperationException("no chained puzzle in the level"), eChainedPuzzleStatus.Active, false));
        Once(24, t, 56f, "chained puzzle solved", () => WorldStates.Puzzle(Nearest<ChainedPuzzles.ChainedPuzzleInstance>(at) ?? throw new InvalidOperationException("no chained puzzle in the level"), eChainedPuzzleStatus.Solved, false));
        Once(25, t, 60f, "generator powered", () =>
        {
            WorldStates.Generator(Nearest<LG_PowerGenerator_Core>(at) ?? throw new InvalidOperationException("no generator in the level"), ePowerGeneratorStatus.Powered, false);
        });
        Once(26, t, 64f, "pickup item picked", () => WorldStates.Item(Nearest<LG_PickupItem_Sync>(at) ?? throw new InvalidOperationException("no pickup item in the level"), ePickupItemStatus.PickedUp, false));
        Once(27, t, 68f, "terminal command", () =>
        {
            var terminal = Nearest<LG_ComputerTerminal>(at) ?? throw new InvalidOperationException("no terminal in the level");
            terminal.m_command.ReceiveCommand(TERM_Command.Help, "HELP", "", "");
        });
        Once(28, t, 72f, "survival wave", () =>
        {
            if (!Dimension.TryGetCourseNodeFromPos(at, out var node) || node == null) throw new InvalidOperationException("no course node at the player");
            var settings = GameData.GameDataBlockBase<GameData.SurvivalWaveSettingsDataBlock>.GetAllBlocks().First(b => b.internalEnabled).persistentID;
            var population = GameData.GameDataBlockBase<GameData.SurvivalWavePopulationDataBlock>.GetAllBlocks().First(b => b.internalEnabled).persistentID;
            if (!Mastermind.Current.TriggerSurvivalWave(node, settings, population, out _)) throw new InvalidOperationException("the game refused the wave");
        });
        // A pack pickup made in the level and taken into the backpack through the game's own synced pickup interaction (the offline start has
        // no pack), so that the wield and apply steps below have one in the hands.
        Once(31, t, 52f, "item spawn and pickup (a medkit)", () => PlayerActions.Give("Medi"));
        Once(30, t, 84f, "loud noise to wake the hibernating group", () => { if (!Combat.Noise(at, 30f, player.CourseNode)) throw new InvalidOperationException("the game made no noise"); });
        // A ping on a point in front of the player, with the game's own manual target (a ping on a thing in the level needs that thing's target).
        Once(41, t, 33f, "marker ping", () =>
        {
            var target = new ManualPingTarget().TryCast<iPlayerPingTarget>() ?? throw new InvalidOperationException("the manual ping target is not a ping target");
            player.TriggerMarkerPing(target, player.gameObject, at + Flat(player) * 6f);
        });
        Once(42, t, 36f, "place a sentry", () => PlaceTool(player, "SentryGun_Burst", "Sentry"));
        Once(48, t, 38f, "place a mine", () => PlaceTool(player, "Mine_Deployer", "Mine"));
        // Something for the sentry to shoot at once it stands (its shots are the recorder's SentryShot events).
        Once(49, t, 40f, "strikers within the sentry's range", () => Spawn(player, "Striker", 3, 0));
        // A scout in the game's scout mode puts its feelers out and back by itself (the recorder's Scout events).
        Once(50, t, 22f, "a scout feeling about", () => Spawn(player, "Scout", 0, 1, Agents.AgentMode.Scout, 18f));
        // The pack is taken in the hands, applied and the melee weapon put back, because the scenario's swings need it.
        Once(43, t, 55f, "wield the resource pack", () => player.Sync.WantsToWieldSlot(InventorySlot.ResourcePack, false));
        Once(44, t, 58f, "use the resource pack", () =>
        {
            (player.Inventory.WieldedItem?.TryCast<ResourcePackFirstPerson>()
                ?? throw new InvalidOperationException("no resource pack in the hands (wielded: " + (player.Inventory.WieldedItem?.name ?? "nothing") + ")")).ApplyPack();
        });
        Once(45, t, 61f, "melee weapon back in the hands", () => player.Sync.WantsToWieldSlot(InventorySlot.GearMelee, false));
        Once(46, t, 88f, "store a checkpoint", () => StoreCheckpoint(player));
        Once(47, t, 97f, "reload the checkpoint", ReloadCheckpoint);
    }

    // The player's forward direction along the ground.
    private static Vector3 Flat(PlayerAgent player)
    {
        var forward = player.TargetLookDir; forward.y = 0f;
        return forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
    }

    // A deployable put into the level the way the game spawns a deployed one: a sentry through ForgeCore's one sentry entry (the class pool is
    // its ammunition), a mine through the game's gear spawn. The gear is the offline tool of that name (whatever the player carries in the tool
    // slot, which is not necessarily a deployable), found in the game's list of tool gear.
    private static void PlaceTool(PlayerAgent player, string offlineName, string family)
    {
        if (family == "Mine") { PlaceMine(player); return; }
        var block = GameData.GameDataBlockBase<GameData.PlayerOfflineGearDataBlock>.GetAllBlocks()
            .FirstOrDefault(b => b.internalEnabled && string.Equals(b.name, offlineName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("no offline gear block named " + offlineName);
        var suffix = "_" + block.persistentID;
        GearIDRange? gear = null;
        foreach (var candidate in GearManager.GetAllGearForSlot(InventorySlot.GearClass))
            if (candidate != null && gear == null && candidate.PlayfabItemInstanceId != null && candidate.PlayfabItemInstanceId.EndsWith(suffix, StringComparison.Ordinal)) gear = candidate;
        if (gear == null) throw new InvalidOperationException("the tool gear list holds no " + offlineName + " (" + suffix + ")");
        var json = gear.ToJSON();
        var flat = Flat(player);
        var at = InFront(player, flat, 2.5f, out var node);
        var done = new Action<ISyncedItem, PlayerAgent>((_, _) => { });
        var rotation = Quaternion.LookRotation(flat, Vector3.up);
        if (!ForgeCore.Gear.SentryAmmoReady(player)) throw new InvalidOperationException("the sentry's ammunition pool is not readable yet");
        ForgeCore.Gear.SpawnSentry(json, done, at, rotation, node, player, 1f, 1f);
    }

    // A point in front of the player in a course node the game knows: the wanted distance, then nearer (a wall or a drop in front has no node),
    // then the player's own place.
    private static Vector3 InFront(PlayerAgent player, Vector3 flat, float distance, out AIGraph.AIG_CourseNode node)
    {
        for (var d = distance; d >= 0f; d -= 0.5f)
        {
            var at = player.Position + flat * d;
            if (Dimension.TryGetCourseNodeFromPos(at, out var found) && found != null) { node = found; return at; }
        }
        node = player.CourseNode ?? throw new InvalidOperationException("the player has no course node");
        return player.Position;
    }

    // A placed mine is an item of its own in the instance mode (the gear spawn has no replicator for it), found among the item blocks by name.
    private static void PlaceMine(PlayerAgent player)
    {
        var flat = Flat(player);
        var at = InFront(player, flat, 2.5f, out var node);
        var mines = GameData.GameDataBlockBase<GameData.ItemDataBlock>.GetAllBlocks().Where(b => b.internalEnabled && b.name.Contains("Mine", StringComparison.OrdinalIgnoreCase)).ToList();
        var tried = new System.Collections.Generic.List<string>();
        foreach (var block in mines)
        {
            try { if (ForgeCore.Items.Spawn(block.persistentID, block.inventorySlot, null, ItemMode.Instance, at, Quaternion.LookRotation(flat, Vector3.up), node, out _) != null) return; tried.Add(block.name + "/" + block.persistentID + " refused"); }
            catch (Exception error) { tried.Add(block.name + "/" + block.persistentID + " " + error.GetType().Name); }
        }
        throw new InvalidOperationException("no item block with a mine in the instance mode: " + string.Join("; ", tried));
    }

    // The checkpoint calls as ForgeDevelopment's pack test makes them (the game's own store, capture and replicated reload interaction).
    private static void StoreCheckpoint(PlayerAgent player)
    {
        var capture = SNetwork.SNet.Capture;
        if (!Session.IsHost || capture == null || capture.IsCapturing || capture.IsRecalling) throw new InvalidOperationException("this machine cannot store a checkpoint now");
        CheckpointManager.StoreCheckpoint(player.transform.position);
        capture.CaptureGameState(SNetwork.eBufferType.Checkpoint);
        capture.SendBuffer(SNetwork.eBufferType.Checkpoint, null, false);
    }

    private static void ReloadCheckpoint()
    {
        var manager = CheckpointManager.Current;
        var replicator = manager?.m_stateReplicator ?? throw new InvalidOperationException("no checkpoint manager in the level");
        manager!.AttemptInteract(new pCheckpointInteraction(eCheckpointInteractionType.ReloadCheckpoint, replicator.State.doorLockPosition));
    }

    private static T Nearest<T>(Vector3 at, string what) where T : Component => Nearest<T>(at) ?? throw new InvalidOperationException("no " + what + " in the level");

    private static void Spawn(PlayerAgent player, string family = "Striker", int count = 8, int sleepers = 4, Agents.AgentMode sleeping = Agents.AgentMode.Hibernate, float farther = 12f)
    {
        // The block named exactly as the family when there is one (a "Tank_Boss" is not a tank), else the first that contains it.
        var blocks = GameData.GameDataBlockBase<GameData.EnemyDataBlock>.GetAllBlocks().Where(value => value.internalEnabled).ToList();
        var block = blocks.FirstOrDefault(value => string.Equals(value.name, family, StringComparison.OrdinalIgnoreCase))
            ?? blocks.FirstOrDefault(value => value.name.Contains(family, StringComparison.OrdinalIgnoreCase));
        if (block == null || !Dimension.TryGetCourseNodeFromPos(player.Position, out var node) || node == null)
        {
            Note("FAILED spawn of " + family + ": no enemy block or no course node at the player");
            return;
        }
        for (var i = 0; i < count; i++)
        {
            var angle = i * Mathf.PI * 2f / count;
            var at = player.Position + new Vector3(Mathf.Cos(angle) * 6f, 0f, Mathf.Sin(angle) * 6f);
            EnemyAllocator.Current.SpawnEnemy(block.persistentID, node, Agents.AgentMode.Agressive, at, Quaternion.identity, null, -1);
        }
        // A sleeping group a little further out, so that noise wakes it (and wakes the rest by propagation).
        for (var i = 0; i < sleepers; i++)
        {
            var at = player.Position + new Vector3(farther + i * 0.8f, 0f, 3f);
            EnemyAllocator.Current.SpawnEnemy(block.persistentID, node, sleeping, at, Quaternion.identity, null, -1);
        }
    }
}
