using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeEmote.Api;
using ForgeUI;
using ForgeVisual;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeEmote;

/// <summary>
/// Plays the local player's action, the view that goes with it and what the other players are doing. All game access goes through the libraries.
/// <para>The local player has two small states. The action: none (<see cref="_current"/> null) or playing. The view: at the eye (<see cref="_lease"/> null), out behind the
/// player (<see cref="_lease"/> held), or on its way back (<see cref="_leaving"/>). <see cref="Start"/> sets both; an action ends one way, <see cref="Stop"/>, either cutting the view back
/// this frame (something interrupted it, or a key the game must act on from the player's own eye) or letting it glide back (the clip ended). <see cref="EndView"/> is the only way the view ends.</para>
/// </summary>
internal static class EmoteRunner
{
    private const float QuickFade = 0.1f, CrossFade = 0.15f;

    /// <summary>One player's body and the action on it; the local player's and the others' are the same code.</summary>
    private sealed class PlayedBody
    {
        private IntPtr _agent;
        private JointPlayer? _joints;
        private JointTrack _track;
        private HeldHiding? _held;
        private ForgeAudio.AudioHandle? _sound;

        internal bool Playing => _track.Playing;

        /// <summary>The action's sound is still going.</summary>
        internal bool SoundPlaying => _sound != null && _sound.State != ForgeAudio.PlaybackState.Ended;

        /// <summary>Is this agent's body already bound (or tried: a body that cannot play clips is not tried again every frame).</summary>
        internal bool Bound(PlayerAgent agent) => _agent == agent.Pointer && (_joints == null || (_joints.Root != null && !_joints.Root.WasCollected));

        /// <summary>Binds the agent's body ahead of the first action, so that the first one does not pay for it.</summary>
        internal bool Bind(PlayerAgent agent)
        {
            // Bound per agent, not per player: someone who leaves and comes back, or a level built again, has a new body.
            if (_joints == null || _agent != agent.Pointer || _joints.Root == null || _joints.Root.WasCollected)
            {
                _agent = agent.Pointer;
                _joints = Models.BindJoints(agent);
            }
            return _joints != null;
        }

        /// <summary>Fades the action over the one playing; false when the body cannot play clips.</summary>
        internal bool Play(PlayerAgent agent, Emotes.Entry entry, JointMask mask, float start)
        {
            _held?.Dispose();
            _held = null;
            if (!Bind(agent)) return false;
            var definition = entry.Definition;
            _track = _joints!.CrossFade(_track, entry.Clip, new JointPlayback(start, definition.Loop, mask, 1f, definition.FadeIn, definition.FadeOut), CrossFade);
            if (definition.HideHeld) _held = Models.HideHeld(agent);
            _sound?.Stop(CrossFade);
            _sound = entry.PlaySound(agent, start);
            return true;
        }

        /// <summary>Heights of this body against the agent's own position (its feet), for the gated readback: hip, lowest foot, the body's root, the first-person eye, and whether the sync IK is on.</summary>
        internal string Readout(PlayerAgent agent)
        {
            if (_joints == null || _joints.Root == null || _joints.Root.WasCollected || _joints.Bone(0) is not { } hip || hip.WasCollected) return "no-body";
            var ground = agent.Position.y;
            var low = float.MaxValue;
            foreach (var joint in new[] { 3, 6 }) if (_joints.Bone(joint) is { } foot && !foot.WasCollected) low = Mathf.Min(low, foot.position.y);
            var eye = agent.EyePosition.y - ground;
            var sync = agent.SyncIK != null && !agent.SyncIK.WasCollected && agent.SyncIK.IsEnabled();
            return "hipY=" + (hip.position.y - ground).ToString("0.000") + " footLowY=" + (low - ground).ToString("0.000") + " bodyRootY=" + (_joints.Root.position.y - ground).ToString("0.000") + " bodyScale=" + _joints.Root.lossyScale.y.ToString("0.000")
                + " eyeY=" + eye.ToString("0.000") + " agentY=" + ground.ToString("0.000") + " syncIk=" + sync + " playing=" + Playing;
        }

        /// <summary>Puts the clip at <paramref name="clipTime"/> seconds in, every frame, for an action that is held or stretched in time (a two-player action).</summary>
        internal void Hold(float clipTime) => _track.Seek(clipTime);

        internal void Stop(float fade)
        {
            _track.Stop(fade);
            _sound?.Stop(fade);
            _sound = null;
            _held?.Dispose();
            _held = null;
        }
    }

    private sealed class Watched
    {
        internal readonly PlayedBody Body = new();
        internal uint Shown;
        internal byte ShownSeq;
        /// <summary>Has a value but no body yet: a late joiner hears about an action before the player's agent exists.</summary>
        internal bool Waiting;
        /// <summary>A two-player action: where the other player is in it and whom with, when it started and where its hands are out in the clip.</summary>
        internal EmotePair.Phase Phase;
        internal ulong Partner;
        internal float Start, Hold;
        internal bool Pair;
    }

    /// <summary>What a player publishes: the action and which beginning of it. The same action done again has a new <see cref="Seq"/>, which tells a new start from a late joiner's resend.</summary>
    private struct State
    {
        internal uint Id;
        internal byte Seq;
        /// <summary>A two-player action's <see cref="EmotePair.Phase"/> and the player it is with (0 for any other action).</summary>
        internal byte Phase;
        internal ulong Partner;
    }

    private const float FollowRange = 8f;
    private static readonly EmotePair Link = new();
    private static readonly HashSet<string> Answerable = new();
    private static InteractPrompt? _prompt;
    private static Emotes.Entry? _promptFor;
    private static string _promptText = "", _promptKey = "";
    private static bool _promptUp, _useWas, _useHeld;

    private static PlayerStateSlot<State> _slot = null!;
    private static byte _seq;
    private static int _warm, _agentCount = -1;
    private static bool _bindPending, _watching;
    private static readonly Dictionary<ulong, Watched> Others = new();
    private static bool _anyWaiting;

    private static Emotes.Entry? _current;
    private static float _began;
    private static readonly PlayedBody Body = new();
    private static InventorySlot _wielded;
    private static float _health;
    private static FirstPersonArms? _arms;
    private static PlayerAgent? _local;

    private static CameraLease? _lease;
    private static InputLease? _input;
    private static OwnBodyView? _own;
    private static bool _leaving;
    private static float _yaw, _pitch;

    internal static void Load()
    {
        var channel = Net.Open(Plugin.Id);
        // ForgeCore stamps a state with its world and drops one from an earlier level or from before a checkpoint reload.
        _slot = channel.PlayerState<State>("state");
        _slot.Changed += Seen;
        EmotePeers.Load(channel, player => { if (_slot.TryGet(player, out _, out _)) Seen(player, true); });
        channel.Refused += (player, reason) => Plugin.Logger.Error("ForgeEmote " + NameOf(player) + " does not match: " + reason);
        channel.PeerSilent += player => Plugin.Logger.ErrorOnce("ForgeEmote: " + NameOf(player) + " has not answered ForgeEmote (not installed, still loading, or another version); they may not see your actions.");
        _prompt = UIKit.Mod(Plugin.Id).AddInteractPrompt("Answer");
        UIKit.LanguageChanged += () => _promptFor = null;
        // After ForgeCore dropped the values (it moves its world before it raises these) and ForgeVisual stopped its tracks (it loaded first).
        GameEvents.LevelEnded += Cleanup;
        GameEvents.CheckpointReloaded += Cleanup;
    }

    internal static string NameOf(ulong player)
    {
        var agent = AgentOf(player);
        return agent != null ? agent.PlayerName : player.ToString();
    }

    private static PlayerAgent? AgentOf(ulong player) => ForgeCore.Level.PlayerByLookup(player);

    private static InventorySlot Wielded(PlayerAgent agent)
    {
        var inventory = agent.Inventory;
        return inventory == null ? InventorySlot.None : inventory.WieldedSlot;
    }

    /// <summary>Starts an action if the rules allow it (the wheel's click and <see cref="EmoteControl"/> both come here); a new action fades over the one playing. Null when it started, else why not.</summary>
    internal static string? Start(Emotes.Entry entry)
    {
        var local = Players.Local;
        if (local == null || local.WasCollected) return "no local player";
        var definition = entry.Definition;
        var motion = Players.Motion(local);
        if (!EmoteRules.CanStart(motion, definition.Move)) return "the player is " + motion + " and the action is " + definition.Move;
        if (UIKit.GameScreenOpen) return "a game screen is open";
        // A two-player action starts with the hands out; with a teammate in range already offering it, this answers them.
        var offerer = definition.Pair ? Offerer(local, entry) : 0UL;
        var now = definition.Pair ? Time.time - definition.PairHold : Follow(local, entry) ?? Time.time;
        // From the eye the body stays where it stands: its spine and hips turning or stepping (a whole-body clip travels half a metre) would carry it, or its shadow in the flashlight, across the view.
        var thirdPerson = ThirdPerson(definition);
        if (!Body.Play(local, entry, thirdPerson ? entry.Mask : entry.FirstPersonMask, now)) return "the body cannot play clips";
        _local = local;
        _current = entry;
        _began = now;
        _wielded = Wielded(local);
        _health = local.Damage.Health;
        Link.Clear();
        if (definition.Pair) StartPair(local, offerer);
        _slot.Publish(new State { Id = entry.NetId, Seq = ++_seq, Phase = (byte)Link.Now, Partner = Link.Partner }, now);
        EmoteWheel.Used(definition.Id);
        if (thirdPerson)
        {
            _arms?.Stop();
            EnterView(local);
        }
        else
        {
            EndView();
            if (entry.FirstPerson == null) _arms?.Stop();
            else
            {
                BindArms(local);
                _arms?.Play(entry.FirstPerson, new JointPlayback(now, definition.Loop), definition.HideHeld);
            }
        }
        Read("start", local, entry.Definition.Id);
        if (definition.Comms != 0 && !Comms.Say(local, definition.Comms)) Plugin.Logger.ErrorOnce("ForgeEmote: the game has no communication line " + definition.Comms + " or its menu is not up (" + definition.Id + ").");
        if (definition.Ping && UIKit.AimPoint(out var aim)) UIKit.Ping(local, aim);
        return null;
    }

    /// <summary>What <see cref="EmoteControl"/> reads: the action now, the sequence number it was published with, seconds played, view and sound.</summary>
    internal static Emotes.Entry? Current => _current;
    internal static byte Seq => _seq;
    internal static float Elapsed => _current == null ? 0f : Time.time - _began;
    internal static bool ThirdPersonView => _lease != null;
    internal static bool SoundPlaying => Body.SoundPlaying;

    /// <summary>Ends the local action with the fade of a clip that ran out (<see cref="EmoteControl.Stop"/>).</summary>
    internal static void StopLocal() => Stop(CrossFade, false);

    /// <summary>Whether the player watches this action from behind, by its definition and the player's settings.</summary>
    internal static bool ThirdPerson(EmoteDefinition definition) => (definition.View == EmoteView.ThirdPerson && Plugin.ThirdPerson.Value) || (definition.Page == "gesture" && Plugin.ThirdPersonGestures.Value);

    /// <summary>A two-player action: with <paramref name="offerer"/> (a teammate in range holding their hand out, 0 for none) this answers them, stepping to arm's length and facing them; otherwise it holds the hand out and waits.</summary>
    private static void StartPair(PlayerAgent local, ulong offerer)
    {
        if (offerer == 0 || AgentOf(offerer) is not { } agent) { Link.Wait(Time.time); return; }
        Link.Accept(offerer, Time.time);
        Meet(local, agent);
    }

    /// <summary>The player whose hand is out for <paramref name="entry"/> within reach of <paramref name="local"/>, or 0.</summary>
    private static ulong Offerer(PlayerAgent local, Emotes.Entry entry)
    {
        foreach (var (player, other) in Others)
            if (other.Shown == entry.NetId && other.Phase == EmotePair.Phase.Offering && AgentOf(player) is { } agent && Near(local, agent)) return player;
        return 0;
    }

    /// <summary>The answering player takes the one standing spot at arm's length in front of the offerer (a short step, only where the level lets them walk it) and faces them; the offerer only turns.</summary>
    private static void Meet(PlayerAgent local, PlayerAgent other)
    {
        var apart = local.Position - other.Position;
        apart.y = 0f;
        if (apart.sqrMagnitude < 1e-4f) { Players.Face(local, other.Position); return; }
        apart.Normalize();
        var spot = other.Position + apart * EmotePair.Gap;
        if (Vector3.Distance(spot, local.Position) <= EmotePair.MaxStep && ForgeCore.Level.TrySnap(spot, 0.4f, out var ground) && !ForgeCore.Level.Blocked(local.Position, ground)
            && Players.Warp(local, local.DimensionIndex, ground, -apart, WarpMode.Local).Accepted) return;
        Players.Face(local, other.Position);
    }

    private static float Distance(PlayerAgent local, PlayerAgent other) => Vector3.Distance(local.Position, other.Position);

    private static bool SameDimension(PlayerAgent local, PlayerAgent other) => local.DimensionIndex == other.DimensionIndex;

    private static bool Near(PlayerAgent local, PlayerAgent other) => EmotePair.InRange(Distance(local, other), SameDimension(local, other));

    /// <summary>The IDs of the two-player actions a teammate in range is waiting on (what the wheel lights).</summary>
    internal static HashSet<string> Answers(PlayerAgent local)
    {
        Answerable.Clear();
        if (_current == null)
            foreach (var (player, other) in Others)
                if (other.Phase == EmotePair.Phase.Offering && Emotes.Find(other.Shown) is { } entry && entry.Definition.Pair && AgentOf(player) is { } agent && Near(local, agent))
                    Answerable.Add(entry.Definition.Id);
        return Answerable;
    }

    private static void WatchIfAny()
    {
        foreach (var other in Others.Values) if (other.Shown != 0) return;
        Watching(false);
    }

    private static void BindArms(PlayerAgent local)
    {
        if (_arms == null || !_arms.Alive) _arms = Models.BindFirstPersonArms(local);
    }

    /// <summary>A dance joins the one a teammate within <see cref="FollowRange"/> is doing: it starts at that player's moment, so both dance the same step.</summary>
    private static float? Follow(PlayerAgent local, Emotes.Entry entry)
    {
        if (entry.Definition.Page != "dance") return null;
        foreach (var (player, other) in Others)
        {
            if (other.Shown != entry.NetId || !other.Body.Playing || !_slot.TryGet(player, out _, out var begun)) continue;
            var agent = AgentOf(player);
            if (agent != null && Vector3.Distance(agent.Position, local.Position) <= FollowRange) return begun;
        }
        return null;
    }

    private static void EnterView(PlayerAgent local)
    {
        _leaving = false;
        if (_lease != null) return;
        _lease = CameraLease.Take(Plugin.Id);
        _input = InputLease.Take(Plugin.Id);
        _input.Block(InputAction.LookHorizontal, InputAction.LookVertical);
        var turn = Quaternion.LookRotation(local.TargetLookDir).eulerAngles;
        _yaw = turn.y;
        _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, turn.x), -30f, 60f);
        _own = Models.ShowOwnBody(local);
    }

    /// <summary>The local action ends; with <paramref name="cut"/> the view is back this frame.</summary>
    private static void Stop(float fade, bool cut)
    {
        if (_current == null) return;
        _readId = _current.Definition.Id;
        _current = null;
        Link.Clear();
        Body.Stop(fade);
        if (Plugin.Readback.Value && _local != null) { Read("end", _local, _readId); _readAt = Time.time + 1f; }
        _slot.Clear();
        _arms?.Stop();
        if (cut || _lease == null) EndView();
        else _leaving = true;
    }

    private static string _readId = "";
    private static float _readAt;

    /// <summary>One line of heights for the local player (the owner reported the body floating after an action and landing only after a step): at the start, the end and one second after it. Off unless <c>Debug / Readback</c> is on.</summary>
    private static void Read(string when, PlayerAgent local, string id)
    {
        if (!Plugin.Readback.Value) return;
        Plugin.Info.LogInfo("ForgeEmote readback " + when + " " + id + " " + Body.Readout(local) + " motion=" + Players.Motion(local) + " view=" + (_lease != null ? "third" : "first"));
    }

    private static void EndView()
    {
        _input?.Dispose();
        _input = null;
        _lease?.Dispose();
        _lease = null;
        _own?.Dispose();
        _own = null;
        _leaving = false;
    }

    /// <summary>The level ended or a checkpoint reloaded: nothing of an action survives it. The bodies themselves are bound again when they are needed.</summary>
    private static void Cleanup()
    {
        Stop(0f, true);
        EndView();
        _local = null;
        Link.Clear();
        foreach (var other in Others.Values) other.Body.Stop(0f);
        Others.Clear();
        _anyWaiting = false;
        _warm = 0;
        _agentCount = -1;
        Watching(false);
    }

    /// <summary>The motion watch reads every player each frame, so it runs only while another player's action is on screen.</summary>
    private static void Watching(bool on)
    {
        if (on == _watching) return;
        _watching = on;
        if (on) Players.MotionChanged += MotionChanged;
        else Players.MotionChanged -= MotionChanged;
    }

    /// <summary>Binds ahead of time what the first action would bind in its own frame: the local body, then the first-person arms, then one other player's body per frame as they appear.</summary>
    private static void Warm(PlayerAgent local)
    {
        if (_warm < 2 && _current == null)
        {
            if (_warm == 0) Body.Bind(local);
            else if (!Models.FirstPersonArmsReady(local)) return; // not built yet (lobby, level load): try again next frame
            else BindArms(local);
            _warm++;
            return;
        }
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) return;
        var count = agents.Count;
        if (count != _agentCount) { _agentCount = count; _bindPending = true; }
        if (!_bindPending) return;
        _bindPending = false;
        for (var i = 0; i < count; i++)
        {
            var agent = agents[i];
            if (agent == null || agent.WasCollected || agent.IsLocallyOwned) continue;
            var owner = agent.Owner;
            if (owner == null) continue;
            var player = owner.Lookup;
            if (!Others.TryGetValue(player, out var other)) Others.Add(player, other = new Watched());
            if (other.Body.Bound(agent)) continue;
            other.Body.Bind(agent);
            _bindPending = true;
            return;
        }
    }

    internal static void Update()
    {
        EmoteWheel.Flush();
        if (!Emotes.Closed && SNet.IsInLobby) Emotes.Close();
        if (_anyWaiting) RetryWaiting();
        if (_readAt > 0f && Time.time >= _readAt && Players.Local is { WasCollected: false } reading) { _readAt = 0f; Read("end+1s", reading, _readId); }
        foreach (var other in Others.Values)
            if (other.Pair && other.Shown != 0 && other.Body.Playing) other.Body.Hold(EmotePair.ClipTime(other.Phase, Time.time - other.Start, other.Hold));
        var local = Players.Local;
        if (local == null || local.WasCollected) return;
        // The wheel being up (its key down) binds everything at once; otherwise one step per idle frame. Motion is read only while the wheel is up or an action is going.
        if (EmoteWheel.IsOpen) for (var step = 0; step < 2 && _warm < 2 && _current == null; step++) Warm(local);
        Warm(local);
        if (EmoteWheel.IsOpen) EmoteWheel.Highlight(Answers(local)); // the two-player slots a teammate in range is waiting on
        if (_current == null)
        {
            if (EmoteWheel.IsOpen) EmoteWheel.Refresh(Players.Motion(local));
            if (_leaving && !EmoteWheel.HoldsInput && GameActionKey()) EndView();
            AnswerPrompt(local);
            return;
        }
        _promptUp = false;
        _prompt?.Hide();
        var motion = Players.Motion(local);
        if (EmoteWheel.IsOpen) EmoteWheel.Refresh(motion);
        var now = Time.time;
        if (_current.Definition.Pair) Body.Hold(EmotePair.ClipTime(Link.Now, now - _began, _current.Definition.PairHold));
        if (Link.Expired(now)) Stop(CrossFade, false);
        else if (!Body.Playing) Stop(0f, false);
        else if (Interrupted(local, motion, _current.Definition)) Stop(QuickFade, true);
    }

    private static bool Interrupted(PlayerAgent local, PlayerMotion motion, EmoteDefinition definition)
    {
        if (EmoteRules.Interrupts(motion) || UIKit.GameScreenOpen) return true;
        // By what is in the hands, not by which key: the scroll wheel, the hacking tool, picking up a big item and the game's own switches change it too, and the action's hidden item would be the wrong one.
        if (Wielded(local) != _wielded) return true;
        if (definition.CancelOnHit)
        {
            // The health itself: the local player's hits go through Dam_PlayerDamageLocal's own overrides of the receive methods, on host and client alike.
            var health = local.Damage.Health;
            if (health < _health) return true;
            _health = health;
        }
        var locked = definition.Move == EmoteMove.Locked;
        if (locked && !EmoteRules.CanStart(motion, EmoteMove.Locked)) return true;
        // The wheel's own clicks and number keys, including the one that chose this action, are not the player acting.
        if (EmoteWheel.HoldsInput) return false;
        // The use key that answered an offer is still down on the next frames; it is not the player cancelling until it has been let go once.
        if (_useHeld) _useHeld = InputLease.Down(InputAction.Use);
        return locked ? GameActionKey(_useHeld) || Moving() : InputLease.Down(InputAction.Reload) || InputLease.Down(InputAction.Melee);
    }

    /// <summary>A key that does something in the world from the player's eye. Read past every lease; the game acts on it itself, nothing holds it back.</summary>
    private static bool GameActionKey(bool ignoreUse = false) => InputLease.Down(InputAction.Fire) || InputLease.Down(InputAction.Aim) || (!ignoreUse && InputLease.Down(InputAction.Use))
        || InputLease.Down(InputAction.Reload) || InputLease.Down(InputAction.Melee);

    /// <summary>The prompt for answering a teammate whose hand is out (a two-player action in range): the game's own interaction text with the use key; pressing it starts the same action as picking it on the wheel.</summary>
    private static void AnswerPrompt(PlayerAgent local)
    {
        Emotes.Entry? entry = null;
        foreach (var (player, other) in Others)
            if (other.Phase == EmotePair.Phase.Offering && Emotes.Find(other.Shown) is { } offered && AgentOf(player) is { } agent && Near(local, agent)) { entry = offered; break; }
        // With the crosshair on something the game lets the player use, the use key is the game's: no prompt, no answer.
        if (entry == null || EmoteWheel.IsOpen || UIKit.GameScreenOpen || (local.Interaction != null && local.Interaction.HasWorldInteraction) || !EmoteRules.CanStart(Players.Motion(local), entry.Definition.Move))
        {
            _promptUp = false;
            _prompt?.Hide();
            return;
        }
        if (_promptFor != entry)
        {
            _promptFor = entry;
            _promptText = UIKit.Mod(Plugin.Id).Text("pair.answer", entry.Texts.Text(entry.Definition.Name));
            _promptKey = "[" + UIKitKeys.Binding(InputAction.Use, "E") + "]";
        }
        _prompt?.Show(_promptText, _promptKey);
        var use = InputLease.Down(InputAction.Use);
        // A use key already down when the offer appeared (opening a door) is not an answer.
        var pressed = use && !_useWas && _promptUp;
        _useWas = use;
        _promptUp = true;
        if (!pressed) return;
        _prompt?.Hide();
        _useHeld = true;
        if (Start(entry) is not null) _useHeld = false;
    }

    private static bool Moving() => InputLease.Down(InputAction.Jump) || InputLease.Down(InputAction.Crouch)
        || Mathf.Abs(InputLease.Axis(InputAction.MoveHorizontal)) > 0.1f || Mathf.Abs(InputLease.Axis(InputAction.MoveVertical)) > 0.1f;

    internal static void LateUpdate()
    {
        if (_lease == null) return;
        if (!_lease.IsHeld || _local == null || _local.WasCollected) { EndView(); return; }
        if (!_leaving && Plugin.Orbit.Value && !EmoteWheel.HoldsInput)
        {
            _yaw += InputLease.Axis(InputAction.LookHorizontal);
            _pitch = Mathf.Clamp(_pitch - InputLease.Axis(InputAction.LookVertical), -30f, 60f);
        }
        var focus = _local.EyePosition + Vec.Up * Plugin.Height.Value;
        var away = _lease.Orbit(focus, _yaw, _pitch, Plugin.Distance.Value, Plugin.Shoulder.Value, Plugin.BlendSeconds.Value, _leaving);
        if (_leaving && away <= 0f) EndView();
    }

    private static void Seen(ulong player, bool active)
    {
        if (!Others.TryGetValue(player, out var other)) Others.Add(player, other = new Watched());
        other.Waiting = false;
        if (!active || !_slot.TryGet(player, out var state, out var start))
        {
            Forget(player, other, CrossFade);
            return;
        }
        var id = state.Id;
        // The same action with the same number is a late-join resend and must not start its fade over; done again it has a new number and plays again.
        if (id == other.Shown && state.Seq == other.ShownSeq) return;
        var entry = Emotes.Find(id);
        if (!EmotePeers.Shows(player, id) || entry == null || (entry.Definition.Crude && !Plugin.CrudeGestures.Value))
        {
            Forget(player, other, CrossFade);
            return;
        }
        var agent = AgentOf(player);
        if (agent == null)
        {
            other.Waiting = _anyWaiting = true;
            return;
        }
        other.Shown = id;
        other.ShownSeq = state.Seq;
        other.Pair = entry.Definition.Pair;
        other.Hold = entry.Definition.PairHold;
        other.Phase = (EmotePair.Phase)state.Phase;
        other.Partner = state.Partner;
        other.Start = start;
        if (_local != null && _current != null && _current.Definition.Pair) Pairing(player, other, agent);
        if (other.Body.Play(agent, entry, entry.Mask, start)) Watching(true);
    }

    /// <summary>A player stopped (or never showed): their body goes back to the game's animation, and a two-player action of ours that was with them ends rather than go on alone.</summary>
    private static void Forget(ulong player, Watched other, float fade)
    {
        other.Shown = 0;
        other.Phase = EmotePair.Phase.None;
        other.Partner = 0;
        other.Body.Stop(fade);
        WatchIfAny();
        if (_current != null && Link.Partner == player && Link.Now is EmotePair.Phase.Accepting or EmotePair.Phase.Paired) Stop(CrossFade, false);
    }

    /// <summary>What <paramref name="other"/>'s new state means for our own two-player action: the offerer sees an answer and starts the shared moment, the answerer sees that start and takes it, and either one ends when the other's state no longer matches.</summary>
    private static void Pairing(ulong player, Watched other, PlayerAgent agent)
    {
        var local = _local!;
        var me = local.Owner.Lookup;
        if (Link.Partner == player && Link.Now is EmotePair.Phase.Accepting or EmotePair.Phase.Paired)
        {
            // The partner must still be in the same action: paired, or (for the answerer) still holding their hand out until they start it.
            var together = other.Shown == _current!.NetId && (Link.Now == EmotePair.Phase.Paired
                ? other.Partner == me && other.Phase == EmotePair.Phase.Paired
                : other.Phase == EmotePair.Phase.Offering || (other.Phase == EmotePair.Phase.Paired && other.Partner == me));
            if (!together) Stop(CrossFade, false);
            if (!together) return;
        }
        // Two hands out for the same action at once (neither had seen the other's yet): the higher player number answers, so both sides decide the same.
        if (Link.Now == EmotePair.Phase.Offering && other.Phase == EmotePair.Phase.Offering && other.Shown == _current!.NetId && me > player && Near(local, agent))
        {
            Link.Accept(player, Time.time);
            Meet(local, agent);
            _slot.Publish(new State { Id = _current.NetId, Seq = ++_seq, Phase = (byte)Link.Now, Partner = player }, _began);
            return;
        }
        if (other.Shown != _current!.NetId || other.Partner != me) return;
        if (Link.Now == EmotePair.Phase.Offering && other.Phase == EmotePair.Phase.Accepting && Link.Pair(player, Distance(local, agent), SameDimension(local, agent)))
        {
            // The offerer sets the moment: the clip is at its hands-out point now. The answerer's confirmation carries the same start back.
            _began = Time.time - _current.Definition.PairHold;
            Players.Face(local, agent.Position);
            _slot.Publish(new State { Id = _current.NetId, Seq = ++_seq, Phase = (byte)EmotePair.Phase.Paired, Partner = player }, _began);
        }
        else if (Link.Now == EmotePair.Phase.Accepting && Link.Partner == player && other.Phase == EmotePair.Phase.Paired)
        {
            Link.Seal();
            _began = other.Start;
            _slot.Publish(new State { Id = _current.NetId, Seq = ++_seq, Phase = (byte)EmotePair.Phase.Paired, Partner = player }, _began);
        }
    }

    private static void RetryWaiting()
    {
        _anyWaiting = false;
        foreach (var (player, other) in Others)
            if (other.Waiting) Seen(player, true);
    }

    private static void MotionChanged(PlayerAgent agent, PlayerMotion from, PlayerMotion to)
    {
        if (agent.IsLocallyOwned || !EmoteRules.Interrupts(to) || agent.Owner == null || !Others.TryGetValue(agent.Owner.Lookup, out var other)) return;
        other.Waiting = false;
        Forget(agent.Owner.Lookup, other, QuickFade);
    }
}
