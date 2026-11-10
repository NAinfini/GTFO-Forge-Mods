using System;
using System.Collections.Generic;
using System.Linq;
using Agents;
using Enemies;
using ForgeCore;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Puts the recording's people and monsters into the replay level: one puppet per enemy near the camera, one per recorded player
/// the level has a bot for. What a puppet shows comes from the recording only: the position and facing between the latest sample and the
/// next, an enemy's walk animation from the speed those samples give and a player's from the recorded walk values, death from the enemy's
/// end event, the player's stance and wielded slot from the latest status. How a puppet is held and driven is ForgeCore's <see cref="Puppets"/>. Main thread only.</summary>
internal static class Puppeteer
{
    // Puppets exist only near the camera: 85 enemies at once is more than the GPU of the test machine took (device removed), and an enemy far
    // from every viewer cannot be seen anyway. One is made within SpawnRadius of the followed player and parked beyond DropRadius.
    private const float SpawnRadius = 70f, DropRadius = 100f;
    private const int SpawnsPerFrame = 4;
    // A sample that is not repeated means the entity did not move; one older than this is standing still.
    private sealed class Foe
    {
        internal EnemyAgent Agent = null!;
        internal EnemyPuppet? Puppet;
        internal Vector3 Smoothed;
        internal ulong DataId;
        internal bool Parked;
        // The custom limbs shown destroyed on this agent (bits as WorldState.LimbsGone).
        internal ulong LimbsShown;
        // The replay's moment the recorded wake-up state was first shown (NotWaking: it is not waking).
        internal uint WakeSeenMs = NotWaking;
    }

    private const uint NotWaking = uint.MaxValue;

    private static readonly Dictionary<int, Foe> Foes = new();
    private static readonly List<int> Remove = new();
    // Enemy types the recording has and this game does not: skipped, not asked of the game again every frame.
    private static readonly HashSet<ulong> MissingTypes = new();
    private static readonly Dictionary<int, PlayerPuppet> Players = new();
    private static readonly Dictionary<int, CosmeticPlayer> Cosmetics = new();
    private static readonly int[] Wielded = { -1, -1, -1, -1 };
    private static readonly bool[] Charging = new bool[4];

    /// <summary>A recorded swing of a player's melee weapon on its puppet (the recorder writes it once the game decided it); false when the player has no puppet.</summary>
    internal static bool MeleeSwing(int slot, ulong kind)
    {
        var puppet = PlayerOf(slot);
        return puppet != null && puppet.MeleeSwing(kind != 0);
    }

    internal static int EnemyCount => Foes.Count;
    internal static int ParkedCount { get { var n = 0; foreach (var foe in Foes.Values) if (foe.Parked) n++; return n; } }
    internal static int PlayerCount => Players.Count;
    /// <summary>Counts every change of a player puppet's wielded item, so what draws round the puppet can be redone.</summary>
    internal static int WieldVersion { get; private set; }
    internal static int Retaken { get { var n = 0; foreach (var foe in Foes.Values) n += foe.Puppet?.Retaken ?? 0; return n; } }

    /// <summary>The enemies a puppet exists for (not parked), for the status line.</summary>
    internal static IEnumerable<(int Entity, EnemyAgent Agent)> Visible()
    {
        foreach (var (entity, foe) in Foes) if (!foe.Parked && foe.Puppet != null && foe.Puppet.Held) yield return (entity, foe.Agent);
    }

    /// <summary>The recorded end of an enemy: <paramref name="died"/> says it was killed rather than gone from sight. Its puppet ends here, at
    /// the event: the recorder numbers enemies by the game's enemy slot, so the next enemy in that slot (a wave fills freed slots at once) comes
    /// with the same number, often in the same frame, and a puppet kept until the next placement would be driven to the new enemy's place.</summary>
    internal static void EnemyEnded(int entity, bool died)
    {
        // It never appeared: its recorded life is over, so this is the moment the skip is real (a retry that works takes it out below).
        if (SpawnFailures.Remove(entity, out var failed)) NoteUnshown(entity, failed.Why, failed.Data);
        if (!Foes.TryGetValue(entity, out var foe)) return;
        // A puppet that is not held cannot die (Die does nothing): the enemy would stay standing where it was last put, so it is taken out instead.
        if (died && foe.Puppet is { Held: true } puppet && !foe.Parked && puppet.Die()) Corpses.Add(foe.Agent, Time.unscaledTime);
        else Park(foe, true);
        Foes.Remove(entity);
    }

    private static readonly CorpseBook<EnemyAgent> Corpses = new();
    private static readonly Action<EnemyAgent> HideCorpse = body =>
    {
        if (body == null || body.WasCollected) return;
        // The game's corpse decay (ES_Dead.DecayDelayed) waits 2-6 s through CoroutineManager, not on the agent. A jump
        // hides every corpse before that, and the delay then runs on a body that is off and may already stand for another
        // enemy: Decay.Initialize gets renderers that no longer belong to it and throws (11-10). Take back the decay that
        // has not started; once it has, its own effect is left to finish.
        if (body.Locomotion != null && body.Locomotion.Dead != null && body.Locomotion.Dead.TryCast<ES_Dead>() is { } dead
            && dead.m_decay == null && dead.m_currentDecayCoroutine != null && CoroutineManager.Current != null)
        {
            CoroutineManager.StopCoroutine(dead.m_currentDecayCoroutine);
            dead.m_currentDecayCoroutine = null;
        }
        body.Sound?.Stop();
        body.gameObject.SetActive(false);
    };

    private static EnemyPuppet? Live(int entity) => Foes.TryGetValue(entity, out var foe) && !foe.Parked && foe.Puppet is { Held: true } puppet ? puppet : null;

    private static PlayerAgent? NearestPlayer(Vector3 from)
    {
        PlayerAgent? best = null; var bestDistance = float.MaxValue;
        foreach (var puppet in Players.Values)
        {
            if (!puppet.Held) continue;
            var distance = Vec.SqrLength(puppet.Agent.transform.position - from);
            if (distance < bestDistance) { bestDistance = distance; best = puppet.Agent; }
        }
        return best;
    }

    /// <summary>A recorded enemy attack: the enemy plays it in the game's own attack state, aimed at the nearest player puppet.</summary>
    internal static void EnemyAttack(int entity, ES_StateEnum state, Vector3 aim, int animation, float seconds)
    {
        if (Live(entity) is { } puppet) puppet.Attack(state, aim, animation, seconds, NearestPlayer(puppet.Agent.transform.position));
    }

    // The tentacle's own callback when it is out: a replay has nothing to settle there.
    private static Il2CppSystem.Action? _nothing;

    /// <summary>A recorded tongue or tentacle launch: the puppet's own tentacle (<paramref name="index"/> in the order under the enemy) reaches
    /// out to <paramref name="at"/>, hangs and comes back on the game's own path, timing and look (<c>MovingEnemyTentacleBase.Attack</c> with
    /// a place). Given a place and no player, the game's tentacle settles no damage. False when the enemy has no puppet or no such tentacle.</summary>
    internal static bool EnemyTongue(int entity, int index, Vector3 at)
    {
        if (Live(entity) is not { } puppet) return false;
        try
        {
            var all = puppet.Agent.GetComponentsInChildren<MovingEnemyTentacleBase>(true);
            if (all == null || all.Length == 0) return false;
            all[Math.Clamp(index, 0, all.Length - 1)].Attack(at, _nothing ??= (System.Action)(static () => { }));
            return true;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A puppet enemy could not reach out with its tongue: " + error.Message); return false; }
    }

    internal static void EnemyHit(int entity, ES_HitreactType type, ImpactDirection direction, int animation) => Live(entity)?.Hitreact(type, direction, animation);

    internal static void EnemyScream(int entity, int animation) => Live(entity)?.Scream(animation, 3f);

    /// <summary>A recorded sleeper's heartbeat (EnemyBeat): the puppet plays it (ForgeCore <c>EnemyPuppet.Heartbeat</c>).</summary>
    internal static void EnemyBeat(int entity, float strength, Vector3 sensed) => Live(entity)?.Heartbeat(strength, sensed);

    /// <summary>A recorded scout's feelers going out or back: the scout's puppet shows them (ForgeCore <c>EnemyPuppet.Feelers</c>).</summary>
    internal static void EnemyFeelers(int entity, bool spread) => Live(entity)?.Feelers(spread);

    /// <summary>A recorded custom limb loss while playing: the enemy's puppet shows it destroyed with the game's destroy effect. An enemy
    /// with no puppet now gets the limb at once when its puppet is made (<see cref="SyncEnemies"/> reads <see cref="WorldState.LimbsGone"/>).</summary>
    internal static void EnemyLimb(int entity, ulong limb)
    {
        if (limb > WorldState.MaxLimbId) Plugin.Logger.ErrorOnce("An enemy lost limb " + limb + ", above the limb ids (0 to " + WorldState.MaxLimbId + ") a jump restores: it shows only while playing past it.");
        if (!Foes.TryGetValue(entity, out var foe) || foe.Parked || foe.Puppet is not { Held: true } puppet) return;
        if (!puppet.DestroyLimb((int)Math.Min(limb, int.MaxValue), false)) Skipped.Note("a puppet enemy has no limb to show destroyed", "data block " + foe.DataId + " limb " + limb);
        foe.LimbsShown |= WorldState.LimbBit(limb);
    }

    /// <summary>Where damage limb <paramref name="limb"/> of the enemy's puppet is now; false when the enemy has no puppet or no such limb.</summary>
    internal static bool LimbPoint(int entity, ulong limb, out Vector3 point)
    {
        point = default;
        if (Live(entity) is not { } puppet) return false;
        var limbs = puppet.Agent.Damage.DamageLimbs;
        if (limbs == null || limb >= (ulong)limbs.Length || limbs[(int)limb] is not { } part) return false;
        point = part.transform.position;
        return true;
    }

    // Puts the puppet's destroyed limbs where the recording has them at this moment. Limbs do not grow back: a puppet that shows a limb the
    // recording has not lost yet (a jump back) is dropped and a new agent is made for the enemy on a later frame. Returns false then.
    private static bool MatchLimbs(int entity, Foe foe, ulong lost)
    {
        if (foe.LimbsShown == lost) return true;
        if ((foe.LimbsShown & ~lost) != 0) { Park(foe, true); Foes.Remove(entity); return false; }
        var missing = lost & ~foe.LimbsShown;
        for (var limb = 0; limb <= WorldState.MaxLimbId; limb++)
        {
            var bit = 1UL << limb;
            if ((missing & bit) == 0) continue;
            if (!foe.Puppet!.DestroyLimb(limb, true)) Skipped.Note("a puppet enemy has no limb to show destroyed", "data block " + foe.DataId + " limb " + limb);
            foe.LimbsShown |= bit;
        }
        return true;
    }

    /// <summary>The player puppet of a recorded player slot, if one exists.</summary>
    /// <summary>The root of a recorded enemy's live puppet, or null (parked, not made yet or ended): where a model or an effect of the recording is hung.</summary>
    internal static Transform? EnemyRoot(int entity) => Live(entity)?.Agent.transform;

    /// <summary>The agent of an enemy's puppet, or null when it has none.</summary>
    internal static EnemyAgent? EnemyAgent(int entity) => Live(entity)?.Agent;

    /// <summary>The wield state the body of <paramref name="slot"/> was last asked to show (an <see cref="InventorySlot"/> value, -1 before any).</summary>
    internal static int WieldedOf(int slot) => Wielded[slot & 3];

    internal static CosmeticPlayer? CosmeticOf(int slot) => Cosmetics.TryGetValue(slot & 3, out var cosmetic) && cosmetic.Held ? cosmetic : null;

    /// <summary>The gear line of the weapon held in <paramref name="wield"/>: the loadout's lines are standard, special, melee, tool.</summary>
    private static string GearLineOf(string loadout, int wield)
    {
        var line = LineIndex(wield);
        var lines = loadout.Split('\n');
        return line >= 0 && line < lines.Length ? lines[line].Trim() : "";
    }

    private static int LineIndex(int wield) => wield switch { (int)InventorySlot.GearStandard => 0, (int)InventorySlot.GearSpecial => 1, (int)InventorySlot.GearMelee => 2, (int)InventorySlot.GearClass => 3, _ => -1 };
    internal static PlayerPuppet? PlayerOf(int slot) => Players.TryGetValue(slot & 3, out var puppet) && puppet.Held ? puppet : null;

    /// <summary>The animator of a recorded player's puppet or cosmetic copy; null when the player has none.</summary>
    internal static Animator? AnimatorOf(int slot) => PlayerOf(slot) is { } puppet ? puppet.Agent.AnimatorBody : Cosmetics.TryGetValue(slot & 3, out var cosmetic) && cosmetic.Held ? cosmetic.Animator : null;

    /// <summary>The body of a recorded player's puppet or cosmetic copy, for hiding and outlining; null when the player has none.</summary>
    internal static GameObject? BodyOf(int slot)
    {
        if (PlayerOf(slot) is { } puppet) return puppet.Agent.gameObject;
        return Cosmetics.TryGetValue(slot & 3, out var cosmetic) && cosmetic.Held ? cosmetic.Body : null;
    }

    /// <summary>The character rig belongs to a body other than the observer's: a player agent of another slot (a puppet's bot) or a cosmetic copy.
    /// The bodies' rigs need not hang under their agents, and the observer's own is hidden by its place.</summary>
    internal static bool OthersRig(Transform rig)
    {
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            if (agent == null || agent.WasCollected || agent.IsLocallyOwned && agent.Owner != null && !agent.Owner.IsBot) continue;
            if (Related(rig, agent.AnimatorBody != null ? agent.AnimatorBody.transform : null) || Related(rig, agent.transform)) return true;
        }
        foreach (var cosmetic in Cosmetics.Values)
            if (cosmetic.Held && Related(rig, cosmetic.Body != null ? cosmetic.Body.transform : null)) return true;
        return false;
    }

    private static bool Related(Transform rig, Transform? body) => body != null && (body.IsChildOf(rig) || rig.IsChildOf(body));

    /// <summary>A jump in time: every enemy's action ends and every puppet's sounds stop (an attack's loop, a scream, a weapon's tail: each
    /// agent's own sound player), so nothing of the moment left goes on in the one landed in.</summary>
    internal static void Jumped()
    {
        try
        {
            Corpses.All(HideCorpse);
            _landed = true;
            foreach (var foe in Foes.Values)
            {
                foe.WakeSeenMs = NotWaking;
                if (foe.Parked || foe.Puppet is not { Held: true } puppet) continue;
                puppet.Settle();
                puppet.Feelers(false, true);
                puppet.Agent.Sound?.Stop();
            }
            foreach (var puppet in Players.Values) if (puppet.Held) puppet.Agent.Sound?.Stop();
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The puppets' actions and sounds could not be stopped after a jump: " + error.Message); }
    }

    /// <summary>Releases everything held; the level cleanup that follows destroys the agents.</summary>
    internal static void Clear()
    {
        foreach (var foe in Foes.Values) foe.Puppet?.Dispose();
        Corpses.All(HideCorpse);
        foreach (var renderer in SpareBodies) if (renderer != null && !renderer.WasCollected) renderer.enabled = true;
        SpareBodies.Clear(); _spareAt = 0f;
        foreach (var puppet in Players.Values) puppet.Dispose();
        HideViewed(-1);
        foreach (var cosmetic in Cosmetics.Values) cosmetic.Dispose();
        Cosmetics.Clear();
        for (var slot = 0; slot < 4; slot++) { AwayBody[slot].Clear(); Away[slot] = false; WornLines[slot] = new string?[GearLines]; CannotWear[slot] = new string?[GearLines]; DressedFor[slot] = null; Redressed[slot] = false; EquipUntil[slot] = 0f; HandFarSince[slot] = 0f; HandSaid[slot] = false; ReloadSaid[slot] = false; GearNoted[slot] = null; }
        _presenceSeen = -1;
        Foes.Clear(); MissingTypes.Clear(); SpawnRetryAt.Clear(); SpawnFailures.Clear(); Refused.Clear(); Players.Clear(); Array.Fill(Wielded, -1); Array.Clear(DressStep, 0, 4); WieldVersion = 0; _botsWaitFrom = -1f; _botsReadyAt = -1f; _gearNotes = 0; RestoreNotes = 0;
    }

    /// <summary>Adds the pointer of every enemy agent the puppeteer holds or parks to <paramref name="into"/>.</summary>
    internal static void CollectEnemies(HashSet<IntPtr> into)
    {
        foreach (var foe in Foes.Values) if (foe.Agent != null && !foe.Agent.WasCollected) into.Add(foe.Agent.Pointer);
    }

    // The viewed player's own body while the camera is at its eyes: the camera would sit inside the head, so its renderers are off
    // (and the weapon in its hands with them). Re-read now and then because the held weapon changes. Hidden with Renderer.forceRenderingOff,
    // which nothing else writes: the game's culling and the other hiders below switch Renderer.enabled, and a hide and a restore of the same
    // flag by two of them left a body off after the eyes view (or a muzzle flash sprite on for good). Only what this hid is given back.
    private static readonly List<Renderer> HiddenBody = new();
    private static int _hiddenSlot = -1, _hideTurn;
    private static float _hideAt;
    // Finding the renderers of a whole body is what costs: one of the three at a time, so a magazine shown for a reload is gone within a few frames and no frame pays for all of them.
    private const float HideEvery = 0.05f;

    /// <summary>Hides the body of the player puppet of <paramref name="slot"/> (-1: show every body again). <paramref name="refresh"/>
    /// looks for renderers that appeared since (a new weapon in the hands, a magazine shown for a reload).</summary>
    internal static void HideViewed(int slot, bool refresh = false)
    {
        var changed = slot != _hiddenSlot;
        if (changed)
        {
            var restored = HiddenBody.Count; var was = _hiddenSlot;
            foreach (var renderer in HiddenBody) if (renderer != null && !renderer.WasCollected) renderer.forceRenderingOff = false;
            HiddenBody.Clear();
            _hiddenSlot = slot;
            if (restored > 0 && RestoreNotes < 4) NoteRestore(was, restored);
        }
        if (slot < 0 || !refresh && !changed) return;
        var now = Time.unscaledTime;
        if (!changed && now < _hideAt) return;
        _hideAt = now + HideEvery;
        var turn = changed ? -1 : _hideTurn++ % 3;
        if (BodyOf(slot) is not { } body) return;
        if (turn is -1 or 0) Hide(body);
        // The body model and the weapon in its hands do not hang under the agent's own object on every player.
        if (PlayerOf(slot) is not { } puppet) return;
        if (turn is -1 or 1 && puppet.Agent.AnimatorBody != null) Hide(puppet.Agent.AnimatorBody.gameObject);
        if (turn is -1 or 2 && puppet.Agent.Inventory?.WieldedItem is { } held) Hide(held.gameObject);
    }

    private static void Hide(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            if (renderer != null && !renderer.forceRenderingOff) { renderer.forceRenderingOff = true; HiddenBody.Add(renderer); }
    }

    // Diagnostic for the owner's hand test (the body gone after the eyes view): the first few times a hidden body is given back, how many renderers
    // were given back and what the body then draws. Remove with the cause found.
    private static int RestoreNotes;

    private static void NoteRestore(int slot, int restored)
    {
        RestoreNotes++;
        var body = slot >= 0 ? BodyOf(slot) : null;
        var skins = body != null ? body.GetComponentsInChildren<SkinnedMeshRenderer>(true) : null;
        var on = 0; var off = 0;
        if (skins != null) foreach (var skin in skins) if (skin.enabled && !skin.forceRenderingOff && skin.gameObject.activeInHierarchy) on++; else off++;
        Plugin.Diagnostic.LogDebug("Eyes view of player " + (slot + 1) + " ended: " + restored + " renderers shown again; its body now has " + on + " skinned meshes drawn and " + off + " off, body active "
            + (body != null && body.activeInHierarchy) + ", away " + (slot >= 0 && Away[slot]) + ", puppet " + (PlayerOf(slot) != null) + ", copy " + (CosmeticOf(slot) != null) + ".");
    }

    /// <summary>One frame, after the game's animation: <paramref name="rate"/> is the playback speed (0 while paused).</summary>
    internal static void Sync(WorldState state, uint nowMs, int follow, Vector3? view, float rate, float seconds, IReadOnlyList<PlayerEntry> recorded)
    {
        ReadHolders(nowMs, recorded);
        Corpses.Due(Time.unscaledTime, HideCorpse);
        SyncEnemies(state, nowMs, follow, view, rate, seconds);
        SyncPlayers(state, nowMs, follow, rate, recorded);
        Deployables.Frame(nowMs);
        Deployables.World(state);
    }

    internal static Vector3 Velocity(WorldState state, int entity, uint nowMs)
    {
        ref var p = ref state.Poses[entity];
        if (!p.Present || !p.HasNext) return Vec.Zero;
        var (vx, vz) = PuppetMotion.Velocity(p.X, p.Z, p.NextX, p.NextZ, (p.NextMs - p.TimeMs) / 1000f);
        return new Vector3(vx, 0f, vz);
    }

    private static void SyncEnemies(WorldState state, uint nowMs, int follow, Vector3? view, float rate, float seconds)
    {
        // Puppets exist around the followed player, or around the camera when it flies free of every player, and only in the dimension
        // that player is in: an enemy recorded in another dimension (an arena) is at the same coordinates but cannot be seen from here.
        var haveFocus = state.Place(follow, nowMs, out var fx, out var fy, out var fz, out _, out _, out _, out _) || view != null;
        var focus = view ?? new Vector3(fx, fy, fz);
        var viewDimension = ReplayExtras.Place(state, follow, out var playerPlace) ? playerPlace.Dimension : 0;
        var spawned = 0;
        Remove.Clear();
        foreach (var (entity, foe) in Foes)
        {
            if (!state.Enemies.TryGetValue(entity, out var info) || info.DataId != foe.DataId || foe.Agent == null || foe.Agent.WasCollected)
            {
                // Gone from the world without an end event passing (a jump took the world away): parked unseen for good.
                Park(foe, true);
                Remove.Add(entity);
                continue;
            }
            if ((int)info.Dimension != viewDimension)
            {
                Park(foe, true);
                Remove.Add(entity);
                continue;
            }
            if (foe.Puppet == null && !foe.Parked) foe.Puppet = Puppets.Enemy(foe.Agent, Plugin.Id); // a spawn that was not alive yet is held on a later frame
            var near = !haveFocus || !state.Place(entity, nowMs, out var ex, out var ey, out var ez, out _, out _, out _, out _) || Vector3.Distance(focus, new Vector3(ex, ey, ez)) <= DropRadius;
            if (!near && !foe.Parked) Park(foe, false);
        }
        foreach (var entity in Remove) { Foes.Remove(entity); }
        foreach (var (entity, info) in state.Enemies)
        {
            if (!state.Shown(entity, nowMs, out var x, out var y, out var z, out var qx, out var qy, out var qz, out var qw)) continue;
            if ((int)info.Dimension != viewDimension) continue;
            var position = new Vector3(x, y, z);
            var rotation = new Quaternion(qx, qy, qz, qw);
            if (!Foes.TryGetValue(entity, out var foe))
            {
                if (haveFocus && Vector3.Distance(focus, position) > SpawnRadius || spawned >= SpawnsPerFrame) continue;
                if (MissingTypes.Contains(info.DataId)) continue;
                if (GameData.EnemyDataBlock.GetBlock((uint)info.DataId) == null)
                {
                    MissingTypes.Add(info.DataId);
                    Skipped.Note("an enemy type this game does not have", "enemy data block " + info.DataId);
                    continue;
                }
                // An enemy that cannot be made now (no node under it, the game refused) waits before it is tried again, and never eats the frame's budget.
                if (SpawnRetryAt.TryGetValue(entity, out var retryAt) && ForgeCore.Frame.UnscaledTime < retryAt) continue;
                if (!ForgeCore.Level.HasDimension((int)info.Dimension) || !ForgeCore.Level.CourseNodeIn((int)info.Dimension, position, 2f, out var node) || node == null)
                { SpawnFailed(entity, "no place to stand in its dimension", info.DataId); continue; }
                var agent = EnemyAllocator.Current.SpawnEnemy((uint)info.DataId, node, AgentMode.Hibernate, position, rotation, null, -1);
                if (agent == null) { SpawnFailed(entity, "the game made nothing", info.DataId); continue; }
                spawned++;
                SpawnRetryAt.Remove(entity); SpawnFailures.Remove(entity);
                foe = new Foe { Agent = agent, DataId = info.DataId };
                Foes[entity] = foe;
                foe.Puppet = Puppets.Enemy(agent, Plugin.Id);
            }
            else if (foe.Parked)
            {
                if (haveFocus && Vector3.Distance(focus, position) > SpawnRadius) continue;
                foe.Agent.gameObject.SetActive(true);
                foe.Parked = false;
                foe.Puppet ??= Puppets.Enemy(foe.Agent, Plugin.Id);
            }
            if (foe.Puppet == null || !foe.Puppet.Held) continue;
            if (!MatchLimbs(entity, foe, state.LimbsGone[entity])) continue;
            foe.Puppet.Rate = rate;
            // 0: no state recorded yet, the enemy walks.
            var pose = (ES_StateEnum)state.EnemyLocomotion[entity];
            if (pose != 0)
            {
                var variant = entity; var turn = false;
                if (pose == ES_StateEnum.HibernateWakeUp && ReplayExtras.WakeUp(state, entity, out var wake))
                {
                    // The game keeps the sleeping pose for the wake-up's delay, then plays the animation it picked (ES_HibernateWakeUp.DoWakeup).
                    if (foe.WakeSeenMs == NotWaking) foe.WakeSeenMs = nowMs;
                    if (nowMs < foe.WakeSeenMs + wake.DelayMs) pose = ES_StateEnum.Hibernate;
                    variant = wake.Index; turn = wake.Turn;
                }
                else foe.WakeSeenMs = NotWaking;
                foe.Puppet.Stance(pose, variant, turn);
            }
            // A sleeper sensing someone (the light sleep): the game's own detection start and end on the puppet.
            if (ReplayExtras.Mind(state, entity, out var mind)) foe.Puppet.Detecting(mind.Detecting);
            var velocity = Velocity(state, entity, nowMs);
            foe.Smoothed = Vector3.Lerp(foe.Smoothed, velocity, 1f - Mathf.Exp(-seconds / 0.12f)); velocity = foe.Smoothed;
            foe.Puppet.Drive(position, rotation, velocity);
        }
    }

    private const float SpawnRetrySeconds = 2f;
    private static readonly Dictionary<int, float> SpawnRetryAt = new();
    // Enemies whose spawn was refused and which are retried: reported only when their recorded life ends without their ever being shown,
    // so a refusal the next try makes good (a spawn the game was not ready for, or several in one frame) is not called a skip.
    private static readonly Dictionary<int, (string Why, ulong Data)> SpawnFailures = new();

    private static void SpawnFailed(int entity, string why, ulong dataId)
    {
        SpawnFailures[entity] = (why, dataId);
        SpawnRetryAt[entity] = ForgeCore.Frame.UnscaledTime + SpawnRetrySeconds;
    }

    private static void NoteUnshown(int entity, string why, ulong dataId)
        => Skipped.Note("a recorded enemy could not be shown: " + why, "enemy data block " + dataId + " (entity " + entity + ")");

    /// <summary>The replay ended: an enemy still in the recording that was never shown is reported now, and the retries stop.</summary>
    internal static void ReportUnshown()
    {
        foreach (var (entity, failed) in SpawnFailures) NoteUnshown(entity, failed.Why, failed.Data);
        SpawnFailures.Clear(); SpawnRetryAt.Clear();
    }

    // Out of sight: the agent is hidden and keeps its puppet, so it costs nothing and comes back as it was. A removal (a jump, an enemy that is
    // no longer in the recording) takes it out of the level through the game's own despawn: a hidden agent stays in the culling system's
    // nodes and lights, where its shadow could still be drawn.
    private static void Park(Foe foe, bool forever)
    {
        if (foe.Agent == null || foe.Agent.WasCollected) return;
        foe.Puppet?.Feelers(false, true);
        foe.Parked = true;
        foe.Agent.gameObject.SetActive(false);
        if (!forever) return;
        foe.Puppet?.Dispose();
        try { if (foe.Agent.Alive) foe.Agent.Sync?.Replicator?.Despawn(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A puppet enemy could not be taken out of the level: " + error.Message); }
    }

    /// <summary>How many bot slots were opened for this replay.</summary>
    internal static int BotsAsked;
    private const float BotsWaitSeconds = 20f, BotsSettleSeconds = 1f, CopySettleSeconds = 1f;
    private static float _botsWaitFrom = -1f, _botsReadyAt = -1f;

    private static void SyncPlayers(WorldState state, uint nowMs, int follow, float rate, IReadOnlyList<PlayerEntry> recorded)
    {
        var bots = PlayerManager.PlayerAgentsInLevel;
        var clock = Time.realtimeSinceStartup;
        // The bots arrive one by one after the level is entered. A player is not held until the ones asked for are here and have had a moment to
        // put their clothes on: the bot taken as it appeared never drew its body (its gear hung in the air), and the players after it became copies.
        if (_botsReadyAt < 0f)
        {
            var arrived = 0;
            foreach (var agent in bots) if (agent != null && !agent.IsLocallyOwned) arrived++;
            if (_botsWaitFrom < 0f) _botsWaitFrom = clock;
            if (arrived < BotsAsked && clock - _botsWaitFrom < BotsWaitSeconds) return;
            _botsReadyAt = clock + BotsSettleSeconds;
        }
        if (clock < _botsReadyAt) return;
        // The recorder's own player comes first: it is the one the camera follows by default. Everyone the recording ever has gets a body from the start,
        // kept out of sight until the moment the player joins and from the moment the player leaves.
        for (var pass = 0; pass < 2; pass++)
            for (var slot = 0; slot < 4; slot++)
            {
                if (!ReplayFeed.EverHeld(slot, recorded, Lines)) continue;
                var isRecorder = slot == Replayer.RecorderSlot;
                if (isRecorder != (pass == 0)) continue;
                var here = Holders[slot].Filled && state.Poses[slot].Present;
                if (Cosmetics.TryGetValue(slot, out var cosmetic))
                {
                    KeepAway(slot, !here);
                    if (!here) cosmetic.Lamp(false); // its renderers are hidden; a light is not one
                    if (here && cosmetic.Held && state.Shown(slot, nowMs, out var cx, out var cy, out var cz, out var cqx, out var cqy, out var cqz, out var cqw))
                    {
                        cosmetic.Lamp(state.PlayerKits[slot] is { Known: true, Flashlight: true });
                        NoteWield(slot, state.PlayerStatus[slot]);
                        if (Wielded[slot] > 0) cosmetic.Hold(Wielded[slot], GearLineOf(Holders[slot].Loadout, Wielded[slot]));
                        cosmetic.Drive(new Vector3(cx, cy, cz), new Quaternion(cqx, cqy, cqz, cqw) * Vec.Forward, state.PlayerForward[slot], state.PlayerRight[slot], CrouchOf(state.PlayerStatus[slot]), rate, DownedOf(state.PlayerStatus[slot]), Wielded[slot] == (int)InventorySlot.GearMelee);
                        if (slot != _hiddenSlot) cosmetic.KeepDrawn(slot);
                    }
                    continue;
                }
                if (!Players.TryGetValue(slot, out var puppet))
                {
                    var bot = FreeBot(bots, slot, recorded);
                    if (bot == null)
                    {
                        // No bot slot is left for this player (the observer holds one of the four): a body copied from a player puppet, with no slot.
                        // Copied as it looks, not as the eyes view hid it (HideViewed hides it again later this frame), and a moment after the donor
                        // was held, so what it wields this frame is in the copy's hands.
                        if (clock < _botsReadyAt + CopySettleSeconds) continue;
                        HideViewed(-1);
                        if (Players.Values.FirstOrDefault(p => p.Held) is { } donor && CosmeticPlayer.Make(donor.Agent) is { } made) { Cosmetics[slot] = made; WieldVersion++; }
                        continue;
                    }
                    puppet = Puppets.Player(bot, Plugin.Id);
                    if (puppet == null) { Refused.Add(bot.Pointer); continue; }
                    Players[slot] = puppet;
                }
                KeepAway(slot, !here);
                Dress(slot, puppet, Holders[slot].Loadout);
                VerifyWorn(slot, puppet, Holders[slot].Loadout);
                if (!here || !puppet.Held || !state.Shown(slot, nowMs, out var x, out var y, out var z, out var qx, out var qy, out var qz, out var qw)) continue;
                puppet.Rate = rate;
                var status = state.PlayerStatus[slot];
                var crouch = 0f;
                if (status != null)
                {
                    var read = PayloadReader.Over(status);
                    read.U();
                    var stance = (PlayerLocomotion.PLOC_State)read.U();
                    crouch = stance == PlayerLocomotion.PLOC_State.Crouch ? 1f : 0f;
                    // The air, the floor, a terminal, a ladder and a tank's tentacle are the game's own states; everything else is walking, driven below.
                    // A trap's or a pouncer's hold is not entered: those states read the holder, which the replay has no such thing as.
                    puppet.State(stance is PlayerLocomotion.PLOC_State.Jump or PlayerLocomotion.PLOC_State.Fall or PlayerLocomotion.PLOC_State.Land or PlayerLocomotion.PLOC_State.Downed
                        or PlayerLocomotion.PLOC_State.OnTerminal or PlayerLocomotion.PLOC_State.ClimbLadder or PlayerLocomotion.PLOC_State.GrabbedByTank ? stance : PlayerLocomotion.PLOC_State.Stand);
                    if (NoteWield(slot, status)) { puppet.Wield((InventorySlot)Wielded[slot]); EquipUntil[slot] = Time.unscaledTime + EquipSeconds; }
                }
                // A charge begins while the button is held; its release is the Melee event.
                var charging = ReplayExtras.MeleeCharging(state, slot);
                if (charging && !Charging[slot]) puppet.MeleeCharge();
                Charging[slot] = charging;
                if (state.PlayerKits[slot] is { Known: true } kit) puppet.Flashlight(kit.Flashlight);
                puppet.Drive(new Vector3(x, y, z), new Quaternion(qx, qy, qz, qw) * Vec.Forward, state.PlayerForward[slot], state.PlayerRight[slot], crouch);
                KeepDrawn(slot, puppet);
                if (rate == 0f) HandsOnItem(puppet, slot);
                HandCheck(slot, puppet, rate);
                GearNote(slot, puppet);
            }
        // A jump lands in the moment: whoever is down there is already on the floor.
        if (_landed) { _landed = false; foreach (var lying in Players.Values) lying.LieDown(); }
        HideSpareBots(bots, clock);
    }

    private static bool _landed;

    // Bots the game gave no puppet: not offered again.
    private static readonly HashSet<IntPtr> Refused = new();

    // The bot that plays the recorded slot: the one sitting in the same slot (its voice and look are that character's), else a free bot no other
    // recorded slot is waiting for. None left means the slot is drawn as a body copy.
    private static PlayerAgent? FreeBot(Il2CppSystem.Collections.Generic.List<PlayerAgent> bots, int slot, IReadOnlyList<PlayerEntry> recorded)
    {
        PlayerAgent? spare = null;
        for (var i = 0; i < bots.Count; i++)
        {
            var agent = bots[i];
            if (agent == null || agent.IsLocallyOwned || Refused.Contains(agent.Pointer) || Taken(agent)) continue;
            if (agent.PlayerSlotIndex == slot) return agent;
            if (spare == null && !(ReplayFeed.EverHeld(agent.PlayerSlotIndex, recorded, Lines) && !Players.ContainsKey(agent.PlayerSlotIndex & 3) && !Cosmetics.ContainsKey(agent.PlayerSlotIndex & 3))) spare = agent;
        }
        return spare;
    }

    /// <summary>The bodies in the scene, for ReplayControl.Inspect: what the puppeteer holds against what is active.</summary>
    internal static string Inspect()
    {
        var text = new System.Text.StringBuilder();
        var alive = 0; var types = new Dictionary<uint, int>();
        foreach (var agent in UnityEngine.Object.FindObjectsOfType<EnemyAgent>())
        {
            if (agent == null) continue;
            alive++; types[agent.EnemyDataID] = types.GetValueOrDefault(agent.EnemyDataID) + 1;
        }
        text.Append("foes ").Append(Foes.Count).Append(" parked ").Append(ParkedCount).Append(" corpses ").Append(Corpses.Count).Append(" active agents ").Append(alive).Append(" by type ").Append(string.Join(",", types.Select(p => p.Key + ":" + p.Value)));
        foreach (var (entity, foe) in Foes) if (foe.DataId is 29 or 36 && foe.Agent != null) text.Append("\nbig ").Append(entity).Append(" type ").Append(foe.DataId).Append(" parked ").Append(foe.Parked).Append(" at ").Append(foe.Agent.Position);
        foreach (var (slot, puppet) in Players)
        {
            var body = puppet.Agent.AnimatorBody;
            text.Append("\nslot ").Append(slot).Append(' ').Append(Holders[slot].Name).Append(" agent ").Append(puppet.Agent.PlayerName).Append(" game slot ").Append(puppet.Agent.PlayerSlotIndex).Append(" state ").Append(puppet.Agent.Locomotion.m_currentStateEnum).Append(" at ").Append(puppet.Agent.Position).Append(" wielded ").Append(puppet.Agent.Inventory?.WieldedItem?.name).Append(" move hash ").Append(body != null ? body.GetCurrentAnimatorStateInfo(1).shortNameHash : 0).Append(" (Die ").Append(Animator.StringToHash("Die")).Append(" Dead ").Append(Animator.StringToHash("Dead")).Append(") reentered ").Append(puppet.Reentered).Append(" skins ").Append(SkinCount(body));
        }
        foreach (var (slot, cosmetic) in Cosmetics) text.Append("\nslot ").Append(slot).Append(' ').Append(Holders[slot].Name).Append(" copy held ").Append(cosmetic.Held).Append(" skins ").Append(SkinCount(cosmetic.Body));
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
            if (agent != null && !Taken(agent)) text.Append("\nuntaken ").Append(agent.PlayerName).Append(" game slot ").Append(agent.PlayerSlotIndex).Append(" bot ").Append(agent.Owner != null && agent.Owner.IsBot).Append(" locally owned ").Append(agent.IsLocallyOwned).Append(" at ").Append(agent.Position).Append(" skins ").Append(SkinCount(agent.AnimatorBody));
        return text.ToString();
    }

    // Only recorded players are shown: a bot no recorded player took stands where it landed, so its body is off for the whole replay.
    private static readonly List<Renderer> SpareBodies = new();
    private static float _spareAt;

    private static void HideSpareBots(Il2CppSystem.Collections.Generic.List<PlayerAgent> bots, float clock)
    {
        if (clock < _botsReadyAt + CopySettleSeconds + 1f || clock < _spareAt) return;
        _spareAt = clock + 1f;
        foreach (var agent in bots)
        {
            if (agent == null || agent.WasCollected || Taken(agent) || agent.Owner != null && agent.Owner.IsLocal && !agent.Owner.IsBot) continue;
            foreach (var renderer in agent.GetComponentsInChildren<Renderer>(false)) if (renderer != null && renderer.enabled) { renderer.enabled = false; SpareBodies.Add(renderer); }
            if (agent.AnimatorBody != null)
                foreach (var renderer in agent.AnimatorBody.GetComponentsInChildren<Renderer>(false)) if (renderer != null && renderer.enabled) { renderer.enabled = false; SpareBodies.Add(renderer); }
        }
    }

    private static string SkinCount(Component? body) => SkinCount(body != null ? body.gameObject : null);

    private static string SkinCount(GameObject? body)
    {
        if (body == null) return "none";
        var on = 0;
        var skins = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var skin in skins) if (skin.enabled && skin.gameObject.activeInHierarchy) on++;
        return on + "/" + skins.Length;
    }

    // The game blends a third-person body's arm IK (the left hand onto the item's grip, the aim) with the frame's delta time, and a paused replay holds the world
    // at time scale 0: a gun wielded or a jump landed in while paused would keep the hands where the weights last were (0 at a fresh wield), the gun off the hand,
    // until play resumed. While paused the blend is taken to its end at once.
    // The wield itself runs on the same clock (PlayerInventorySynced puts the item in the hands after its unequip and equip times): a wield asked for
    // in the last EquipSeconds is ended at once, or the gun hung on the body's back or hip until play went on (the 11-09 probe: 0.79 m from the hand at time scale 0).
    private static void HandsOnItem(PlayerPuppet puppet, int slot)
    {
        var ik = puppet.Agent.SyncIK;
        if (ik != null && !ik.WasCollected) { ik.m_aimWeight = ik.m_aimWeightTarget; ik.m_lookWeight = ik.m_lookWeightTarget; ik.m_leftArmWeight = ik.m_leftArmWeightTarget; }
        if (Time.unscaledTime < EquipUntil[slot]) puppet.FinishEquip();
    }

    private const float EquipSeconds = 3f;
    private static readonly float[] EquipUntil = new float[4];

    // Diagnostics for the owner's hand test (a held gun off the hand, no reload shown, the wrong gun in the hands); remove with the cause found.
    // A held item that stays away from the right hand for 3 s while the replay plays, said once per player with where it hangs and the state of the arm IK.
    private static readonly float[] HandFarSince = new float[4];
    private static readonly bool[] HandSaid = new bool[4], ReloadSaid = new bool[4];

    private static void HandCheck(int slot, PlayerPuppet puppet, float rate)
    {
        if (HandSaid[slot] || rate <= 0f || Time.timeScale == 0f || puppet.IsEquipping || HandAway(puppet) is not { } away || away < 0.45f) { HandFarSince[slot] = 0f; return; }
        var now = Time.unscaledTime;
        if (HandFarSince[slot] == 0f) { HandFarSince[slot] = now; return; }
        if (now - HandFarSince[slot] < 3f) return;
        HandSaid[slot] = true;
        var agent = puppet.Agent; var item = agent.Inventory!.WieldedItem; var ik = agent.SyncIK;
        var chain = new System.Text.StringBuilder();
        for (var node = item.transform; node != null && chain.Length < 240; node = node.parent) chain.Append(node.name).Append('<');
        Plugin.Logger.Error("Held item off the hand, player " + (slot + 1) + ": " + item.name + " is " + away.ToString("0.00") + " m from the right hand, wielded slot " + agent.Inventory.WieldedSlot + ", asked " + Wielded[slot]
            + ", left arm weight " + (ik != null ? ik.m_leftArmWeight + " of " + ik.m_leftArmWeightTarget : "no IK") + ", chain " + chain);
    }

    // How far the item in the body's hands is from its right hand; null when it has no item or hand.
    private static float? HandAway(PlayerPuppet puppet)
    {
        var agent = puppet.Agent; var body = agent.AnimatorBody; var item = agent.Inventory?.WieldedItem;
        if (body == null || item == null || item.WasCollected || !body.isHuman || body.GetBoneTransform(HumanBodyBones.RightHand) is not { } hand) return null;
        return Vector3.Distance(item.transform.position, hand.position);
    }

    /// <summary>A recorded reload is about to be played on the body of <paramref name="slot"/>: the first of each player says what the body's hands hold and whether the
    /// game's equip is still running, so a reload that shows nothing has its state in the log.</summary>
    internal static void NoteReload(int slot)
    {
        if (ReloadSaid[slot & 3] || PlayerOf(slot) is not { } puppet) return;
        ReloadSaid[slot & 3] = true;
        var inventory = puppet.Agent.Inventory;
        Plugin.Diagnostic.LogDebug("Reload of player " + ((slot & 3) + 1) + " on its body: wielded slot " + inventory?.WieldedSlot + " (asked " + Wielded[slot & 3] + "), item " + inventory?.WieldedItem?.name + ", equipping " + puppet.IsEquipping
            + ", " + (HandAway(puppet) is { } away ? away.ToString("0.00") + " m from the right hand" : "no hand") + ", time scale " + Time.timeScale + ".");
    }

    // The gear the body holds against what the recording says, each time the recorded wield changes (at most 12 lines a replay), once the game had time to put it on.
    private static readonly string?[] GearNoted = new string?[4];
    private static int _gearNotes;

    private static void GearNote(int slot, PlayerPuppet puppet)
    {
        if (_gearNotes >= 12 || DressStep[slot] <= GearLines || Time.unscaledTime < EquipUntil[slot] || Wielded[slot] <= 0 || puppet.Agent.Inventory?.WieldedItem is not { } held || held.WasCollected) return;
        var key = Wielded[slot] + "|" + held.name;
        if (GearNoted[slot] == key) return;
        GearNoted[slot] = key; _gearNotes++;
        var index = LineIndex(Wielded[slot]);
        Plugin.Diagnostic.LogDebug("Body gear, player " + (slot + 1) + ": wielded slot " + puppet.Agent.Inventory.WieldedSlot + " (asked " + Wielded[slot] + "), item " + held.name + ", the recording's " + (index >= 0 ? ReplayFeed.GearName(Holders[slot].Loadout, index) : "?")
            + ", " + (HandAway(puppet) is { } away ? away.ToString("0.00") + " m from the right hand" : "no hand") + ".");
    }


    // A present player's body draws, whatever switched it off: only the eyes view and a player's absence may. What was found off is said once, so a next case has its cause in the log.
    private const float DrawnCheckSeconds = 2f;
    private static readonly float[] DrawnCheckAt = new float[4];

    private static void KeepDrawn(int slot, PlayerPuppet puppet)
    {
        if (slot == _hiddenSlot || Time.unscaledTime < DrawnCheckAt[slot] || puppet.Agent.AnimatorBody == null) return;
        DrawnCheckAt[slot] = Time.unscaledTime + DrawnCheckSeconds;
        var skins = puppet.Agent.AnimatorBody.GetComponentsInChildren<SkinnedMeshRenderer>(false);
        foreach (var skin in skins) if (skin.enabled) return;
        foreach (var skin in skins) skin.enabled = true;
        Plugin.Logger.ErrorOnce("Player " + (slot + 1) + " had no body drawn while present: " + skins.Length + " skinned meshes were off and turned on, body active " + puppet.Agent.AnimatorBody.gameObject.activeInHierarchy + ", " + (skins.Length > 0 ? "layer " + skins[0].gameObject.layer : "nothing to draw"));
    }

    // Who holds each slot now: the file header's player, then the Presence lines up to this moment (a join, a leave, a changed loadout). Read again when a line passes.
    private static readonly SlotHolder[] Holders = new SlotHolder[4];
    private static int _presenceSeen = -1;
    private static readonly IReadOnlyList<PresenceLine> NoLines = Array.Empty<PresenceLine>();
    private static IReadOnlyList<PresenceLine> Lines => Replayer.Report?.Presence ?? NoLines;

    private static void ReadHolders(uint nowMs, IReadOnlyList<PlayerEntry> recorded)
    {
        var seen = ReplayFeed.PresenceUpTo(Lines, nowMs);
        if (seen == _presenceSeen) return;
        _presenceSeen = seen;
        for (var slot = 0; slot < 4; slot++) Holders[slot] = ReplayFeed.Holder(slot, nowMs, recorded, Lines, "");
    }

    /// <summary>The gear lines the player of <paramref name="slot"/> carries at this moment (the first-person view, a sentry's model).</summary>
    internal static string LoadoutOf(int slot) => Holders[slot & 3].Loadout ?? "";

    /// <summary>The line of the tool (the class slot) in a loadout.</summary>
    internal const int ToolLine = 3;

    // A player not in the game at this moment: the body stays where it is with its renderers off, looked at again now and then for gear that appeared on it.
    private static readonly List<Renderer>[] AwayBody = { new(), new(), new(), new() };
    private static readonly bool[] Away = new bool[4];
    private static readonly float[] AwayCheckAt = new float[4];
    private const float AwayCheckSeconds = 1f;

    private static void KeepAway(int slot, bool away)
    {
        if (!away)
        {
            if (!Away[slot]) return;
            Away[slot] = false;
            foreach (var renderer in AwayBody[slot]) if (renderer != null && !renderer.WasCollected) renderer.enabled = true;
            AwayBody[slot].Clear();
            return;
        }
        Away[slot] = true;
        if (Time.unscaledTime < AwayCheckAt[slot] || BodyOf(slot) is not { } body) return;
        AwayCheckAt[slot] = Time.unscaledTime + AwayCheckSeconds;
        foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
            if (renderer != null && renderer.enabled) { renderer.enabled = false; AwayBody[slot].Add(renderer); }
    }

    // What the body shows in its hands for a PlayerStatus: the gear the recording says. A bot has no resource pack or consumable to bring up, and
    // empty hands are the game's arms-out pose, so with one of those the last gear stays up, as far as the body goes. True when it changed.
    private static bool NoteWield(int slot, byte[]? status)
    {
        if (status == null) return false;
        var read = PayloadReader.Over(status);
        read.U(); read.U();
        var wield = (InventorySlot)read.U();
        var gear = wield is InventorySlot.GearStandard or InventorySlot.GearSpecial or InventorySlot.GearClass or InventorySlot.GearMelee;
        // A gear slot the recording has no line for (a client recorder reads another player's gear only as it syncs) is not shown: what a bot holds there is the game's
        // default for a bot, a copy of the observer's own loadout, and not what the player held.
        if (gear && GearLineOf(Holders[slot].Loadout, (int)wield).Length == 0)
        {
            Skipped.Note("a player's gear in the hands is not in the recording; the last known gear is shown", "player " + (slot + 1) + " slot " + wield);
            gear = false;
        }
        var shown = gear ? (int)wield : Wielded[slot] > 0 ? Wielded[slot] : FirstKnownGear(slot);
        if (shown == Wielded[slot]) return false;
        Wielded[slot] = shown; WieldVersion++;
        return true;
    }

    // The first gear slot the recording has a line for, or the standard slot.
    private static int FirstKnownGear(int slot)
    {
        foreach (var gear in GearSlots) if (GearLineOf(Holders[slot].Loadout, (int)gear).Length > 0) return (int)gear;
        return (int)InventorySlot.GearStandard;
    }

    // The recorded player's four gear lines on the bot that plays them, one a frame, or again a little later while its assets load.
    private static readonly int[] DressStep = new int[4];
    private static readonly float[] DressAt = new float[4];
    private const int GearLines = 4;
    // What each body wears, line by line, and the loadout it was last asked to wear: a later Presence line (a tool changed in the level) changes only what differs.
    private static readonly string?[][] WornLines = { new string?[GearLines], new string?[GearLines], new string?[GearLines], new string?[GearLines] };
    private static readonly string?[] DressedFor = new string?[4];
    private static readonly bool[] Redressed = new bool[4];
    // The lines the game would not put on (gear this install does not have): taken as worn and never checked again, or the check would dress the body anew every few seconds.
    private static readonly string?[][] CannotWear = { new string?[GearLines], new string?[GearLines], new string?[GearLines], new string?[GearLines] };

    private static void Dress(int slot, PlayerPuppet puppet, string loadout)
    {
        if (DressedFor[slot] != loadout)
        {
            Redressed[slot] = DressedFor[slot] != null;
            DressedFor[slot] = loadout;
            DressStep[slot] = 0;
        }
        if (DressStep[slot] > GearLines || Time.unscaledTime < DressAt[slot]) return;
        var lines = loadout.Split('\n');
        while (DressStep[slot] < GearLines && (DressStep[slot] >= lines.Length || lines[DressStep[slot]].Trim().Length == 0 || lines[DressStep[slot]].Trim() == WornLines[slot][DressStep[slot]])) DressStep[slot]++;
        if (DressStep[slot] >= GearLines)
        {
            // The hands went on holding what was replaced, or what the game brought up with the last line it put on (the tool): asked again
            // once, after the first dressing too.
            if (Wielded[slot] >= 0) { puppet.Wield((InventorySlot)Wielded[slot]); EquipUntil[slot] = Time.unscaledTime + EquipSeconds; }
            Redressed[slot] = false;
            DressStep[slot] = GearLines + 1;
            return;
        }
        var line = lines[DressStep[slot]].Trim();
        try
        {
            var reading = ForgeCore.Gear.EquipBot(puppet.Agent.Owner, new global::Gear.GearIDRange(line));
            if (reading.Status == GearAssetStatus.Loading) { DressAt[slot] = Time.unscaledTime + 0.25f; return; }
            if (reading.Status == GearAssetStatus.Failed) { Skipped.Note("a player puppet could not take its gear", "line " + DressStep[slot] + ": " + reading.Detail); CannotWear[slot][DressStep[slot]] = line; }
        }
        catch (Exception error) { Skipped.Note("a player puppet could not take its gear", error.Message); CannotWear[slot][DressStep[slot]] = line; }
        WornLines[slot][DressStep[slot]] = line;
        DressStep[slot]++;
    }

    // What the body's backpack really holds against what the recording says, now and then once dressed: another mod may put its own gear on a bot
    // after the replay did (a favourite loadout given when the level starts), and a line taken as worn would then never be put on again.
    private static readonly float[] VerifyAt = new float[4];
    private const float VerifySeconds = 2f;

    /// <summary>The first-person view just put a gear line on the local player: the bots are checked at once, not at the next two-second turn, so a bot that took the observer's
    /// new gear along (the game's copy to a bot) is dressed again before it is seen holding it.</summary>
    internal static void RecheckGear() => Array.Clear(VerifyAt, 0, 4);
    private static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearMelee, InventorySlot.GearClass };
    private static readonly global::Gear.eGearComponent[] GearComponents = Enum.GetValues<global::Gear.eGearComponent>();

    private static void VerifyWorn(int slot, PlayerPuppet puppet, string loadout)
    {
        if (DressStep[slot] <= GearLines || Time.unscaledTime < VerifyAt[slot]) return;
        VerifyAt[slot] = Time.unscaledTime + VerifySeconds;
        if (puppet.Agent.Owner == null || !PlayerBackpackManager.TryGetBackpack(puppet.Agent.Owner, out var backpack) || backpack?.Slots == null) return;
        var lines = loadout.Split('\n');
        for (var line = 0; line < GearLines && line < lines.Length; line++)
        {
            var wanted = lines[line].Trim();
            var index = (int)GearSlots[line];
            if (wanted.Length == 0 || WornLines[slot][line] != wanted || CannotWear[slot][line] == wanted || index >= backpack.Slots.Length || backpack.Slots[index]?.GearIDRange is not { } worn) continue;
            global::Gear.GearIDRange want;
            try { want = new global::Gear.GearIDRange(wanted); }
            catch (Exception error) { CannotWear[slot][line] = wanted; Skipped.Note("a player puppet's gear line cannot be read", error.Message); continue; }
            var same = true;
            foreach (var component in GearComponents) if (want.GetCompID(component) != worn.GetCompID(component)) { same = false; break; }
            if (same) continue;
            Skipped.Note("a player puppet's gear was changed after the replay put it on", "slot " + slot + " line " + line);
            WornLines[slot][line] = null;
            DressStep[slot] = 0;
        }
    }
    /// <summary>Every recorded player present at this moment has its body, wearing its gear (asked for), so the replay can start.</summary>
    internal static bool AllHeld(WorldState state, IReadOnlyList<PlayerEntry> recorded)
    {
        if (_presenceSeen < 0) return false;
        for (var slot = 0; slot < 4; slot++)
        {
            if (!Holders[slot].Filled || !state.Poses[slot].Present) continue;
            if (Cosmetics.ContainsKey(slot)) continue;
            if (!Players.ContainsKey(slot) || DressStep[slot] < GearLines) return false;
        }
        return true;
    }

    // 1 while the recorded locomotion state of a PlayerStatus payload is crouching.
    private static float CrouchOf(byte[]? status)
    {
        if (status == null) return 0f;
        var read = PayloadReader.Over(status);
        read.U();
        return (PlayerLocomotion.PLOC_State)read.U() == PlayerLocomotion.PLOC_State.Crouch ? 1f : 0f;
    }

    private static bool DownedOf(byte[]? status)
    {
        if (status == null) return false;
        var read = PayloadReader.Over(status);
        read.U();
        return (PlayerLocomotion.PLOC_State)read.U() == PlayerLocomotion.PLOC_State.Downed;
    }

    private static bool Taken(PlayerAgent agent)
    {
        foreach (var puppet in Players.Values) if (puppet.Agent.Pointer == agent.Pointer) return true;
        return false;
    }
}
