using System;
using System.Collections.Generic;
using Enemies;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance.Frames;

/// <summary>One suit on this peer: its record (the host's truth, or this peer's mirror of it) and its presentation.</summary>
internal sealed class FrameUnit
{
    internal FrameUnit(FrameRecord record) => Record = record;
    internal FrameRecord Record { get; }
    internal FrameVisual? Visual;
    // Host only.
    internal bool Dirty;
    internal float NextSend;
    internal bool RetainPending;
    internal bool SentShieldUp = true;
    internal float NextRepel;
    private PlayerAgent? _hostPilot;
    private ForgeCore.PlayerNoiseLease? _noise;
    /// <summary>The pilot as the host knows it; enemies hear that player <see cref="FrameNoise.PilotMultiplier"/> times as far while it is set.</summary>
    internal PlayerAgent? HostPilot
    {
        get => _hostPilot;
        set
        {
            if (ReferenceEquals(_hostPilot, value)) return;
            _noise?.Dispose();
            _noise = null;
            _hostPilot = value;
            if (value != null) _noise = Noise.Scale(value);
        }
    }
    internal int HostPilotIndex = -1;
}

/// <summary>Every suit in the level. The host decides (boarding, leaving, damage, wrecks, ejection) and sends each record
/// change to every peer as the carrier's per-call data; every peer, the host included, presents from the record.</summary>
internal static class FrameWorld
{
    /// <summary>Health changes from a stream of hits are sent at most this often.</summary>
    private const float DamageSendInterval = 0.2f;

    private static readonly List<FrameUnit> Units = new();
    private static readonly Dictionary<(ulong, uint), FrameUnit> ById = new();
    private static readonly Dictionary<(ulong, uint), FrameStateMessage> Pending = new();
    private static bool _inLevel;
    private static uint _sequenceFloor;

    internal static IReadOnlyList<FrameUnit> All => Units;

    /// <summary>A suit model is known to this level (picked in the lobby or called in).</summary>
    internal static bool HasSpecs => Specs.Count > 0;

    /// <summary>Every model this peer parsed, by <see cref="FrameSpecKey"/>: a late joiner builds suits from the host's state
    /// with the spec it parsed from the same definition.</summary>
    private static readonly Dictionary<ulong, FrameSpec> Specs = new();

    internal static void Known(FrameSpec spec) => Specs[FrameSpecKey.Of(spec)] = spec;

    /// <summary>The suit's name in capitals: the display name (<see cref="OrdnanceContent.Name"/>) of the definition whose key the record
    /// carries (<see cref="FrameRecord.DefinitionKey"/>: every peer loads the same definitions, so a client, a late joiner and a reloaded
    /// checkpoint find it without having seen a call). Null when no loaded definition has the key.</summary>
    internal static string? DefinitionName(uint key) =>
        OrdnanceContent.Set.TryGet(key, out var definition) ? OrdnanceContent.Name(definition).ToUpperInvariant() : null;

    /// <summary>Any model this peer parsed (the prewarm builds the HUD for it before a suit is called), or null with none.</summary>
    internal static FrameSpec? AnySpec
    {
        get
        {
            foreach (var spec in Specs.Values) return spec;
            return null;
        }
    }

    private static float Now => Time.time;

    // ---- arrival ----------------------------------------------------------------------------------------------

    /// <summary>Host, when the call arrives: the suit starts falling at the landing point, or (a direct arrival) stands there empty already.</summary>
    internal static void HostArrive(OrdnanceExecution e)
    {
        // A suit already here (restored from a checkpoint, mirrored by a new host) keeps its numbers; only a new one takes the placement's.
        var fresh = !ById.ContainsKey((e.SummonerLookup, e.ThrowId));
        var unit = GetOrCreate(e.SummonerLookup, e.ThrowId, (FrameSpec)e.Parameters);
        var r = unit.Record;
        r.DefinitionKey = e.Definition.Key;
        if (fresh)
        {
            if (e.Overrides.TryGetValue("Armor", out var armor)) r.Armor = (float)armor;
            if (e.Overrides.TryGetValue("Magazine", out var magazine)) r.Magazine = (int)magazine;
            if (e.Overrides.TryGetValue("Reserve", out var reserve)) r.Reserve = (int)reserve;
        }
        Land(r, e.Arrival);
        FrameRules.Place(r, e.Position.x, e.Position.y, e.Position.z, e.Rotation.eulerAngles.y);
        Send(unit, FrameEjection.None);
    }

    /// <summary>The suit's first state: falling in for a drop, empty and ready for a direct arrival.</summary>
    private static void Land(FrameRecord r, OrdnanceArrivalMode arrival)
    {
        r.State = arrival == OrdnanceArrivalMode.Direct ? FrameState.Idle : FrameState.Arriving;
        r.StateEndsAt = arrival == OrdnanceArrivalMode.Direct ? Now : Now + FrameVisual.ArriveSeconds;
    }

    /// <summary>Every peer: the presentation starts. On a client the record may already hold the host's state.</summary>
    internal static void Arrived(OrdnancePresentation p)
    {
        var unit = GetOrCreate(p.SummonerLookup, p.ThrowId, (FrameSpec)p.Parameters);
        var r = unit.Record;
        r.DefinitionKey = p.Definition.Key;
        if (!ForgeCore.Session.IsHost && r.Sequence == 0)
        {
            Land(r, p.Arrival);
            r.X = p.Position.x; r.Y = p.Position.y; r.Z = p.Position.z; r.Yaw = p.Rotation.eulerAngles.y;
        }
        if (Pending.Remove((p.SummonerLookup, p.ThrowId), out var early) && early.Authority == SNet.Master?.Lookup) early.ApplyTo(r, Now);
        // A direct arrival is built standing, as a joining player would see it: no fall, no landing sounds.
        unit.Visual ??= FrameVisual.Create(unit, r.State != FrameState.Arriving);
    }

    private static FrameUnit GetOrCreate(ulong thrower, uint throwId, FrameSpec spec)
    {
        if (ById.TryGetValue((thrower, throwId), out var unit)) return unit;
        unit = new FrameUnit(new FrameRecord(thrower, throwId, spec));
        ById.Add((thrower, throwId), unit);
        Units.Add(unit);
        return unit;
    }

    // ---- state replication ------------------------------------------------------------------------------------

    private static void Send(FrameUnit unit, FrameEjection ejection)
    {
        var r = unit.Record;
        var now = Now;
        _sequenceFloor = Math.Max(_sequenceFloor, r.Sequence);
        unit.Dirty = false;
        unit.SentShieldUp = r.ShieldUp;
        unit.NextSend = now + DamageSendInterval;
        r.Authority = SNet.LocalPlayer.Lookup;
        // Kept by the carrier for players who join later; always the whole record.
        unit.RetainPending = !OrdnanceApi.SendState(FramesPayload.Type, r.Thrower, r.ThrowId, FrameMessages.StateTag,
            ForgeCore.NetBody.Of(FrameStateMessage.From(r, now, ejection)));
        if (ejection != FrameEjection.None)
        {
            if (LocalPilot.Unit == unit) LocalPilot.Ejected(ejection);
        }
    }

    /// <summary>Host, for a state kept for a player who joins later: the suit's times are seconds from the moment of sending, so they are cut again from
    /// the suit's record now (a cooldown that ran out while it was kept must not start over). A suit the host no longer holds is replayed as kept.</summary>
    internal static byte[] RefreshState(ulong thrower, uint throwId, byte tag, byte[] data)
    {
        if (tag != FrameMessages.StateTag || !ById.TryGetValue((thrower, throwId), out var unit) || !ForgeCore.NetBody.TryRead(data, out FrameStateMessage kept)) return data;
        var fresh = FrameStateMessage.From(unit.Record, Now, FrameEjection.None);
        fresh.Sequence = kept.Sequence;
        return ForgeCore.NetBody.Of(fresh);
    }

    /// <summary>Every peer: data the host sent for a suit call. The host already holds the truth and ignores its own copy.</summary>
    internal static void OnData(OrdnancePayloadData data)
    {
        if (data.Tag != FrameMessages.StateTag)
        {
            Plugin.Logger.ErrorOnce("frames-data-tag", "A suit message came with the tag " + data.Tag + ", which this version does not know.");
            return;
        }
        if (ForgeCore.Session.IsHost) return;
        if (!ForgeCore.NetBody.TryRead(data.Data, out FrameStateMessage message)) { Plugin.Logger.ErrorOnce("Exosuit state of " + data.Data.Length + " bytes does not match this version; update Forge Ordnance Frames."); return; }
        if (SNet.Master == null || message.Authority != SNet.Master.Lookup) return;
        if (!ById.TryGetValue((data.SummonerLookup, data.ThrowId), out var unit))
        {
            if (!data.Replayed && message.State == (byte)FrameState.Arriving) { Pending[(data.SummonerLookup, data.ThrowId)] = message; return; }
            if (!data.Replayed && message.State == (byte)FrameState.Destroyed) return;
            // Replayed or restored after recovery: an arrival callback need not run, so build from the state alone.
            if (!Specs.TryGetValue(message.Spec, out var spec))
            {
                Plugin.Logger.ErrorOnce("Exosuit " + data.ThrowId + " uses a model this player's definitions do not have; it is not shown.");
                return;
            }
            unit = GetOrCreate(data.SummonerLookup, data.ThrowId, spec);
        }
        var wasLocalPilot = unit.Record.Pilot == SNet.LocalPlayer.Lookup && unit.Record.HasPilot;
        if (!message.ApplyTo(unit.Record, Now)) return;
        _sequenceFloor = Math.Max(_sequenceFloor, message.Sequence);
        if (message.Ejection != 0 && wasLocalPilot) LocalPilot.Ejected((FrameEjection)message.Ejection);
        unit.Visual ??= FrameVisual.Create(unit, data.Replayed || message.State != (byte)FrameState.Arriving);
    }

    // ---- requests ---------------------------------------------------------------------------------------------

    /// <summary>The local player asks the host to board, leave, report its gun or use a special. The host decides its own at once; a
    /// client gets one answer: the refusal, or that none came (the host did not answer in time, or is gone). A granted request
    /// shows in the suit's state like everyone's.</summary>
    internal static void Request(FrameUnit unit, FrameRequestKind kind, int magazine = 0, int reserve = 0, byte value = 0)
    {
        var message = new FrameRequestMessage
        {
            Thrower = unit.Record.Thrower,
            ThrowId = unit.Record.ThrowId,
            Kind = (byte)kind,
            Magazine = magazine,
            Reserve = reserve,
            Drive = unit.Record.Drive,
            Value = value
        };
        if (ForgeCore.Session.IsHost) Answered(kind, Decide(SNet.LocalPlayer.Lookup, message));
        else FrameNetwork.Request.AskHost(ForgeCore.NetBody.Of(message), answer => Answered(kind, ReasonOf(answer)));
    }

    /// <summary>The host's one-byte answer, or that none came. A request the level or a checkpoint reload overtook is void without a word.</summary>
    private static FrameRejection ReasonOf(ForgeCore.NetAnswer answer) => answer.Status switch
    {
        ForgeCore.NetAnswerStatus.Answered => answer.Body.Length == 1 ? (FrameRejection)answer.Body[0] : FrameRejection.None,
        ForgeCore.NetAnswerStatus.TimedOut or ForgeCore.NetAnswerStatus.Left => FrameRejection.NoAnswer,
        _ => FrameRejection.None,
    };

    /// <summary>The gun's stock report is no request of the player's: nothing is shown for it.</summary>
    private static void Answered(FrameRequestKind kind, FrameRejection reason)
    {
        if (reason != FrameRejection.None && kind != FrameRequestKind.Ammo) LocalPilot.Refused(kind, reason);
    }

    internal static void ServeRequest(ForgeCore.NetAsk ask)
    {
        if (!ForgeCore.Session.IsHost) return;
        ask.Answer(new[] { (byte)(ForgeCore.NetBody.TryRead(ask.Body, out FrameRequestMessage message) ? Decide(ask.From, message) : FrameRejection.None) });
    }

    /// <summary>Host: a peer asks to board, leave, report its gun or use a special.</summary>
    private static FrameRejection Decide(ulong sender, FrameRequestMessage m)
    {
        if (!ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) return FrameRejection.None;
        var agent = ForgeCore.Level.PlayerByLookup(sender, out var agentIndex);
        if (agent == null) return FrameRejection.None;
        var r = unit.Record;
        var now = Now;
        var kind = (FrameRequestKind)m.Kind;
        // A kind outside the enum is a malformed request: nothing is done for it.
        if (!Enum.IsDefined(kind)) return FrameRejection.None;
        // Every request but boarding belongs to one boarding: one made in an earlier one is no request of this pilot.
        if (kind != FrameRequestKind.Enter && m.Drive != r.Drive) return FrameRejection.None;
        // A player the game holds (grabbed, stunned, controls taken) cannot board, leave or use a special until it lets go, as the
        // game's own actions wait; only the gun's stock report goes through.
        var rejection = kind != FrameRequestKind.Ammo && IsSeized(agent) ? FrameRejection.Seized : FrameRejection.None;
        if (rejection == FrameRejection.None) switch (kind)
            {
                case FrameRequestKind.Enter:
                    rejection = FrameRules.CanEnter(r, Vector3.Distance(agent.Position, new Vector3(r.X, r.Y, r.Z)) - FrameRules.BoardLag, PilotUnit(sender) != null, IsDown(agent));
                    if (rejection == FrameRejection.None)
                    {
                        FrameRules.Enter(r, sender, now);
                        unit.HostPilot = agent;
                        unit.HostPilotIndex = agentIndex;
                        Send(unit, FrameEjection.None);
                    }
                    break;
                case FrameRequestKind.Exit:
                    rejection = FrameRules.CanExit(r, sender);
                    if (rejection == FrameRejection.None)
                    {
                        // The suit stays where it stands; the pilot steps out backwards with the game's own movement.
                        FrameRules.Exit(r, now, r.X, GroundY(r.X, r.Y, r.Z), r.Z, r.Yaw);
                        Send(unit, FrameEjection.None);
                    }
                    break;
                case FrameRequestKind.Ammo:
                    if (FrameRules.ReportAmmo(r, sender, m.Magazine, m.Reserve)) unit.Dirty = true;
                    break;
                case FrameRequestKind.Boost:
                    rejection = FrameRules.CanBoost(r, sender, now);
                    if (rejection == FrameRejection.None) { FrameRules.Boost(r, now); Send(unit, FrameEjection.None); }
                    break;
                case FrameRequestKind.Siege:
                    rejection = FrameRules.CanSiege(r, sender);
                    if (rejection == FrameRejection.None && FrameRules.SetSiege(r, now, m.Value != 0)) Send(unit, FrameEjection.None);
                    break;
                default:
                    Plugin.Logger.ErrorOnce("frames-request-kind", "A suit request of kind " + m.Kind + " has no handler.");
                    break;
            }
        return rejection;
    }

    /// <summary>The local pilot asks for a rocket salvo at <paramref name="target"/>, as <see cref="Request"/> asks.</summary>
    internal static void RequestRockets(FrameUnit unit, Vector3 target)
    {
        var message = new FrameRocketMessage { Thrower = unit.Record.Thrower, ThrowId = unit.Record.ThrowId, TargetX = target.x, TargetY = target.y, TargetZ = target.z, Drive = unit.Record.Drive };
        if (ForgeCore.Session.IsHost) Answered(FrameRequestKind.Rockets, DecideRockets(SNet.LocalPlayer.Lookup, message));
        else FrameNetwork.Rockets.AskHost(ForgeCore.NetBody.Of(message), answer => Answered(FrameRequestKind.Rockets, ReasonOf(answer)));
    }

    internal static void ServeRockets(ForgeCore.NetAsk ask)
    {
        if (!ForgeCore.Session.IsHost) return;
        ask.Answer(new[] { (byte)(ForgeCore.NetBody.TryRead(ask.Body, out FrameRocketMessage message) ? DecideRockets(ask.From, message) : FrameRejection.None) });
    }

    /// <summary>Host: the pilot asks for a rocket salvo at a point within its range; a granted one goes to every peer.</summary>
    private static FrameRejection DecideRockets(ulong sender, FrameRocketMessage m)
    {
        if (!ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) return FrameRejection.None;
        var r = unit.Record;
        if (m.Drive != r.Drive) return FrameRejection.None;
        var target = new Vector3(m.TargetX, m.TargetY, m.TargetZ);
        var pilot = HostPilotOf(unit);
        var rejection = pilot != null && r.Pilot == sender && IsSeized(pilot) ? FrameRejection.Seized
            : !float.IsFinite(m.TargetX) || !float.IsFinite(m.TargetY) || !float.IsFinite(m.TargetZ)
            || r.Spec.Rockets != null && !(Vector3.Distance(target, new Vector3(r.X, r.Y, r.Z)) <= r.Spec.Rockets.Range + 5f)
            ? FrameRejection.TooFar : FrameRules.Rockets(r, sender, Now);
        if (rejection != FrameRejection.None) return rejection;
        m.Seed = (ushort)UnityEngine.Random.Range(0, 65536);
        Send(unit, FrameEjection.None);
        FrameNetwork.RocketLaunch.Broadcast(m);
        Launched(unit, m);
        return FrameRejection.None;
    }

    /// <summary>Every other peer: a salvo the host granted.</summary>
    internal static void OnRocketLaunch(ulong sender, FrameRocketMessage m)
    {
        if (!OrdnanceNetwork.Channel.FromHost(sender)) return;
        if (!ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) return;
        Launched(unit, m);
    }

    private static void Launched(FrameUnit unit, FrameRocketMessage m)
    {
        var now = Now;
        FrameRockets.Launch(unit, m, now);
        LocalPilot.RocketsLaunched(unit, now);
    }

    /// <summary>Every peer: the pilot fired; play its gun here. Only the suit's own pilot is believed.</summary>
    internal static void OnFire(ulong sender, FrameFireMessage m)
    {
        if (!ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) return;
        if (unit.Record.Pilot != sender || unit.Record.State != FrameState.Piloted) return;
        unit.Visual?.Shots(m.Shots, new Vector3(m.OriginX, m.OriginY, m.OriginZ), new Vector3(m.TargetX, m.TargetY, m.TargetZ));
    }

    internal static void OnBump(ulong sender, FrameBumpMessage m)
    {
        if (!ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) return;
        if (unit.Record.Pilot != sender || unit.Record.State != FrameState.Piloted) return;
        unit.Visual?.Bash(new Vector3(m.X, m.Y, m.Z), new Vector3(m.DirX, 0f, m.DirZ), Now);
    }

    // ---- damage (host) ----------------------------------------------------------------------------------------

    /// <summary>The suit a player wears (boarding, worn or leaving), or null.</summary>
    internal static FrameUnit? PilotUnit(ulong lookup)
    {
        if (lookup == 0) return null;
        for (var i = 0; i < Units.Count; i++)
        {
            var r = Units[i].Record;
            if (r.HasPilot && r.Pilot == lookup) return Units[i];
        }
        return null;
    }

    /// <summary>Host: a hit on a pilot. Returns true when the suit took it (the player's own damage must be cancelled).</summary>
    internal static bool HostDamage(FrameUnit unit, PlayerAgent pilot, float damage, Agents.Agent? source)
    {
        var r = unit.Record;
        var before = r.Armor;
        SendHit(unit, From(source), damage, false);
        if (FrameRules.Damage(r, damage, Now))
        {
            ClearHostPilot(unit);
            var p = pilot.Position;
            FrameRules.Place(r, p.x, GroundY(p.x, p.y, p.z), p.z, Yaw(pilot));
            Send(unit, FrameEjection.Destroyed);
            FrameRepel.Wrecked(pilot, p);
        }
        else if (r.Armor != before) unit.Dirty = true;
        return true;
    }

    /// <summary>Any peer: the suit whose raised barrier stops an attack by <paramref name="source"/> on a player standing at
    /// <paramref name="target"/>, or null. Every peer asks the same question using the current pilot position, so the hit is cancelled
    /// wherever the game applies it; only the host charges the barrier.</summary>
    internal static FrameUnit? ShelterFor(Vector3 target, Agents.Agent? source)
    {
        var enemy = source == null || source.WasCollected ? null : source.TryCast<EnemyAgent>();
        return enemy == null ? null : ShelterFor(target, enemy.Position);
    }

    /// <summary>Any peer: the suit whose raised barrier stops an attack coming from <paramref name="s"/> at <paramref name="target"/>
    /// (<see cref="FrameRules.Shelters"/>: the target inside the dome, where the attack came from outside it), or null.</summary>
    internal static FrameUnit? ShelterFor(Vector3 target, Vector3 s)
    {
        for (var i = 0; i < Units.Count; i++)
        {
            var unit = Units[i];
            var r = unit.Record;
            var pilot = DomePilot(unit);
            if (pilot == null) continue;
            var p = pilot.Position;
            if (FrameRules.Shelters(r, p.x, p.y, p.z, target.x, target.y, target.z, s.x, s.y, s.z)) return unit;
        }
        return null;
    }

    /// <summary>The pilot a raised barrier stands on (its dome follows the pilot as this peer sees them), or null when the suit's
    /// barrier is down or its pilot is not resolved here.</summary>
    internal static PlayerAgent? DomePilot(FrameUnit unit)
    {
        var r = unit.Record;
        if (!r.HasPilot || !FrameRules.BarrierUp(r)) return null;
        return ForgeCore.Session.IsHost ? HostPilotOf(unit) : ForgeCore.Level.PlayerByLookup(r.Pilot);
    }

    /// <summary>Where an attack came from: its attacker's place, NaN without one.</summary>
    internal static Vector3 From(Agents.Agent? source) => source == null || source.WasCollected ? new Vector3(float.NaN, 0f, float.NaN) : source.Position;

    /// <summary>Host: an attack the barrier stopped, which came from <paramref name="from"/> (NaN when unknown).</summary>
    internal static void HostShield(FrameUnit unit, float damage, Vector3 from, bool onPilot)
    {
        if (onPilot) SendHit(unit, from, damage, true);
        if (FrameRules.ShieldHit(unit.Record, damage, Now))
        {
            Send(unit, FrameEjection.None);
        }
        else unit.Dirty = true;
    }

    /// <summary>Host: tells the suit's pilot where a hit came from (the cockpit's edge glow and silhouette); NaN when unknown.</summary>
    private static void SendHit(FrameUnit unit, Vector3 at, float damage, bool shielded)
    {
        var r = unit.Record;
        if (!r.HasPilot) return;
        var message = new FrameHitMessage { Thrower = r.Thrower, ThrowId = r.ThrowId, SourceX = at.x, SourceZ = at.z, Damage = damage, Shielded = shielded ? (byte)1 : (byte)0 };
        if (SNet.LocalPlayer != null && r.Pilot == SNet.LocalPlayer.Lookup) LocalPilot.Hit(unit, message); // the host is the pilot
        else FrameNetwork.Hit.To(r.Pilot, message);
    }

    /// <summary>The pilot: a hit the host reported for this player's suit.</summary>
    internal static void OnHit(ulong sender, FrameHitMessage m)
    {
        if (SNet.Master == null || sender != SNet.Master.Lookup) return;
        if (ById.TryGetValue((m.Thrower, m.ThrowId), out var unit)) LocalPilot.Hit(unit, m);
    }

    // ---- frame ------------------------------------------------------------------------------------------------

    /// <summary>Every frame, with the frame's time and whether this peer hosts as the plugin read them once.</summary>
    internal static void Tick(float now, bool isHost)
    {
        if (isHost) HostTick(now);
        if (Units.Count > 0)
        {
            for (var i = 0; i < Units.Count; i++)
            {
                var visual = Units[i].Visual;
                if (visual == null) continue;
                try { visual.Tick(now); }
                catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit presentation failed and was removed: " + error); visual.Destroy(); Units[i].Visual = null; }
            }
        }
        if (!isHost) ClientExpire(now);
        BarrierProjectiles.Tick();
        FrameRockets.Tick(now);
        LocalPilot.Tick(now);
    }

    internal static void LateTick(float now)
    {
        for (var i = 0; i < Units.Count; i++) Units[i].Visual?.LateTick(now);
        LocalPilot.LateTick(now);
    }

    /// <summary>A body's hands were just put on its item's grips (<see cref="PoseAfterIK"/>): the suit that body wears copies it again.</summary>
    internal static void HandsSolved(IntPtr syncIK)
    {
        for (var i = 0; i < Units.Count; i++) Units[i].Visual?.HandsSolved(syncIK);
    }

    private static void HostTick(float now)
    {
        for (var i = Units.Count - 1; i >= 0; i--)
        {
            var unit = Units[i];
            var r = unit.Record;
            if (FrameRules.Tick(r, now, out var remove))
            {
                if (!r.HasPilot) ClearHostPilot(unit);
                Send(unit, FrameEjection.None);
            }
            if (remove) { Remove(i); continue; }
            if (r.State != FrameState.Destroyed && FrameRules.RegenShield(r, now, Time.deltaTime))
            {
                if (r.Shield >= r.Spec.Shield!.Capacity || r.ShieldUp != unit.SentShieldUp) Send(unit, FrameEjection.None);
                else unit.Dirty = true;
            }
            if (r.HasPilot) HostWatchPilot(unit, now);
            if (unit.Dirty && now >= unit.NextSend) Send(unit, FrameEjection.None);
            else if (unit.RetainPending && now >= unit.NextSend)
            {
                // The carrier's state table was full for the last state: send it again until it is kept.
                r.Sequence++;
                Send(unit, FrameEjection.None);
            }
        }
    }

    /// <summary>Where an empty suit stands when its pilot lets go at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): the
    /// solid floor below, so a pilot who leaves, is thrown out or goes down in mid-air never leaves the suit hanging (the floor under the
    /// pilot's feet when there is none within reach below).</summary>
    private static float GroundY(float x, float y, float z)
        => ForgeCore.Level.FloorBelow(new Vector3(x, y + 0.5f, z), 60f, out var hit) ? hit.point.y : y;

    /// <summary>Host: forced exits (pilot down or gone), the suit's place while worn (the seat behind the pilot's feet; the suit
    /// does not move while being boarded or left), and footstep noise.</summary>
    private static void HostWatchPilot(FrameUnit unit, float now)
    {
        var r = unit.Record;
        var agent = HostPilotOf(unit);
        if (agent == null)
        {
            FrameRules.Eject(r, r.X, GroundY(r.X, r.Y, r.Z), r.Z, r.Yaw);
            ClearHostPilot(unit);
            Send(unit, FrameEjection.PilotLeft);
            return;
        }
        var feet = agent.Position;
        if (r.State == FrameState.Piloted)
        {
            var yaw = Yaw(agent);
            FrameSequence.Ahead(feet.x, feet.z, yaw, -FrameSequence.CockpitBack, out var seatX, out var seatZ);
            r.X = seatX; r.Y = feet.y; r.Z = seatZ; r.Yaw = yaw;
        }
        if (IsDown(agent))
        {
            FrameRules.Eject(r, r.X, GroundY(r.X, r.Y, r.Z), r.Z, r.Yaw);
            ClearHostPilot(unit);
            Send(unit, FrameEjection.PilotDown);
            return;
        }
        // Boarding or leaving, the game took the pilot (a grab, a warp, a dimension change): the sequence ends with the suit empty
        // where it stands. A worn suit follows its pilot instead (above), wherever the game puts it.
        var seized = IsSeized(agent);
        if (FrameRules.Interrupted(r, seized, feet.x, feet.y, feet.z))
        {
            FrameRules.Eject(r, r.X, r.Y, r.Z, r.Yaw);
            ClearHostPilot(unit);
            Send(unit, FrameEjection.PilotTaken);
            return;
        }
        if (r.State != FrameState.Piloted) return;
        if (FrameRules.BarrierUp(r) && now >= unit.NextRepel) { unit.NextRepel = now + FrameRepel.Interval; FrameRepel.Host(unit, agent, now); }
    }

    /// <summary>Clients drop a wreck when its time is up (the host removes its own record at the same moment).</summary>
    private static void ClientExpire(float now)
    {
        for (var i = Units.Count - 1; i >= 0; i--)
            if (Units[i].Record.State == FrameState.Destroyed && now >= Units[i].Record.StateEndsAt) Remove(i);
    }

    private static void Remove(int index)
    {
        var unit = Units[index];
        unit.Visual?.Destroy();
        unit.Visual = null;
        ClearHostPilot(unit);
        Units.RemoveAt(index);
        ById.Remove((unit.Record.Thrower, unit.Record.ThrowId));
        if (ForgeCore.Session.IsHost) OrdnanceApi.ClearState(FramesPayload.Type, unit.Record.Thrower, unit.Record.ThrowId);
    }

    // ---- level lifecycle --------------------------------------------------------------------------------------

    /// <summary>The level is entered, after the carrier (whose host published the checkpoint copy of a level without suits).</summary>
    internal static void LevelEntered()
    {
        _inLevel = true;
        AuthorityChanged();
    }

    /// <summary>The session's host changed or a migration recalled the same host, after the carrier dropped the states the old host
    /// kept (also when entering a level): the host sends every suit's state again.</summary>
    internal static void AuthorityChanged()
    {
        if (!_inLevel) return;
        foreach (var unit in Units) ClearHostPilot(unit);
        Republish();
    }

    /// <summary>Host: every suit's state again, into the carrier's state table.</summary>
    private static void Republish()
    {
        if (!ForgeCore.Session.IsHost) return;
        foreach (var unit in Units) Send(unit, FrameEjection.None);
    }

    internal static void LevelCleanup()
    {
        _inLevel = false;
        _sequenceFloor = 0;
        // Every step stands alone: a failure (an object the unload already destroyed) is logged and the rest still runs.
        Step("local pilot", LocalPilot.LevelCleanup);
        for (var i = Units.Count - 1; i >= 0; i--)
        {
            var visual = Units[i].Visual;
            if (visual != null) Step("suit visual", visual.Destroy);
            Units[i].Visual = null;
        }
        Units.Clear();
        ById.Clear();
        Pending.Clear();
        Step("repel", FrameRepel.Clear);
        Step("barrier-projectiles", BarrierProjectiles.Clear);
        Step("rockets", FrameRockets.Clear);
        Step("audio", FrameAudio.Clear);
    }

    private static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit level cleanup step '" + what + "' failed: " + error); }
    }

    /// <summary>Host: the suits as they stand at the checkpoint (a worn suit's record already holds its place around its pilot), as
    /// the suits' part of the mod's one checkpoint copy, which the carrier publishes and ForgeCore keeps on every peer, a player who
    /// joins later and a later host included.</summary>
    internal static byte[] CaptureCheckpoint()
    {
        var records = new List<FrameRecord>();
        foreach (var unit in Units) records.Add(unit.Record);
        return FrameCheckpoint.Capture(records, Now);
    }

    /// <summary>Every peer rebuilds exactly the saved population from the checkpoint copy, including suits destroyed since the save,
    /// after the carrier dropped the states it kept. The host publishes fresh records; clients also restore locally regardless of
    /// callback/packet arrival order.</summary>
    internal static void CheckpointReloaded()
    {
        var now = Now;
        FrameRockets.Clear();
        LocalPilot.Release("checkpoint reload");
        MechGun.AfterReload(now);
        var top = _sequenceFloor;
        foreach (var unit in Units) top = Math.Max(top, unit.Record.Sequence);
        // Resolve all saved specifications before replacing anything, so unavailable definitions fail explicitly.
        var restored = FrameCheckpoint.Restore(OrdnanceSession.CheckpointContent, Specs, now, top);
        foreach (var unit in Units)
        {
            ClearHostPilot(unit);
            unit.Visual?.Destroy();
            if (ForgeCore.Session.IsHost) OrdnanceApi.ClearState(FramesPayload.Type, unit.Record.Thrower, unit.Record.ThrowId);
        }
        Units.Clear();
        ById.Clear();
        Pending.Clear();
        FrameRepel.Clear();
        BarrierProjectiles.Clear();
        foreach (var record in restored)
        {
            if (!ForgeCore.Session.IsHost) record.Sequence = 0; // Accept the host's first restored state on any callback ordering.
            var unit = new FrameUnit(record);
            Units.Add(unit);
            ById.Add((record.Thrower, record.ThrowId), unit);
            unit.Visual = FrameVisual.Create(unit, true);
        }
        Republish();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static PlayerAgent? HostPilotOf(FrameUnit unit)
    {
        var lookup = unit.Record.Pilot;
        if (lookup == 0) { ClearHostPilot(unit); return null; }
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) { ClearHostPilot(unit); return null; }
        var cached = unit.HostPilot;
        var index = unit.HostPilotIndex;
        if (ForgeCore.Level.IsPlayer(cached, lookup) && index >= 0 && index < agents.Count)
        {
            var current = agents[index];
            if (ForgeCore.Level.IsPlayer(current, lookup) && current.Pointer == cached!.Pointer) return cached;
        }
        var resolved = ForgeCore.Level.PlayerByLookup(lookup, out index);
        unit.HostPilot = resolved;
        unit.HostPilotIndex = resolved == null ? -1 : index;
        return resolved;
    }

    private static void ClearHostPilot(FrameUnit unit)
    {
        unit.HostPilot = null;
        unit.HostPilotIndex = -1;
    }

    internal static bool IsDown(PlayerAgent agent)
    {
        if (!agent.Alive) return true;
        var locomotion = agent.Locomotion;
        return locomotion != null && locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Downed;
    }

    /// <summary>Whether the game holds the player: a snatcher (<c>GrabbedByPouncer</c>, entered from <c>PouncerBehaviour.ProcessHelding</c>), a
    /// tank's tentacle (<c>GrabbedByTank</c>), a trap (<c>GrabbedByTrap</c>), a stun (<c>Stunned</c>), or its controls taken
    /// (<c>StandStill</c>, which <c>PlayerAgent.RequestToggleControlsEnabled(false)</c> sets, as the snatcher does while it swallows).</summary>
    internal static bool IsSeized(PlayerAgent agent)
    {
        var locomotion = agent.Locomotion;
        return locomotion != null && locomotion.m_currentStateEnum is PlayerLocomotion.PLOC_State.GrabbedByPouncer
            or PlayerLocomotion.PLOC_State.GrabbedByTank or PlayerLocomotion.PLOC_State.GrabbedByTrap
            or PlayerLocomotion.PLOC_State.Stunned or PlayerLocomotion.PLOC_State.StandStill;
    }

    internal static float Yaw(PlayerAgent agent)
    {
        var forward = agent.Rotation * Vec.Forward;
        forward.y = 0f;
        return Vec.SqrLength(forward) > 1e-4f ? Quaternion.LookRotation(forward).eulerAngles.y : 0f;
    }

}
