using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The engine's channel and the Map domain's two host tables on it. Only the host writes a table; ForgeCore sends
/// the rows that changed, the whole table to a player who joins and to everyone after a checkpoint reload or a host
/// change, and empties it when the level changes. Everything else Map shares rides the engine's state stream
/// (<c>DomainStateChannel.RegisterNativeState</c>) or its commands.</summary>
internal static class MapNet
{
    internal static NetChannel Channel { get; private set; } = null!;

    /// <summary>One row per placed trigger zone, keyed by its index in the level's zone table: the zone's
    /// <see cref="TriggerZoneClockState"/> as JSON.</summary>
    internal static NetByteTable<int> TriggerZones { get; private set; } = null!;

    /// <summary>One row per timed terminal sequence whose running phase the host moved in time, keyed by
    /// <see cref="ObjectiveRoundsNative.TimeKey"/>.</summary>
    internal static NetTable<int, TimedRoundTime> RoundTime { get; private set; } = null!;

    /// <summary>Once per process, before any session: every player registers the same messages.</summary>
    internal static void Open()
    {
        Channel = ForgeEngine.GameBindings.NetworkBinding.Channel;
        TriggerZones = Channel.Table<int>("map/trigger-zones", 1);
        RoundTime = Channel.Table<int, TimedRoundTime>("map/timed-round-time");
    }
}

/// <summary>How far the host moved the stamp of one phase of a timed terminal sequence, in total since that phase began.
/// <see cref="Phase"/> is the round index doubled, plus one while the round waits for its confirmation.</summary>
internal struct TimedRoundTime
{
    public int Phase;
    public float Seconds;
}
