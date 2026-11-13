using ForgeCore;
using System;
using System.Diagnostics;
using BoosterImplants;
using Gear;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Records every player in the level: position, look direction and health, entity number = player slot (0 to 3). The recorder's
/// own player is sampled every frame (about 60 Hz), the others at most every 33 ms (30 Hz). Read by polling
/// <c>PlayerManager.PlayerAgentsInLevel</c> each frame, so there is no hook that can be missed when a player joins or leaves.</summary>
internal static class PlayerRecorder
{
    private const uint RemoteIntervalMs = 33;
    private static readonly uint[] LastRemoteMs = new uint[4];
    private static readonly Stopwatch Clock = new();
    private static BlockPipeline? _pipeline;
    private static readonly bool[] Present = new bool[4];
    private const uint StatusIntervalMs = 100;
    private static readonly uint[] LastStatusMs = new uint[4];
    private static readonly int[] LastStatus = new int[4 * StatusSize];
    private const int StatusSize = 7;
    // The game's own network sends a player's walk values about 20 times a second.
    private const uint MotionIntervalMs = 50;
    private static readonly uint[] LastMotionMs = new uint[4];
    private static readonly (long Forward, long Right)[] LastMotion = new (long, long)[4];
    // What a player carries changes rarely, at the speed of a person; the gear only at a checkpoint, and reading it allocates.
    private const uint KitIntervalMs = 250, GearIntervalMs = 2000, ObjectiveIntervalMs = 500, PlaceIntervalMs = 500, ThrownIntervalMs = 100;
    private static readonly uint[] LastKitMs = new uint[4], LastGearMs = new uint[4], LastPlaceMs = new uint[4];
    private static uint _lastThrownMs;
    private static readonly (long Clip, long Size, long Stamina)[] LastHands = new (long, long, long)[4];
    private static readonly (long Dimension, long Zone, long Layer)[] LastPlace = new (long, long, long)[4];
    private static readonly byte[]?[] LastBoosters = new byte[]?[4];
    private static readonly byte[]?[] LastKit = new byte[]?[4];
    private static readonly string?[] LastLoadout = new string?[4];
    private static readonly long[] LastLoadoutKey = { -1, -1, -1, -1 };
    private static uint _lastObjectiveMs;
    // Which polled writer was running, for the one try/catch around a player's polls (no allocation unless one throws).
    private static string _poll = "";

    private static Analysis _counted = new(exact: true);
    private static readonly PayloadReader Reader = new();
    private static DateTime _startedUtc;
    // The completeness block of the footer: the recording frames' times and what the writer and the hooks lost.
    private static RecordingHealth _health = new();
    private static double _lastFrameMs = -1;

    // The parts of each player read every sample, taken once per agent: every property returning a game object makes a new interop wrapper.
    private sealed class PlayerParts
    {
        internal IntPtr Agent;
        internal Dam_PlayerDamageBase? Damage;
        internal PlayerLocomotion? Locomotion;
        internal PlayerInventoryBase? Inventory;
        internal SNetwork.SNet_Player? Owner;
        internal PlayerBackpack? Backpack;
        internal FirstPersonItemHolder? Holder;
    }

    private static readonly PlayerParts?[] Parts = new PlayerParts?[4];

    private static PlayerParts PartsOf(PlayerAgent agent, int slot)
    {
        var parts = Parts[slot];
        if (parts != null && parts.Agent == agent.Pointer) return parts;
        return Parts[slot] = new PlayerParts { Agent = agent.Pointer, Damage = agent.Damage, Locomotion = agent.Locomotion, Inventory = agent.Inventory, Owner = agent.Owner, Holder = agent.FPItemHolder };
    }

    internal static uint Now => (uint)Clock.ElapsedMilliseconds;

    /// <summary>Counts an event the recorder just wrote, for the summary at the end of the file. Only the kinds the summary holds are read.</summary>
    internal static void Count(ushort kind, ReadOnlySpan<byte> payload)
    {
        if (_pipeline == null) return;
        if (kind is EventKind.EnemySpawn or EventKind.Shot or EventKind.EnemyHurt or EventKind.EnemyGone or EventKind.PlayerHurt or EventKind.PlayerStatus or EventKind.Revive or EventKind.Pack
            or EventKind.PlayerKit or EventKind.Interact or EventKind.Supply or EventKind.Wake or EventKind.HostHit or EventKind.HostWake or EventKind.HostLink or EventKind.HostPack or EventKind.HostRay)
            _counted.Take(Now, kind, Reader.Load(payload));
    }
    internal static BlockPipeline? Pipeline => _pipeline;

    /// <summary>What the host has settled so far for each player, from this recording's own count: the totals a snapshot carries.</summary>
    internal static void SnapshotTotals(System.Span<HostTotals> into) => _counted.HostTotalsOf(into);

    internal static void Start(ForgeCore.ChunkWriter writer)
    {
        Clock.Restart(); _health = new RecordingHealth(); _lastFrameMs = -1; _startedUtc = DateTime.UtcNow; _counted = new Analysis(exact: Session.Header?.RecorderIsHost ?? true) { TotalsOnly = true, RecorderSlot = Session.Header?.RecorderSlot ?? -1 };
        Array.Clear(LastKitMs); Array.Clear(LastGearMs); Array.Clear(LastKit); _lastObjectiveMs = 0; Array.Clear(LastPlaceMs); _lastThrownMs = 0; ForgetExtras();
        Array.Fill(LastAim, (sbyte)-1); _poll = "";
        Array.Clear(LastRemoteMs); Array.Clear(Present); Array.Clear(LastStatusMs); Array.Fill(LastStatus, -1); Array.Clear(LastMotionMs); Array.Fill(LastMotion, (long.MinValue, long.MinValue)); Rec.Reset(); EnemyRecorder.Reset();
        // ForgeCore seeds its list of living enemies (a scan of the scene, tens of milliseconds and megabytes) on the first read: do it now,
        // while the level loads, not in the first frame of play.
        _ = ForgeCore.Enemies.Live.Length;
        Array.Clear(Parts);
        _pipeline = new BlockPipeline(writer, Plugin.Logger.Error, FullState, 0);
        // The keyframe the pipeline writes as it is made runs before _pipeline is set: the first tick writes the opening aim state instead.
        Array.Fill(LastAim, (sbyte)-1);
        ShownRecorder.Start();
        AnimatorStateProbe.Start();
        _settled = false;
        HostSync.RecordingStarted();
    }

    // False from the start, a reload or a recall (host migration, joining a level in progress) until the game has put the state back:
    // the block after it is a keyframe, so a jump past it starts from the recalled state, not from what was there before.
    private static bool _settled;
    internal static void Unsettled() => _settled = false;

    /// <summary>Ends the recording and finishes the file; what the writer lost goes to the log.</summary>
    internal static void Stop()
    {
        ShownRecorder.Stop();
        AnimatorStateProbe.Stop();
        var pipeline = _pipeline;
        _pipeline = null;
        if (pipeline == null) return;
        var end = Now;
        _health.LostSamples = pipeline.LostSamples; _health.LostEvents = pipeline.LostEvents; _health.HookFailures = Rec.HookFailures; _health.MaxQueue = pipeline.MaxQueue;
        pipeline.Finish(end, RunSummary.Of(Session.Outcome, end, new DateTimeOffset(_startedUtc).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _counted, _health).Encode());
        Clock.Stop();
        // Nothing of the finished level stays held: the wrappers of its enemies and players, the remembered states.
        EnemyRecorder.Reset(); Array.Clear(Parts); Rec.Reset();
        if (pipeline.LostSamples + pipeline.LostEvents > 0)
            Plugin.Logger.Error($"The recording lost {pipeline.LostSamples} samples and {pipeline.LostEvents} events because the writer fell behind.");
    }

    internal static void Tick()
    {
        var pipeline = _pipeline;
        if (pipeline == null || pipeline.Failed) return;
        if (ForgeCore.Checkpoint.Recalling) _settled = false;
        else if (!_settled && ForgeCore.Checkpoint.Settled) { _settled = true; pipeline.KeyframeNext(); }
        var now = Now;
        var frameAt = Clock.Elapsed.TotalMilliseconds;
        if (_lastFrameMs >= 0) _health.Frame(now, frameAt - _lastFrameMs);
        _lastFrameMs = frameAt;
        Hooks.Melee.Settle();
        var agents = PlayerManager.PlayerAgentsInLevel;
        var seen = 0;
        var probe = AnimatorStateProbe.Due();
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (agent == null) continue;
            var slot = agent.PlayerSlotIndex;
            if ((uint)slot >= 4) continue;
            seen |= 1 << slot;
            if (probe) AnimatorStateProbe.Look(agent, slot);
            Present[slot] = true;
            // One try for every polled writer of this player: a game object that throws costs this player this frame only, and the no-throw
            // path is a single guarded block rather than six (the per-call try/catch kept these writers out of the JIT's reach).
            var local = agent.IsLocallyOwned;
            try
            {
                if (now - LastStatusMs[slot] >= StatusIntervalMs) { LastStatusMs[slot] = now; _poll = "player-status"; Status(agent, slot); if (local) { _poll = "player-hands"; Hands(agent, slot); } }
                if (now - LastMotionMs[slot] >= MotionIntervalMs) { LastMotionMs[slot] = now; _poll = "player-motion"; Motion(agent, slot); }
                if (now - LastKitMs[slot] >= KitIntervalMs) { LastKitMs[slot] = now; _poll = "player-kit"; Kit(agent, slot); }
                if (now - LastGearMs[slot] >= GearIntervalMs) { LastGearMs[slot] = now; _poll = "player-gear"; Gear(agent, slot); _poll = "player-boosters"; Boosters(slot); }
                if (now - LastPlaceMs[slot] >= PlaceIntervalMs) { LastPlaceMs[slot] = now; _poll = "player-place"; Place(agent, slot); }
                if (local) { _poll = "player-aim"; Aim(agent, slot); }
                if (local || now - LastRemoteMs[slot] >= RemoteIntervalMs)
                {
                    LastRemoteMs[slot] = now;
                    _poll = "player-sample"; SampleOne(pipeline, now, slot, agent);
                }
            }
            catch (Exception e) { Rec.Fail(_poll, e); }
        }
        for (var slot = 0; slot < 4; slot++)
            if (Present[slot] && (seen & (1 << slot)) == 0) { Present[slot] = false; pipeline.Forget(slot); }
        if (now - _lastObjectiveMs >= ObjectiveIntervalMs) { _lastObjectiveMs = now; Hooks.PollObjective(); Hooks.PollMusic(); Hooks.PollTimer(); Hooks.PollSpitters(); }
        if (now - _lastThrownMs >= ThrownIntervalMs) { _lastThrownMs = now; Hooks.PollThrown(); Hooks.PollLights(); }
        EnemyRecorder.Tick(pipeline, now);
        ShownRecorder.Tick();
        pipeline.Tick(now);
    }

    // Called by the pipeline at the start of every keyframe block: every present player is written in full.
    private static void FullState(BlockPipeline pipeline, uint nowMs)
    {
        Array.Fill(LastStatus, -1);
        Array.Fill(LastMotion, (long.MinValue, long.MinValue));
        Array.Clear(LastKit);
        ForgetExtras();
        Rec.States.Write(pipeline, nowMs);
        EnemyRecorder.Full(pipeline, nowMs);
        var agents = PlayerManager.PlayerAgentsInLevel;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (agent == null || (uint)agent.PlayerSlotIndex >= 4) continue;
            var slot = agent.PlayerSlotIndex;
            try
            {
                _poll = "player-status"; Status(agent, slot);
                _poll = "player-motion"; Motion(agent, slot);
                _poll = "player-kit"; Kit(agent, slot);
                if (agent.IsLocallyOwned) { _poll = "player-hands"; Hands(agent, slot); }
                _poll = "player-place"; Place(agent, slot);
                _poll = "player-boosters"; Boosters(slot);
                if (agent.IsLocallyOwned) { _poll = "player-aim"; Aim(agent, slot); }
                _poll = "player-sample"; SampleOne(pipeline, nowMs, slot, agent);
            }
            catch (Exception e) { Rec.Fail(_poll, e); }
        }
    }

    private static void SampleOne(BlockPipeline pipeline, uint now, int slot, PlayerAgent agent)
    {
        var position = agent.Position;
        var look = agent.TargetLookDir;
        // A Vector3 / Quaternion wrapper is read by its fields only; its properties recurse in Il2CppInterop.
        var direction = new Vector3(look.x, look.y, look.z);
        var rotation = Vec.SqrLength(direction) > 1e-6f ? Quaternion.LookRotation(direction) : Vec.Identity;
        var damage = PartsOf(agent, slot).Damage;
        var max = damage == null ? 0f : damage.HealthMax;
        var health = max <= 0f ? (byte)0 : (byte)Mathf.Clamp(Mathf.RoundToInt(127f * damage!.Health / max), 0, 127);
        pipeline.Sample(now, slot, position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, health);
    }

    private static void ForgetExtras() { Array.Fill(LastHands, (-1L, -1L, -1L)); Array.Fill(LastPlace, (-1L, -1L, -1L)); Array.Clear(LastBoosters); }

    // 108 PlayerAim: U slot, U 1 aiming down sights. Written at the two transitions only, never per frame or at a fixed rate; the replay holds
    // the last one (the state is kept in every keyframe). Only the owner's machine knows the aim: FPItemHolder.ItemAimTrigger is the local input.
    private static void Aim(PlayerAgent agent, int slot)
    {
        var parts = PartsOf(agent, slot);
        if (parts.Owner is { IsBot: true }) return;
        var holder = parts.Holder;
        var aiming = holder != null && !holder.WasCollected && holder.ItemAimTrigger;
        ref var last = ref LastAim[slot];
        if (last >= 0 && (last == 1) == aiming) return;
        last = aiming ? (sbyte)1 : (sbyte)0;
        Rec.Begin().U((ulong)slot).U(aiming ? 1UL : 0UL);
        Rec.EmitState(EventKind.PlayerAim, (ulong)slot);
    }

    // The last aim written per slot, -1 before the first: a recording starts with the player not aiming.
    private static readonly sbyte[] LastAim = { -1, -1, -1, -1 };

    // 62 PlayerGun: U slot, U rounds in the magazine + 1 (0 not a magazine), U magazine size, U 0 (the field an old recording held the aim in),
    // U stamina percent + 1, in steps of 5. Only the player's own machine has these: a remote gun's magazine is not sent and
    // BulletWeaponSynced.Fire does not count it.
    private static void Hands(PlayerAgent agent, int slot)
    {
        // A bot on the host fires BulletWeaponSynced too.
        var parts = PartsOf(agent, slot);
        if (parts.Owner is { IsBot: true }) return;
        var item = parts.Inventory?.WieldedItem;
        var bullets = item?.TryCast<BulletWeapon>();
        var melee = item?.TryCast<MeleeWeaponFirstPerson>();
        Hooks.NoteCharge(agent, melee != null && melee.CurrentState?.TryCast<MWS_ChargeUp>() != null);
        var stamina = agent.Stamina;
        var now = (bullets == null ? 0L : bullets.GetCurrentClip() + 1L, bullets == null ? 0L : bullets.ClipSize,
            stamina == null ? 0L : 1L + 5L * Mathf.RoundToInt(stamina.StaminaRelative * 20f));
        if (now == LastHands[slot]) return;
        LastHands[slot] = now;
        Rec.Begin().U((ulong)slot).U((ulong)now.Item1).U((ulong)now.Item2).U(0).U((ulong)now.Item3);
        Rec.Emit(EventKind.PlayerGun);
    }

    // 88 PlayerPlace: U slot, U dimension, U zone alias + 1 (0 unknown), U layer. A warp shows here and as the jump of the position.
    private static void Place(PlayerAgent agent, int slot)
    {
        var zone = agent.CourseNode?.m_zone;
        var now = ((long)(int)agent.DimensionIndex, zone == null ? 0L : zone.Alias + 1L, zone == null ? 0L : (long)(int)zone.Layer.m_type);
        if (now == LastPlace[slot]) return;
        LastPlace[slot] = now;
        Rec.Begin().U((ulong)slot).U((ulong)now.Item1).U((ulong)now.Item2).U((ulong)now.Item3);
        Rec.Emit(EventKind.PlayerPlace);
    }

    // 86 Boosters: U slot, U how many; each U booster id, U uses, U how many effects; each U effect id, S value x 1000. Read every 2 s, written when changed.
    private static void Boosters(int slot)
    {
        var players = BoosterImplantManager.Current?.m_boosterPlayers;
        if (players == null || slot >= players.Length || players[slot]?.BoosterImplantDatas is not { } implants) return;
        var write = Rec.Begin().U((ulong)slot);
        // Each implant's id is read once: the count of worn implants goes before them.
        Span<uint> ids = stackalloc uint[implants.Length];
        var count = 0;
        for (var i = 0; i < implants.Length; i++) if ((ids[i] = implants[i].BoosterImplantID) != 0) count++;
        write.U((ulong)count);
        for (var i = 0; i < implants.Length; i++)
        {
            var id = ids[i];
            if (id == 0) continue;
            var implant = implants[i];
            var effects = implant.BoosterEffectCount;
            var datas = implant.BoosterEffectDatas;
            write.U(id).U((ulong)Math.Max(0, implant.UseCount)).U((ulong)Math.Max(0, effects));
            for (var e = 0; e < effects && e < datas.Length; e++) write.U(datas[e].BoosterEffectID).S((long)Math.Round(datas[e].EffectValue * 1000f));
        }
        var built = Rec.Built;
        if (LastBoosters[slot] is { } last && built.SequenceEqual(last)) return;
        LastBoosters[slot] = built.ToArray();
        Rec.Emit(EventKind.Boosters);
    }

    // The walk values the game gives the player's animator, whoever moves the player. Written when they changed.
    private static void Motion(PlayerAgent agent, int slot)
    {
        var locomotion = PartsOf(agent, slot).Locomotion!;
        var motion = ((long)MathF.Round(locomotion.m_animVelFwd / WorldState.MotionStep), (long)MathF.Round(locomotion.m_animVelRight / WorldState.MotionStep));
        if (motion == LastMotion[slot]) return;
        LastMotion[slot] = motion;
        Rec.Begin().U((ulong)slot).S(motion.Item1).S(motion.Item2);
        Rec.Emit(EventKind.PlayerMotion);
    }

    // 55 PlayerKit: what the player carries, written when it changed (layout in DEVELOPERS.md). Every machine holds every player's backpack:
    // the game syncs what is in it and the pack and consumable ammunition.
    private static void Kit(PlayerAgent agent, int slot)
    {
        var parts = PartsOf(agent, slot);
        var owner = parts.Owner;
        if (owner == null) return;
        if (parts.Backpack == null && PlayerBackpackManager.TryGetBackpack(owner, out var found)) parts.Backpack = found;
        var backpack = parts.Backpack;
        if (backpack == null) return;
        var write = Rec.Begin().U((ulong)slot)
            .U(ItemIn(backpack, InventorySlot.ResourcePack)).U((ulong)Math.Max(0, PlayerBackpackManager.GetBulletsInPack(AmmoType.ResourcePackRel, owner)))
            .U(ItemIn(backpack, InventorySlot.Consumable)).U((ulong)Math.Max(0, PlayerBackpackManager.GetBulletsInPack(AmmoType.CurrentConsumable, owner)))
            .U(ItemIn(backpack, InventorySlot.InLevelCarry)).U(parts.Inventory!.FlashlightEnabled ? 1UL : 0UL);
        // The pockets are walked once: their count goes before their items.
        var groups = backpack.PocketItemsGroups;
        Pockets.Clear();
        for (int i = 0, groupCount = groups.Count; i < groupCount; i++)
        {
            var group = groups[i];
            for (int j = 0, count = group.Count; j < count; j++) Pockets.Add(group[j].itemID);
        }
        write.U((ulong)Pockets.Count);
        foreach (var pocket in Pockets) write.U(pocket);
        var built = Rec.Built;
        if (LastKit[slot] is { } last && built.SequenceEqual(last)) return;
        LastKit[slot] = built.ToArray();
        Rec.Emit(EventKind.PlayerKit);
    }

    private static readonly System.Collections.Generic.List<uint> Pockets = new();

    private static ulong ItemIn(PlayerBackpack backpack, InventorySlot slot) => backpack.TryGetBackpackItem(slot, out var item) && item != null ? item.ItemID : 0UL;

    internal static void NoteLoadout(int slot, string loadout) { LastLoadout[slot] = loadout; LastLoadoutKey[slot] = -1; }

    internal static void ForgetLoadouts() { Array.Clear(LastLoadout); Array.Fill(LastLoadoutKey, -1L); }

    private static void Gear(PlayerAgent agent, int slot)
    {
        var owner = PartsOf(agent, slot).Owner;
        if (owner == null) return;
        // The game's checksum of each gear is read first: the JSON (several KB of garbage per player) is built only when it moved.
        var key = Session.LoadoutKey(owner);
        if (key >= 0 && key == LastLoadoutKey[slot]) return;
        LastLoadoutKey[slot] = key;
        var loadout = Session.Loadout(owner);
        if (string.IsNullOrWhiteSpace(loadout)) return;
        var said = LastLoadout[slot];
        if (said == null && Session.Header is { } header)
            foreach (var p in header.Players) if (p.Slot == slot) said = p.Loadout;
        if (loadout == said) { LastLoadout[slot] = loadout; return; }
        Hooks.GearChanged(owner, slot, loadout);
    }

    // The player's state that is not a position: locomotion state (standing, crouching, downed ...), wielded slot, infection in percent and
    // the three ammo pools. Written as one event whenever any of them changed.
    private static void Status(PlayerAgent agent, int slot)
    {
        var parts = PartsOf(agent, slot);
        var owner = parts.Owner;
        var infection = parts.Damage == null ? 0 : Mathf.RoundToInt(100f * parts.Damage.Infection);
        var values = LastStatus.AsSpan(slot * StatusSize, StatusSize);
        Span<int> now = stackalloc int[StatusSize];
        var stance = parts.Locomotion!.m_currentStateEnum;
        now[0] = (int)stance; now[1] = (int)parts.Inventory!.WieldedSlot; now[2] = infection;
        now[3] = owner == null ? 0 : PlayerBackpackManager.GetBulletsInPack(AmmoType.Standard, owner);
        now[4] = owner == null ? 0 : PlayerBackpackManager.GetBulletsInPack(AmmoType.Special, owner);
        now[5] = owner == null ? 0 : PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, owner);
        now[6] = stance == PlayerLocomotion.PLOC_State.Downed ? 1 : 0;
        if (now.SequenceEqual(values)) return;
        now.CopyTo(values);
        var writer = Rec.Begin().U((ulong)slot);
        for (var i = 0; i < StatusSize; i++) writer.U((ulong)Math.Max(0, now[i]));
        Rec.Emit(EventKind.PlayerStatus);
    }
}
