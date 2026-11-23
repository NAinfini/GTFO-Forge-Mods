using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;
using Gear;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// Weapon facts only the machine holding the weapon can read, carried to the host so that a client's weapon triggers the same rows as the
/// host's own. Who publishes is decided in one place (<see cref="RouteOf"/>): the host publishes its own and its bots' items at
/// the hook, a client sends its own items here, and the host's copy of a client's item publishes nothing at the hook, so one
/// action is one fact whether or not the native body also runs on the host's copy.
///
/// The cost is the row's: a record is only written while a loaded plan listens to its row. Shots and impacts wait for the next
/// kernel tick (one fixed step, so the pellets and impacts of one frame share a message); every other record is an edge and goes at
/// once, carrying what waited ahead of it, which keeps the sender's order.
///
/// The host answers each record on its own: it checks the sender's player, the item in the named slot, the reported point against the
/// sender's replicated eye and the target's replicated body, and that no world geometry stands between the shooter's reported eye and
/// the point. A refused record is logged by name and never retargeted; the other records of the batch still stand.
/// </summary>
internal sealed class ClientWeaponReport : IDisposable
{
    internal const string Endpoint = "forge.weapon.client_facts";
    private const string Resource = "b";
    /// <summary>Farther than any corridor a gun reaches across in a level, unless the weapon's own `MaxRayDist` reaches farther: a report beyond it does not describe this sender's shot.</summary>
    internal const double MaximumReach = 200d;
    /// <summary>How far a reported eye may stand from the sender's replicated one: a sprint over half a second of lag, doubled.</summary>
    internal const double OriginDrift = 12d;
    /// <summary>How far a reported point may stand from the target's replicated body: lag plus the largest enemy's reach.</summary>
    internal const double TargetDrift = 15d;
    /// <summary>The point itself lies on a surface, so the sight line stops this short of it.</summary>
    private const float SightBackoff = 0.25f;
    /// <summary>How often a charge's progress is reported, by a client and by the host for its own weapons alike.</summary>
    internal const float ProgressSeconds = 0.1f;
    /// <summary>Under the channel's 512-byte payload after its envelope and this resource id.</summary>
    private const int PayloadLimit = 480;
    /// <summary>The largest count a batch's answer reports. Every record of the batch is applied; only the number in the
    /// answer is capped, because the answer's count column is bounded and a batch of short records can hold more than this.</summary>
    private const int ReportedCountLimit = 128;
    /// <summary>The bit of a shot's outcome byte that says its eye follows inline rather than in the shot's origin record.</summary>
    private const byte InlineEye = 0x80;

    internal enum Kind : byte
    {
        Shot = 1, Impact, Aim, Charge, Burst, DryFire, Push, Reload, UseStarted, UseEnded, UseFailed, Throw, Glue, Flashlight, MeleeCharged, UseCompleted, Origin
    }

    private enum Route { Publish, Report, Drop }

    /// <summary>What a record's impact reached, in the terms two machines share: an agent's replicated key, or a map object's
    /// address and life in the map domain's own spelling (<see cref="ForgeMap.MapObjectWire"/>). The shooter's machine holds the
    /// reference it resolved; the host reads one stamped with the world it serves the record in.</summary>
    internal readonly record struct Reached(ushort Agent, short Limb, EntityReference? MapObject)
    {
        internal void Write(BinaryWriter writer)
        {
            writer.Write((byte)(Agent != 0 ? 1 : MapObject != null ? 2 : 0));
            if (Agent != 0) { writer.Write(Agent); writer.Write(Limb); }
            else if (MapObject != null) ForgeMap.MapObjectWire.Write(writer, MapObject);
        }

        internal static Reached Read(BinaryReader reader, long world) => reader.ReadByte() switch
        {
            0 => default,
            1 => new(reader.ReadUInt16(), reader.ReadInt16(), null),
            2 => new(0, 0, ForgeMap.MapObjectWire.Read(reader, world)),
            _ => throw new FormatException("unknown target form")
        };
    }

    internal static ClientWeaponReport? Current { get; private set; }

    private readonly WeaponNativeSession _session;
    private readonly IForgeEngine _kernel;
    private readonly WeaponLog _log;
    private readonly IDisposable _endpoint;
    private readonly RuntimeSubscriptionGate? _shotGate, _hitGate, _contextGate, _aimGate, _chargeGate, _burstStartedGate, _burstEndedGate,
        _dryFireGate, _pushGate, _reloadStartedGate, _reloadTransferredGate, _reloadCompletedGate, _useStartedGate, _useCompletedGate, _useFailedGate,
        _throwGate, _firedGate, _flashlightGate, _meleeHitGate;
    private readonly MemoryStream _buffer = new();
    private readonly BinaryWriter _writer;
    private readonly Dictionary<IntPtr, bool> _lights = new();
    private readonly Dictionary<IntPtr, (bool Charged, long At)> _meleeCharged = new();
    /// <summary>Host side: each sender's last reported shot and the eye it was fired from.</summary>
    private readonly Dictionary<IntPtr, (ushort Shot, Vector3 Eye)> _origins = new();
    /// <summary>How long a swing report waits for its damage packet; one that outlives it is dropped, so a swing that never produced a
    /// hit cannot mark the next one.</summary>
    private const long MeleeChargedLifetimeMs = 2000;
    private long _world = -1;
    private float _progressAt;
    private bool _notSentReported;
    // The firing body in progress on this client.
    private bool _open, _firstTaken, _firstThrough, _anyTarget, _originSent;
    private byte _slot;
    private ushort _shot;
    private int _hits;
    private Vector3 _eye, _firstPoint, _firstNormal;
    private Reached _first;
    // The reload in progress on this client, and the refusal this frame already told.
    private IntPtr _reloading;
    private int _reloadBaseline, _refusalFrame = -1;

    internal ClientWeaponReport(WeaponNativeSession session, IForgeEngine kernel, WeaponLog log)
    {
        _session = session; _kernel = kernel; _log = log;
        _writer = new BinaryWriter(_buffer, Encoding.UTF8);
        var adapter = session.Adapter;
        _shotGate = adapter.GateOf(CombatPrimitiveContract.ShotResolvedBinding);
        _hitGate = adapter.GateOf(ModuleDefinition.HitCandidateBinding);
        _contextGate = adapter.GateOf(HitContextContract.BindingId);
        _aimGate = adapter.GateOf(WeaponStateTriggerContract.AimStateBinding);
        _chargeGate = adapter.GateOf(WeaponStateTriggerContract.ChargeStateBinding);
        _burstStartedGate = adapter.GateOf(WeaponBurstContract.BurstStartedBinding);
        _burstEndedGate = adapter.GateOf(WeaponBurstContract.BurstEndedBinding);
        _dryFireGate = adapter.GateOf(WeaponBurstContract.DryFireBinding);
        _pushGate = adapter.GateOf(EquipmentEventContract.MeleePushBinding);
        _reloadStartedGate = adapter.GateOf(ReloadInventoryContract.ReloadStartedBinding);
        _reloadTransferredGate = adapter.GateOf(ReloadInventoryContract.ReloadTransferredBinding);
        _reloadCompletedGate = adapter.GateOf(ReloadInventoryContract.ReloadCompletedBinding);
        _useStartedGate = adapter.GateOf(ReloadInventoryContract.UseStartedBinding);
        _useCompletedGate = adapter.GateOf(ReloadInventoryContract.UseCompletedBinding);
        _useFailedGate = adapter.GateOf(ReloadInventoryContract.UseFailedBinding);
        _throwGate = adapter.GateOf(EquipmentEventContract.ThrowReleasedBinding);
        _firedGate = adapter.GateOf(WeaponDeployableFactsContract.FiredBinding);
        _flashlightGate = adapter.GateOf(EquipmentEventContract.FlashlightBinding);
        _meleeHitGate = adapter.GateOf(WeaponMeleeHitContract.MeleeHitBinding);
        _endpoint = DomainCommands.Register(Endpoint, Execute);
        Current = this;
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _endpoint.Dispose();
    }

    /// <summary>This machine holds no authority and reports its own items instead.</summary>
    internal static bool Reporting => !Session.IsHost;

    private static Route RouteOf(PlayerAgent? owner)
    {
        var player = owner == null ? null : owner.Owner;
        if (player == null) return Session.IsHost ? Route.Publish : Route.Drop;
        if (player.IsLocal) return Session.IsHost ? Route.Publish : Route.Report;
        return Session.IsHost && player.IsBot ? Route.Publish : Route.Drop;
    }

    private static Route RouteOf(Item? item) => RouteOf(item == null ? null : item.Owner);

    /// <summary>True when the hook of this item must not publish: its facts travel through a report, or belong to another machine.</summary>
    internal static bool NotOurs(Item? item) => RouteOf(item) != Route.Publish;

    private static bool Closed(RuntimeSubscriptionGate? gate) => gate is { HasSubscribers: false };

    // -------------------------------------------------------------------------------------------------------
    // The client's side. Each member answers true when the hook must not publish.
    // -------------------------------------------------------------------------------------------------------

    internal void BeginShot(Weapon? weapon)
    {
        _open = false;
        if (weapon == null || RouteOf(weapon) != Route.Report || Closed(_shotGate) && Closed(_hitGate) && Closed(_contextGate)) return;
        if (Slot(weapon) is not { } slot) return;
        _open = true; _firstTaken = false; _firstThrough = false; _anyTarget = false; _originSent = false; _hits = 0; _first = default;
        var shooter = weapon.Owner!;
        _slot = slot; _shot++; _eye = shooter.EyePosition;
        _shooter = Closed(_shotGate) ? null : _session.Adapter.OwnerOf(shooter);
    }

    // The shooting player's own reference, for the shot's early copy on this machine (EndShot).
    private EntityReference? _shooter;

    /// <summary>One impact of the shot. The first one is the shot's reading, as on the host; every one is a candidate.</summary>
    internal bool Hit(Weapon.WeaponHitData? data)
    {
        if (data == null) return false;
        var route = RouteOf(data.owner);
        if (route == Route.Publish) return false;
        if (route == Route.Drop || !_open) return true;
        var ray = data.rayHit;
        var reached = ReachedBy(ray.collider);
        var through = _session.Combat.ThroughWall();
        _hits++;
        if (reached != default) _anyTarget = true;
        if (!_firstTaken)
        {
            _firstTaken = true; _first = reached; _firstThrough = through;
            _firstPoint = ray.point; _firstNormal = ray.normal;
        }
        if (Closed(_hitGate) && Closed(_contextGate)) return true;
        Origin();
        var start = Head(Kind.Impact, _slot);
        _writer.Write(_shot);
        reached.Write(_writer);
        Write(ray.point); Write(ray.normal);
        _writer.Write(through);
        _writer.Write(data.damageFalloff.x); _writer.Write(data.damageFalloff.y);
        Close(start, edge: false);
        return true;
    }

    /// <summary>The tail of the firing body: what the shot reached, once. A suppressed shot's rays were answered as misses and report nothing.
    /// A shot whose impacts sent no origin carries its eye itself (<see cref="InlineEye"/> on the outcome byte); any other names its number.</summary>
    internal void EndShot(bool ran)
    {
        if (!_open) return;
        _open = false;
        if (!ran || Closed(_shotGate) || WeaponShotRules.Current is { ShotReplaced: true }) return;
        var start = Head(Kind.Shot, _slot);
        _writer.Write((ushort)Math.Min(_hits, ushort.MaxValue));
        _writer.Write((byte)((_anyTarget ? 2 : _hits > 0 ? 1 : 0) | (_originSent ? 0 : InlineEye)));
        // The shot's number always travels: the host hands it back with what the shot shows this player (see below).
        _writer.Write(_shot);
        if (!_originSent) Write(_eye);
        if (_hits > 0)
        {
            _first.Write(_writer);
            Write(_firstPoint); Write(_firstNormal);
            _writer.Write(_firstThrough);
        }
        Close(start, edge: false);
        // A shot whose first impact is a map object shows this player its result now; the host's order for it is then
        // answered, not replayed. A first impact on an agent is the host's to judge and is never shown early.
        if (_hits > 0 && _first.Agent == 0 && _first.MapObject is { } target && _shooter is { } shooter)
            _session.CombatFacts.ShotPredicted(shooter, (byte)(_anyTarget ? 2 : 1), target, Math.Min(_hits, ushort.MaxValue),
                _firstPoint, _firstNormal, ShotToken(_shot));
    }

    /// <summary>The number a shot's early copy is kept under on the shooter's machine and named by on the host; never zero.</summary>
    private static long ShotToken(ushort shot) => shot + 1L;

    /// <summary>The shooter's eye and the shot's number travel once per shot, ahead of its first impact; the host keeps them for the
    /// shot's later records, which name the shot's number: one whose origin did not arrive (its batch was not sent) is refused, never
    /// judged from an earlier shot's eye.</summary>
    private void Origin()
    {
        if (_originSent) return;
        _originSent = true;
        var start = Head(Kind.Origin, _slot);
        _writer.Write(_shot);
        Write(_eye);
        Close(start, edge: false);
    }

    internal bool Aim(Item? item, bool aiming) => Edge(item, _aimGate, Kind.Aim, aiming ? (byte)1 : (byte)0);
    internal bool Burst(Item? weapon, bool started) => Edge(weapon, started ? _burstStartedGate : _burstEndedGate, Kind.Burst, started ? (byte)1 : (byte)0);
    internal bool DryFire(Item? weapon) => Edge(weapon, _dryFireGate, Kind.DryFire, 0);
    internal bool UseStarted(Item? item, bool tracked = true) => Edge(item, _useStartedGate, Kind.UseStarted, tracked ? (byte)0 : (byte)1);
    internal bool UseEnded(Item? item) => Edge(item, _useStartedGate, Kind.UseEnded, 0);
    internal bool UseCompleted(Item? item) => Edge(item, _useCompletedGate, Kind.UseCompleted, 0);
    internal bool Glue(Item? tool) => Edge(tool, _firedGate, Kind.Glue, 0);
    /// <summary>A hit of the local swing: only whether it was charged travels, the host reads everything else off the damage packet.</summary>
    internal bool MeleeCharged(Item? weapon, bool charged) => Edge(weapon, _meleeHitGate, Kind.MeleeCharged, charged ? (byte)1 : (byte)0);

    internal bool Flashlight(Item? item, bool on)
    {
        if (RouteOf(item) == Route.Report)
        {
            if (_lights.TryGetValue(item!.Pointer, out var was) && was == on) return true;
            _lights[item.Pointer] = on;
        }
        return Edge(item, _flashlightGate, Kind.Flashlight, on ? (byte)1 : (byte)0);
    }

    internal bool UseFailed(Item? item, string reason, int frame)
    {
        if (RouteOf(item) == Route.Report)
        {
            if (frame == _refusalFrame) return true;
            _refusalFrame = frame;
        }
        return Edge(item, _useFailedGate, Kind.UseFailed, UseFailureReads.Member(reason));
    }

    internal bool Throw(Item? item, Vector3 velocity)
    {
        var route = RouteOf(item);
        if (route == Route.Publish) return false;
        if (route == Route.Report && !Closed(_throwGate) && Slot(item!) is { } slot)
        {
            var start = Head(Kind.Throw, slot);
            Write(velocity);
            Close(start, edge: true);
        }
        return true;
    }

    /// <summary>One charge phase. Progress runs every frame on the holder, so it travels at most once per window.</summary>
    internal bool Charge(Item? weapon, int phase, double? ratio, bool progress)
    {
        var route = RouteOf(weapon);
        if (route == Route.Publish) return false;
        if (route == Route.Report && !Closed(_chargeGate) && Slot(weapon!) is { } slot)
        {
            if (progress && Clock.Time < _progressAt) return true;
            if (progress) _progressAt = Clock.Time + ProgressSeconds;
            var start = Head(Kind.Charge, slot);
            _writer.Write((byte)phase);
            _writer.Write(ratio is { } value ? (float)value : float.NaN);
            Close(start, edge: !progress);
        }
        return true;
    }

    internal bool Push(Item? weapon, byte phase, GameObject? hit)
    {
        var route = RouteOf(weapon);
        if (route == Route.Publish) return false;
        if (route == Route.Report && !Closed(_pushGate) && Slot(weapon!) is { } slot)
        {
            var start = Head(Kind.Push, slot);
            _writer.Write(phase);
            var body = hit == null ? null : EquipmentNativeAdapter.BodyOf(hit, out _);
            _writer.Write(body == null ? (ushort)0 : EquipmentNativeAdapter.KeyOf(body));
            Close(start, edge: true);
        }
        return true;
    }

    /// <summary>The item's own reload flag on the client: a rising edge opens the reload and takes the magazine as its baseline, the falling
    /// edge reports what entered it. A reload that moved nothing reports an amount of zero, which the host closes without a fact.</summary>
    internal bool ReloadFlag(ItemEquippable? item, bool reloading)
    {
        var route = RouteOf(item);
        if (route == Route.Publish) return false;
        if (route == Route.Drop) return true;
        var pointer = item!.Pointer;
        if (reloading)
        {
            if (_reloading == pointer) return true;
            _reloading = pointer;
            _reloadBaseline = item.GetCurrentClip();
            WriteReload(item, 0, 0);
        }
        else if (_reloading == pointer)
        {
            _reloading = IntPtr.Zero;
            WriteReload(item, 1, Math.Max(0, item.GetCurrentClip() - _reloadBaseline));
        }
        return true;
    }

    private void WriteReload(Item item, byte stage, int amount)
    {
        if (Closed(_reloadStartedGate) && Closed(_reloadTransferredGate) && Closed(_reloadCompletedGate) || Slot(item) is not { } slot) return;
        var start = Head(Kind.Reload, slot);
        _writer.Write(stage);
        _writer.Write(amount);
        Close(start, edge: true);
    }

    private bool Edge(Item? item, RuntimeSubscriptionGate? gate, Kind kind, byte value)
    {
        var route = RouteOf(item);
        if (route == Route.Publish) return false;
        if (route == Route.Report && !Closed(gate) && Slot(item!) is { } slot)
        {
            var start = Head(kind, slot);
            _writer.Write(value);
            Close(start, edge: true);
        }
        return true;
    }

    private byte? Slot(Item item) => _kernel.StartupState == RuntimeStartupState.Ready ? EquipmentNativeAdapter.SlotOf(item) : null;

    private Reached ReachedBy(Collider? collider)
    {
        if (collider == null) return default;
        var climb = EquipmentNativeAdapter.Climb(collider);
        if (climb.Agent is { } body) return new(EquipmentNativeAdapter.KeyOf(body), (short)climb.Part, null);
        return new(0, 0, _kernel.ResolveEntityInstance(EntityKinds.MapObject, collider));
    }

    private void Write(Vector3 value) { _writer.Write(value.x); _writer.Write(value.y); _writer.Write(value.z); }

    private int Head(Kind kind, byte slot)
    {
        if (_world != _kernel.WorldEpoch) { _world = _kernel.WorldEpoch; Forget(); }
        _writer.Flush();
        var start = (int)_buffer.Length;
        _writer.Write((byte)kind);
        _writer.Write(slot);
        return start;
    }

    /// <summary>One record is complete. A batch past the limit goes without it, so a record never splits across two messages.</summary>
    private void Close(int start, bool edge)
    {
        _writer.Flush();
        if (_buffer.Length > PayloadLimit && start > 0)
        {
            var bytes = _buffer.GetBuffer();
            var length = (int)_buffer.Length;
            Send(bytes.AsSpan(0, start).ToArray());
            _buffer.SetLength(0);
            _writer.Write(bytes, start, length - start);
            _writer.Flush();
            start = 0;
        }
        if (edge) Flush();
    }

    /// <summary>Every advanced tick: the waiting batch goes.</summary>
    internal void Tick()
    {
        if (_buffer.Length == 0) return;
        if (_world != _kernel.WorldEpoch) { _world = _kernel.WorldEpoch; Forget(); return; }
        Flush();
    }

    private void Flush()
    {
        _writer.Flush();
        if (_buffer.Length == 0) return;
        Send(_buffer.ToArray());
        _buffer.SetLength(0);
    }

    /// <summary>A record that alone is past the command limit (a map object with a long authored address) goes as a batch of its own
    /// (<see cref="Close"/>) and is not sent: it is reported by code, never thrown inside a weapon hook.</summary>
    private void Send(byte[] payload)
    {
        var reason = payload.Length > DomainCommandRouting.MaximumPayloadBytes ? ForgeEngine.Network.NetworkCodes.PayloadTooLarge
            : DomainCommands.Send(Endpoint, Resource, 0, payload) || !DomainCommands.HostUnreachable ? null : "client-report-not-sent";
        if (reason == null || _notSentReported) return;
        _notSentReported = true;
        _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, reason);
    }

    private void Forget()
    {
        _buffer.SetLength(0);
        _lights.Clear(); _meleeCharged.Clear(); _origins.Clear();
        _reloading = IntPtr.Zero; _open = false; _notSentReported = false;
    }

    // -------------------------------------------------------------------------------------------------------
    // The host's side.
    // -------------------------------------------------------------------------------------------------------

    private DomainCommandResult Refused(string code)
    {
        Note(code);
        return new("rejected", "none", code, "", 0);
    }

    private void Note(string code)
        => _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.FactSkipped, code);

    internal DomainCommandResult Execute(DomainCommandRequest request)
    {
        if (!_session.Adapter.Authoritative()) return Refused("not-authoritative");
        if (request.WorldEpoch != _kernel.WorldEpoch) return Refused("stale-world");
        if (_world != request.WorldEpoch) { _world = request.WorldEpoch; Forget(); }
        var sender = Level.PlayerByLookup(request.SenderSession);
        if (sender == null || _session.Adapter.OwnerOf(sender) is not { } source) return Refused("sender-not-player");
        if (request.Payload.Length == 0) return Refused("invalid-payload");
        int published = 0;
        string? firstCode = null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(request.Payload), Encoding.UTF8);
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var code = Apply(reader, sender, source, ref published);
                if (code == null) continue;
                firstCode ??= code;
                Note(code);
            }
        }
        catch (Exception error) when (error is EndOfStreamException or IOException or FormatException or ArgumentException)
        {
            firstCode ??= "invalid-payload";
            Note("invalid-payload");
        }
        published = Math.Min(published, ReportedCountLimit);
        if (firstCode == null) return published > 0 ? new("succeeded", "confirmed", "ok", "", published) : new("succeeded", "none", "not-observed", "", 0);
        return published > 0 ? new("partial", "confirmed", firstCode, "", published) : new("rejected", "none", firstCode, "", 0);
    }

    /// <summary>One record, read whole before it is judged, so a refusal leaves the next record in place. Null when it stands.</summary>
    private string? Apply(BinaryReader reader, PlayerAgent sender, EntityReference source, ref int published)
    {
        var kind = (Kind)reader.ReadByte();
        var slot = reader.ReadByte();
        switch (kind)
        {
            case Kind.Shot: return ApplyShot(reader, sender, source, slot, ref published);
            case Kind.Impact: return ApplyImpact(reader, sender, source, slot, ref published);
            case Kind.Charge:
                {
                    var phase = reader.ReadByte();
                    var ratio = reader.ReadSingle();
                    if (Held(sender, slot) is not { } item) return "no-slot-gear";
                    _session.CombatFacts.ChargePublished(item, phase, float.IsFinite(ratio) ? ratio : null);
                    break;
                }
            case Kind.Throw:
                {
                    var velocity = ReadVector(reader);
                    if (Held(sender, slot) is not { } item) return "no-slot-gear";
                    EquipmentEventFacts.Current?.ThrowReleased(item, velocity);
                    break;
                }
            case Kind.Push:
                {
                    var phase = reader.ReadByte();
                    var key = reader.ReadUInt16();
                    if (Held(sender, slot) is not { } item) return "no-slot-gear";
                    EntityReference? target = null;
                    if (key != 0 && (EquipmentNativeAdapter.AgentBy(key) is not { } agent || (target = _session.Adapter.ImpactOn(agent, -1).Target) == null))
                        return "stale-target";
                    EquipmentEventFacts.Current?.PushReported(item, phase, target);
                    break;
                }
            case Kind.Reload:
                {
                    var stage = reader.ReadByte();
                    var amount = reader.ReadInt32();
                    if (amount < 0) return "invalid-payload";
                    if (Held(sender, slot) is not { } item) return "no-slot-gear";
                    ReloadInventoryHooks.Reload?.ReportedBy(item, stage == 0, amount);
                    break;
                }
            case Kind.MeleeCharged:
                _meleeCharged[sender.Pointer] = (reader.ReadByte() != 0, Environment.TickCount64);
                return null;
            case Kind.Origin:
                {
                    var shot = reader.ReadUInt16();
                    var eye = ReadVector(reader);
                    if (!ForgeCore.Vec.Finite(eye)) { _origins.Remove(sender.Pointer); return "invalid-payload"; }
                    _origins[sender.Pointer] = (shot, eye);
                    return null;
                }
            case Kind.Aim or Kind.Burst or Kind.DryFire or Kind.UseStarted or Kind.UseEnded or Kind.UseCompleted or Kind.UseFailed or Kind.Glue or Kind.Flashlight:
                {
                    var value = reader.ReadByte();
                    if (Held(sender, slot) is not { } item) return "no-slot-gear";
                    ApplyEdge(kind, item, value, source);
                    break;
                }
            default: throw new FormatException("unknown record");
        }
        published++;
        return null;
    }

    // The use rows name the reporting player as their actor: the host found the item in that player's backpack.
    private void ApplyEdge(Kind kind, ItemEquippable item, byte value, EntityReference sender)
    {
        switch (kind)
        {
            case Kind.Aim: _session.CombatFacts.AimReported(item, value != 0); break;
            case Kind.Burst: _session.Attack.Burst(item.TryCast<BulletWeapon>(), value != 0); break;
            case Kind.DryFire: _session.Attack.DryFire(item.TryCast<BulletWeapon>()); break;
            case Kind.UseStarted: ReloadInventoryHooks.Inventory?.UseStarted(item, value == 0, sender); break;
            case Kind.UseEnded: ReloadInventoryHooks.Inventory?.UseEnded(item); break;
            case Kind.UseCompleted: ReloadInventoryHooks.Inventory?.UseCompleted(item, sender); break;
            case Kind.UseFailed:
                ReloadInventoryHooks.Inventory?.Refused(item, value switch
                {
                    0 => UseFailureReads.Empty,
                    1 => UseFailureReads.Cooldown,
                    2 => UseFailureReads.NotReady,
                    _ => UseFailureReads.NoNeed
                }, reporter: sender);
                break;
            case Kind.Glue: WeaponDeployableFacts.Current?.GlueFired(item); break;
            case Kind.Flashlight: EquipmentEventFacts.Current?.FlashlightSet(item, value != 0); break;
        }
    }

    /// <summary>Whether a client's melee hit was charged, told by the swing's own report and taken once by the hit's damage packet. The two
    /// travel on separate channels, so a packet that arrives first answers null, as a hit with no report does; a report older than
    /// <see cref="MeleeChargedLifetimeMs"/> answers null too.</summary>
    internal bool? TakeMeleeCharged(PlayerAgent player)
        => _meleeCharged.Remove(player.Pointer, out var report) && Environment.TickCount64 - report.At <= MeleeChargedLifetimeMs ? report.Charged : null;

    /// <summary>The equipment a client's settled damage came from: the packet names a gear category, and the item the sender holds is that
    /// weapon when the two agree. A category the held item does not match stays unattributed, never guessed from the backpack.</summary>
    internal EntityReference? HeldEquipment(PlayerAgent? sender, uint category)
    {
        if (sender == null || category == 0) return null;
        // Unity objects are tested with `==`, which sees a destroyed object; `?.` would read through it.
        var inventory = sender.Inventory;
        var held = inventory == null ? null : inventory.WieldedItem;
        if (held == null || !NotOurs(held)) return null;
        return held.GearCategoryData?.persistentID == category ? _session.Adapter.CurrentEntityOf(held) : null;
    }

    private ItemEquippable? Held(PlayerAgent sender, byte slot)
    {
        var item = EquipmentNativeAdapter.ItemIn(sender, slot);
        return item != null && _session.Adapter.CurrentEntityOf(item) != null ? item : null;
    }

    private string? ApplyShot(BinaryReader reader, PlayerAgent sender, EntityReference source, byte slot, ref int published)
    {
        var hits = reader.ReadUInt16();
        var flags = reader.ReadByte();
        var outcome = flags & ~InlineEye;
        var shot = reader.ReadUInt16();
        Vector3? eye = (flags & InlineEye) != 0 ? ReadVector(reader) : OriginOf(sender, shot);
        Reached reached = default;
        Vector3 point = default, normal = default;
        var through = false;
        if (hits > 0)
        {
            reached = Reached.Read(reader, _kernel.WorldEpoch);
            point = ReadVector(reader); normal = ReadVector(reader);
            through = reader.ReadBoolean();
        }
        if (outcome > 2 || (outcome == 0) != (hits == 0) || !ForgeCore.Vec.Finite(point) || !ForgeCore.Vec.Finite(normal)
            || eye is not { } from || !ForgeCore.Vec.Finite(from)) return "invalid-payload";
        if (Held(sender, slot) is not { } item || _session.Adapter.CurrentEntityOf(item) is not { } equipment) return "no-slot-gear";
        if (Distance(from, sender.EyePosition) > OriginDrift) return "origin-drift";
        EntityReference? target = null;
        if (hits > 0)
        {
            var code = Admit(from, point, reached, through, item, out var impact);
            if (code != null) return code;
            target = impact.Target;
        }
        if (_session.CombatFacts.ShotReported(source, equipment, (byte)outcome, target, hits, point, normal, ShotToken(shot))) published++;
        return null;
    }

    /// <summary>The eye of the sender's shot <paramref name="shot"/>: its origin record, which precedes the shot's other records. Null when
    /// the last origin that arrived is another shot's.</summary>
    private Vector3? OriginOf(PlayerAgent sender, ushort shot)
        => _origins.TryGetValue(sender.Pointer, out var origin) && origin.Shot == shot ? origin.Eye : null;

    /// <summary>An impact names its shot, not its eye: the eye is that shot's origin record.</summary>
    private string? ApplyImpact(BinaryReader reader, PlayerAgent sender, EntityReference source, byte slot, ref int published)
    {
        var shot = reader.ReadUInt16();
        var reached = Reached.Read(reader, _kernel.WorldEpoch);
        var point = ReadVector(reader);
        var normal = ReadVector(reader);
        var through = reader.ReadBoolean();
        float start = reader.ReadSingle(), end = reader.ReadSingle();
        if (!ForgeCore.Vec.Finite(point) || !ForgeCore.Vec.Finite(normal) || OriginOf(sender, shot) is not { } eye) return "invalid-payload";
        if (Held(sender, slot) is not { } item || _session.Adapter.CurrentEntityOf(item) is not { } equipment) return "no-slot-gear";
        if (Distance(eye, sender.EyePosition) > OriginDrift) return "origin-drift";
        var code = Admit(eye, point, reached, through, item, out var impact);
        if (code != null) return code;
        if (_session.Combat.HitReported(source, equipment, shot, impact, point, normal, eye, through, start, end)) published++;
        return null;
    }

    /// <summary>The checks a reported impact must pass, against what the host itself can read. Static geometry does not lag, so the sight line
    /// runs from the client's reported eye (itself checked against the replicated one) rather than from where the host last saw the sender.
    /// A piercing weapon's shot may legally reach past geometry: its claim is held against the host's copy of that weapon.</summary>
    private string? Admit(Vector3 eye, Vector3 point, Reached reached, bool through, ItemEquippable item, out EquipmentNativeAdapter.RayImpact impact)
    {
        impact = default;
        var reach = item.TryCast<Weapon>() is { } gun && float.IsFinite(gun.MaxRayDist) ? Math.Max(MaximumReach, gun.MaxRayDist) : MaximumReach;
        if (Distance(eye, point) > reach) return "out-of-range";
        if (reached.Agent != 0)
        {
            if (EquipmentNativeAdapter.AgentBy(reached.Agent) is not { } agent) return "stale-target";
            impact = _session.Adapter.ImpactOn(agent, reached.Limb);
            if (impact.Target == null) return "target-kind";
            if (Distance(point, agent.Position) > TargetDrift) return "target-drift";
        }
        else if (reached.MapObject is { } target)
        {
            // The address and the life the shooter saw, never the object's current one: a report of an object this world does
            // not hold, or of an earlier life, is refused, not retargeted.
            if (!_kernel.IsEntityCurrent(target)) return "stale-target";
            impact = new EquipmentNativeAdapter.RayImpact(target, IntPtr.Zero, -1);
        }
        if (through && item.ArchetypeData is { PiercingBullets: true }) return null;
        var length = Distance(eye, point);
        if (length <= SightBackoff) return null;
        var keep = (float)((length - SightBackoff) / length);
        var end = new Vector3(eye.x + (point.x - eye.x) * keep, eye.y + (point.y - eye.y) * keep, eye.z + (point.z - eye.z) * keep);
        return Physics.Linecast(eye, end, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore) ? "no-line-of-sight" : null;
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static double Distance(Vector3 a, Vector3 b)
    {
        double x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}
