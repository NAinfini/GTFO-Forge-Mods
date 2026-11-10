using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ForgeCore;

internal interface IPlayerStateSlot
{
    void Forget(ulong player);
    void ForgetOthers();
}

/// <summary>
/// One value per player with the moment it began (<see cref="NetChannel.PlayerState{T}(string)"/>): who is doing what, since when. Each player
/// writes only their own; the host does not arbitrate. On the wire the moment is "seconds ago", turned back into this peer's own clock on
/// arrival, so clocks are never synchronised and the error is one trip's latency. A player whose copy answers later (a late joiner, a
/// reconnect) gets the current value with the seconds counted to then. A player who leaves, the session, the level ending and a
/// checkpoint reload clear it; a value stamped with another world's <see cref="NetChannel.Epoch"/> is dropped. On a channel that follows the
/// world such a value waits for this peer's next epoch and goes in only if it is that one, so a peer between worlds neither shows an earlier
/// world's value nor loses the current one, which is sent only once. Main thread only.
/// </summary>
public sealed class PlayerStateSlot<T> : IPlayerStateSlot where T : unmanaged
{
    private readonly NetChannel _channel;
    private readonly string _event;
    private readonly Func<float> _clock;
    private readonly Dictionary<ulong, (T Value, float Start)> _values = new();
    private readonly ModLog _log;
    private readonly Dictionary<ulong, (byte[] Data, float Arrived)> _waiting = new();
    private bool _hasOwn;
    private T _own;
    private float _ownStart;

    internal PlayerStateSlot(NetChannel channel, string eventName, Func<float> clock, ModLog log)
    {
        _channel = channel;
        _event = eventName;
        _clock = clock;
        _log = log;
    }

    /// <summary>A player's value arrived (true) or went (false). The local player's own never raises it.</summary>
    public event Action<ulong, bool>? Changed;

    /// <summary>Sets the local player's value, begun at <paramref name="start"/> (this peer's clock), and sends it to the others.</summary>
    public void Publish(T value, float start)
    {
        _hasOwn = true;
        _own = value;
        _ownStart = start;
        _channel.SendState(_event, Frame(true), 0);
    }

    /// <summary>Takes the local player's value back; nothing happens when there is none.</summary>
    public void Clear()
    {
        if (!_hasOwn) return;
        _hasOwn = false;
        _channel.SendState(_event, Frame(false), 0);
    }

    /// <summary>The current value of <paramref name="player"/> (the local player's too) and when it began on this peer's clock.</summary>
    public bool TryGet(ulong player, out T value, out float start)
    {
        if (player == _channel.LocalPlayer)
        {
            value = _own;
            start = _ownStart;
            return _hasOwn;
        }
        if (_values.TryGetValue(player, out var held))
        {
            value = held.Value;
            start = held.Start;
            return true;
        }
        value = default;
        start = 0f;
        return false;
    }

    internal void Resend(ulong player)
    {
        if (_hasOwn) _channel.SendState(_event, Frame(true), player);
    }

    private byte[] Frame(bool has)
    {
        var size = Marshal.SizeOf<T>();
        var data = new byte[HeaderSize + size];
        data[0] = has ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(data.AsSpan(1), _channel.Epoch);
        BitConverter.TryWriteBytes(data.AsSpan(9), has ? _clock() - _ownStart : 0f);
        if (has) MemoryMarshal.Write(data.AsSpan(HeaderSize), ref _own);
        return data;
    }

    private const int HeaderSize = 13;

    internal void Receive(ulong sender, byte[] data)
    {
        if (data.Length != HeaderSize + Marshal.SizeOf<T>())
        {
            Net.Drop(Net.DropStateSize);
            _log.ErrorOnce(Net.DropStateSize, "A player state of " + _channel.ModId + " came with " + data.Length + " bytes; it was dropped.");
            return;
        }
        var epoch = BitConverter.ToUInt64(data, 1);
        if (epoch != 0 && epoch != _channel.Epoch)
        {
            if (_channel.FollowsWorld) _waiting[sender] = (data, _clock());
            if (_channel.FollowsWorld || _channel.Epoch != 0) return;
        }
        _waiting.Remove(sender);
        Apply(sender, data, _clock());
    }

    /// <summary>This peer took a new epoch: what waited for it goes in, as of when it arrived; anything else was another world's.</summary>
    internal void EpochTaken()
    {
        if (_waiting.Count == 0) return;
        var waiting = new List<KeyValuePair<ulong, (byte[] Data, float Arrived)>>(_waiting);
        _waiting.Clear();
        foreach (var entry in waiting)
            if (BitConverter.ToUInt64(entry.Value.Data, 1) == _channel.Epoch) Apply(entry.Key, entry.Value.Data, entry.Value.Arrived);
    }

    private void Apply(ulong sender, byte[] data, float arrived)
    {
        var ago = BitConverter.ToSingle(data, 9);
        if (data[0] > 1 || !float.IsFinite(ago) || ago < 0f)
        {
            Invalid();
            return;
        }
        if (data[0] == 0) { Forget(sender); return; }
        var value = MemoryMarshal.Read<T>(data.AsSpan(HeaderSize));
        if (!Finite(value)) { Invalid(); return; }
        _values[sender] = (value, arrived - ago);
        Changed?.Invoke(sender, true);
    }

    // A packet the sender could not have written (a flag other than 0 or 1, seconds that are not a finite non-negative number, a float
    // field that is NaN or infinite) is counted and dropped; the sender's earlier value stays.
    private void Invalid()
    {
        Net.Drop(Net.DropStateInvalid);
        _log.ErrorOnce(Net.DropStateInvalid, "A player state of " + _channel.ModId + " carried an impossible flag, time or number; it was dropped.");
    }

    private static readonly System.Reflection.FieldInfo[] FloatFields = FloatsOf(typeof(T));

    private static System.Reflection.FieldInfo[] FloatsOf(Type type)
    {
        var found = new List<System.Reflection.FieldInfo>();
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            if (field.FieldType == typeof(float) || field.FieldType == typeof(double)) found.Add(field);
        return found.ToArray();
    }

    private static bool Finite(T value)
    {
        if (FloatFields.Length == 0) return true;
        object boxed = value;
        foreach (var field in FloatFields)
            if (field.GetValue(boxed) is float single ? !float.IsFinite(single) : !double.IsFinite((double)field.GetValue(boxed)!)) return false;
        return true;
    }

    /// <inheritdoc cref="IPlayerStateSlot.Forget"/>
    public void Forget(ulong player)
    {
        _waiting.Remove(player);
        if (_values.Remove(player)) Changed?.Invoke(player, false);
    }

    /// <summary>Every player's value goes, the local player's too (the world or session ended).</summary>
    public void ForgetAll()
    {
        _hasOwn = false;
        ForgetOthers();
    }

    /// <summary>Every other player's value goes and the local player's stays: the host changed, this peer's action is still going, and the players who answer the new greeting get it again.</summary>
    public void ForgetOthers()
    {
        if (_values.Count == 0) return;
        var players = new List<ulong>(_values.Keys);
        foreach (var player in players) Forget(player);
    }
}
