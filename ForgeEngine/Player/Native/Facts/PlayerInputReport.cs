using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgePlayer.Native;

/// <summary>The local player's own events that only their machine reads: the fire button and key edges, a completed hold, a ping, a posture
/// change, a supply use, low health and a completed warp (the game moves a player only on the machine that simulates it). Only the host may publish, so a client reports them through the domain request channel, which
/// already carries the sender and the world; the host publishes them for the sender's own player. The report names no player. The hook
/// on the host stays silent for a client's player, so one event is one fact whichever native entries also reach the host. Each of these
/// is a rare edge, not a per-frame value, so each travels alone. The one exception is the local player's stamina, which the game does not
/// replicate: while a loaded plan reads it, a client reports it at a low fixed rate and only when it moved, and the host answers a
/// teammate's stamina read from the last report of this world, refusing one that has gone stale.</summary>
internal sealed class PlayerInputReport : IDisposable
{
    internal const string Endpoint = "forge.player.input";
    private const string Fire = "fire", Key = "key", HeldResource = "held", PingResource = "ping", MovementResource = "movement",
        Supply = "supply", LowHealthResource = "low_health", StaminaResource = "stamina";
    /// <summary>Seconds between a client's stamina samples, the change that makes one travel, the longest an unchanged value goes
    /// unreported, and how old a report may be and still answer a read on the host.</summary>
    private const float StaminaPeriod = 0.25f, StaminaChange = 0.01f, StaminaRefresh = 1.5f, StaminaFresh = 3f;
    private static readonly string[] SupplyKinds = { "medikit", "ammokit", "disinfection", "tool_refill" };
    internal static PlayerInputReport? Current { get; private set; }
    /// <summary>A client's edge the host can never take (it runs no Forge Engine, or refused this one): reported once, the edge
    /// itself is dropped. An edge sent while the greeting with a new host is still under way is dropped without a report.</summary>
    private const string NotSent = "player-input.fire-not-sent";
    private static readonly byte[] NoPayload = Array.Empty<byte>();
    private readonly PlayerEventFacts _events;
    private readonly IForgeEngine _kernel;
    private readonly Action<string> _report;
    private readonly IDisposable _endpoint;
    private bool _reported;
    private long _pingTick = -1;
    private float _pingX, _pingY, _pingZ;
    private float _staminaSampled, _staminaSentAt, _staminaSentValue = float.NaN;
    private readonly Dictionary<IntPtr, (float Value, float Relative, float At)> _staminas = new();
    private long _staminaWorld = -1;

    internal PlayerInputReport(PlayerEventFacts events, IForgeEngine kernel, Action<string> report)
    {
        _events = events; _kernel = kernel; _report = report;
        _endpoint = DomainCommands.Register(Endpoint, Execute);
        Current = this;
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _endpoint.Dispose();
        _staminas.Clear();
    }

    /// <summary>False when the hook publishes this player's event itself: the host's own player or a bot.</summary>
    private bool Diverted(PlayerAgent player, out bool mine)
    {
        var owner = player.Owner;
        mine = !Session.IsHost && owner != null && owner.IsLocal;
        return !(Session.IsHost && (owner == null || owner.IsLocal || owner.IsBot));
    }

    internal bool LowHealth(PlayerAgent player)
    {
        if (!Diverted(player, out var mine)) return false;
        if (mine) Report(LowHealthResource, NoPayload);
        return true;
    }

    internal bool SupplyUsed(PlayerAgent player, string kind)
    {
        if (!Diverted(player, out var mine)) return false;
        if (mine) Report(Supply, new[] { (byte)Array.IndexOf(SupplyKinds, kind) });
        return true;
    }

    internal bool Held(PlayerAgent player, double seconds)
    {
        if (!Diverted(player, out var mine)) return false;
        if (mine) Report(HeldResource, BitConverter.GetBytes((float)seconds));
        return true;
    }

    internal bool Movement(PlayerAgent player, int state, int previous)
    {
        if (!Diverted(player, out var mine)) return false;
        if (mine) Report(MovementResource, new[] { (byte)state, (byte)previous });
        return true;
    }

    /// <summary>A warp this machine just applied to its own player: the two ends it read travel to the host, which publishes
    /// `player.teleported` for the sender. A warp whose landing this machine cannot read is not reported.</summary>
    internal bool Teleported(PlayerAgent player, double[] from, double[]? to)
    {
        if (!Diverted(player, out var mine)) return false;
        if (mine && to != null) Report(PlayerTeleportReport.Resource, PlayerTeleportReport.Encode(from, to));
        return true;
    }

    /// <summary>A ping reaches this machine through two entries with one position; the second of the same tick does not travel.</summary>
    internal bool Ping(PlayerAgent player, double[]? position)
    {
        if (!Diverted(player, out var mine)) return false;
        if (!mine || position is not { Length: 3 }) return true;
        float x = (float)position[0], y = (float)position[1], z = (float)position[2];
        if (_pingTick == _kernel.CurrentTick && _pingX == x && _pingY == y && _pingZ == z) return true;
        (_pingTick, _pingX, _pingY, _pingZ) = (_kernel.CurrentTick, x, y, z);
        var payload = new byte[12];
        BitConverter.TryWriteBytes(payload.AsSpan(0), x);
        BitConverter.TryWriteBytes(payload.AsSpan(4), y);
        BitConverter.TryWriteBytes(payload.AsSpan(8), z);
        Report(PingResource, payload);
        return true;
    }

    /// <summary>Client side, every frame: while a loaded plan reads stamina, samples the local player's stamina at the low fixed
    /// rate and reports it when it moved or the last report is about to go stale; the first sample after a reader appears always
    /// travels. The host never reports, it reads its own players directly.</summary>
    internal void StaminaFrame()
    {
        if (Session.IsHost) return;
        if (!_events.StaminaRead) { _staminaSentValue = float.NaN; return; }
        var now = UnityEngine.Time.time;
        if (now - _staminaSampled < StaminaPeriod) return;
        _staminaSampled = now;
        float value, relative;
        try
        {
            var stamina = Players.Local?.Stamina;
            if (stamina == null || stamina.Pointer == IntPtr.Zero) return;
            value = stamina.Stamina; relative = stamina.StaminaRelative;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("stamina-report-read", error); return; }
        if (!float.IsFinite(value) || !float.IsFinite(relative)) return;
        if (Math.Abs(relative - _staminaSentValue) < StaminaChange && now - _staminaSentAt < StaminaRefresh) return;
        (_staminaSentValue, _staminaSentAt) = (relative, now);
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0), value);
        BitConverter.TryWriteBytes(payload.AsSpan(4), relative);
        Report(StaminaResource, payload);
    }

    /// <summary>Host side: the last stamina a client reported for this player, or false when it never did or the report is stale.</summary>
    internal bool TryStamina(PlayerAgent player, out float value, out float relative)
    {
        value = relative = 0f;
        if (_staminaWorld != _kernel.WorldEpoch) return false;
        if (!_staminas.TryGetValue(player.Pointer, out var sample) || UnityEngine.Time.time - sample.At > StaminaFresh) return false;
        (value, relative) = (sample.Value, sample.Relative);
        return true;
    }

    internal void FirePressed()
    {
        if (Session.IsHost) PlayerEventHooks.FirePressed(_events, Players.Local);
        else Report(Fire, NoPayload);
    }

    internal void KeyEdge(PlayerAgent local, int key, int phase)
    {
        if (Session.IsHost) PlayerEventHooks.KeyPressed(_events, local, key, phase);
        else Report(Key, new[] { (byte)key, (byte)(key >> 8), (byte)phase });
    }

    private void Report(string resource, byte[] payload)
    {
        if (DomainCommands.Send(Endpoint, resource, 0, payload) || _reported || !DomainCommands.HostUnreachable) return;
        _reported = true;
        _report(NotSent + ": a " + resource + " edge could not be sent to the host, which runs no matching Forge Engine; this machine's edges are not published there.");
    }

    internal DomainCommandResult Execute(DomainCommandRequest request)
    {
        if (!Session.IsHost) return Rejected("not-authoritative");
        if (request.WorldEpoch != _kernel.WorldEpoch) return Rejected("stale-world");
        var sender = Level.PlayerByLookup(request.SenderSession);
        if (sender == null) return Rejected("sender-not-player");
        var payload = request.Payload;
        bool published;
        if (request.ResourceId == Fire && payload.Length == 0) published = _events.FirePressed(sender);
        else if (request.ResourceId == Key && payload.Length == 3)
            published = _events.KeyPressed(sender, payload[0] | payload[1] << 8, payload[2]);
        else if (request.ResourceId == HeldResource && payload.Length == 4 && AllFinite(payload))
            published = _events.InputHeld(sender, BitConverter.ToSingle(payload, 0));
        else if (request.ResourceId == PingResource && payload.Length == 12 && AllFinite(payload))
            published = _events.Ping(sender, new double[] { BitConverter.ToSingle(payload, 0), BitConverter.ToSingle(payload, 4), BitConverter.ToSingle(payload, 8) });
        else if (request.ResourceId == MovementResource && payload.Length == 2)
            published = _events.MovementStateChanged(sender, payload[0], payload[1]);
        else if (request.ResourceId == Supply && payload.Length == 1 && payload[0] < SupplyKinds.Length)
            published = _events.SupplyUsed(sender, SupplyKinds[payload[0]]);
        else if (request.ResourceId == StaminaResource && payload.Length == 8 && AllFinite(payload))
        {
            // The reports of a world that ended name agents that are gone: they go with it.
            if (_staminaWorld != _kernel.WorldEpoch) { _staminas.Clear(); _staminaWorld = _kernel.WorldEpoch; }
            _staminas[sender.Pointer] = (BitConverter.ToSingle(payload, 0), BitConverter.ToSingle(payload, 4), UnityEngine.Time.time);
            published = true;
        }
        else if (request.ResourceId == PlayerTeleportReport.Resource && PlayerTeleportReport.TryDecode(payload, out var from, out var to))
        {
            var raised = false;
            PlayerHost.Session?.GuardLifeFacts(facts => raised = facts.Teleported(sender, from, to));
            published = raised;
        }
        else if (request.ResourceId == LowHealthResource && payload.Length == 0)
            published = PlayerStateFacts.Installed?.LowHealth(sender) == true;
        else return Rejected("invalid-payload");
        return published ? new("succeeded", "confirmed", "ok", "", 1) : new("succeeded", "none", "not-observed", "", 0);
    }

    /// <summary>Whether every float in a client's payload is finite; a NaN or infinite value is refused as an invalid payload.</summary>
    private static bool AllFinite(byte[] payload)
    {
        for (var offset = 0; offset < payload.Length; offset += 4)
            if (!float.IsFinite(BitConverter.ToSingle(payload, offset))) return false;
        return true;
    }

    private static DomainCommandResult Rejected(string code) => new("rejected", "none", code, "", 0);
}
