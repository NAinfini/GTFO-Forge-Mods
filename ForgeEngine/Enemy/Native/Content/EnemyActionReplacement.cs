using System;
using System.Collections.Generic;
using BepInEx;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEnemy.Profile;
using ForgeVisual;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// An author's action in place of a native one. The game's animator keeps running, so attack timing, hit reactions, root motion and
/// collision do not change; when it cross-fades to a state of a replaced group, the body's bones show the replacement instead, on every
/// peer, because every peer makes the same cross-fade. A state of no replaced group stops the replacement and the game's pose shows.
/// A death plays until the game's own death window ends and is cut there: the window is not lengthened.
/// </summary>
internal sealed class EnemyActionReplacement
{
    private const float Fade = 0.15f;
    /// <summary>Metres per second below which a moving enemy counts as standing, and from which it counts as running.</summary>
    private const float StandBelow = 0.2f, RunFrom = 2.5f, SpeedSample = 0.25f;

    private sealed class Body
    {
        internal readonly EnemyAgent Enemy;
        internal readonly JointPlayer Player;
        internal readonly JointClip?[] Clips = new JointClip?[EnemyActionSlots.Count];
        internal JointTrack Track;
        internal int Playing = -1;
        internal bool OnGround;
        /// <summary>Game clock (<c>Clock.Time</c>), like <see cref="SampledAt"/>.</summary>
        internal float DeathEnds = float.PositiveInfinity;
        internal Vector3 Last;
        internal float SampledAt;
        /// <summary>When to read back whether the track just started is really on the body, on Unity's clock
        /// (<c>Time.time</c>), the clock the joint track itself runs on.</summary>
        internal float UnityCheckAt = float.PositiveInfinity;
        internal Body(EnemyAgent enemy, JointPlayer player) { Enemy = enemy; Player = player; Last = enemy.Position; }
    }

    /// <summary>A replacement started (<c>cut</c> false: the clip's length, and the seconds of death window left when it is a death) or was cut at the end of the death window.</summary>
    internal Action<uint, EnemyActionSlot, float, float, bool>? Shown;

    /// <summary>What the replacement saw, for reading a run back: the enemy type, the event (<c>hook-live</c> the first cross-fade of any animator,
    /// <c>bound</c> a life got its clips, <c>crossfade</c> the game changed this body's state, <c>death</c> a death reaction, <c>pose</c> a track 0.3 s after it
    /// started), the state hash, the row it counts as, the enemy's state, and how long the track has played (0: it is not playing).</summary>
    internal Action<uint, string, int, string, string, float>? Traced;

    /// <summary>Whether <see cref="Traced"/> would write now (its log gate); the row and state names are made only then.</summary>
    internal Func<bool>? Tracing;

    private bool TraceOn => Traced != null && Tracing?.Invoke() != false;

    private readonly Dictionary<IntPtr, Body> _bodies = new();
    private readonly List<IntPtr> _gone = new();
    private readonly Dictionary<int, EnemyActionSlot> _slotByHash = new();
    /// <summary>Parsed clips by package and revision: the package a profile came from decides which files it can see (I-RESOLVE), so the
    /// answer, found or refused, is kept per package; one revision is read and parsed once for each package that names it.</summary>
    private readonly Dictionary<string, JointClip> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _refused = new(StringComparer.Ordinal);
    // A game clip by body (avatar), source enemy and clip, found or refused: asked once per level, not on every spawn (an ask reads the source
    // prefab's animator and can scan every loaded clip). A source enemy the level has no prefab for is asked again.
    private readonly Dictionary<(IntPtr Avatar, uint Enemy, string Clip), (JointClip? Clip, string? Reason)> _gameClips = new();
    private bool _hooked;
    private int _moveOnGround;
    private bool _hookSeen;

    /// <summary>Puts one life's replaced actions on its body. An animation resource is found by its revision in the package of the
    /// profile <paramref name="source"/> came from, then in that package's dependencies (I-RESOLVE). Returns the first refusal
    /// (action and reason), or null; an action that cannot play stays the game's.</summary>
    internal (EnemyActionSlot Slot, string Action, string Reason)? Apply(EnemyAgent enemy, EnemyActionEntry[] actions, string source)
    {
        var player = Models.BindJoints(enemy, out var refusal);
        if (player == null) return (actions[0].Slot, actions[0].Resource ?? actions[0].GameClip!, refusal!);
        Hook();
        // A body this animator already carried (the same skeleton bound again) stops first: two players must not write one skeleton.
        if (_bodies.TryGetValue(enemy.Anim.Pointer, out var previous)) previous.Player.Stop();
        var body = new Body(enemy, player);
        (EnemyActionSlot, string, string)? first = null;
        foreach (var entry in actions)
        {
            var clip = entry.Resource != null ? Resource(entry.Resource, source, out var reason) : GameClip(enemy, player, entry, out reason);
            if (clip == null) { first ??= (entry.Slot, entry.Resource ?? entry.GameClip!, reason!); continue; }
            body.Clips[(int)entry.Slot] = clip;
        }
        _bodies[enemy.Anim.Pointer] = body;
        if (TraceOn) Trace(body, "bound", 0, string.Join(",", Filled(body)));
        // The game may already have cross-faded into the state this life starts in, before this ran.
        switch (enemy.Locomotion?.CurrentStateEnum)
        {
            case ES_StateEnum.Hibernate: Show(body, (int)EnemyActionSlot.Hibernate); break;
            case ES_StateEnum.PathMove: body.OnGround = true; body.SampledAt = Clock.Time; Move(body, 0f); break;
        }
        return first;
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Slots(EnemyLocomotion.s_hashHitLights_Fwd, EnemyActionSlot.HitLight);
        Slots(EnemyLocomotion.s_hashHitLights_Bwd, EnemyActionSlot.HitLight);
        Slots(EnemyLocomotion.s_hashHitLights_Rt, EnemyActionSlot.HitLight);
        Slots(EnemyLocomotion.s_hashHitLights_Lt, EnemyActionSlot.HitLight);
        Slots(EnemyLocomotion.s_hashHitHeavys_Fwd, EnemyActionSlot.HitHeavy);
        Slots(EnemyLocomotion.s_hashHitHeavys_Bwd, EnemyActionSlot.HitHeavy);
        Slots(EnemyLocomotion.s_hashHitHeavys_Rt, EnemyActionSlot.HitHeavy);
        Slots(EnemyLocomotion.s_hashHitHeavys_Lt, EnemyActionSlot.HitHeavy);
        Slots(EnemyLocomotion.s_hashScreams, EnemyActionSlot.Scream);
        Slots(EnemyLocomotion.s_hashScreamTurns, EnemyActionSlot.Scream);
        Slots(EnemyLocomotion.s_hashHearbeats, EnemyActionSlot.Hibernate);
        Slots(EnemyLocomotion.s_hashHibernationWakeups, EnemyActionSlot.Wake);
        Slots(EnemyLocomotion.s_hashHibernationWakeupTurns, EnemyActionSlot.Wake);
        Slots(EnemyLocomotion.s_hashAbilityFires, EnemyActionSlot.Ranged);
        Slots(EnemyLocomotion.s_hashAbilityUse, EnemyActionSlot.Ranged);
        Slots(EnemyLocomotion.s_hashMeleeWalkSequences, EnemyActionSlot.Melee);
        Slots(EnemyLocomotion.s_hashMeleeSequences, EnemyActionSlot.Melee);
        Slots(EnemyLocomotion.s_hashMelee180Sequences, EnemyActionSlot.Melee);
        _slotByHash[EnemyLocomotion.s_hashHibernationIn] = EnemyActionSlot.Hibernate;
        _slotByHash[EnemyLocomotion.s_hashClimbLadder] = EnemyActionSlot.Ladder;
        _slotByHash[EnemyLocomotion.s_hashJumpStart] = EnemyActionSlot.Jump;
        _slotByHash[EnemyLocomotion.s_hashJumpLand] = EnemyActionSlot.Jump;
        _moveOnGround = EnemyLocomotion.s_hashMoveOnGround;
        ForgeCore.GameHooks.AnimatorCrossFaded.After(static (call, _) => Instance?.CrossFaded(call.Animator, call.Hash));
        ForgeCore.GameHooks.EnemyHitreacted.After(static (state, _) => Instance?.Reacted(state));
        Instance = this;
        ForgeCore.Frame.OnUpdate(EnemyHost.ModId + ".ActionReplacement", Tick);
    }

    private static EnemyActionReplacement? Instance;

    private void Slots(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>? hashes, EnemyActionSlot slot)
    {
        if (hashes == null) return;
        foreach (var hash in hashes) _slotByHash[hash] = slot;
    }

    private static IEnumerable<string> Filled(Body body)
    {
        for (var slot = 0; slot < body.Clips.Length; slot++)
            if (body.Clips[slot] != null) yield return ((EnemyActionSlot)slot).ToString();
    }

    // Callers ask TraceOn before building the row name.
    private void Trace(Body body, string name, int hash, string row, float elapsed = 0f) =>
        Traced?.Invoke(body.Enemy.EnemyData.persistentID, name, hash, row, body.Enemy.Locomotion?.CurrentStateEnum.ToString() ?? "-", elapsed);

    private void CrossFaded(Animator animator, int hash)
    {
        if (!_hookSeen) { _hookSeen = true; Traced?.Invoke(0, "hook-live", hash, "-", "-", 0f); }
        if (!_bodies.TryGetValue(animator.Pointer, out var body)) return;
        if (hash == _moveOnGround) { if (TraceOn) Trace(body, "crossfade", hash, "ground"); body.OnGround = true; body.Last = body.Enemy.Position; body.SampledAt = Clock.Time; Move(body, 0f); return; }
        body.OnGround = false;
        if (!_slotByHash.TryGetValue(hash, out var slot))
        {
            // Some bodies sleep in states of their own (the big enemies' second set); the enemy's state says what they are.
            var sleeping = body.Enemy.Locomotion?.CurrentStateEnum == ES_StateEnum.Hibernate;
            if (TraceOn) Trace(body, "crossfade", hash, sleeping ? "Hibernate" : "none");
            Show(body, sleeping ? (int)EnemyActionSlot.Hibernate : -1);
            return;
        }
        if (TraceOn) Trace(body, "crossfade", hash, slot.ToString());
        // A death cross-fades to a heavy hit's state, or not at all when the enemy is moving: Reacted starts it either way.
        if (slot is EnemyActionSlot.HitLight or EnemyActionSlot.HitHeavy && Dying(body.Enemy.Locomotion?.Hitreact?.CurrentReactionType)) return;
        // Every heartbeat is a cross-fade too; a sleep keeps playing through it.
        Show(body, (int)slot, restart: !Loops((int)slot));
    }

    private static bool Dying(ES_HitreactType? type) => type is ES_HitreactType.ToDeath or ES_HitreactType.InstantRagdollDeath;

    /// <summary>The game started a reaction. A death starts its action here; one the game turns straight into a ragdoll has no window, so nothing shows over it.</summary>
    private void Reacted(ES_Hitreact state)
    {
        var enemy = state.m_enemyAgent;
        if (enemy == null || !_bodies.TryGetValue(enemy.Anim.Pointer, out var body)) return;
        var type = state.CurrentReactionType;
        if (!Dying(type)) return;
        body.OnGround = false;
        body.DeathEnds = state.m_endTime;
        if (TraceOn) Trace(body, "death", 0, type.ToString());
        Show(body, type == ES_HitreactType.ToDeath ? (int)EnemyActionSlot.Death : -1, restart: true);
    }

    private static bool Loops(int slot) => slot is (int)EnemyActionSlot.Idle or (int)EnemyActionSlot.Walk or (int)EnemyActionSlot.Run or (int)EnemyActionSlot.Hibernate or (int)EnemyActionSlot.Ladder;

    private void Show(Body body, int slot, bool restart = false)
    {
        var clip = slot >= 0 ? body.Clips[slot] : null;
        if (clip == null)
        {
            if (body.Playing >= 0) body.Track.Stop(Fade);
            body.Playing = -1;
            return;
        }
        if (body.Playing == slot && !restart) return;
        var now = Time.time;
        body.Track = body.Player.CrossFade(body.Track, clip, new JointPlayback(now, Loops(slot), fadeIn: Fade, fadeOut: Fade), Fade);
        body.Playing = slot;
        body.UnityCheckAt = now + 0.3f;
        Shown?.Invoke(body.Enemy.EnemyData.persistentID, (EnemyActionSlot)slot, clip.Duration, slot == (int)EnemyActionSlot.Death ? body.DeathEnds - Clock.Time : 0f, false);
    }

    /// <summary>The ground state is one blend tree the game cross-fades to once, so standing, walking and running are told apart by how
    /// fast the body goes.</summary>
    private void Move(Body body, float speed) =>
        Show(body, (int)(speed < StandBelow ? EnemyActionSlot.Idle : speed < RunFrom ? EnemyActionSlot.Walk : EnemyActionSlot.Run));

    private void Tick()
    {
        if (_bodies.Count == 0) return;
        var now = Clock.Time;
        var seconds = ForgeCore.Frame.GameTime;
        foreach (var (pointer, body) in _bodies)
        {
            if (ForgeCore.NativeObjects.Gone(body.Enemy)) { _gone.Add(pointer); continue; }
            if (now >= body.DeathEnds)
            {
                if (body.Playing == (int)EnemyActionSlot.Death) Shown?.Invoke(body.Enemy.EnemyData.persistentID, EnemyActionSlot.Death, 0f, 0f, true);
                body.Player.Stop();
                _gone.Add(pointer);
                continue;
            }
            if (seconds >= body.UnityCheckAt)
            {
                body.UnityCheckAt = float.PositiveInfinity;
                if (TraceOn) Trace(body, "pose", 0, body.Playing >= 0 ? ((EnemyActionSlot)body.Playing).ToString() : "none", body.Track.Elapsed);
            }
            // Over a stretch, not a frame: the game moves an enemy on its own update turn, so most frames it does not move at all.
            var elapsed = now - body.SampledAt;
            if (!body.OnGround || elapsed < SpeedSample) continue;
            var at = body.Enemy.Position;
            Move(body, (at - body.Last).magnitude / elapsed);
            body.Last = at;
            body.SampledAt = now;
        }
        foreach (var pointer in _gone) _bodies.Remove(pointer);
        _gone.Clear();
    }

    /// <summary>Reads and checks the action files one profile names while content is installed, so a spawn finds them parsed and never reads
    /// a file inside a level. A refusal is kept and reported by the spawn that needs the action, as before.</summary>
    internal void Preload(EnemyActionEntry[] actions, string source)
    {
        foreach (var entry in actions)
            if (entry.Resource != null)
                try { Resource(entry.Resource, source, out _); }
                catch (Exception error) { EnemyLog.Contained(error); }
    }

    /// <summary>The action file <c>&lt;revision&gt;.glb</c> (the revision is the file's SHA-256, as a sound's is) as the package of
    /// <paramref name="source"/> sees it, read once per revision. A file that is not there is <c>resource-missing</c>; one whose bytes
    /// are not that revision, or that is no Forge animation file, is <c>invalid-clip</c>.</summary>
    private JointClip? Resource(string revision, string source, out string? reason)
    {
        reason = null;
        var key = PublishedResources.PackageRoot(source) + "\n" + revision;
        if (_resources.TryGetValue(key, out var known)) return known;
        if (_refused.TryGetValue(key, out reason)) return null;
        var clip = ReadResource(key, revision, source, out reason);
        if (clip == null) _refused[key] = reason;
        return clip;
    }

    private JointClip? ReadResource(string key, string revision, string source, out string? reason)
    {
        reason = null;
        PublishedResourceFailure refused = default;
        if (ContentPackages.FindResource(Paths.BepInExRootPath, PublishedResources.PackageRoot(source), revision, ActionResourceCodes.Extension,
                source, failure => refused = failure) is not { } file)
        { reason = refused.Code; return null; }
        if (!PublishedResources.TryReadHashed(file, ActionResourceCodes.Extension, ActionResourceCodes.Read, "action-resource-format",
                "An action filename is its 64-character lower-case SHA-256 followed by " + ActionResourceCodes.Extension + ".", "action-resource-file-size",
                "The action file is empty.", "invalid-clip", "The action bytes do not match the SHA-256 in the filename.", out _, out var bytes, out refused))
        { reason = refused.Code; return null; }
        try { return _resources[key] = JointClip.Read(bytes!, EnemyHost.ModId + "/" + revision); }
        // The refusal names the file and its first fault; the action stays the game's.
        catch (FormatException problem) { EnemyLog.Contained(new FormatException(file.Relative + ": " + problem.Message)); reason = "invalid-clip"; return null; }
    }

    /// <summary>A clip of the game's own, from the enemy the author picked.</summary>
    private JointClip? GameClip(EnemyAgent enemy, JointPlayer player, EnemyActionEntry entry, out string? reason)
    {
        var avatar = enemy.Anim.avatar;
        var key = (avatar != null ? avatar.Pointer : IntPtr.Zero, entry.GameEnemy, entry.GameClip!);
        if (_gameClips.TryGetValue(key, out var known)) { reason = known.Reason; return known.Clip; }
        var prefab = EnemyPrefabManager.GetEnemyPrefab(entry.GameEnemy);
        var source = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
        var clip = player.CaptureGameClip(entry.GameClip!, enemy.Anim, source, out reason);
        if (reason == "no-such-clip" && (source == null || source.runtimeAnimatorController == null)) reason = "no-source-enemy";
        else if (clip != null || reason is "no-such-clip" or "not-human") _gameClips[key] = (clip, reason);
        return clip;
    }

    /// <summary>A life ended or was replaced: its player stops now, instead of writing a skeleton the game hands to the next life.</summary>
    internal void Release(EnemyAgent enemy)
    {
        IntPtr pointer;
        try { pointer = enemy.Anim.Pointer; }
        catch (Exception error) { EnemyLog.Contained(error); return; }
        if (!_bodies.Remove(pointer, out var body)) return;
        body.Player.Stop();
    }

    /// <summary>The level ended and its enemies with it. Read resources stay: they name no life.</summary>
    internal void ClearWorld()
    {
        foreach (var body in _bodies.Values)
            try { body.Player.Stop(); }
            catch (Exception error) { EnemyLog.Contained(error); }
        _bodies.Clear();
        _gameClips.Clear();
    }
}

internal static class ActionResourceCodes
{
    /// <summary>The file extension of a shipped action (a Forge animation file).</summary>
    internal const string Extension = ".glb";
    internal static readonly PublishedBytesCodes Read = new("action-resource-path", "action-resource-unreadable", "action-resource-file-size");
}
