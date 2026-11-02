using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ForgeCore;

namespace ForgeOrdnance;

/// <summary>Why a call was refused. The value travels in <see cref="RejectMessage.Reason"/>.</summary>
internal enum Rejection : byte
{
    None = 0,
    NotInLoadout = 1,
    Cooldown = 2,
    NoUses = 3,
    TooFar = 4,
    InvalidLanding = 5,
    DefinitionMismatch = 6,
    NotHost = 7,
    Disabled = 8,
    UnknownDefinition = 9,
    LoadoutInvalid = 10,
    LandingTimeout = 11,
    CallMenuOff = 12
}

internal static class Messages
{
    /// <summary>Fixed on every peer; native pReplicationData.PrefabID is only 16 bits.</summary>
    public const ushort BeaconItemId = 65114;
    public const int MaxLoadout = 8;

    public const string HelloEvent = "Hello";
    public const string LoadoutEvent = "Loadout";
    public const string LoadoutStateEvent = "LoadoutState";
    public const string LandingEvent = "Landing";
    public const string RejectEvent = "Reject";
    public const string AcceptedEvent = "Accepted";
    public const string ArrivedEvent = "Arrived";
    public const string SlotEvent = "Slot";
    public const string CheckpointEvent = "Checkpoint";
    public const string SentryExpiryEvent = "SentryExpiry";
    public const string DeliveryEvent = "Delivery";
    public const string PayloadDataEvent = "PayloadData";
    /// <summary>Kept per-call state the host replays to a player who joined later (same layout as <see cref="PayloadDataEvent"/>).</summary>
    public const string PayloadReplayEvent = "PayloadReplay";
    /// <summary>A drop pod or turret shell the host replays to a player who joined later.</summary>
    public const string DeliveryReplayEvent = "DeliveryReplay";
    /// <summary>A player asks the host for a claimable (client to host), and the host's word on that claim (host to every peer).</summary>
    public const string ClaimEvent = "Claim", ClaimStateEvent = "ClaimState";
    /// <summary>Lobby values (ForgeCore per-player lobby data): each player's picks, and the host's rules for the selected expedition.</summary>
    public const string LobbyPicks = "picks", LobbyRules = "rules";
    /// <summary>Bytes one <see cref="PayloadDataMessage"/> carries.</summary>
    public const int MaxPayloadData = 192;

    public static string Describe(Rejection reason) => reason switch
    {
        Rejection.NotInLoadout => "not in your loadout",
        Rejection.Cooldown => "still on cooldown",
        Rejection.NoUses => "no uses left",
        Rejection.TooFar => "landed too far away",
        Rejection.InvalidLanding => "no reachable ground at the landing point",
        Rejection.DefinitionMismatch => "your call-in files differ from the host's",
        Rejection.NotHost => "only the host can do that",
        Rejection.Disabled => "call-ins are disabled for you",
        Rejection.UnknownDefinition => "unknown call-in",
        Rejection.LoadoutInvalid => "the host refused your loadout",
        Rejection.LandingTimeout => "the beacon never settled",
        Rejection.CallMenuOff => "this level has no call-ins",
        _ => "refused"
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Vec3
{
    public float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public readonly bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    public static float Distance(Vec3 a, Vec3 b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}

/// <summary>Both directions of the level handshake. A client sends it to the host, the host answers (and broadcasts it when a level
/// starts): it carries the host's recovery epoch and throw numbering. Whether the two copies match (message formats, call-in files, payload
/// types) is ForgeCore's channel handshake.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HelloMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Greeting;
    public uint ThrowFloor;
}

/// <summary>A loadout: sent by a client to the host as a submission (Player ignored) and broadcast by the host as the accepted
/// loadout of <see cref="Player"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct LoadoutMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Player;
    public byte Count;
    public fixed uint Keys[Messages.MaxLoadout];

    public static LoadoutMessage From(ulong player, IReadOnlyList<uint> keys)
    {
        if (keys.Count > Messages.MaxLoadout) throw new ArgumentException("Loadout is longer than " + Messages.MaxLoadout + ".", nameof(keys));
        var message = new LoadoutMessage { Player = player, Count = (byte)keys.Count };
        for (var i = 0; i < keys.Count; i++) message.Keys[i] = keys[i];
        return message;
    }

    /// <summary>The keys, or null when <see cref="Count"/> exceeds the fixed capacity (a malformed message).</summary>
    public readonly uint[]? ToKeys()
    {
        if (Count > Messages.MaxLoadout) return null;
        var keys = new uint[Count];
        fixed (uint* source = Keys) for (var i = 0; i < Count; i++) keys[i] = source[i];
        return keys;
    }
}

/// <summary>The thrower's report that the ball came to rest.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LandingMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public uint ThrowId;
    public uint Key;
    public ushort Replicator;
    public Vec3 Origin;
    public Vec3 Landing;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RejectMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public uint ThrowId;
    public uint Key;
    public byte Reason;
}

/// <summary>The host accepted the call: the beacon stands at <see cref="Landing"/> and the arrival follows after
/// <see cref="Delay"/> seconds on every peer's own clock.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AcceptedMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Thrower;
    public uint ThrowId;
    public uint Key;
    public Vec3 Landing;
    public float Delay;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ArrivedMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Thrower;
    public uint ThrowId;
    public uint Key;
    public Vec3 Landing;
    /// <summary>Degrees about the vertical: the host's arrival rotation, so every peer orients the drop pod the same way.</summary>
    public float Yaw;
    /// <summary><see cref="OrdnanceArrivalMode"/>: with or without the vehicle.</summary>
    public byte Arrival;
}

/// <summary>The host's word on a drop pod: <see cref="Emptied"/> when its last pickup has been taken.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DeliveryMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public const byte Emptied = 1, Failed = 2;
    public ulong Thrower;
    public uint ThrowId;
    public byte State;
}

/// <summary>Host to one late joiner: an arrival already in the level, so the joiner shows it in its current state.
/// <see cref="Elapsed"/> is seconds since the arrival started on the host; <see cref="SinceEmptied"/> seconds since a pod was
/// reported empty, or negative.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DeliveryReplayMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public const byte Pod = 1;
    public const byte Shell = 2;
    public byte Kind;
    public ulong Thrower;
    public uint ThrowId;
    public Vec3 Landing;
    public float Yaw;
    public float Elapsed;
    public float SinceEmptied;
    public uint DefinitionKey;
    public uint LookKey;
}

/// <summary>Host to every peer: data a payload type sends about one call (see <c>OrdnanceApi.SendData</c>). <see cref="Type"/>
/// is the FNV-1a hash of the payload type name.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PayloadDataMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Thrower;
    public uint ThrowId;
    public uint Type;
    public byte Tag;
    public byte Length;
    public fixed byte Data[Messages.MaxPayloadData];

    public static PayloadDataMessage From(ulong thrower, uint throwId, uint type, byte tag, ReadOnlySpan<byte> data)
    {
        if (data.Length > Messages.MaxPayloadData) throw new ArgumentException("Payload data is longer than " + Messages.MaxPayloadData + " bytes.", nameof(data));
        var message = new PayloadDataMessage { Thrower = thrower, ThrowId = throwId, Type = type, Tag = tag, Length = (byte)data.Length };
        for (var i = 0; i < data.Length; i++) message.Data[i] = data[i];
        return message;
    }

    /// <summary>The data, or null when <see cref="Length"/> exceeds the fixed capacity (a malformed message).</summary>
    public readonly byte[]? ToBytes()
    {
        if (Length > Messages.MaxPayloadData) return null;
        var bytes = new byte[Length];
        fixed (byte* source = Data) for (var i = 0; i < Length; i++) bytes[i] = source[i];
        return bytes;
    }
}

/// <summary>One slot of one player, broadcast by the host after every change.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SlotMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ulong Player;
    public uint Key;
    /// <summary>Negative: the call-in has no use limit.</summary>
    public int UsesLeft;
    public float CooldownRemaining;
}

/// <summary>Host's recovery image: Begin, every Entry, then Commit on the reliable ordered channel.
/// Cooldowns stay frozen at checkpoint time.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CheckpointMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public const byte Begin = 0, Entry = 1, Commit = 2;
    public byte Kind;
    public ulong Player;
    public uint Key;
    public int UsesLeft;
    public double CooldownRemaining;
}

/// <summary>Native gear key-plus-one, exact per-spawn identity and seconds until removal, also replayed to late joiners.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SentryExpiryMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public ushort Replicator;
    public ulong Identity;
    public float Remaining;
}

/// <summary>Client to host: a step of taking a claimable (<c>OrdnancePod.AddClaimable</c>). <see cref="Attempt"/> is the claimant's
/// own sequence number; later steps and the host's answers match it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ClaimMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    /// <summary>Ask for it; the gear is fully installed; installing failed or timed out.</summary>
    public const byte Request = 0, Installed = 1, Failed = 2;
    public ulong Thrower;
    public uint ThrowId;
    public uint Attempt;
    public byte Action;
}

/// <summary>Host to every peer: <see cref="Reserved"/> while the claimant installs it, <see cref="Committed"/> when it is taken (with
/// the claiming package's own <see cref="Token"/>), <see cref="Cancelled"/> when it is open again.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ClaimStateMessage : INetEpoch
{
    public ulong Epoch;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
    public const byte Reserved = 0, Committed = 1, Cancelled = 2;
    public ulong Thrower;
    public uint ThrowId;
    public ulong Player;
    public uint Attempt;
    public byte State;
    public Guid Token;
}