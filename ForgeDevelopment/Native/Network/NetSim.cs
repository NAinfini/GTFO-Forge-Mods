using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;

namespace ForgeDevelopment.Native;

/// <summary>
/// Network conditions for testing on one LAN: every packet this machine sends (the game's own and every mod's, which all leave
/// through <c>SNet_Core_STEAM.SendBytes</c>) is held for a delay with jitter, and unreliable packets are dropped at a rate. Only
/// the send side is touched, so both machines of a test run it and each direction gets its own delay. A reliable packet is
/// never dropped (the transport would have sent it again): its "loss" is one more delay, and the reliable packets for one
/// player leave in the order they were sent, whatever their channel (one connection carries them all). The recipients of a
/// group or list send are fixed when the game sends, as on a real link: a player who joins the group while the packet waits
/// does not get it and one who leaves it still does. Voice is not covered if it leaves by another path.
/// <para>Off until <see cref="Set"/> is called: by a command file's <c>call</c> step, or on the first frame from the
/// <c>[Network Simulation]</c> settings, which is the way to have it on in a multiplayer session (the command console is
/// closed while another player is in the session). The three hooks go on through ForgeCore's hook layer on that first call and
/// pass everything through while the simulation is off.</para>
/// </summary>
// patch-targets: SNet_Core_STEAM.SendBytes
internal static class NetSim
{
    private const string Writer = "ForgeDevelopment.NetSim";
    /// <summary>The data of an il2cpp array starts after its object header and bounds.</summary>
    private static readonly int ArrayHeader = IntPtr.Size * 4;

    private struct Held
    {
        public byte[] Bytes;
        public SNet_SendQuality Quality;
        public int Channel;
        public SNet_Player Player;
        public long ReleaseAt;
    }

    private static readonly List<Held> Queue = new(256);
    // The latest release time of the reliable packets for each player, so order is kept.
    private static readonly Dictionary<IntPtr, long> StreamTail = new();
    private static readonly Random Dice = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _installed, _framed, _passing, _configured;
    private static double _delayMs, _jitterMs, _lossPercent;
    private static long _held, _dropped, _released;
    private static BepInEx.Configuration.ConfigEntry<double> _configDelay = null!, _configJitter = null!, _configLoss = null!;

    internal static bool Active => _delayMs > 0 || _jitterMs > 0 || _lossPercent > 0;

    internal static void Bind(BepInEx.Configuration.ConfigFile config)
    {
        const string section = "Network Simulation";
        _configDelay = config.Bind(section, "DelayMs", 0d, "Hold every packet this machine sends for this many milliseconds, from the first frame on. Set it on each machine of a test for a delay in both directions. All three at 0 leaves the simulation off. Restart required.");
        _configJitter = config.Bind(section, "JitterMs", 0d, "Up to this many more milliseconds per packet. Restart required.");
        _configLoss = config.Bind(section, "LossPercent", 0d, "Drop this share of the unreliable packets; a reliable packet waits once more instead. Restart required.");
    }

    /// <summary>Turns the simulation on with the configured values on the first frame, when any of them is set.</summary>
    internal static void StartConfigured()
    {
        if (_configDelay.Value == 0 && _configJitter.Value == 0 && _configLoss.Value == 0) return;
        _configured = true;
        if (!_framed) { _framed = true; ForgeCore.Frame.OnUpdate(Writer, Tick); }
    }

    /// <summary>Turns the simulation on: every packet waits <paramref name="delayMs"/> plus up to <paramref name="jitterMs"/>,
    /// and <paramref name="lossPercent"/> of the unreliable ones are dropped (of the reliable ones, delayed once more).</summary>
    public static string Set(double delayMs, double jitterMs, double lossPercent)
    {
        if (!(delayMs >= 0 && delayMs <= 5000) || !(jitterMs >= 0 && jitterMs <= 5000) || !(lossPercent >= 0 && lossPercent <= 100))
            return Refused("delay and jitter are 0 to 5000 ms, loss is 0 to 100 percent");
        if (!_installed)
        {
            var reasons = Install();
            // A hook that is on stays on and passes everything through; a partial simulation would lie about the link.
            if (reasons.Count > 0) return Refused(string.Join("; ", reasons));
            _installed = true;
        }
        if (!_framed) { _framed = true; ForgeCore.Frame.OnUpdate(Writer, Tick); }
        // All three at zero is the simulation off, and is said so.
        if (delayMs == 0 && jitterMs == 0 && lossPercent == 0) return Off();
        _delayMs = delayMs; _jitterMs = jitterMs; _lossPercent = lossPercent;
        var line = "net-sim on delay=" + Text(delayMs) + "ms jitter=" + Text(jitterMs) + "ms loss=" + Text(lossPercent) + "% (send side of this machine)";
        Plugin.PluginLog.LogWarning(line);
        return line;
    }

    /// <summary>Turns the simulation off; what is held leaves at once, in order.</summary>
    public static string Off()
    {
        _delayMs = _jitterMs = _lossPercent = 0;
        Flush(long.MaxValue);
        StreamTail.Clear();
        Plugin.PluginLog.LogWarning("net-sim off");
        return Report();
    }

    /// <summary>The state and the counts; also written to the log, so a session's log says whether the simulation was on.</summary>
    public static string Report()
    {
        var line = "net-sim " + (Active ? "on" : "off") + " delay=" + Text(_delayMs) + "ms jitter=" + Text(_jitterMs) + "ms loss=" + Text(_lossPercent)
            + "% held=" + _held.ToString(CultureInfo.InvariantCulture) + " released=" + _released.ToString(CultureInfo.InvariantCulture)
            + " dropped=" + _dropped.ToString(CultureInfo.InvariantCulture) + " waiting=" + Queue.Count.ToString(CultureInfo.InvariantCulture);
        Plugin.PluginLog.LogInfo(line);
        return line;
    }

    private static string Refused(string reason)
    {
        var line = "refused: " + reason;
        Plugin.PluginLog.LogError("net-sim " + line + "; the simulation is not on");
        return line;
    }

    private static string Text(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static void Tick()
    {
        if (_configured) { _configured = false; Set(_configDelay.Value, _configJitter.Value, _configLoss.Value); }
        if (Queue.Count > 0) Flush(Clock.ElapsedMilliseconds);
    }

    // Due packets leave in the order they were held; a later packet of the same stream is never due before an earlier one.
    private static void Flush(long now)
    {
        var kept = 0;
        _passing = true;
        try
        {
            for (var i = 0; i < Queue.Count; i++)
            {
                var held = Queue[i];
                if (held.ReleaseAt > now) { Queue[kept++] = held; continue; }
                try { Send(held); _released++; }
                catch (Exception error) { Plugin.PluginLog.LogError("net-sim: a held packet could not be sent and is lost: " + error.Message); }
            }
        }
        finally { _passing = false; }
        Queue.RemoveRange(kept, Queue.Count - kept);
    }

    // The sender a held packet leaves through: the one the game was sending through when a packet was last held.
    private static SNet_Core_STEAM? _core;

    private static void Send(in Held held)
    {
        var core = _core;
        // A player who left while the packet waited gets nothing, as a packet on the wire would have reached nobody.
        if (core == null || core.Pointer == IntPtr.Zero || held.Player.Pointer == IntPtr.Zero) return;
        core.SendBytes(held.Bytes, held.Quality, held.Channel, held.Player);
    }

    // The game sends the whole array (build 20403457: all three overloads pass the array's length to SendP2PPacket) and
    // reuses it for the next packet of the same kind, so what waits is a copy.
    private static byte[] Copy(Il2CppStructArray<byte> bytes)
    {
        var copy = new byte[bytes.Length];
        Marshal.Copy(IntPtr.Add(bytes.Pointer, ArrayHeader), copy, 0, copy.Length);
        return copy;
    }

    /// <summary>Holds or drops one packet for one player.</summary>
    private static void Hold(byte[] bytes, SNet_SendQuality quality, int channel, SNet_Player player)
    {
        var reliable = quality >= SNet_SendQuality.Reliable;
        var lost = _lossPercent > 0 && Dice.NextDouble() * 100d < _lossPercent;
        if (lost && !reliable) { _dropped++; return; }
        var wait = _delayMs + (_jitterMs > 0 ? Dice.NextDouble() * _jitterMs : 0) + (lost ? Math.Max(_delayMs, 100d) : 0);
        var release = Clock.ElapsedMilliseconds + (long)wait;
        if (reliable)
        {
            // One tail per player, whatever the channel and whether the packet was a group, list or single send: they all
            // travel on that player's one connection, which keeps reliable packets in the order they were sent.
            if (StreamTail.TryGetValue(player.Pointer, out var tail) && tail > release) release = tail;
            StreamTail[player.Pointer] = release;
        }
        Queue.Add(new Held { Bytes = bytes, Quality = quality, Channel = channel, Player = player, ReleaseAt = release });
        _held++;
    }

    /// <summary>True when the original runs now; false when the packet was held or dropped for each of the players.</summary>
    private static bool HoldEach(Il2CppStructArray<byte>? bytes, SNet_SendQuality quality, int channel, Il2CppSystem.Collections.Generic.List<SNet_Player>? players)
    {
        if (_passing || !Active || bytes == null || players == null) return true;
        var copy = Copy(bytes);
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player != null) Hold(copy, quality, channel, player);
        }
        return false;
    }

    // The three prefixes decide whether the original runs, so they are this writer's own declarations on a method no other
    // package hooks. They are not [HarmonyPatch] classes: the collection sweep installs every such class of this assembly as
    // a read-only observer and refuses a callback that returns a value.
    private static List<string> Install()
    {
        var reasons = new List<string>();
        void Declare(string name, Func<System.Reflection.MethodInfo> target)
        {
            try { DevelopmentHookInstall.Managed(Writer, target(), Prefix(name), prefixPriority: Priority.Normal); }
            catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException) { reasons.Add(name + ": " + error.Message); }
        }
        Declare(nameof(GroupSend), () =>
            typeof(SNet_Core_STEAM).GetMethod(nameof(SNet_Core_STEAM.SendBytes), new[] { typeof(Il2CppStructArray<byte>), typeof(SNet_SendGroup), typeof(SNet_SendQuality), typeof(int) })
                ?? throw new NotSupportedException("native-hook-target-signature-unverified"));
        Declare(nameof(ListSend), () =>
            typeof(SNet_Core_STEAM).GetMethod(nameof(SNet_Core_STEAM.SendBytes), new[] { typeof(Il2CppStructArray<byte>), typeof(SNet_SendQuality), typeof(int), typeof(Il2CppSystem.Collections.Generic.List<SNet_Player>) })
                ?? throw new NotSupportedException("native-hook-target-signature-unverified"));
        Declare(nameof(PlayerSend), () =>
            typeof(SNet_Core_STEAM).GetMethod(nameof(SNet_Core_STEAM.SendBytes), new[] { typeof(Il2CppStructArray<byte>), typeof(SNet_SendQuality), typeof(int), typeof(SNet_Player) })
                ?? throw new NotSupportedException("native-hook-target-signature-unverified"));
        return reasons;
    }

    private static System.Reflection.MethodInfo Prefix(string name)
        => typeof(NetSim).GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

    // The group's members as they are now, which is who the game is sending to: the game moves players in and out of its
    // send groups as they join, sync and leave, and a packet resolved against the group when it is released would reach
    // players it was not sent to and miss players it was.
    private static bool GroupSend(SNet_Core_STEAM __instance, Il2CppStructArray<byte> bytes, SNet_SendGroup group, SNet_SendQuality quality, int channel)
    {
        _core = __instance;
        if (_passing || !Active) return true;
        return HoldEach(bytes, quality, channel, SNet_PlayerSendGroupManager.GetGroup(group));
    }

    // A list is the caller's own and may change while a packet waits: each player gets its own held packet.
    private static bool ListSend(SNet_Core_STEAM __instance, Il2CppStructArray<byte> bytes, SNet_SendQuality quality, int channel, Il2CppSystem.Collections.Generic.List<SNet_Player> toPlayers)
    {
        _core = __instance;
        return HoldEach(bytes, quality, channel, toPlayers);
    }

    private static bool PlayerSend(SNet_Core_STEAM __instance, Il2CppStructArray<byte> bytes, SNet_SendQuality quality, int channel, SNet_Player toPlayer)
    {
        _core = __instance;
        if (_passing || !Active || bytes == null || toPlayer == null) return true;
        Hold(Copy(bytes), quality, channel, toPlayer);
        return false;
    }
}
